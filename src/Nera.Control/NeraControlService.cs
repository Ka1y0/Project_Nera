using System.Security.Cryptography;
using System.Text.Json;

namespace ChipsStudio.Nera.Control;

public interface INeraControlClient
{
    event EventHandler<NeraStateSnapshot>? StateChanged;

    ValueTask<NeraStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<NeraCommandResult> ExecuteAsync(
        NeraCommandEnvelope command,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NeraDisplaySummary>> ListDisplaysAsync(CancellationToken cancellationToken = default);
}

public sealed record NeraControlOptions
{
    public int IdempotencyCapacity { get; init; } = 2048;
    public TimeSpan BackendStepTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan FailSafeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ActiveObservationInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan IdleObservationInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ObservationTimeout { get; init; } = TimeSpan.FromSeconds(1);

    internal void Validate()
    {
        if (IdempotencyCapacity is < 64 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(IdempotencyCapacity));
        }
        if (BackendStepTimeout <= TimeSpan.Zero || FirstFrameTimeout <= TimeSpan.Zero ||
            FailSafeTimeout <= TimeSpan.Zero || ActiveObservationInterval <= TimeSpan.Zero ||
            IdleObservationInterval <= TimeSpan.Zero || ObservationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BackendStepTimeout), "超时时间必须大于零。");
        }
        if (ActiveObservationInterval > IdleObservationInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(ActiveObservationInterval),
                "活动状态观察间隔不能超过空闲状态观察间隔。");
        }
    }
}

/// <summary>
/// Serializes every mutation from UI, hotkeys, AgentBridge, CLI, Orchestrator, and tests.
/// EmergencyStop cancels the active backend wait, jumps ahead of queued work, and then completes on
/// the same serialized state lane.
/// </summary>
public sealed class NeraControlService : INeraControlClient, IAsyncDisposable
{
    private readonly INeraControlBackend backend_;
    private readonly NeraControlOptions options_;
    private readonly NeraControlStateMachine stateMachine_;
    private readonly INeraControlAuditSink? auditSink_;
    private readonly bool ownsAuditSink_;
    private NeraStateSnapshot auditPreviousState_;
    private NeraCommandEnvelope? auditCommand_;
    private long auditSequence_;
    private readonly object queueGate_ = new();
    private readonly PriorityQueue<QueuedCommand, (int Priority, long Sequence)> queue_ = new();
    private readonly Dictionary<string, IdempotencyEntry> idempotency_ = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim queueSignal_ = new(0);
    private readonly CancellationTokenSource shutdown_ = new();
    private readonly Task worker_;
    private long queueSequence_;
    private CancellationTokenSource? activeCommandCancellation_;
    private bool disposed_;

    public NeraControlService(
        INeraControlBackend backend,
        NeraStateSnapshot initialState,
        NeraControlOptions? options = null,
        INeraControlAuditSink? auditSink = null,
        bool ownsAuditSink = false)
    {
        backend_ = backend ?? throw new ArgumentNullException(nameof(backend));
        options_ = options ?? new NeraControlOptions();
        options_.Validate();
        stateMachine_ = new NeraControlStateMachine(initialState);
        auditSink_ = auditSink;
        ownsAuditSink_ = ownsAuditSink;
        auditPreviousState_ = stateMachine_.Current;
        stateMachine_.Changed += OnStateChanged;
        worker_ = Task.Run(ProcessQueueAsync);
    }

    public event EventHandler<NeraStateSnapshot>? StateChanged;

    public NeraControlStateMachine StateMachine => stateMachine_;

