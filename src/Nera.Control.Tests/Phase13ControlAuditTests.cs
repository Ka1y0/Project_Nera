using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ChipsStudio.Nera.Control;

namespace ChipsStudio.Nera.Control.Tests;

internal static class Phase13ControlAuditTests
{
    internal static async Task InputOriginCannotToggleDisplay()
    {
        var backend = new FakeControlBackend();
        var audit = new CollectingAuditSink();
        await using var control = Create(backend, audit);
        foreach (uint message in new uint[]
        {
            0x200, 0x201, 0x202, 0x203, 0x204, 0x205, 0x206, 0x207, 0x208, 0x209,
            0x20A, 0x20B, 0x20C, 0x20D, 0x20E, 0x240, 0x241, 0x245, 0x246, 0x247,
            0x24E, 0x24F, 0x119, 0xFF, 0xA1, 0x777
        })
        foreach (NeraCommandKind kind in new[]
            { NeraCommandKind.EnableDldr, NeraCommandKind.DisableDldr, NeraCommandKind.EmergencyStop })
        {
            NeraCommandResult result = await control.ExecuteAsync(NeraCommandEnvelope.Create(kind,
                NeraCommandSource.Ui, 0, inputOrigin: new() { Message = message }));
            Check(!result.Ok && result.Code == NeraControlCodes.InputOriginRejected && result.CurrentRevision == 0,
                "A mouse/pointer/raw/unknown message must not mutate or interrupt DLDR.");
        }
        foreach (NeraInputKind inputKind in new[]
            { NeraInputKind.Mouse, NeraInputKind.Wheel, NeraInputKind.Pointer, NeraInputKind.RawMouse })
        {
            NeraCommandResult result = await control.ExecuteAsync(NeraCommandEnvelope.Create(
                NeraCommandKind.EmergencyStop, NeraCommandSource.Hotkey, 0,
                inputOrigin: new() { InputKind = inputKind }));
            Check(result.Code == NeraControlCodes.InputOriginRejected, "Input-kind evidence cannot be relabeled as Hotkey.");
        }
        Check(backend.Calls.IsEmpty && backend.SignalEmergencyStopCount == 0 &&
            audit.Records.All(value => value.Event != "StateTransition"), "Rejected input touched the backend or state.");

        backend.BlockingCommand = NeraCommandKind.SetStrength;
        Task<NeraCommandResult> active = control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetStrength, NeraCommandSource.Ui, 0, NeraCommandPayload.ForStrength(63)));
        await backend.BlockingCommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<NeraCommandResult> forbiddenEmergency = control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.EmergencyStop, NeraCommandSource.Ui, 0,
            inputOrigin: new() { InputKind = NeraInputKind.Wheel, Message = 0x20A }));
        await Task.Delay(30);
        Check(!active.IsCompleted && backend.SignalEmergencyStopCount == 0,
            "Forbidden EmergencyStop must not exploit the out-of-band cancellation signal.");
        NeraCommandResult emergency = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.EmergencyStop, NeraCommandSource.Ui, 0,
            inputOrigin: NeraInputOrigin.UiButton("EmergencyStopButton")));
        Check(emergency.Ok && backend.SignalEmergencyStopCount > 0,
            "Semantic user emergency button must retain highest priority.");
        Check((await active).Code == NeraControlCodes.InterruptedByEmergencyStop,
            "The real emergency must interrupt pending work.");
        Check((await forbiddenEmergency).Code == NeraControlCodes.InputOriginRejected,
            "Queued forbidden input must still be rejected after a real emergency.");
    }

    internal static async Task CommandAndObservationAudit()
    {
        var backend = new FakeControlBackend();
        var audit = new CollectingAuditSink();
        await using var control = Create(backend, audit, observe: true);
        NeraCommandEnvelope enable = NeraCommandEnvelope.Create(NeraCommandKind.EnableDldr,
            NeraCommandSource.Ui, 0, inputOrigin: NeraInputOrigin.UiButton("HomeDldrToggle", 123, 456));
        NeraCommandResult enabled = await control.ExecuteAsync(enable);
        Check(enabled.Ok && enabled.State.DldrOn, enabled.TechnicalMessage);
        Check((await control.ExecuteAsync(enable)).Ok, "Idempotent replay failed.");
        Check(audit.Records.Count(value => value.Event == "CommandCompleted" && value.RequestId == enable.RequestId) == 1,
            "Idempotent replay must not pretend a second action ran.");
        Check(audit.Records.Any(value => value.Event == "StateTransition" && value.NextState.On &&
            value.Source == "UI" && value.ControlId == "HomeDldrToggle" && value.FrameId > 0 &&
            value.PresenterShowHideResult == "PRESENTER_FIRST_FRAME_CONFIRMED"),
            "Actual first-frame transition must retain UI source and frame evidence.");

        backend.AuthoritativeObservation = current => current with
        {
            DldrActualState = NeraDldrActualState.Recovering, OverlayBypass = true,
            ProcessingActive = false, FirstFeature18FrameSucceeded = false,
            DldrSucceeded = false, PresenterSucceeded = false
        };
        await control.WaitForStateAsync(state => state.OverlayBypass, TimeSpan.FromSeconds(2));
        Check(audit.Records.Any(value => value.Event == "StateTransition" && value.Reason == "OverlayYield" &&
            value.Source == "OVERLAY_COMPATIBILITY" && value.CallSiteCategory == "InternalObservation" &&
            value.ControlId is null && value.OriginMessage is null && !value.NextState.On),
            "Native Overlay observation must never be labeled as a user button or confirmed ON.");

        backend.AuthoritativeObservation = current => current;
        NeraStateSnapshot currentState = await control.GetSnapshotAsync();
        NeraCommandResult disabled = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.DisableDldr, NeraCommandSource.Cli, currentState.Revision));
        Check(disabled.Ok, disabled.TechnicalMessage);
        Check(audit.Records.Any(value => value.Event == "CommandCompleted" && value.Source == "CLI" &&
            value.RollbackPerformed && value.PresenterShowHideResult == "FULL_RELEASE_CONFIRMED"),
            "Compatibility CLI commands without origin must still work and report exact restore evidence.");

        const string privateText = @"C:\private-user\photo-secret.jpg";
        NeraCommandResult privateRequest = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.RunSelfTest, NeraCommandSource.Agent, disabled.CurrentRevision,
            new() { Options = JsonSerializer.SerializeToElement(new { note = privateText }) }, requestId: privateText));
        Check(privateRequest.Ok, "Private diagnostic payload test failed.");
        string serialized = JsonSerializer.Serialize(audit.Records);
        Check(!serialized.Contains("private-user", StringComparison.Ordinal) &&
              !serialized.Contains(FakeControlBackend.FakeRuntimePath, StringComparison.Ordinal) &&
              !serialized.Contains("photo-secret", StringComparison.Ordinal) && !serialized.Contains("payload", StringComparison.Ordinal),
            "Audit must never include a payload, arbitrary request path, runtime path or private text.");
        Check(audit.Records.Any(value => value.RequestId.StartsWith("sha256-", StringComparison.Ordinal)),
            "Unsafe opaque request IDs must be represented by a one-way token.");

        int transitions = audit.Records.Count(value => value.Event == "StateTransition");
        backend.AuthoritativeObservation = state => state with
        { Performance = state.Performance with { CpuPercent = state.Performance.CpuPercent + 0.001 } };
        await Task.Delay(100);
        Check(audit.Records.Count(value => value.Event == "StateTransition") == transitions,
            "Performance samples must not create per-frame state-transition log spam.");

        await using var brokenAuditControl = Create(new FakeControlBackend(), new ThrowingAuditSink());
        NeraCommandResult stillWorks = await brokenAuditControl.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.ShowHud, NeraCommandSource.Ui, 0));
        Check(stillWorks.Ok && stillWorks.State.HudVisible, "Diagnostic failure must not affect a valid command.");
    }

    internal static async Task RollingAuditIsBounded()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), "NeraPhase13Audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        string logs = Path.Combine(testDirectory, "Logs");
        try
        {
            NeraControlAuditState state = NeraControlAuditState.From(NeraStateSnapshot.CreateInitial());
            await using (var sink = new RollingNeraControlAuditSink(testDirectory, 4096, fileCount: 2, queueCapacity: 128))
            {
                var timer = Stopwatch.StartNew();
                for (int index = 0; index < 100; index++) Check(sink.TryWrite(new()
                {
                    Sequence = index, Event = "Test", RequestId = "audit-test", PreviousState = state, NextState = state
                }), "The isolated queue should accept a bounded batch.");
                Check(timer.Elapsed < TimeSpan.FromSeconds(1), "Audit enqueue performed blocking disk work.");
            }
            string[] files = Directory.GetFiles(logs);
            Check(files.Length is > 0 and <= 2 && files.All(file =>
                Path.GetFileName(file) is "control-audit.jsonl" or "control-audit.1.jsonl"),
                "Rotation must create only its two exact owned filenames.");
            foreach (string file in files)
            {
                Check(new FileInfo(file).Length <= 4096, "A rotated audit file exceeded its configured bound.");
                foreach (string line in File.ReadLines(file))
                {
                    using JsonDocument parsed = JsonDocument.Parse(line);
                    Check(parsed.RootElement.TryGetProperty("previousState", out _) &&
                        !parsed.RootElement.TryGetProperty("payload", out _), "Audit line is incomplete or overbroad.");
                }
            }
            string blockedPath = Path.Combine(testDirectory, "data-path-is-a-file");
            File.WriteAllText(blockedPath, "isolated-test");
            try
            {
                await using var sink = new RollingNeraControlAuditSink(blockedPath);
                Check(sink.TryWrite(new() { PreviousState = state, NextState = state }),
                    "A storage failure must not block enqueue.");
                await sink.DisposeAsync();
                Check(sink.FailedRecords == 1, "Storage failure must be counted, not mistaken for persisted evidence.");
            }
            finally { File.Delete(blockedPath); }
        }
        finally
        {
            // Exact private test directory created above; no recursive or cross-tree cleanup.
            foreach (string name in new[] { "control-audit.jsonl", "control-audit.1.jsonl" })
                if (File.Exists(Path.Combine(logs, name))) File.Delete(Path.Combine(logs, name));
            if (Directory.Exists(logs)) Directory.Delete(logs, recursive: false);
            Directory.Delete(testDirectory, recursive: false);
        }
    }

    private static NeraControlService Create(FakeControlBackend backend, INeraControlAuditSink audit,
        bool observe = false)
    {
        NeraDisplaySummary display = FakeControlBackend.Display(selected: true);
        NeraStateSnapshot initial = NeraStateSnapshot.CreateInitial() with
        {
            TargetDisplay = display, AvailableDisplays = [display], InputWidth = display.Width,
            InputHeight = display.Height, OutputWidth = display.Width, OutputHeight = display.Height,
            NeuralInputWidth = display.Width, NeuralInputHeight = display.Height,
            HdrSystemEnabled = true, HdrPipelineEnabled = true, HdrInputDetected = true
        };
        return new(backend, initial, new()
        {
            BackendStepTimeout = TimeSpan.FromSeconds(3), FirstFrameTimeout = TimeSpan.FromSeconds(3),
            ActiveObservationInterval = observe ? TimeSpan.FromMilliseconds(20) : TimeSpan.FromSeconds(10),
            IdleObservationInterval = observe ? TimeSpan.FromMilliseconds(40) : TimeSpan.FromSeconds(10)
        }, audit);
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class CollectingAuditSink : INeraControlAuditSink
    {
        public ConcurrentQueue<NeraControlAuditRecord> Records { get; } = new();
        public bool TryWrite(NeraControlAuditRecord record) { Records.Enqueue(record); return true; }
    }
    private sealed class ThrowingAuditSink : INeraControlAuditSink
    {
        public bool TryWrite(NeraControlAuditRecord record) => throw new IOException("Isolated diagnostic failure");
    }
}
