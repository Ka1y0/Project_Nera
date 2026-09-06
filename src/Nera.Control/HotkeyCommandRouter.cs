using System.Collections.Concurrent;
using System.Threading.Channels;
using ChipsStudio.Nera.Localization;

namespace ChipsStudio.Nera.Control;

public sealed class NeraHotkeyCommandCompletedEventArgs : EventArgs
{
    public required NeraHotkeyAction Action { get; init; }
    public required NeraCommandResult Result { get; init; }
}

public sealed class NeraHotkeyRoutingFaultedEventArgs : EventArgs
{
    public required NeraHotkeyAction Action { get; init; }
    public required Exception Error { get; init; }
}

/// <summary>A registered keyboard chord observed while the local recorder is open.
/// This is a recording candidate, never a command or a physical-input claim.</summary>
public sealed class NeraHotkeyRecordingCandidateEventArgs : EventArgs
{
    public required NeraHotkeyAction Action { get; init; }
    public required NeraHotkeyChord Chord { get; init; }
    public required NeraInputOrigin Origin { get; init; }
}

/// <summary>
/// Converts global hotkey activations into the same revisioned commands used by UI, Agent, CLI,
/// and Orchestrator. Normal hotkeys preserve activation order; EmergencyStop bypasses that local
/// queue and relies on NeraControlService's priority lane to interrupt active work.
/// </summary>
public sealed class HotkeyCommandRouter : IAsyncDisposable
{
    private readonly INeraControlClient control_;
    private readonly Channel<Activation> normalActions_ = Channel.CreateUnbounded<Activation>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource shutdown_ = new();
    private readonly Task worker_;
    private readonly ConcurrentDictionary<long, Task> emergencyTasks_ = new();
    private long emergencySequence_;
    private int disposed_;
    private int recording_;

    public HotkeyCommandRouter(INeraControlClient control)
    {
        control_ = control ?? throw new ArgumentNullException(nameof(control));
        worker_ = Task.Run(ProcessNormalActionsAsync);
    }

    public event EventHandler<NeraHotkeyCommandCompletedEventArgs>? CommandCompleted;
    public event EventHandler<NeraHotkeyRoutingFaultedEventArgs>? RoutingFaulted;
    public event EventHandler<NeraHotkeyRecordingCandidateEventArgs>? RecordingCandidate;

    /// <summary>Window-local recorder gate; fixed and custom emergency shortcuts always remain active.</summary>
    public void SetRecording(bool recording) => Interlocked.Exchange(ref recording_, recording ? 1 : 0);