    public ValueTask<NeraStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return ValueTask.FromResult(stateMachine_.Current);
    }

    public async Task<IReadOnlyList<NeraDisplaySummary>> ListDisplaysAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var displays = (await backend_.ListDisplaysAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        foreach (var display in displays)
        {
            NeraStatePolicy.ValidateDisplay(display);
        }
        if (displays.GroupBy(display => display.DisplayId, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
        {
            throw new InvalidDataException("后端返回了重复的显示器标识。");
        }
        return displays;
    }

    public async Task<NeraCommandResult> ExecuteAsync(
        NeraCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            NeraCommandPolicy.Validate(command);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        {
            var snapshot = stateMachine_.Current;
            var rejected = Failure(command?.RequestId ?? string.Empty, snapshot.Revision, snapshot,
                NeraControlCodes.InvalidArgument, "请求参数无效", error.Message);
            EmitAudit("InvalidCommand", command, snapshot, snapshot, rejected.Code, rejected);
            return rejected;
        }

        var fingerprint = Fingerprint(command);
        bool emergencyAccepted = command.Kind == NeraCommandKind.EmergencyStop &&
            !NeraInputOriginPolicy.IsForbiddenDisplayToggle(command);
        Task<NeraCommandResult> resultTask;
        CancellationTokenSource? interrupted = null;
        lock (queueGate_)
        {
            ThrowIfDisposedLocked();
            if (idempotency_.TryGetValue(command.RequestId, out var existing))
            {
                if (string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    resultTask = existing.ResultTask;
                }
                else
                {
                    var snapshot = stateMachine_.Current;
                    resultTask = Task.FromResult(Failure(command.RequestId, snapshot.Revision, snapshot,
                        NeraControlCodes.RequestIdReused,
                        "requestId 已用于另一条命令",
                        "同一个 requestId 不能用于内容不同的命令信封。"));
                }
            }
            else
            {
                var queued = new QueuedCommand(command);
                var sequence = checked(++queueSequence_);
                var priority = emergencyAccepted ? 0 : 1;
                queue_.Enqueue(queued, (priority, sequence));
                idempotency_.Add(command.RequestId,
                    new IdempotencyEntry(fingerprint, queued.Completion.Task, sequence));
                TrimIdempotencyCacheLocked();
                if (emergencyAccepted)
                {
                    interrupted = activeCommandCancellation_;
                }
                queueSignal_.Release();
                resultTask = queued.Completion.Task;
            }
        }

        if (emergencyAccepted && interrupted is not null)
        {
            try
            {
                backend_.SignalEmergencyStop();
            }
            catch
            {
                // The serialized EmergencyStop command remains authoritative and will report the outcome.
            }
            interrupted.Cancel();
        }

        return cancellationToken.CanBeCanceled
            ? await resultTask.WaitAsync(cancellationToken).ConfigureAwait(false)
            : await resultTask.ConfigureAwait(false);
    }

    public async Task<NeraStateSnapshot> WaitForStateAsync(
        Func<NeraStateSnapshot, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var current = stateMachine_.Current;
        if (predicate(current))
        {
            return current;
        }

        var completion = new TaskCompletionSource<NeraStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<NeraStateSnapshot>? handler = null;
        handler = (_, snapshot) =>
        {
            if (predicate(snapshot))
            {
                completion.TrySetResult(snapshot);
            }
        };
        StateChanged += handler;
        try
        {
            current = stateMachine_.Current;
            if (predicate(current))
            {
                return current;
            }
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StateChanged -= handler;
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? active;
        lock (queueGate_)
        {
            if (disposed_)
            {
                return;
            }
            disposed_ = true;
            active = activeCommandCancellation_;
        }
        active?.Cancel();
        shutdown_.Cancel();
        queueSignal_.Release();
        try
        {
            await worker_.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        stateMachine_.Changed -= OnStateChanged;
        if (ownsAuditSink_ && auditSink_ is IAsyncDisposable ownedAudit)
            await ownedAudit.DisposeAsync().ConfigureAwait(false);
        shutdown_.Dispose();
        queueSignal_.Dispose();
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            bool commandReady = await queueSignal_.WaitAsync(
                CurrentObservationInterval(), shutdown_.Token).ConfigureAwait(false);
            if (!commandReady)
            {
                await ProcessAuthoritativeObservationAsync().ConfigureAwait(false);
                continue;
            }
            QueuedCommand? queued;
            CancellationTokenSource? commandCancellation = null;
            lock (queueGate_)
            {
                if (queue_.Count == 0)
                {
                    if (disposed_)
                    {
                        return;
                    }
                    continue;
                }
                queued = queue_.Dequeue();
                commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown_.Token);
                activeCommandCancellation_ = commandCancellation;
            }

            NeraStateSnapshot auditStarting = stateMachine_.Current;
            auditCommand_ = queued.Command;
            EmitAudit("CommandStarted", queued.Command, auditStarting, auditStarting, queued.Command.Kind.ToString());
            try
            {
                var result = await ExecuteSerializedAsync(queued.Command, commandCancellation.Token)
                    .ConfigureAwait(false);
                EmitAudit("CommandCompleted", queued.Command, auditStarting, result.State, result.Code, result);
                queued.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (shutdown_.IsCancellationRequested)
            {
                var snapshot = stateMachine_.Current;
                var result = Failure(queued.Command.RequestId, snapshot.Revision, snapshot,
                    NeraControlCodes.OperationFailed, "Nera 正在退出", "控制服务关闭过程已中断此命令。");
                EmitAudit("CommandCompleted", queued.Command, auditStarting, snapshot, result.Code, result);
                queued.Completion.TrySetResult(result);
            }
            catch (Exception error)
            {
                var snapshot = stateMachine_.Current;
                var result = Failure(queued.Command.RequestId, snapshot.Revision, snapshot,
                    NeraControlCodes.OperationFailed, "操作失败", error.ToString());
                EmitAudit("CommandCompleted", queued.Command, auditStarting, snapshot, result.Code, result);
                queued.Completion.TrySetResult(result);
            }
            finally
            {
                auditCommand_ = null;
                lock (queueGate_)
                {
                    if (ReferenceEquals(activeCommandCancellation_, commandCancellation))
                    {
                        activeCommandCancellation_ = null;
                    }
                }
                commandCancellation.Dispose();
            }
        }
    }

    private TimeSpan CurrentObservationInterval()
    {
        NeraStateSnapshot state = stateMachine_.Current;
        return RequiresFailSafe(state) || state.DldrRequested ||
               state.DldrActualState is NeraDldrActualState.Processing or
                   NeraDldrActualState.Recovering
            ? options_.ActiveObservationInterval
            : options_.IdleObservationInterval;
    }

    private async Task ProcessAuthoritativeObservationAsync()
    {
        CancellationTokenSource observationCancellation;
        lock (queueGate_)
        {
            if (disposed_)
            {
                return;
            }
            observationCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown_.Token);
            activeCommandCancellation_ = observationCancellation;
        }

        try
        {
            await ObserveSerializedAsync(observationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (observationCancellation.IsCancellationRequested)
        {
            // Shutdown or a queued EmergencyStop owns the next state transition.
        }
        finally
        {
            lock (queueGate_)
            {
                if (ReferenceEquals(activeCommandCancellation_, observationCancellation))
                {
                    activeCommandCancellation_ = null;
                }
            }
            observationCancellation.Dispose();
        }
    }

    private async Task ObserveSerializedAsync(CancellationToken cancellationToken)
    {
        NeraStateSnapshot starting = stateMachine_.Current;
        try
        {
            NeraStateSnapshot observed = await WithTimeout(
                token => backend_.ObserveAuthoritativeStateAsync(starting, token),
                options_.ObservationTimeout,
                cancellationToken).ConfigureAwait(false) ??
                throw new InvalidDataException("后端返回的权威状态观察结果为 null。");
            NeraStateSnapshot merged = MergeAuthoritativeObservation(starting, observed);
            if (!AuthoritativeEquivalent(starting, merged))
            {
                stateMachine_.Commit(_ => merged);
            }
            bool enteredUnreleasedFailure =
                merged.DldrActualState == NeraDldrActualState.Failed &&
                !merged.DldrRequested && !merged.ProcessingActive &&
                RequiresFailSafe(merged) &&
                (starting.DldrActualState != NeraDldrActualState.Failed ||
                 starting.DldrRequested || starting.ProcessingActive ||
                 !RequiresFailSafe(starting));
            if (enteredUnreleasedFailure)
            {
                NeraErrorInfo? failure = merged.LastError;
                stateMachine_.Commit(state => state with
                {
                    DldrActualState = NeraDldrActualState.Recovering,
                    RecoveryState = NeraRecoveryState.Recovering,
                    ProcessingActive = false
                });
                await TryFailSafeAsync(
                    failure?.Code ?? NeraControlCodes.FailSafeFailed,
                    failure?.UserMessage ?? "清理尚未完成",
                    failure?.TechnicalMessage ??
                        "权威 OFF 证据仍显示原生资源由清理流程持有。")
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            EmitObservationFailure(error);
            await HandleObservationFailureAsync(error, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleObservationFailureAsync(
        Exception error,
        CancellationToken cancellationToken)
    {
        // A valid DisableDldr (or EmergencyStop) already at the head of the
        // serialized lane owns the release transition. The native host can finish
        // its orderly shutdown while an in-flight status read is still completing;
        // that superseded sample must not run fail-safe a second time. Do not apply
        // this exemption to any other command or to a stale-revision DisableDldr:
        // a real health-observation failure must still revoke Processing.
        lock (queueGate_)
        {
            if (disposed_)
            {
                return;
            }
            if (queue_.TryPeek(out QueuedCommand? next, out _) &&
                !NeraInputOriginPolicy.IsForbiddenDisplayToggle(next.Command) &&
                (next.Command.Kind == NeraCommandKind.EmergencyStop ||
                 next.Command.Kind == NeraCommandKind.DisableDldr &&
                 next.Command.ExpectedRevision == stateMachine_.Current.Revision))
            {
                return;
            }
        }

        string userMessage = ChipsStudio.Nera.Localization.NeraLocalizer.Get("Control.ObservationUnconfirmed");
        string technicalMessage = error.ToString();
        NeraStateSnapshot current = stateMachine_.Current;
        bool ownsGlobalOutput = RequiresFailSafe(current);
        if (!ownsGlobalOutput)
        {
            // A status sample can exceed the observation timeout while the native
            // backend is finishing an orderly OFF transition. Once the strict OFF
            // gate proves that no global output resource is owned, that late sample
            // is neither a DLDR failure nor grounds for a user-visible error. Keep
            // the healthy quiescent state intact; active/recovering states still take
            // the fail-safe path below.
            return;
        }

        if (current.DldrActualState == NeraDldrActualState.Failed &&
            current.RecoveryState == NeraRecoveryState.Failed &&
            current.LastError?.Code == NeraControlCodes.FailSafeFailed)
        {
            // One fail-safe attempt already failed. Preserve ownership evidence and
            // keep observing; a later successful native observation may prove release.
            return;
        }

        stateMachine_.Commit(state => state with
        {
            DldrRequested = false,
            ProcessingActive = false,
            DldrActualState = NeraDldrActualState.Recovering,
            RecoveryState = NeraRecoveryState.Recovering,
            LastError = Error(NeraControlCodes.OperationFailed,
                userMessage, technicalMessage) with { ReasonCode = NeraLifecycleReasonPolicy.ObservationFailure(error) }
        });

        NeraBackendOutcome failSafe;
        try
        {
            failSafe = await WithTimeout(
                token => backend_.EnterFailSafeBypassAsync(
                    NeraControlCodes.OperationFailed, token),
                options_.FailSafeTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failSafeError) when (failSafeError is not OutOfMemoryException and not StackOverflowException)
        {
            failSafe = NeraBackendOutcome.Failure(
                NeraControlCodes.FailSafeFailed,
                "无法确认普通 Windows 显示已恢复",
                failSafeError.ToString());
        }

        stateMachine_.Commit(state => failSafe.Succeeded
            ? QuiescentBypass(state) with
            {
                LastError = Error(NeraControlCodes.OperationFailed,
                    ChipsStudio.Nera.Localization.NeraLocalizer.Get("Control.ObservationInterrupted"), technicalMessage)
                    with { ReasonCode = NeraLifecycleReasonPolicy.ObservationFailure(error) }
            }
            : FailSafeReleaseUnconfirmed(state,
                NeraControlCodes.FailSafeFailed,
                "无法确认普通 Windows 显示已恢复",
                $"权威状态观察失败：{technicalMessage}\n" +
                $"故障安全恢复失败：{failSafe.TechnicalMessage}"));
    }

    private async Task<NeraCommandResult> ExecuteSerializedAsync(
        NeraCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var starting = stateMachine_.Current;
        if (NeraInputOriginPolicy.IsForbiddenDisplayToggle(command))
            return Failure(command.RequestId, starting.Revision, starting,
                NeraControlCodes.InputOriginRejected,
                ChipsStudio.Nera.Localization.NeraLocalizer.Get("Control.InputOriginRejected"),
                "Mouse, wheel, pointer and raw-input messages cannot issue display state commands.");
        bool mutationNeedsFailSafe = command.Kind == NeraCommandKind.EnableDldr ||
            (starting.DldrOn && command.Kind is NeraCommandKind.SetMode or NeraCommandKind.SetStrength or
                NeraCommandKind.SetFeature18Tuning or NeraCommandKind.SetDldrTuning or NeraCommandKind.SetPerformanceMode);
        if (command.Kind != NeraCommandKind.EmergencyStop && command.ExpectedRevision != starting.Revision)
        {
            return Failure(command.RequestId, starting.Revision, starting,
                NeraControlCodes.StateChanged,
                "状态已变化，请刷新后重试",
                $"expectedRevision={command.ExpectedRevision}; currentRevision={starting.Revision}.");
        }

        try
        {
            return command.Kind switch
            {
                NeraCommandKind.SetLanguage => ApplyLanguagePreference(command, starting),
                NeraCommandKind.EnableDldr => await EnableDldrAsync(command, starting, cancellationToken)
                    .ConfigureAwait(false),
                NeraCommandKind.DisableDldr => await DisableDldrAsync(command, starting, cancellationToken)
                    .ConfigureAwait(false),
                NeraCommandKind.EmergencyStop => await EmergencyStopAsync(command, starting)
                    .ConfigureAwait(false),
                _ => await ExecuteSimpleAsync(command, starting, cancellationToken).ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException) when (!shutdown_.IsCancellationRequested)
        {
            var current = stateMachine_.Current;
            if (current.DldrActualState != NeraDldrActualState.Recovering)
            {
                stateMachine_.Commit(state => state with
                {
                    DldrRequested = false,
                    ProcessingActive = false,
                    DldrActualState = NeraDldrActualState.Recovering,
                    RecoveryState = NeraRecoveryState.Recovering,
                    LastError = Error(NeraControlCodes.InterruptedByEmergencyStop,
                        "正在紧急停止", "正在执行的命令已由 EmergencyStop 取消。")
                });
            }
            current = stateMachine_.Current;
            return Failure(command.RequestId, starting.Revision, current,
                NeraControlCodes.InterruptedByEmergencyStop,
                "操作已被紧急停止中断",
                "EmergencyStop 已抢占后端等待。" );
        }
        catch (TimeoutException error)
        {
            var timeoutFailure = NeraBackendOutcome.Failure(
                NeraControlCodes.Timeout,
                "处理超时，已撤销 DLDR ON",
                error.Message);
            if (mutationNeedsFailSafe && RequiresFailSafe(stateMachine_.Current))
            {
                return await RecoverAfterFailureAsync(command, starting.Revision, timeoutFailure,
                    NeraControlCodes.Timeout).ConfigureAwait(false);
            }
            var failed = stateMachine_.Commit(state => state with
            {
                DldrRequested = command.Kind == NeraCommandKind.EnableDldr ? false : state.DldrRequested,
                ProcessingActive = command.Kind == NeraCommandKind.EnableDldr ? false : state.ProcessingActive,
                DldrActualState = command.Kind == NeraCommandKind.EnableDldr
                    ? NeraDldrActualState.Failed
                    : state.DldrActualState,
                LastError = Error(NeraControlCodes.Timeout, "操作超时", error.Message)
            });
            return Failure(command.RequestId, starting.Revision, failed,
                NeraControlCodes.Timeout, "操作超时", error.Message);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            var operationFailure = NeraBackendOutcome.Failure(
                NeraControlCodes.OperationFailed,
                "操作失败，已撤销 DLDR ON",
                error.ToString());
            if (mutationNeedsFailSafe && RequiresFailSafe(stateMachine_.Current))
            {
                return await RecoverAfterFailureAsync(command, starting.Revision, operationFailure,
                    NeraControlCodes.OperationFailed).ConfigureAwait(false);
            }
            var failed = stateMachine_.Commit(state => state with
            {
                DldrRequested = command.Kind == NeraCommandKind.EnableDldr ? false : state.DldrRequested,
                ProcessingActive = command.Kind == NeraCommandKind.EnableDldr ? false : state.ProcessingActive,
                DldrActualState = command.Kind == NeraCommandKind.EnableDldr
                    ? NeraDldrActualState.Failed
                    : state.DldrActualState,
                LastError = Error(NeraControlCodes.OperationFailed, "操作失败", error.ToString())
            });
            return Failure(command.RequestId, starting.Revision, failed,
                NeraControlCodes.OperationFailed, "操作失败", error.ToString());
        }
    }

    private async Task<NeraCommandResult> EnableDldrAsync(
        NeraCommandEnvelope command,
        NeraStateSnapshot starting,
        CancellationToken cancellationToken)
    {
        if (starting.DldrOn)
        {
            return Success(command, starting.Revision, starting, "DLDR 已开启");
        }
        if (starting.WarmStandby && starting.WarmOffGateSatisfied)
            return await ResumeWarmDldrAsync(command, starting, cancellationToken).ConfigureAwait(false);
        if (RequiresFailSafe(starting))
        {
            NeraBackendOutcome cleanup = await DeactivateDldrAsync(
                emergency: true, cancellationToken).ConfigureAwait(false);
            if (!cleanup.Succeeded)
            {
                NeraStateSnapshot failed = stateMachine_.Current;
                return Failure(command.RequestId, starting.Revision, failed,
                    cleanup.Code, cleanup.UserMessage, cleanup.TechnicalMessage);
            }
        }
        if (starting.TargetDisplay is null)
        {
            return Failure(command.RequestId, starting.Revision, starting,
                NeraControlCodes.DisplayRequired, "请选择显示器", "尚未选择目标显示器。" );
        }
        if (!starting.TargetDisplay.Connected || !starting.TargetDisplay.Capturable)
        {
            return Failure(command.RequestId, starting.Revision, starting,
                NeraControlCodes.DisplayUnavailable, "目标显示器不可用",
                "所选显示器已断开连接，或无法按显示器进行捕获。" );
        }

        stateMachine_.Commit(state => state with
        {
            DldrRequested = true,
            WarmStandby = false,
            WarmOffGateSatisfied = false,
            OverlayBypass = false,
            AppliedParameterRevision = 0,
            AppliedParameterFrameId = 0,
            DldrActualState = NeraDldrActualState.DisplayChecking,
            GpuVerified = false,
            RuntimeVerified = false,
            BrokerProcessOwned = false,
            BrokerConnected = false,
            HostConnected = false,
            MonitorCaptureCreated = false,
            FeatureCreated = false,
            FirstFeature18FrameSucceeded = false,
            DldrSucceeded = false,
            PresenterSucceeded = false,
            ForegroundPreserved = false,
            ProcessingActive = false,
            OffGateSatisfied = false,
            LastSuccessfulFrameId = 0,
            LastFeature18FrameId = 0,
            LastDldrFrameId = 0,
            LastPresentedFrameId = 0,
            Feature18SuccessfulFrames = 0,
            DldrSuccessfulFrames = 0,
            PresentedFrames = 0,
            RecoveryState = NeraRecoveryState.None,
            LastError = null
        });

        var displayCheck = await WithTimeout(
            token => backend_.ValidateCurrentDisplayAsync(token), options_.BackendStepTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!displayCheck.Succeeded || displayCheck.Display is null)
        {
            var failure = displayCheck.Succeeded
                ? NeraBackendOutcome.Failure(NeraControlCodes.DisplayUnavailable,
                    "目标显示器不可用", "显示器验证结果缺少显示器身份依据。")
                : displayCheck;
            return PreflightFailure(command, starting.Revision, failure,
                NeraControlCodes.DisplayUnavailable);
        }
        NeraStatePolicy.ValidateDisplay(displayCheck.Display);
        if (!string.Equals(displayCheck.Display.DisplayId, starting.TargetDisplay.DisplayId,
                StringComparison.Ordinal))
        {
            return PreflightFailure(command, starting.Revision,
                NeraBackendOutcome.Failure(NeraControlCodes.DisplayUnavailable,
                    "目标显示器已变化", "验证到的显示器身份与所选显示器不一致。"),
                NeraControlCodes.DisplayUnavailable);
        }
        var checkedDisplay = stateMachine_.Commit(state => ApplyDisplay(state, displayCheck.Display) with
        {
            DldrActualState = NeraDldrActualState.HdrChecking
        });

        if (!checkedDisplay.HdrSystemEnabled || !displayCheck.Display.HdrSupported ||
            !displayCheck.Display.AdvancedColorEnabled)
        {
            return PreflightFailure(command, starting.Revision,
                NeraBackendOutcome.Failure(NeraControlCodes.HdrSystemDisabled,
                    "请先在 Windows 中开启 HDR",
                    "目标显示器未报告 HDR 与高级颜色已开启。"),
                NeraControlCodes.HdrSystemDisabled);
        }

        stateMachine_.Commit(state => state with { DldrActualState = NeraDldrActualState.GpuChecking });
        var gpu = await WithTimeout(
            token => backend_.VerifyRtxGpuAsync(token), options_.BackendStepTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!gpu.Succeeded || gpu.GpuVerified != true)
        {
            var failure = gpu.Succeeded
                ? NeraBackendOutcome.Failure(NeraControlCodes.RtxGpuRequired,
                    "需要受支持的 RTX GPU", "GPU 验证结果缺少明确的 RTX 支持证据。")
                : gpu;
            return PreflightFailure(command, starting.Revision, failure, NeraControlCodes.RtxGpuRequired);
        }

        stateMachine_.Commit(state => state with
        {
            GpuVerified = true,
            DldrActualState = NeraDldrActualState.RuntimeChecking
        });

        var runtime = await WithTimeout(
            token => backend_.VerifyRuntimeAsync(token), options_.BackendStepTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!runtime.Succeeded)
        {
            var state = stateMachine_.Commit(current => current with
            {
                DldrRequested = false,
                DldrActualState = runtime.Code == NeraControlCodes.RuntimeMissing
                    ? NeraDldrActualState.RuntimeMissing
                    : NeraDldrActualState.Failed,
                RuntimeVerified = false,
                BrokerProcessOwned = false,
                BrokerConnected = false,
                HostConnected = false,
                MonitorCaptureCreated = false,
                FeatureCreated = false,
                ProcessingActive = false,
                OffGateSatisfied = true,
                LastError = Error(runtime.Code, runtime.UserMessage, runtime.TechnicalMessage)
            });
            return Failure(command.RequestId, starting.Revision, state, runtime.Code,
                runtime.UserMessage, runtime.TechnicalMessage);
        }

        if (!NeraStatePolicy.HasVerifiedRuntimeIdentity(runtime.RuntimeBinaryVersion, runtime.RuntimeSha256,
                runtime.RuntimeSignatureValid, runtime.RuntimeSigner) ||
            !NeraStatePolicy.IsLocalAbsolutePath(runtime.RuntimePath))
        {
            var invalidIdentity = stateMachine_.Commit(state => state with
            {
                DldrRequested = false,
                DldrActualState = NeraDldrActualState.Failed,
                RuntimeVerified = false,
                OffGateSatisfied = true,
                LastError = Error(NeraControlCodes.RuntimeUnverified,
                    "尚未验证此版本", "运行时后端返回成功，但没有有效的版本和 SHA-256 身份。")
            });
            return Failure(command.RequestId, starting.Revision, invalidIdentity,
                NeraControlCodes.RuntimeUnverified, "尚未验证此版本",
                "运行时后端返回成功，但没有有效的版本和 SHA-256 身份。" );
        }

        stateMachine_.Commit(state => state with
        {
            DldrActualState = NeraDldrActualState.RuntimeVerified,
            RuntimeVerified = true,
            RuntimeBinaryVersion = runtime.RuntimeBinaryVersion,
            RuntimeSha256 = runtime.RuntimeSha256,
            RuntimePath = runtime.RuntimePath,
            RuntimeSignatureValid = runtime.RuntimeSignatureValid,
            RuntimeSigner = runtime.RuntimeSigner
        });

        stateMachine_.Commit(state => state with { DldrActualState = NeraDldrActualState.HostStarting });
        var broker = await WithTimeout(
            token => backend_.StartRuntimeBrokerAsync(token), options_.BackendStepTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!broker.Succeeded)
        {
            return await RecoverAfterFailureAsync(command, starting.Revision, broker,
                NeraControlCodes.BrokerStartFailed).ConfigureAwait(false);
        }
        stateMachine_.Commit(state => state with
        {
            BrokerProcessOwned = true,
            BrokerConnected = true
        });

        var host = await WithTimeout(
            token => backend_.StartCompatHostAsync(token), options_.BackendStepTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!host.Succeeded)
        {
            return await RecoverAfterFailureAsync(command, starting.Revision, host,
                NeraControlCodes.HostStartFailed).ConfigureAwait(false);
        }
        stateMachine_.Commit(state => state with
        {
            HostConnected = true,
            DldrActualState = NeraDldrActualState.CaptureCreating
        });

        var capture = await WithTimeout(
            token => backend_.CreateMonitorCaptureAsync(token), options_.BackendStepTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!capture.Succeeded)
        {
            return await RecoverAfterFailureAsync(command, starting.Revision, capture,
                NeraControlCodes.MonitorCaptureFailed).ConfigureAwait(false);
        }

        stateMachine_.Commit(state => state with
        {
            MonitorCaptureCreated = true,
            DldrActualState = NeraDldrActualState.FeatureCreating
        });
        var feature = await WithTimeout(
            token => backend_.CreateFeature18Async(token), options_.BackendStepTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!feature.Succeeded)
        {
            return await RecoverAfterFailureAsync(command, starting.Revision, feature,
                NeraControlCodes.FeatureCreateFailed).ConfigureAwait(false);
        }
        stateMachine_.Commit(state => state with
        {
            FeatureCreated = true,
            DldrActualState = NeraDldrActualState.WaitingForFirstFrame
        });

        var firstFrame = await WithTimeout(
            token => backend_.WaitForFirstFrameAsync(token), options_.FirstFrameTimeout, cancellationToken)
            .ConfigureAwait(false);
        var beforeGate = stateMachine_.Current;
        if (!firstFrame.IsFreshFor(beforeGate))
        {
            var failure = NeraBackendOutcome.Failure(
                string.IsNullOrWhiteSpace(firstFrame.Code) ? NeraControlCodes.FirstFrameGateFailed : firstFrame.Code,
                string.IsNullOrWhiteSpace(firstFrame.UserMessage)
                    ? "处理未能启动，已撤销 DLDR ON"
                    : firstFrame.UserMessage,
                $"Feature18={firstFrame.Feature18ProcessSucceeded}; DLDR={firstFrame.DldrSucceeded}; " +
                 $"Presenter={firstFrame.PresenterSucceeded}; foreground={firstFrame.ForegroundPreserved}; " +
                 $"frameId={firstFrame.FrameId}; " +
                firstFrame.TechnicalMessage);
            return await RecoverAfterFailureAsync(command, starting.Revision, failure,
                NeraControlCodes.FirstFrameGateFailed).ConfigureAwait(false);
        }

        var processing = stateMachine_.Commit(state => state with
        {
            DldrActualState = NeraDldrActualState.Processing,
            ProcessingActive = true,
            HostSessionGeneration = firstFrame.HostSessionGeneration,
            FirstFeature18FrameSucceeded = firstFrame.Feature18ProcessSucceeded,
            DldrSucceeded = firstFrame.DldrSucceeded,
            PresenterSucceeded = firstFrame.PresenterSucceeded,
            ForegroundPreserved = firstFrame.ForegroundPreserved,
            LastSuccessfulFrameId = firstFrame.FrameId,
            LastFeature18FrameId = firstFrame.FrameId,
            LastDldrFrameId = firstFrame.FrameId,
            LastPresentedFrameId = firstFrame.FrameId,
            Feature18SuccessfulFrames = firstFrame.Feature18SuccessfulFrames,
            DldrSuccessfulFrames = firstFrame.DldrSuccessfulFrames,
            PresentedFrames = firstFrame.PresentedFrames,
            RecoveryState = NeraRecoveryState.None,
            LastError = null
        });
        return Success(command, starting.Revision, processing, "DLDR 已开启");
    }

    private async Task<NeraCommandResult> ResumeWarmDldrAsync(
        NeraCommandEnvelope command, NeraStateSnapshot starting, CancellationToken cancellationToken)
    {
        // Retain resource ownership, but revoke every old first-frame proof.
        // Core revalidates exact Runtime/display and resumes the existing Feature.
        stateMachine_.Commit(state => state with
        {
            DldrRequested = true, WarmStandby = false, WarmOffGateSatisfied = false,
            DldrActualState = NeraDldrActualState.WaitingForFirstFrame,
            ProcessingActive = false, FirstFeature18FrameSucceeded = false,
            DldrSucceeded = false, PresenterSucceeded = false, LastError = null,
            RecoveryState = NeraRecoveryState.None
        });
        var resumed = await WithTimeout(token => backend_.StartCompatHostAsync(token),
            options_.BackendStepTimeout, cancellationToken).ConfigureAwait(false);
        if (!resumed.Succeeded)
            return await RecoverAfterFailureAsync(command, starting.Revision, resumed,
                NeraControlCodes.HostStartFailed).ConfigureAwait(false);
        var frame = await WithTimeout(token => backend_.WaitForFirstFrameAsync(token),
            options_.FirstFrameTimeout, cancellationToken).ConfigureAwait(false);
        if (!frame.IsFreshFor(starting))
            return await RecoverAfterFailureAsync(command, starting.Revision,
                NeraBackendOutcome.Failure(NeraControlCodes.FirstFrameGateFailed,
                    "恢复处理未收到新画面，已恢复原画", "Warm resume requires a fresh processed and presented frame."),
                NeraControlCodes.FirstFrameGateFailed).ConfigureAwait(false);
        var processing = stateMachine_.Commit(state => state with
        {
            DldrActualState = NeraDldrActualState.Processing, ProcessingActive = true,
            HostSessionGeneration = frame.HostSessionGeneration,
            FirstFeature18FrameSucceeded = true, DldrSucceeded = true, PresenterSucceeded = true,
            ForegroundPreserved = frame.ForegroundPreserved, OverlayBypass = false,
            LastSuccessfulFrameId = frame.FrameId, LastFeature18FrameId = frame.FrameId,
            LastDldrFrameId = frame.FrameId, LastPresentedFrameId = frame.FrameId,
            Feature18SuccessfulFrames = frame.Feature18SuccessfulFrames,
            DldrSuccessfulFrames = frame.DldrSuccessfulFrames, PresentedFrames = frame.PresentedFrames,
            RecoveryState = NeraRecoveryState.None, LastError = null
        });
        return Success(command, starting.Revision, processing, "DLDR 已开启");
    }

    private async Task<NeraCommandResult> DisableDldrAsync(
        NeraCommandEnvelope command,
        NeraStateSnapshot starting,
        CancellationToken cancellationToken)
    {
        if (starting.WarmStandby && starting.WarmOffGateSatisfied)
            return Success(command, starting.Revision, starting, "DLDR 已关闭");
        if (!starting.DldrRequested && !starting.BrokerProcessOwned &&
            !starting.BrokerConnected && !starting.HostConnected && starting.OffGateSatisfied &&
            !starting.MonitorCaptureCreated &&
            starting.DldrActualState is NeraDldrActualState.Disabled or NeraDldrActualState.Bypass or
                NeraDldrActualState.RuntimeMissing)
        {
            return Success(command, starting.Revision, starting, "DLDR 已关闭");
        }

        var result = await DeactivateDldrAsync(emergency: false, cancellationToken).ConfigureAwait(false);
        var state = stateMachine_.Current;
        return result.Succeeded
            ? Success(command, starting.Revision, state, "已恢复普通 Windows 显示", rollbackPerformed: true)
            : Failure(command.RequestId, starting.Revision, state, result.Code,
                result.UserMessage, result.TechnicalMessage, rollbackPerformed: state.RecoveryState == NeraRecoveryState.BypassRestored);
    }

    private async Task<NeraCommandResult> EmergencyStopAsync(
        NeraCommandEnvelope command,
        NeraStateSnapshot starting)
    {
        try
        {
            backend_.SignalEmergencyStop();
        }
        catch
        {
        }
        if (stateMachine_.Current.DldrActualState != NeraDldrActualState.Recovering)
        {
            stateMachine_.Commit(state => state with
            {
                DldrRequested = false,
                ProcessingActive = false,
                DldrActualState = NeraDldrActualState.Recovering,
                RecoveryState = NeraRecoveryState.Recovering,
                LastError = null
            });
        }

        NeraBackendOutcome outcome;
        try
        {
            outcome = await WithTimeout(
                token => backend_.DisableDldrAsync(emergency: true, token),
                options_.FailSafeTimeout,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            outcome = NeraBackendOutcome.Failure(NeraControlCodes.Timeout,
                "紧急停止超时", error.Message);
        }

        var final = stateMachine_.Commit(state => outcome.Succeeded
            ? QuiescentBypass(state) with
            {
                HudVisible = false,
                HoldOriginal = false,
                AppVisible = true,
                LastError = null
            }
            : FailSafeReleaseUnconfirmed(state, outcome.Code,
                outcome.UserMessage, outcome.TechnicalMessage) with
            {
                HoldOriginal = false,
                AppVisible = true
            });
        return outcome.Succeeded
            ? Success(command, starting.Revision, final, "已紧急停止", rollbackPerformed: true)
            : Failure(command.RequestId, starting.Revision, final, outcome.Code,
                outcome.UserMessage, outcome.TechnicalMessage);
    }

    private async Task<NeraCommandResult> ExecuteSimpleAsync(
        NeraCommandEnvelope command,
        NeraStateSnapshot starting,
        CancellationToken cancellationToken)
    {
        if (IsAlreadyApplied(command, starting, out var idempotentMessage))
        {
            return Success(command, starting.Revision, starting, idempotentMessage);
        }

        if (command.Kind == NeraCommandKind.ConfigureRuntimeFolder &&
            (starting.DldrRequested || starting.DldrOn || RequiresFailSafe(starting)))
        {
            return Failure(command.RequestId, starting.Revision, starting,
                NeraControlCodes.StateChanged,
                "请先关闭 DLDR，再更改运行时文件夹",
                "只有在严格 OFF 且没有任何原生资源所有权时，才能更改运行时身份。" );
        }

        bool visualCommand = command.Kind is NeraCommandKind.SetMode or NeraCommandKind.SetFeature18Tuning or
            NeraCommandKind.SetDldrTuning or NeraCommandKind.SetStrength;
        if (visualCommand && !starting.DldrOn && !starting.WarmStandby &&
            (starting.DldrRequested || RequiresFailSafe(starting)))
        {
            return Failure(command.RequestId, starting.Revision, starting,
                NeraControlCodes.StateChanged, "画面正在切换，请稍后调整",
                "HOT updates require confirmed Processing or a safe OFF state.");
        }

        var rollbackPerformed = false;
        bool rebuildAfterScale = command.Kind == NeraCommandKind.SetPerformanceMode && starting.DldrOn;
        if (command.Kind is (NeraCommandKind.SetDisplay or NeraCommandKind.SetPerformanceMode) &&
            (starting.DldrRequested || RequiresFailSafe(starting)))
        {
            var disabled = await DeactivateDldrAsync(emergency: true, cancellationToken).ConfigureAwait(false);
            if (!disabled.Succeeded)
            {
                var failed = stateMachine_.Current;
                return Failure(command.RequestId, starting.Revision, failed, disabled.Code,
                    disabled.UserMessage, disabled.TechnicalMessage,
                    rollbackPerformed: failed.RecoveryState == NeraRecoveryState.BypassRestored);
            }
            rollbackPerformed = true;
        }

        var backendResult = await WithTimeout(
            token => backend_.ExecuteAsync(command.Kind, command.Payload, token),
            options_.BackendStepTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!backendResult.Succeeded)
        {
            if (visualCommand && starting.DldrOn)
            {
                // A missing/rejected live ACK does not prove which bundle reached the GPU.
                // Keep the last confirmed canonical values, but retire processing ownership.
                return await RecoverAfterFailureAsync(command, starting.Revision, backendResult,
                    NeraControlCodes.OperationFailed).ConfigureAwait(false);
            }
            if (command.Kind == NeraCommandKind.ConfigureRuntimeFolder)
            {
                // An explicit selection attempt changes the native runtime candidate. Never keep
                // reporting a previously verified identity in this session after that attempt fails.
                var runtimeFailed = stateMachine_.Commit(state => state with
                {
                    RuntimeVerified = false,
                    RuntimeBinaryVersion = null,
                    RuntimeSha256 = null,
                    RuntimePath = null,
                    RuntimeSignatureValid = false,
                    RuntimeSigner = null,
                    DldrActualState = backendResult.Code == NeraControlCodes.RuntimeMissing
                        ? NeraDldrActualState.RuntimeMissing
                        : NeraDldrActualState.Failed,
                    LastError = Error(
                        string.IsNullOrWhiteSpace(backendResult.Code)
                            ? NeraControlCodes.RuntimeUnverified : backendResult.Code,
                        backendResult.UserMessage,
                        backendResult.TechnicalMessage)
                });
                return Failure(command.RequestId, starting.Revision, runtimeFailed,
                    string.IsNullOrWhiteSpace(backendResult.Code)
                        ? NeraControlCodes.RuntimeUnverified : backendResult.Code,
                    backendResult.UserMessage, backendResult.TechnicalMessage);
            }
            if (command.Kind is NeraCommandKind.SetHotkey or NeraCommandKind.RestoreHotkeys or NeraCommandKind.InitializeHotkeys &&
                backendResult.Hotkeys is not null)
            {
                // Registration conflicts are a command failure, but the actual OS registration
                // evidence must still replace stale UI/Agent state.
                var hotkeyState = stateMachine_.Commit(state => state with
                {
                    Hotkeys = backendResult.Hotkeys.ToArray()
                });
                return Failure(command.RequestId, starting.Revision, hotkeyState,
                    backendResult.Code, backendResult.UserMessage, backendResult.TechnicalMessage,
                    rollbackPerformed);
            }
            return BackendFailure(command, starting.Revision, stateMachine_.Current, backendResult,
                NeraControlCodes.OperationFailed, rollbackPerformed);
        }

        if (visualCommand && starting.DldrOn &&
            (backendResult.AppliedParameterRevision is null ||
             backendResult.AppliedParameterRevision <= starting.AppliedParameterRevision ||
             backendResult.AppliedParameterFrameId is null ||
             backendResult.AppliedParameterFrameId <= starting.LastSuccessfulFrameId))
            return await RecoverAfterFailureAsync(command, starting.Revision,
                NeraBackendOutcome.Failure(NeraControlCodes.OperationFailed,
                    "参数应用未确认，已恢复原画", "HOT mutation lacks a newer exact revision and presented-frame ACK."),
                NeraControlCodes.OperationFailed).ConfigureAwait(false);
        var updated = stateMachine_.Commit(state => ApplySimpleResult(state, command, backendResult) with
        {
            AppliedParameterRevision = backendResult.AppliedParameterRevision ?? state.AppliedParameterRevision,
            AppliedParameterFrameId = backendResult.AppliedParameterFrameId ?? state.AppliedParameterFrameId
        });
        if (visualCommand && starting.WarmStandby)
        {
            await ObserveSerializedAsync(cancellationToken).ConfigureAwait(false);
            updated = stateMachine_.Current;
        }
        if (rebuildAfterScale)
            return await EnableDldrAsync(command, updated, cancellationToken).ConfigureAwait(false);
        return Success(command, starting.Revision, updated,
            string.IsNullOrWhiteSpace(backendResult.UserMessage) ? "操作已完成" : backendResult.UserMessage,
            rollbackPerformed,
            backendResult.OperationId);
    }

    private async Task<NeraBackendOutcome> DeactivateDldrAsync(bool emergency, CancellationToken cancellationToken)
    {
        var current = stateMachine_.Current;
        if (current.DldrActualState != NeraDldrActualState.Recovering)
        {
            stateMachine_.Commit(state => state with
            {
                DldrRequested = false,
                ProcessingActive = false,
                DldrActualState = NeraDldrActualState.Recovering,
                RecoveryState = NeraRecoveryState.Recovering
            });
        }

        NeraBackendOutcome outcome;
        try
        {
            outcome = await WithTimeout(
                token => backend_.DisableDldrAsync(emergency, token), options_.FailSafeTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            outcome = NeraBackendOutcome.Failure(NeraControlCodes.Timeout,
                "停止处理超时，正在保持原画", error.Message);
        }

        if (outcome.Succeeded)
        {
            if (!emergency && outcome.WarmStandby)
            {
                var observed = await backend_.ObserveAuthoritativeStateAsync(
                    stateMachine_.Current, cancellationToken).ConfigureAwait(false);
                if (!observed.WarmStandby || !observed.WarmOffGateSatisfied)
                    return await TryFailSafeAsync(NeraControlCodes.FailSafeFailed,
                        "暖待机未确认，正在恢复原画", "Pause ACK lacked native warm OFF evidence.").ConfigureAwait(false);
                stateMachine_.Commit(state => MergeAuthoritativeObservation(state, observed));
            }
            else stateMachine_.Commit(QuiescentBypass);
            return outcome;
        }

        return await TryFailSafeAsync(outcome.Code, outcome.UserMessage, outcome.TechnicalMessage)
            .ConfigureAwait(false);
    }

    private async Task<NeraCommandResult> RecoverAfterFailureAsync(
        NeraCommandEnvelope command,
        long previousRevision,
        NeraBackendOutcome failure,
        string fallbackCode)
    {
        var code = string.IsNullOrWhiteSpace(failure.Code) ? fallbackCode : failure.Code;
        stateMachine_.Commit(state => state with
        {
            DldrRequested = false,
            ProcessingActive = false,
            DldrActualState = NeraDldrActualState.Recovering,
            RecoveryState = NeraRecoveryState.Recovering,
            LastError = Error(code, failure.UserMessage, failure.TechnicalMessage)
        });
        var rollback = await TryFailSafeAsync(code, failure.UserMessage, failure.TechnicalMessage)
            .ConfigureAwait(false);
        var final = stateMachine_.Current;
        bool releaseConfirmed = rollback.Succeeded && !RequiresFailSafe(final) &&
            !final.DldrRequested && !final.ProcessingActive && !final.DldrOn;
        string resultMessage = releaseConfirmed
            ? "处理未完成，已恢复原画"
            : "已撤销 DLDR ON，但清理尚未完成";
        return Failure(command.RequestId, previousRevision, final, code,
            resultMessage, failure.TechnicalMessage, rollbackPerformed: releaseConfirmed);
    }

    private NeraCommandResult PreflightFailure(
        NeraCommandEnvelope command,
        long previousRevision,
        NeraBackendOutcome failure,
        string fallbackCode)
    {
        var code = string.IsNullOrWhiteSpace(failure.Code) ? fallbackCode : failure.Code;
        var userMessage = string.IsNullOrWhiteSpace(failure.UserMessage)
            ? "DLDR 开启失败"
            : failure.UserMessage;
        var final = stateMachine_.Commit(state => QuiescentFailed(state, code,
            userMessage, failure.TechnicalMessage));
        return Failure(command.RequestId, previousRevision, final, code,
            userMessage, failure.TechnicalMessage);
    }

    private async Task<NeraBackendOutcome> TryFailSafeAsync(
        string reasonCode,
        string userMessage,
        string technicalMessage)
    {
        NeraBackendOutcome bypass;
        try
        {
            bypass = await WithTimeout(
                token => backend_.EnterFailSafeBypassAsync(reasonCode, token),
                options_.FailSafeTimeout,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            bypass = NeraBackendOutcome.Failure(NeraControlCodes.FailSafeFailed,
                "恢复原画失败", error.Message);
        }

        stateMachine_.Commit(state => bypass.Succeeded
            ? QuiescentBypass(state) with
            {
                LastError = Error(reasonCode, userMessage, technicalMessage)
            }
            : FailSafeReleaseUnconfirmed(state, NeraControlCodes.FailSafeFailed,
                "恢复原画失败", bypass.TechnicalMessage));
        return bypass;
    }

    private static NeraStateSnapshot ApplySimpleResult(
        NeraStateSnapshot state,
        NeraCommandEnvelope command,
        NeraBackendOutcome outcome)
    {
        return command.Kind switch
        {
            NeraCommandKind.SetDisplay => ApplyDisplay(state,
                outcome.Display ?? throw new InvalidDataException("SetDisplay 后端缺少显示器依据。"),
                outcome.Displays),
            NeraCommandKind.SetMode => ApplyCalibratedMode(state, command.Payload.Mode!.Value),
            NeraCommandKind.SetFeature18Tuning => ApplyFeature18Tuning(
                state, command.Payload.Feature18Tuning! with { Custom = true }) with { RecipeCustom = true },
            NeraCommandKind.SetDldrTuning => ApplyDldrTuning(state, command.Payload.DldrTuning!) with { RecipeCustom = true },
            NeraCommandKind.SetStrength => state with { Strength = command.Payload.Strength!.Value, RecipeCustom = true },
            NeraCommandKind.SetPerformanceMode => ApplyNeuralScale(state,
                command.Payload.PerformanceMode!.Value switch
                {
                    NeraPerformanceMode.Quality => 100,
                    NeraPerformanceMode.Balanced => 90,
                    NeraPerformanceMode.Smooth => 80,
                    _ => throw new InvalidDataException("不支持此性能档位。")
                }),
            NeraCommandKind.SetUiProtection => state with
            {
                UiProtection = NeraUiProtectionMode.Off
            },
            NeraCommandKind.ShowHud => state with { HudVisible = true },
            NeraCommandKind.HideHud => state with { HudVisible = false },
            NeraCommandKind.ShowApp => state with { AppVisible = true },
            NeraCommandKind.HideApp => state with { AppVisible = false },
            NeraCommandKind.SetHoldOriginal => state with
            {
                HoldOriginal = command.Payload.HoldOriginal!.Value
            },
            NeraCommandKind.ConfigureRuntimeFolder => ApplyRuntimeConfiguration(state, outcome),
            NeraCommandKind.SetHotkey or NeraCommandKind.RestoreHotkeys or NeraCommandKind.InitializeHotkeys => state with
            {
                Hotkeys = outcome.Hotkeys?.ToArray()
                    ?? throw new InvalidDataException("快捷键后端缺少实际注册依据。")
            },
            NeraCommandKind.SetAiPermissions => state with
            {
                AiPermissions = command.Payload.AiPermissions!
            },
            NeraCommandKind.RunSelfTest => state,
            _ => throw new InvalidOperationException($"{command.Kind} 不是简单命令。")
        };
    }

    private static NeraStateSnapshot MergeAuthoritativeObservation(
        NeraStateSnapshot current,
        NeraStateSnapshot observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        NeraStateSnapshot merged = current with
        {
            RuntimeBinaryVersion = observed.RuntimeBinaryVersion,
            RuntimeSha256 = observed.RuntimeSha256,
            RuntimePath = observed.RuntimePath,
            RuntimeSignatureValid = observed.RuntimeSignatureValid,
            RuntimeSigner = observed.RuntimeSigner,
            RuntimeVerified = observed.RuntimeVerified,
            TargetDisplay = observed.TargetDisplay,
            AvailableDisplays = observed.AvailableDisplays.ToArray(),
            InputWidth = observed.InputWidth,
            InputHeight = observed.InputHeight,
            InputFps = observed.InputFps,
            OutputWidth = observed.OutputWidth,
            OutputHeight = observed.OutputHeight,
            OutputFps = observed.OutputFps,
            HdrSystemEnabled = observed.HdrSystemEnabled,
            HdrPipelineEnabled = observed.HdrPipelineEnabled,
            HdrInputDetected = observed.HdrInputDetected,
            DldrRequested = observed.DldrRequested,
            DldrActualState = observed.DldrActualState,
            RecipeCustom = observed.RecipeCustom,
            ProcessingMode = observed.ProcessingMode,
            Strength = observed.Strength,
            NeuralScale = observed.NeuralScale,
            HudVisible = observed.HudVisible,
            DldrHighlightProtection = observed.DldrHighlightProtection,
            DldrShadowProtection = observed.DldrShadowProtection,
            DldrChromaStrength = observed.DldrChromaStrength,
            DldrTemporalResponse = observed.DldrTemporalResponse,
            Feature18Custom = observed.Feature18Custom,
            Feature18Style = observed.Feature18Style,
            Feature18Intensity = observed.Feature18Intensity,
            Feature18LocalToneStrength = observed.Feature18LocalToneStrength,
            Feature18LocalStructureStrength = observed.Feature18LocalStructureStrength,
            Feature18SkinStructureStrength = observed.Feature18SkinStructureStrength,
            Feature18UseAutoMask = observed.Feature18UseAutoMask,
            NeuralInputWidth = ScaleDimension(observed.OutputWidth, observed.NeuralScale),
            NeuralInputHeight = ScaleDimension(observed.OutputHeight, observed.NeuralScale),
            Performance = observed.Performance,
            LastSuccessfulFrameId = observed.LastSuccessfulFrameId,
            LastFeature18FrameId = observed.LastFeature18FrameId,
            LastDldrFrameId = observed.LastDldrFrameId,
            LastPresentedFrameId = observed.LastPresentedFrameId,
            Feature18SuccessfulFrames = observed.Feature18SuccessfulFrames,
            DldrSuccessfulFrames = observed.DldrSuccessfulFrames,
            PresentedFrames = observed.PresentedFrames,
            LastError = observed.LastError,
            RecoveryState = observed.RecoveryState,
            GpuVerified = observed.GpuVerified,
            BrokerProcessOwned = observed.BrokerProcessOwned,
            BrokerConnected = observed.BrokerConnected,
            HostConnected = observed.HostConnected,
            HostSessionGeneration = observed.HostSessionGeneration,
            NativeLifecycleHealth = observed.NativeLifecycleHealth ?? NeraNativeLifecycleHealth.Unknown,
            ProtectedRecoveryPending = observed.ProtectedRecoveryPending,
            MonitorCaptureCreated = observed.MonitorCaptureCreated,
            FeatureCreated = observed.FeatureCreated,
            FirstFeature18FrameSucceeded = observed.FirstFeature18FrameSucceeded,
            DldrSucceeded = observed.DldrSucceeded,
            PresenterSucceeded = observed.PresenterSucceeded,
            ForegroundPreserved = observed.ForegroundPreserved,
            ProcessingActive = observed.ProcessingActive,
            OffGateSatisfied = observed.OffGateSatisfied,
            WarmStandby = observed.WarmStandby,
            WarmOffGateSatisfied = observed.WarmOffGateSatisfied,
            OverlayBypass = observed.OverlayBypass,
            AppliedParameterRevision = observed.AppliedParameterRevision,
            AppliedParameterFrameId = observed.AppliedParameterFrameId,
            Hotkeys = observed.Hotkeys.ToArray()
        };
        NeraStatePolicy.Validate(NeraStatePolicy.WithComputedDldrGate(merged));
        return merged;
    }

    private static bool AuthoritativeEquivalent(
        NeraStateSnapshot left,
        NeraStateSnapshot right) =>
        string.Equals(left.RuntimeBinaryVersion, right.RuntimeBinaryVersion, StringComparison.Ordinal) &&
        string.Equals(left.RuntimeSha256, right.RuntimeSha256, StringComparison.Ordinal) &&
        string.Equals(left.RuntimePath, right.RuntimePath, StringComparison.Ordinal) &&
        left.HostSessionGeneration == right.HostSessionGeneration &&
        Equals(left.NativeLifecycleHealth, right.NativeLifecycleHealth) &&
        left.ProtectedRecoveryPending == right.ProtectedRecoveryPending &&
        left.RuntimeSignatureValid == right.RuntimeSignatureValid &&
        string.Equals(left.RuntimeSigner, right.RuntimeSigner, StringComparison.Ordinal) &&
        left.RuntimeVerified == right.RuntimeVerified &&
        Equals(left.TargetDisplay, right.TargetDisplay) &&
        left.AvailableDisplays.SequenceEqual(right.AvailableDisplays) &&
        left.InputWidth == right.InputWidth && left.InputHeight == right.InputHeight &&
        left.InputFps.Equals(right.InputFps) &&
        left.OutputWidth == right.OutputWidth && left.OutputHeight == right.OutputHeight &&
        left.OutputFps.Equals(right.OutputFps) &&
        left.NeuralInputWidth == right.NeuralInputWidth &&
        left.NeuralInputHeight == right.NeuralInputHeight &&
        left.HdrSystemEnabled == right.HdrSystemEnabled &&
        left.HdrPipelineEnabled == right.HdrPipelineEnabled &&
        left.HdrInputDetected == right.HdrInputDetected &&
        left.DldrRequested == right.DldrRequested &&
        left.DldrActualState == right.DldrActualState &&
        left.RecipeCustom == right.RecipeCustom &&
        left.ProcessingMode == right.ProcessingMode && left.Strength == right.Strength &&
        left.NeuralScale == right.NeuralScale && left.HudVisible == right.HudVisible &&
        NeraDldrTuning.FromState(left) == NeraDldrTuning.FromState(right) &&
        left.Feature18Custom == right.Feature18Custom &&
        left.Feature18Style == right.Feature18Style &&
        left.Feature18Intensity.Equals(right.Feature18Intensity) &&
        left.Feature18LocalToneStrength.Equals(right.Feature18LocalToneStrength) &&
        left.Feature18LocalStructureStrength.Equals(right.Feature18LocalStructureStrength) &&
        left.Feature18SkinStructureStrength.Equals(right.Feature18SkinStructureStrength) &&
        left.Feature18UseAutoMask == right.Feature18UseAutoMask &&
        Equals(left.Performance, right.Performance) &&
        left.LastSuccessfulFrameId == right.LastSuccessfulFrameId &&
        left.LastFeature18FrameId == right.LastFeature18FrameId &&
        left.LastDldrFrameId == right.LastDldrFrameId &&
        left.LastPresentedFrameId == right.LastPresentedFrameId &&
        left.Feature18SuccessfulFrames == right.Feature18SuccessfulFrames &&
        left.DldrSuccessfulFrames == right.DldrSuccessfulFrames &&
        left.PresentedFrames == right.PresentedFrames &&
        SameError(left.LastError, right.LastError) &&
        left.RecoveryState == right.RecoveryState &&
        left.GpuVerified == right.GpuVerified &&
        left.BrokerProcessOwned == right.BrokerProcessOwned &&
        left.BrokerConnected == right.BrokerConnected &&
        left.HostConnected == right.HostConnected &&
        left.MonitorCaptureCreated == right.MonitorCaptureCreated &&
        left.FeatureCreated == right.FeatureCreated &&
        left.FirstFeature18FrameSucceeded == right.FirstFeature18FrameSucceeded &&
        left.DldrSucceeded == right.DldrSucceeded &&
        left.PresenterSucceeded == right.PresenterSucceeded &&
        left.ForegroundPreserved == right.ForegroundPreserved &&
        left.ProcessingActive == right.ProcessingActive &&
        left.OffGateSatisfied == right.OffGateSatisfied &&
        left.WarmStandby == right.WarmStandby && left.WarmOffGateSatisfied == right.WarmOffGateSatisfied &&
        left.OverlayBypass == right.OverlayBypass &&
        left.AppliedParameterRevision == right.AppliedParameterRevision &&
        left.AppliedParameterFrameId == right.AppliedParameterFrameId &&
        left.Hotkeys.SequenceEqual(right.Hotkeys);

    private static bool SameError(NeraErrorInfo? left, NeraErrorInfo? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null &&
        SameError(left, right.Code, right.UserMessage, right.TechnicalMessage);

    private static bool SameError(
        NeraErrorInfo? error,
        string code,
        string userMessage,
        string technicalMessage) =>
        error is not null &&
        string.Equals(error.Code, code, StringComparison.Ordinal) &&
        string.Equals(error.UserMessage, userMessage, StringComparison.Ordinal) &&
        string.Equals(error.TechnicalMessage, technicalMessage, StringComparison.Ordinal);

    private static NeraStateSnapshot ApplyDisplay(
        NeraStateSnapshot state,
        NeraDisplaySummary display,
        IReadOnlyList<NeraDisplaySummary>? available = null)
    {
        NeraStatePolicy.ValidateDisplay(display);
        var selected = display with { Selected = true };
        var source = available?.ToArray() ?? state.AvailableDisplays.ToArray();
        var normalized = source.Select(candidate => candidate with
        {
            Selected = string.Equals(candidate.DisplayId, selected.DisplayId, StringComparison.Ordinal)
        }).ToList();
        if (!normalized.Any(candidate =>
                string.Equals(candidate.DisplayId, selected.DisplayId, StringComparison.Ordinal)))
        {
            normalized.Add(selected);
        }
        else
        {
            var index = normalized.FindIndex(candidate =>
                string.Equals(candidate.DisplayId, selected.DisplayId, StringComparison.Ordinal));
            normalized[index] = selected;
        }
        foreach (var candidate in normalized)
        {
            NeraStatePolicy.ValidateDisplay(candidate);
        }
        return state with
        {
            TargetDisplay = selected,
            AvailableDisplays = normalized.ToArray(),
            InputWidth = selected.Width,
            InputHeight = selected.Height,
            InputFps = selected.RefreshRateHz,
            OutputWidth = selected.Width,
            OutputHeight = selected.Height,
            OutputFps = selected.RefreshRateHz,
            NeuralInputWidth = ScaleDimension(selected.Width, state.NeuralScale),
            NeuralInputHeight = ScaleDimension(selected.Height, state.NeuralScale),
            HdrSystemEnabled = selected.HdrEnabled,
            HdrInputDetected = selected.HdrEnabled,
            LastError = null
        };
    }

    private static NeraStateSnapshot ApplyRuntimeConfiguration(
        NeraStateSnapshot state,
        NeraBackendOutcome outcome)
    {
        if (outcome.RuntimeVerified != true ||
            !NeraStatePolicy.HasVerifiedRuntimeIdentity(outcome.RuntimeBinaryVersion, outcome.RuntimeSha256,
                outcome.RuntimeSignatureValid == true, outcome.RuntimeSigner) ||
            !NeraStatePolicy.IsLocalAbsolutePath(outcome.RuntimePath))
        {
            throw new InvalidDataException("运行时配置成功，但缺少已验证的身份依据。");
        }
        return state with
        {
            DldrActualState = NeraDldrActualState.RuntimeVerified,
            RuntimeVerified = true,
            RuntimeBinaryVersion = outcome.RuntimeBinaryVersion,
            RuntimeSha256 = outcome.RuntimeSha256,
            RuntimePath = outcome.RuntimePath,
            RuntimeSignatureValid = outcome.RuntimeSignatureValid == true,
            RuntimeSigner = outcome.RuntimeSigner,
            LastError = null
        };
    }

    private static NeraStateSnapshot ApplyNeuralScale(NeraStateSnapshot state, int neuralScale) => state with
    {
        NeuralScale = neuralScale,
        NeuralInputWidth = ScaleDimension(state.OutputWidth, neuralScale),
        NeuralInputHeight = ScaleDimension(state.OutputHeight, neuralScale)
    };

    private static NeraStateSnapshot ApplyCalibratedMode(
        NeraStateSnapshot state,
        NeraProcessingMode mode) => ApplyDldrTuning(ApplyFeature18Tuning(
            state with { ProcessingMode = mode, RecipeCustom = false, Strength = NeraRecipe.StrengthForMode(mode) },
            NeraFeature18Tuning.ForMode(mode)), NeraDldrTuning.ForMode(mode));

    private static NeraStateSnapshot ApplyDldrTuning(NeraStateSnapshot state, NeraDldrTuning tuning)
    {
        NeraStatePolicy.ValidateDldrTuning(tuning);
        tuning = tuning.CanonicalizedForNative();
        return state with
        {
            DldrHighlightProtection = tuning.HighlightProtection,
            DldrShadowProtection = tuning.ShadowProtection,
            DldrChromaStrength = tuning.ChromaStrength,
            DldrTemporalResponse = tuning.TemporalResponse
        };
    }

    private static NeraStateSnapshot ApplyFeature18Tuning(
        NeraStateSnapshot state,
        NeraFeature18Tuning tuning)
    {
        NeraStatePolicy.ValidateFeature18Tuning(tuning);
        tuning = tuning.CanonicalizedForNative();
        return state with
        {
            Feature18Custom = tuning.Custom,
            Feature18Style = tuning.Style,
            Feature18Intensity = tuning.Intensity,
            Feature18LocalToneStrength = tuning.LocalToneStrength,
            Feature18LocalStructureStrength = tuning.LocalStructureStrength,
            Feature18SkinStructureStrength = tuning.SkinStructureStrength,
            Feature18UseAutoMask = tuning.UseAutoMask
        };
    }

    private NeraCommandResult ApplyLanguagePreference(NeraCommandEnvelope command, NeraStateSnapshot starting)
    {
        string preference = command.Payload.LanguagePreference!;
        ChipsStudio.Nera.Localization.NeraLocalizer.SetLanguage(preference);
        var state = stateMachine_.Commit(current => current with
        {
            Language = ChipsStudio.Nera.Localization.NeraLocalizer.Language,
            LanguagePreference = preference
        });
        return new NeraCommandResult
        {
            Ok = true, RequestId = command.RequestId, PreviousRevision = starting.Revision,
            CurrentRevision = state.Revision, State = state, Code = NeraControlCodes.Ok,
            UserMessage = ChipsStudio.Nera.Localization.NeraLocalizer.Get("Common.Applied")
        };
    }

    private static int ScaleDimension(int dimension, int percent) =>
        dimension == 0 ? 0 : Math.Max(1, (int)Math.Round(dimension * percent / 100d));

    private static bool IsAlreadyApplied(
        NeraCommandEnvelope command,
        NeraStateSnapshot state,
        out string message)
    {
        var applied = command.Kind switch
        {
            NeraCommandKind.SetDisplay => string.Equals(state.TargetDisplay?.DisplayId,
                command.Payload.DisplayId, StringComparison.Ordinal),
            NeraCommandKind.SetMode => state.ProcessingMode == command.Payload.Mode &&
                !state.RecipeCustom && !state.Feature18Custom &&
                state.Strength == NeraRecipe.StrengthForMode(command.Payload.Mode!.Value) &&
                NeraDldrTuning.FromState(state) == NeraDldrTuning.ForMode(command.Payload.Mode!.Value).CanonicalizedForNative() &&
                Feature18TuningEquals(state, NeraFeature18Tuning.ForMode(command.Payload.Mode!.Value)),
            NeraCommandKind.SetFeature18Tuning =>
                state.RecipeCustom && Feature18TuningEquals(state, command.Payload.Feature18Tuning! with { Custom = true }),
            NeraCommandKind.SetDldrTuning => state.RecipeCustom &&
                NeraDldrTuning.FromState(state) == command.Payload.DldrTuning!.CanonicalizedForNative(),
            NeraCommandKind.SetStrength => state.RecipeCustom && state.Strength == command.Payload.Strength,
            NeraCommandKind.SetPerformanceMode => state.PerformanceMode == command.Payload.PerformanceMode,
            NeraCommandKind.SetUiProtection => state.UiProtection == command.Payload.UiProtection,
            NeraCommandKind.ShowHud => state.HudVisible,
            NeraCommandKind.HideHud => !state.HudVisible,
            // A visible window can still be minimized/occluded or lack foreground. Explicit
            // ShowApp must reach the native promotion path; requestId deduplication still applies.
            NeraCommandKind.ShowApp => false,
            NeraCommandKind.HideApp => !state.AppVisible,
            NeraCommandKind.SetHoldOriginal => state.HoldOriginal == command.Payload.HoldOriginal,
            NeraCommandKind.SetAiPermissions => state.AiPermissions == command.Payload.AiPermissions,
            _ => false
        };
        message = applied ? "状态无需更改" : string.Empty;
        return applied;
    }

    private static bool Feature18TuningEquals(
        NeraStateSnapshot state,
        NeraFeature18Tuning tuning)
    {
        tuning = tuning.CanonicalizedForNative();
        return state.Feature18Custom == tuning.Custom &&
            state.Feature18Style == tuning.Style &&
            ((double)(float)state.Feature18Intensity).Equals(tuning.Intensity) &&
            ((double)(float)state.Feature18LocalToneStrength).Equals(tuning.LocalToneStrength) &&
            ((double)(float)state.Feature18LocalStructureStrength).Equals(tuning.LocalStructureStrength) &&
            ((double)(float)state.Feature18SkinStructureStrength).Equals(tuning.SkinStructureStrength) &&
            state.Feature18UseAutoMask == tuning.UseAutoMask;
    }

    private static NeraStateSnapshot QuiescentBypass(NeraStateSnapshot state) => state with
    {
        ProtectedRecoveryPending = false,
        WarmStandby = false,
        WarmOffGateSatisfied = false,
        OverlayBypass = false,
        DldrRequested = false,
        DldrActualState = NeraDldrActualState.Bypass,
        ProcessingActive = false,
        BrokerProcessOwned = false,
        BrokerConnected = false,
        HostConnected = false,
        MonitorCaptureCreated = false,
        FeatureCreated = false,
        FirstFeature18FrameSucceeded = false,
        DldrSucceeded = false,
        PresenterSucceeded = false,
        ForegroundPreserved = false,
        OffGateSatisfied = true,
        RecoveryState = NeraRecoveryState.BypassRestored
    };

    private static NeraStateSnapshot QuiescentFailed(
        NeraStateSnapshot state,
        string code,
        string userMessage,
        string technicalMessage) => state with
    {
        ProtectedRecoveryPending = false,
        WarmStandby = false,
        WarmOffGateSatisfied = false,
        OverlayBypass = false,
        DldrRequested = false,
        DldrActualState = NeraDldrActualState.Failed,
        ProcessingActive = false,
        BrokerProcessOwned = false,
        BrokerConnected = false,
        HostConnected = false,
        MonitorCaptureCreated = false,
        FeatureCreated = false,
        FirstFeature18FrameSucceeded = false,
        DldrSucceeded = false,
        PresenterSucceeded = false,
        ForegroundPreserved = false,
        OffGateSatisfied = true,
        RecoveryState = NeraRecoveryState.Failed,
        LastError = Error(code, userMessage, technicalMessage)
    };

    // A failed/timeout recovery attempt is not evidence that the Host,
    // Capture, Feature or Presenter released ownership. Preserve the last
    // observed ownership flags so UI/OSD/Agent clients cannot claim that the
    // normal Windows output is restored until the backend proves the OFF gate.
    private static NeraStateSnapshot FailSafeReleaseUnconfirmed(
        NeraStateSnapshot state,
        string code,
        string userMessage,
        string technicalMessage) => state with
    {
        DldrRequested = false,
        DldrActualState = NeraDldrActualState.Failed,
        ProcessingActive = false,
        RecoveryState = NeraRecoveryState.Failed,
        LastError = Error(code, userMessage, technicalMessage)
    };

    private static bool RequiresFailSafe(NeraStateSnapshot state) =>
        state.BrokerProcessOwned || state.BrokerConnected || state.HostConnected ||
        state.MonitorCaptureCreated || state.FeatureCreated || state.PresenterSucceeded ||
        !state.OffGateSatisfied ||
        state.DldrActualState is NeraDldrActualState.HostStarting or NeraDldrActualState.CaptureCreating or
            NeraDldrActualState.FeatureCreating or NeraDldrActualState.WaitingForFirstFrame or
            NeraDldrActualState.Processing or NeraDldrActualState.Recovering;

    private static NeraErrorInfo Error(string code, string userMessage, string technicalMessage) => new()
    {
        Code = code,
        UserMessage = userMessage,
        TechnicalMessage = technicalMessage
    };

    private static NeraCommandResult Success(
        NeraCommandEnvelope command,
        long previousRevision,
        NeraStateSnapshot state,
        string userMessage,
        bool rollbackPerformed = false,
        string? operationId = null) => new()
    {
        Ok = true,
        RequestId = command.RequestId,
        PreviousRevision = previousRevision,
        CurrentRevision = state.Revision,
        State = state,
        Code = NeraControlCodes.Ok,
        UserMessage = userMessage,
        RollbackPerformed = rollbackPerformed,
        OperationId = operationId
    };

    private static NeraCommandResult Failure(
        string requestId,
        long previousRevision,
        NeraStateSnapshot state,
        string code,
        string userMessage,
        string technicalMessage,
        bool rollbackPerformed = false) => new()
    {
        Ok = false,
        RequestId = requestId,
        PreviousRevision = previousRevision,
        CurrentRevision = state.Revision,
        State = state,
        Code = code,
        UserMessage = userMessage,
        TechnicalMessage = technicalMessage,
        RollbackPerformed = rollbackPerformed
    };

    private static NeraCommandResult BackendFailure(
        NeraCommandEnvelope command,
        long previousRevision,
        NeraStateSnapshot state,
        NeraBackendOutcome result,
        string fallbackCode,
        bool rollbackPerformed = false) => Failure(
            command.RequestId,
            previousRevision,
            state,
            string.IsNullOrWhiteSpace(result.Code) ? fallbackCode : result.Code,
            string.IsNullOrWhiteSpace(result.UserMessage) ? "操作失败" : result.UserMessage,
            result.TechnicalMessage,
            rollbackPerformed);

    private static async Task<T> WithTimeout<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            return await operation(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested &&
                                                        timeoutCancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"后端步骤超过时限 {timeout}。", error);
        }
    }

    private static string Fingerprint(NeraCommandEnvelope command)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(command, NeraJson.Options);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private void OnStateChanged(NeraStateSnapshot snapshot)
    {
        NeraStateSnapshot previous = auditPreviousState_;
        auditPreviousState_ = snapshot;
        if (NeraControlAuditPolicy.HasTransition(previous, snapshot))
            EmitAudit("StateTransition", auditCommand_, previous, snapshot,
                auditCommand_?.Kind.ToString() ?? NeraControlAuditPolicy.ObservationReason(previous, snapshot));
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }
        foreach (EventHandler<NeraStateSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch
            {
                // UI/Agent observers cannot break state publication.
            }
        }
    }

    private void EmitAudit(string eventName, NeraCommandEnvelope? command,
        NeraStateSnapshot before, NeraStateSnapshot after, string reason, NeraCommandResult? result = null)
    {
        if (auditSink_ is null) return;
        try
        {
            auditSink_.TryWrite(NeraControlAuditPolicy.Create(Interlocked.Increment(ref auditSequence_),
                eventName, command, before, after, reason, result));
        }
        catch
        {
            // Diagnostic storage is not allowed to take ownership of the display or break input recovery.
        }
    }

    private void EmitObservationFailure(Exception error)
    {
        if (auditSink_ is null) return;
        try
        {
            NeraStateSnapshot current = stateMachine_.Current;
            var record = NeraControlAuditPolicy.Create(Interlocked.Increment(ref auditSequence_),
                "ObservationFailed", null, current, current, "ObservationFailed");
            auditSink_.TryWrite(record with
            {
                LifecycleReasonCode = NeraLifecycleReasonPolicy.ObservationFailure(error),
                ExceptionType = NeraControlAuditPolicy.Token(error.GetType().FullName),
                ExceptionHResult = error.HResult,
                // Keep arbitrary paths/messages out of the audit; exact text remains local diagnostics.
                ExceptionFingerprint = Convert.ToHexString(SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(error.ToString())))
            });
        }
        catch { /* Diagnostics must not change display ownership or the safety decision. */ }
    }

    private void TrimIdempotencyCacheLocked()
    {
        if (idempotency_.Count <= options_.IdempotencyCapacity)
        {
            return;
        }
        var removeCount = idempotency_.Count - options_.IdempotencyCapacity;
        foreach (var item in idempotency_.Where(pair => pair.Value.ResultTask.IsCompleted)
                     .OrderBy(pair => pair.Value.Sequence).Take(removeCount).ToArray())
        {
            idempotency_.Remove(item.Key);
        }
    }

    private void ThrowIfDisposed()
    {
        lock (queueGate_)
        {
            ThrowIfDisposedLocked();
        }
    }

    private void ThrowIfDisposedLocked() => ObjectDisposedException.ThrowIf(disposed_, this);

    private sealed class QueuedCommand(NeraCommandEnvelope command)
    {
        public NeraCommandEnvelope Command { get; } = command;
        public TaskCompletionSource<NeraCommandResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record IdempotencyEntry(string Fingerprint, Task<NeraCommandResult> ResultTask, long Sequence);
}

internal sealed class NeraProcessingModeJsonConverter :
    System.Text.Json.Serialization.JsonConverter<NeraProcessingMode>
{
    public override NeraProcessingMode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("处理模式必须是字符串。");
        }

        return reader.GetString() switch
        {
            "natural" => NeraProcessingMode.Natural,
            "clear" => NeraProcessingMode.Sharp,
            "cinema" => NeraProcessingMode.Cinema,
            _ => throw new JsonException("处理模式只接受“自然”“清晰”或“电影”。")
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        NeraProcessingMode value,
        JsonSerializerOptions options) => writer.WriteStringValue(value switch
        {
            NeraProcessingMode.Natural => "natural",
            NeraProcessingMode.Sharp => "clear",
            NeraProcessingMode.Cinema => "cinema",
            _ => throw new JsonException("不支持此处理模式。")
        });
}

internal static class NeraJson
{
    internal static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new NeraProcessingModeJsonConverter());
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
