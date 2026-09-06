using System.Collections.Concurrent;
using ChipsStudio.Nera.Control;

namespace ChipsStudio.Nera.Control.Tests;

internal sealed class FakeControlBackend : INeraControlBackend
{
    internal const string VerifiedSha256 =
        "E16BCF15E16E13F527491CDF7845B2FE6521A738D8F7C9C721866A8496E1FC8E";
    internal const string FakeRuntimePath = @"C:\FakeRuntime\nvngx_dlssnr.dll";

    private int activeCalls_;
    private int maximumConcurrentCalls_;
    private int signalEmergencyStopCount_;
    private ulong lastFrameId_;
    private ulong appliedParameterRevision_;
    private bool featureCreated_;
    private bool warmStandby_;
    private bool resumePending_;
    private readonly ConcurrentDictionary<NeraCommandKind, int> executeCounts_ = new();

    public ConcurrentQueue<string> Calls { get; } = new();
    public IReadOnlyList<NeraDisplaySummary> Displays { get; set; } = [Display(selected: true)];
    public NeraRuntimeVerificationOutcome RuntimeOutcome { get; set; } =
        NeraRuntimeVerificationOutcome.Verified("310.8.0.0", VerifiedSha256, FakeRuntimePath);
    public NeraBackendOutcome ValidateDisplayOutcome { get; set; }
    public NeraBackendOutcome GpuOutcome { get; set; } = NeraBackendOutcome.Success() with
    {
        GpuVerified = true
    };
    public NeraBackendOutcome CaptureOutcome { get; set; } = NeraBackendOutcome.Success();
    public NeraBackendOutcome BrokerOutcome { get; set; } = NeraBackendOutcome.Success();
    public NeraBackendOutcome HostOutcome { get; set; } = NeraBackendOutcome.Success();
    public NeraBackendOutcome FeatureOutcome { get; set; } = NeraBackendOutcome.Success();
    public NeraBackendOutcome? ConfigureRuntimeOutcome { get; set; }
    public NeraBackendOutcome FailSafeOutcome { get; set; } = NeraBackendOutcome.Success("已恢复原画");
    public NeraBackendOutcome DisableOutcome { get; set; } = NeraBackendOutcome.Success("已停止 DLDR");
    // Tests may independently reject a native command, omit its apply ACK,
    // or provide stale proof. Success-by-default never means receipt-only.
    public NeraBackendOutcome? HotUpdateOutcome { get; set; }
    public Exception? HotUpdateException { get; set; }
    public bool AutoAcknowledgeHotUpdates { get; set; } = true;
    public bool UseWarmStandby { get; set; }
    public bool WarmEvidenceValid { get; set; } = true;
    public bool FreshWarmResumeFrame { get; set; } = true;
    public bool ExpireWarmHostBeforeResume { get; set; }
    public int WarmResumeCount { get; private set; }
    public int ColdHostStartCount { get; private set; }
    public NeraFirstFrameGateOutcome FirstFrameOutcome { get; set; } = new()
    {
        Feature18ProcessSucceeded = true,
        DldrSucceeded = true,
        PresenterSucceeded = true,
        ForegroundPreserved = true,
        FrameId = 1,
        HostSessionGeneration = 1,
        Feature18SuccessfulFrames = 1,
        DldrSuccessfulFrames = 1,
        PresentedFrames = 1
    };
    public NeraCommandKind? BlockingCommand { get; set; }
    public TaskCompletionSource BlockingCommandStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TimeSpan GenericDelay { get; set; }
    public TimeSpan FirstFrameDelay { get; set; }
    public TimeSpan ObservationDelay { get; set; }
    public bool ObservationDelayIgnoresCancellation { get; set; }
    public Func<NeraStateSnapshot, NeraStateSnapshot>? AuthoritativeObservation { get; set; }
    public Exception? ObservationException { get; set; }
    public TaskCompletionSource ObservationOccurred { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int MaximumConcurrentCalls => Volatile.Read(ref maximumConcurrentCalls_);
    public int SignalEmergencyStopCount => Volatile.Read(ref signalEmergencyStopCount_);
    public int ExecuteCount(NeraCommandKind kind) =>
        executeCounts_.TryGetValue(kind, out int count) ? count : 0;

    public FakeControlBackend()
    {
        ValidateDisplayOutcome = NeraBackendOutcome.Success("显示器有效") with
        {
            Display = Display(selected: true),
            Displays = [Display(selected: true)]
        };
    }

    public async Task<NeraStateSnapshot> ObserveAuthoritativeStateAsync(
        NeraStateSnapshot current,
        CancellationToken cancellationToken)
    {
        using IDisposable call = EnterCall("observeAuthoritativeState");
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        ObservationOccurred.TrySetResult();
        if (ObservationDelay > TimeSpan.Zero)
        {
            await Task.Delay(ObservationDelay,
                ObservationDelayIgnoresCancellation ? CancellationToken.None : cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (ObservationException is not null)
        {
            throw ObservationException;
        }
        if (AuthoritativeObservation is not null) return AuthoritativeObservation(current);
        if (warmStandby_ && WarmEvidenceValid)
            return NeraStatePolicy.WithComputedDldrGate(current with
            {
                DldrRequested = false,
                DldrActualState = NeraDldrActualState.Bypass,
                ProcessingActive = false,
                FirstFeature18FrameSucceeded = false,
                DldrSucceeded = false,
                PresenterSucceeded = false,
                WarmStandby = true,
                WarmOffGateSatisfied = true,
                BrokerProcessOwned = true,
                BrokerConnected = true,
                HostConnected = true,
                MonitorCaptureCreated = true,
                FeatureCreated = true,
                OffGateSatisfied = false,
                RecoveryState = NeraRecoveryState.BypassRestored
            });
        return current;
    }

    public ValueTask<IReadOnlyList<NeraDisplaySummary>> ListDisplaysAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue("listDisplays");
        return ValueTask.FromResult(Displays);
    }

    public async Task<NeraBackendOutcome> ExecuteAsync(
        NeraCommandKind command,
        NeraCommandPayload payload,
        CancellationToken cancellationToken)
    {
        using IDisposable call = EnterCall("execute:" + command);
        executeCounts_.AddOrUpdate(command, 1, static (_, count) => count + 1);
        if (BlockingCommand == command)
        {
            BlockingCommandStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        if (GenericDelay > TimeSpan.Zero)
            await Task.Delay(GenericDelay, cancellationToken).ConfigureAwait(false);

        if (featureCreated_ && command is (NeraCommandKind.SetMode or
            NeraCommandKind.SetFeature18Tuning or NeraCommandKind.SetDldrTuning or NeraCommandKind.SetStrength))
        {
            if (HotUpdateException is not null) throw HotUpdateException;
            if (HotUpdateOutcome is not null) return HotUpdateOutcome;
            if (AutoAcknowledgeHotUpdates)
                return NeraBackendOutcome.Success("新处理帧已确认参数") with
                {
                    AppliedParameterRevision = ++appliedParameterRevision_,
                    AppliedParameterFrameId = ++lastFrameId_
                };
        }

        if (command == NeraCommandKind.SetDisplay)
        {
            var selected = Display(payload.DisplayId!, selected: true);
            Displays = [selected];
            return NeraBackendOutcome.Success("已选择显示器") with
            {
                Display = selected,
                Displays = Displays
            };
        }
        if (command == NeraCommandKind.ConfigureRuntimeFolder)
        {
            if (ConfigureRuntimeOutcome is not null) return ConfigureRuntimeOutcome;
            return NeraBackendOutcome.Success("运行时已验证") with
            {
                RuntimeVerified = true,
                RuntimeBinaryVersion = "310.8.0.0",
                RuntimeSha256 = VerifiedSha256,
                RuntimePath = Path.Combine(payload.RuntimeFolder!, "nvngx_dlssnr.dll"),
                RuntimeSignatureValid = true,
                RuntimeSigner = "NVIDIA Corporation"
            };
        }
        if (command is NeraCommandKind.SetHotkey or NeraCommandKind.RestoreHotkeys)
        {
            return NeraBackendOutcome.Success("快捷键已注册") with
            {
                Hotkeys =
                [
                    payload.Hotkey ?? new NeraHotkeyBinding
                    {
                        Action = NeraHotkeyAction.ToggleDldr,
                        Modifiers = 0x4003,
                        VirtualKey = 0x44,
                        Registered = true
                    }
                ]
            };
        }
        return command == NeraCommandKind.RunSelfTest
            ? NeraBackendOutcome.Success("自测已启动", "operation:self-test")
            : NeraBackendOutcome.Success();
    }

    public Task<NeraBackendOutcome> ValidateCurrentDisplayAsync(CancellationToken cancellationToken) =>
        ReturnStepAsync("validateDisplay", ValidateDisplayOutcome, cancellationToken);

    public Task<NeraBackendOutcome> VerifyRtxGpuAsync(CancellationToken cancellationToken) =>
        ReturnStepAsync("verifyRtxGpu", GpuOutcome, cancellationToken);

    public async Task<NeraRuntimeVerificationOutcome> VerifyRuntimeAsync(
        CancellationToken cancellationToken)
    {
        using IDisposable call = EnterCall("verifyRuntime");
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return RuntimeOutcome;
    }

    public Task<NeraBackendOutcome> StartRuntimeBrokerAsync(CancellationToken cancellationToken)
    {
        appliedParameterRevision_ = 0;
        return ReturnStepAsync("startBroker", BrokerOutcome, cancellationToken);
    }

    public Task<NeraBackendOutcome> StartCompatHostAsync(CancellationToken cancellationToken)
    {
        if (warmStandby_)
        {
            if (ExpireWarmHostBeforeResume)
            {
                ++ColdHostStartCount;
                // Native Enable internally recreates expired resources and returns new proof.
                FirstFrameOutcome = FirstFrameOutcome with
                {
                    HostSessionGeneration = FirstFrameOutcome.HostSessionGeneration + 1,
                    FrameId = 1, Feature18SuccessfulFrames = 1,
                    DldrSuccessfulFrames = 1, PresentedFrames = 1
                };
                resumePending_ = false;
            }
            else { ++WarmResumeCount; resumePending_ = true; }
            warmStandby_ = false;
        }
        else ++ColdHostStartCount;
        return ReturnStepAsync("startHost", HostOutcome, cancellationToken);
    }

    public Task<NeraBackendOutcome> CreateMonitorCaptureAsync(CancellationToken cancellationToken) =>
        ReturnStepAsync("createMonitorCapture", CaptureOutcome, cancellationToken);

    public Task<NeraBackendOutcome> CreateFeature18Async(CancellationToken cancellationToken)
    {
        featureCreated_ = FeatureOutcome.Succeeded;
        return ReturnStepAsync("createFeature18", FeatureOutcome, cancellationToken);
    }

    public async Task<NeraFirstFrameGateOutcome> WaitForFirstFrameAsync(
        CancellationToken cancellationToken)
    {
        using IDisposable call = EnterCall("waitFirstFrame");
        if (FirstFrameDelay > TimeSpan.Zero)
            await Task.Delay(FirstFrameDelay, cancellationToken).ConfigureAwait(false);
        else
            await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        NeraFirstFrameGateOutcome outcome = FirstFrameOutcome;
        if (resumePending_ && outcome.GateSatisfied)
        {
            ulong frame = FreshWarmResumeFrame ? lastFrameId_ + 1 : lastFrameId_;
            outcome = outcome with
            {
                FrameId = frame,
                Feature18SuccessfulFrames = frame,
                DldrSuccessfulFrames = frame,
                PresentedFrames = frame
            };
        }
        resumePending_ = false;
        if (outcome.GateSatisfied) lastFrameId_ = outcome.FrameId;
        return outcome;
    }

    public Task<NeraBackendOutcome> EnterFailSafeBypassAsync(
        string reasonCode,
        CancellationToken cancellationToken)
    {
        if (FailSafeOutcome.Succeeded) { warmStandby_ = false; featureCreated_ = false; resumePending_ = false; }
        return ReturnStepAsync("failSafe:" + reasonCode, FailSafeOutcome, cancellationToken);
    }

    public Task<NeraBackendOutcome> DisableDldrAsync(
        bool emergency,
        CancellationToken cancellationToken)
    {
        NeraBackendOutcome outcome = DisableOutcome;
        if (outcome.Succeeded)
        {
            warmStandby_ = UseWarmStandby && !emergency;
            if (!warmStandby_) { featureCreated_ = false; resumePending_ = false; }
            outcome = outcome with { WarmStandby = warmStandby_ };
        }
        return ReturnStepAsync("disableDldr:" + emergency, outcome, cancellationToken);
    }

    public void SignalEmergencyStop()
    {
        Interlocked.Increment(ref signalEmergencyStopCount_);
        Calls.Enqueue("signalEmergencyStop");
    }

    private async Task<NeraBackendOutcome> ReturnStepAsync(
        string name,
        NeraBackendOutcome outcome,
        CancellationToken cancellationToken)
    {
        using IDisposable call = EnterCall(name);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return outcome;
    }

    private IDisposable EnterCall(string name)
    {
        Calls.Enqueue(name);
        int active = Interlocked.Increment(ref activeCalls_);
        while (true)
        {
            int maximum = Volatile.Read(ref maximumConcurrentCalls_);
            if (active <= maximum ||
                Interlocked.CompareExchange(ref maximumConcurrentCalls_, active, maximum) == maximum)
                break;
        }
        return new CallbackDisposable(() => Interlocked.Decrement(ref activeCalls_));
    }

    internal static NeraDisplaySummary Display(
        string id = "display:1",
        bool selected = false,
        bool hdrEnabled = true,
        bool capturable = true) => new()
    {
        DisplayId = id,
        DisplayName = "Redmi G Pro 27U",
        SourceName = @"\\.\DISPLAY1",
        Width = 3840,
        Height = 2160,
        RefreshNumerator = 160000,
        RefreshDenominator = 1000,
        HdrSupported = true,
        HdrEnabled = hdrEnabled,
        AdvancedColorEnabled = hdrEnabled,
        SdrWhiteLevelNits = 203.0,
        AdapterLuid = "00000000:00000001",
        TargetId = "1",
        Connected = true,
        Selected = selected,
        Capturable = capturable
    };

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? callback_ = callback;
        public void Dispose() => Interlocked.Exchange(ref callback_, null)?.Invoke();
    }
}
