using System.Collections.Concurrent;
using System.Text.Json;
using ChipsStudio.Nera.Control;

namespace ChipsStudio.Nera.Control.Tests;

internal static class Phase14LifecycleTests
{
    internal static async Task WarmExpiryUsesNewSessionProof()
    {
        var backend = new FakeControlBackend { UseWarmStandby = true, ExpireWarmHostBeforeResume = true };
        backend.FirstFrameOutcome = backend.FirstFrameOutcome with { FrameId = 1000 };
        await using var control = Create(backend);
        var enabled = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.EnableDldr, NeraCommandSource.Test, 0));
        Check(enabled.Ok && enabled.State.HostSessionGeneration == 1, "Cold gate must record its generation immediately.");
        var paused = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.DisableDldr, NeraCommandSource.Test, enabled.CurrentRevision));
        Check(paused.Ok && paused.State.WarmStandby, "Warm OFF precondition failed.");
        var resumed = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.EnableDldr, NeraCommandSource.Test, paused.CurrentRevision));
        Check(resumed.Ok && resumed.State.DldrOn && resumed.State.HostSessionGeneration == 2 &&
              resumed.State.LastSuccessfulFrameId == 1 && backend.ColdHostStartCount == 2,
            "Warm expiry must accept only fresh fully gated proof from the new owned Host.");
    }

    internal static async Task RecoveryRequiresProof()
    {
        var backend = new FakeControlBackend();
        await using var control = Create(backend);
        var enabled = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.EnableDldr, NeraCommandSource.Test, 0));
        Check(enabled.Ok, enabled.TechnicalMessage);
        var live = Frame(enabled.State with { HostSessionGeneration = 17 }, 1000);
        var waiting = NeraStatePolicy.WithComputedDldrGate(live with
        {
            DldrActualState = NeraDldrActualState.WaitingForFirstFrame,
            ProcessingActive = false, FirstFeature18FrameSucceeded = false,
            DldrSucceeded = false, PresenterSucceeded = false
        });
        Check(NeraStatePolicy.CanTransition(live, waiting), "A sampled hidden Presenter safely revokes ON.");
        Check(!NeraStatePolicy.CanTransition(waiting, live), "Old frame cannot restore ON.");
        Check(NeraStatePolicy.CanTransition(waiting, Frame(live, 1001)), "A fresh same-Host frame restores ON.");
        var restarted = Frame(live with { HostSessionGeneration = 18 }, 1);
        Check(NeraStatePolicy.CanTransition(waiting, restarted),
            "An expired warm session may cold-start a new owned generation with full first-frame proof.");
        var proof = backend.FirstFrameOutcome with { HostSessionGeneration = 18, FrameId = 1 };
        Check(proof.IsFreshFor(waiting), "New-generation frame one is fresh, not an old-generation stale frame.");
        Check(!(proof with { HostSessionGeneration = 0 }).IsFreshFor(waiting) &&
              !(proof with { HostSessionGeneration = 16 }).IsFreshFor(waiting) &&
              !(proof with { HostSessionGeneration = 17, FrameId = 1000 }).IsFreshFor(waiting) &&
              !(proof with { PresenterSucceeded = false }).IsFreshFor(waiting),
            "Unknown/older generation, stale same-generation frame and incomplete proof remain closed.");
        foreach (var invalid in new[]
        {
            waiting with { HostSessionGeneration = 18 },
            waiting with { HostConnected = false },
            waiting with { FeatureCreated = false },
            waiting with { RuntimeSha256 = new string('0', 64) },
            waiting with { DldrRequested = false },
            waiting with { DldrOn = true }
        }) Check(!NeraStatePolicy.CanTransition(live, invalid), "Yield shortcut requires exact retained ownership.");

        var recovering = waiting with
        {
            DldrActualState = NeraDldrActualState.Recovering, ProtectedRecoveryPending = true,
            HostConnected = false, MonitorCaptureCreated = false, FeatureCreated = false
        };
        var restored = Frame(live with { HostSessionGeneration = 18 }, 3);
        Check(NeraStatePolicy.CanTransition(recovering, restored), "New Host FrameId is not compared with old Host FrameId.");
        var observedNewHost = recovering with
        {
            HostSessionGeneration = 18, LastSuccessfulFrameId = 0,
            LastFeature18FrameId = 0, LastDldrFrameId = 0, LastPresentedFrameId = 0
        };
        Check(NeraStatePolicy.CanTransition(observedNewHost, restored),
            "A poll during new Host initialization cannot prevent later verified recovery.");
        foreach (var invalid in new[]
        {
            restored with { HostSessionGeneration = 0 },
            restored with { HostSessionGeneration = 16 },
            restored with { RuntimeSha256 = new string('0', 64) },
            restored with { TargetDisplay = restored.TargetDisplay! with { DisplayId = "another" } },
            restored with { DldrRequested = false },
            restored with { PresenterSucceeded = false },
            restored with { ForegroundPreserved = false },
            restored with { BrokerProcessOwned = false },
            restored with { LastDldrFrameId = 2 },
            restored with { OverlayBypass = true },
            restored with { ProtectedRecoveryPending = true }
        }) Check(!NeraStatePolicy.CanTransition(recovering, NeraStatePolicy.WithComputedDldrGate(invalid)),
            "Recovery cannot skip identity, new session or complete first-frame proof.");
        Check(!NeraStatePolicy.CanTransition(recovering with { ProtectedRecoveryPending = false }, restored),
            "Unknown recovery never receives a generic bypass around the state graph.");
        Check(!NeraStatePolicy.CanTransition(recovering with { DldrRequested = false }, restored),
            "User cancellation wins over an automatic recovery.");
    }

    internal static async Task ObservationCause()
    {
        var backend = new FakeControlBackend(); var audit = new Sink();
        await using var control = Create(backend, audit, true);
        var enabled = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.EnableDldr, NeraCommandSource.Test, 0));
        Check(enabled.Ok, "Fake enable failed.");
        backend.ObservationException = new NeraStateTransitionException(
            NeraDldrActualState.Recovering, NeraDldrActualState.Processing);
        var recovered = await control.WaitForStateAsync(state => state.OffGateSatisfied &&
            state.LastError?.ReasonCode == "STATE_TRANSITION_REJECTED", TimeSpan.FromSeconds(3));
        Check(!recovered.DldrOn && recovered.LastError?.Code == NeraControlCodes.OperationFailed,
            "Stable API error code and precise structured cause must both survive safe restore.");
        var record = audit.Records.FirstOrDefault(value => value.Event == "ObservationFailed");
        Check(record?.LifecycleReasonCode == "STATE_TRANSITION_REJECTED" &&
            record.ExceptionFingerprint?.Length == 64 && record.ExceptionHResult is not null,
            "Audit lost the exact exception category/fingerprint.");
        Check(NeraLifecycleReasonPolicy.ObservationFailure(new InvalidOperationException()) == "OBSERVATION_INVALID_OPERATION",
            "An unrelated InvalidOperation must not be falsely labeled as a state graph failure.");
        Check(NeraLifecycleReasonPolicy.ObservationFailure(new Exception()) == "UNKNOWN",
            "Unclassified causes remain unknown, not invented.");
        Check(!JsonSerializer.Serialize(audit.Records).Contains("非法", StringComparison.Ordinal),
            "Audit must store structured cause, not arbitrary exception text.");
    }

    internal static async Task ExplicitShow()
    {
        var backend = new FakeControlBackend(); await using var control = Create(backend);
        for (int index = 0; index < 2; index++)
        {
            var state = await control.GetSnapshotAsync();
            var result = await control.ExecuteAsync(NeraCommandEnvelope.Create(
                NeraCommandKind.ShowApp, NeraCommandSource.Test, state.Revision));
            Check(result.Ok && result.State.AppVisible, "Show failed.");
        }
        Check(backend.Calls.Count(call => call == "execute:ShowApp") == 2,
            "Explicit Show must reach native activation even when AppVisible was already true.");
    }

    private static NeraStateSnapshot Frame(NeraStateSnapshot state, ulong frame) =>
        NeraStatePolicy.WithComputedDldrGate(state with
        {
            DldrActualState = NeraDldrActualState.Processing, DldrRequested = true,
            ProcessingActive = true, FirstFeature18FrameSucceeded = true, DldrSucceeded = true,
            PresenterSucceeded = true, LastSuccessfulFrameId = frame, LastFeature18FrameId = frame,
            LastDldrFrameId = frame, LastPresentedFrameId = frame,
            Feature18SuccessfulFrames = frame, DldrSuccessfulFrames = frame, PresentedFrames = frame
        });

    private static NeraControlService Create(FakeControlBackend backend, INeraControlAuditSink? audit = null, bool observe = false)
    {
        var display = FakeControlBackend.Display(selected: true);
        return new(backend, NeraStateSnapshot.CreateInitial() with
        {
            TargetDisplay = display, AvailableDisplays = [display],
            InputWidth = display.Width, InputHeight = display.Height,
            OutputWidth = display.Width, OutputHeight = display.Height,
            NeuralInputWidth = display.Width, NeuralInputHeight = display.Height,
            HdrSystemEnabled = true, HdrPipelineEnabled = true, HdrInputDetected = true
        }, new()
        {
            BackendStepTimeout = TimeSpan.FromSeconds(3), FirstFrameTimeout = TimeSpan.FromSeconds(3),
            ActiveObservationInterval = observe ? TimeSpan.FromMilliseconds(20) : TimeSpan.FromMinutes(10),
            IdleObservationInterval = observe ? TimeSpan.FromMilliseconds(40) : TimeSpan.FromMinutes(10)
        }, audit);
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Sink : INeraControlAuditSink
    {
        internal ConcurrentQueue<NeraControlAuditRecord> Records { get; } = new();
        public bool TryWrite(NeraControlAuditRecord value) { Records.Enqueue(value); return true; }
    }
}