    /// <summary>
    /// Non-blocking entry point suitable for a WM_HOTKEY callback. EmergencyStop is dispatched
    /// immediately; every other supported action is placed on the ordered normal lane.
    /// </summary>
    public bool TryPost(NeraHotkeyAction action, NeraInputOrigin? origin = null)
    {
        if (Volatile.Read(ref disposed_) != 0)
        {
            return false;
        }
        if (Volatile.Read(ref recording_) != 0 &&
            action is not (NeraHotkeyAction.EmergencyStop or NeraHotkeyAction.EmergencyStopFallback))
        {
            // RegisterHotKey may consume the chord before WinUI receives KeyDown.
            // The production platform already validated ID, VK and modifiers;
            // retain that candidate for the dialog without invoking its action.
            if (NeraHotkeyPolicy.EditableActions.Contains(action) &&
                origin is { Message: 0x0312, RegistrationId: > 0, Vk: uint key, Modifiers: uint modifiers } &&
                origin.InputKind is NeraInputKind.KeyboardHotkey or NeraInputKind.SyntheticTest &&
                new NeraHotkeyChord(modifiers, key) is { IsValid: true } chord)
            {
                try
                {
                    RecordingCandidate?.Invoke(this, new()
                    { Action = action, Chord = chord, Origin = origin });
                }
                catch
                {
                    // A recorder observer cannot break the registered hotkey thread.
                }
            }
            return false;
        }
        if (action is NeraHotkeyAction.EmergencyStop or NeraHotkeyAction.EmergencyStopFallback)
        {
            var sequence = Interlocked.Increment(ref emergencySequence_);
            var task = DispatchAndPublishAsync(new(action, origin), CancellationToken.None);
            emergencyTasks_[sequence] = task;
            _ = task.ContinueWith(
                (completed, state) =>
                {
                    _ = completed.Exception;
                    var tuple = ((ConcurrentDictionary<long, Task> Tasks, long Sequence))state!;
                    tuple.Tasks.TryRemove(tuple.Sequence, out _);
                },
                (emergencyTasks_, sequence),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return true;
        }
        return NeraHotkeyPolicy.EditableActions.Contains(action) && normalActions_.Writer.TryWrite(new(action, origin));
    }

    public async Task<NeraCommandResult> RouteAsync(
        NeraHotkeyAction action,
        CancellationToken cancellationToken = default,
        NeraInputOrigin? origin = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed_) != 0, this);
        var snapshot = await control_.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref recording_) != 0 &&
            action is not (NeraHotkeyAction.EmergencyStop or NeraHotkeyAction.EmergencyStopFallback))
            return new NeraCommandResult
            {
                Ok = false, RequestId = "hotkey-recording-" + Guid.NewGuid().ToString("N"),
                PreviousRevision = snapshot.Revision, CurrentRevision = snapshot.Revision, State = snapshot,
                Code = "HOTKEY_RECORDING_ACTIVE", UserMessage = NeraLocalizer.Get("Hotkey.Recording")
            };
        if (action == NeraHotkeyAction.HoldOriginal)
        {
            // RegisterHotKey cannot observe key-up. Refuse the unsafe half-gesture instead of
            // leaving HoldOriginal stuck true. A local key-down/up source may use SetHoldOriginal.
            return new NeraCommandResult
            {
                Ok = false,
                RequestId = "hotkey-reveal-refused-" + Guid.NewGuid().ToString("N"),
                PreviousRevision = snapshot.Revision,
                CurrentRevision = snapshot.Revision,
                State = snapshot,
                Code = NeraControlCodes.HotkeyHoldUnsupported,
                UserMessage = NeraLocalizer.Get("Hotkey.HoldUnsupported"),
                TechnicalMessage = "RegisterHotKey 只能报告按下事件，无法可靠报告松开事件。"
            };
        }

        var kind = MapAction(action, snapshot);
        var command = NeraCommandEnvelope.Create(
            kind,
            NeraCommandSource.Hotkey,
            snapshot.Revision,
            requestId: $"hotkey-{ActionKey(action)}-{Guid.NewGuid():N}",
            inputOrigin: origin);
        return await control_.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed_, 1) != 0)
        {
            return;
        }

        normalActions_.Writer.TryComplete();
        shutdown_.Cancel();
        try
        {
            await worker_.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        var emergency = emergencyTasks_.Values.ToArray();
        if (emergency.Length > 0)
        {
            await Task.WhenAll(emergency).ConfigureAwait(false);
        }
        shutdown_.Dispose();
    }

    private async Task ProcessNormalActionsAsync()
    {
        await foreach (var action in normalActions_.Reader.ReadAllAsync(shutdown_.Token).ConfigureAwait(false))
        {
            await DispatchAndPublishAsync(action, shutdown_.Token).ConfigureAwait(false);
        }
    }

    private async Task DispatchAndPublishAsync(Activation activation, CancellationToken cancellationToken)
    {
        NeraHotkeyAction action = activation.Action;
        try
        {
            var result = await RouteAsync(action, cancellationToken, activation.Origin).ConfigureAwait(false);
            CommandCompleted?.Invoke(this, new NeraHotkeyCommandCompletedEventArgs
            {
                Action = action,
                Result = result
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            RoutingFaulted?.Invoke(this, new NeraHotkeyRoutingFaultedEventArgs
            {
                Action = action,
                Error = error
            });
        }
    }

    private sealed record Activation(NeraHotkeyAction Action, NeraInputOrigin? Origin);

    private static NeraCommandKind MapAction(NeraHotkeyAction action, NeraStateSnapshot state) => action switch
    {
        NeraHotkeyAction.ToggleDldr => IsDldrRequestedOrStarting(state)
            ? NeraCommandKind.DisableDldr
            : NeraCommandKind.EnableDldr,
        NeraHotkeyAction.ToggleHud => state.HudVisible
            ? NeraCommandKind.HideHud
            : NeraCommandKind.ShowHud,
        NeraHotkeyAction.ToggleAppVisibility => state.AppVisible
            ? NeraCommandKind.HideApp
            : NeraCommandKind.ShowApp,
        NeraHotkeyAction.EmergencyStop or NeraHotkeyAction.EmergencyStopFallback => NeraCommandKind.EmergencyStop,
        NeraHotkeyAction.HoldOriginal => throw new InvalidOperationException(
            "HoldOriginal 必须在命令映射前被拒绝。"),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static bool IsDldrRequestedOrStarting(NeraStateSnapshot state) =>
        state.DldrRequested || state.DldrActualState is
            NeraDldrActualState.DisplayChecking or
            NeraDldrActualState.HdrChecking or
            NeraDldrActualState.GpuChecking or
            NeraDldrActualState.RuntimeChecking or
            NeraDldrActualState.RuntimeVerified or
            NeraDldrActualState.HostStarting or
            NeraDldrActualState.CaptureCreating or
            NeraDldrActualState.FeatureCreating or
            NeraDldrActualState.WaitingForFirstFrame or
            NeraDldrActualState.Processing;

    private static string ActionKey(NeraHotkeyAction action) => action switch
    {
        NeraHotkeyAction.ToggleDldr => "dldr",
        NeraHotkeyAction.ToggleHud => "hud",
        NeraHotkeyAction.ToggleAppVisibility => "visibility",
        NeraHotkeyAction.HoldOriginal => "hold-original",
        NeraHotkeyAction.EmergencyStop => "emergency-stop",
        NeraHotkeyAction.EmergencyStopFallback => "emergency-stop-fallback",
        _ => "unknown"
    };
}
