using System.Collections.Concurrent;
using System.Text.Json;
using ChipsStudio.Nera.Control;

namespace ChipsStudio.Nera.Control.Tests;

internal static class Phase14NativeLifecycleHealthTests
{
    internal static Task ExactNativeFields()
    {
        NeraNativeLifecycleHealth health = Read("""
            {"startupRetryPending":false,"startupRetryAttempts":0,"startupAutoRetryEnabled":false,
             "startupRetryReason":"BLACK_FRAME_GUARD","host":{
              "reasonCode":"DEVICE_REMOVED","lifecycleStage":"PROCESSING",
              "hostHeartbeatAgeMilliseconds":31,"successfulFrameAgeMilliseconds":17,
              "processingConfirmed":true,"lastSuccessfulFrameId":401,
              "failSafeHresult":2289696773,"failSafeNativeError":5,"lastWin32Error":87,
              "processExitConfirmed":true,"processExitCode":3221225794}}
            """);
        Check(health is { ReasonCode: "DEVICE_REMOVED", LifecycleStage: "PROCESSING",
            HeartbeatAgeMs: 31, LastSuccessfulFrameAgeMs: 17, NativeHResult: 0x887A0005,
            NativeError: 5, LastWin32Error: 87, ProcessExitCode: 0xC0000142,
            StartupRetryPending: false, StartupRetryAttempts: 0, StartupAutoRetryEnabled: false,
            StartupRetryReason: "BLACK_FRAME_GUARD" }, "Exact native diagnostics were lost or reinterpreted.");
        foreach (string stage in new[] { "DISABLED", "HOST_START", "RUNTIME_CHECK", "FEATURE_CREATE",
            "CAPTURE_START", "FIRST_FEATURE18_FRAME", "FIRST_DLDR_FRAME", "FIRST_PRESENT_FRAME", "PROCESSING" })
            Check(new NeraNativeLifecycleHealth { LifecycleStage = stage }.LifecycleStage == stage,
                "Verified native lifecycle stage was not allowlisted.");
        return Task.CompletedTask;
    }

    internal static Task UnknownAndMissingStayUnavailable()
    {
        foreach (string json in new[] { "{}", "null", "[]", "42", "{\"host\":[]}" })
            Check(Read(json) == NeraNativeLifecycleHealth.Unknown, "Absent/invalid native health must not inherit old evidence.");
        NeraNativeLifecycleHealth health = Read("""
            {"startupRetryPending":"true","startupRetryAttempts":-1,"startupAutoRetryEnabled":1,
             "startupRetryReason":"private-window-title","host":{
              "reasonCode":"C:\\private\\runtime.dll","lifecycleStage":"private-window-title",
              "hostHeartbeatAgeMilliseconds":"12","successfulFrameAgeMilliseconds":-1,
              "processingConfirmed":true,"lastSuccessfulFrameId":1,
              "failSafeHresult":4294967296,"failSafeNativeError":-5,"lastWin32Error":0.5,
              "processExitConfirmed":true,"processExitCode":{},
              "blackProbe":{"samples":123,"windowTitle":"private-window-title"},
              "hostReportFailureDetail":"BLACK_FRAME_GUARD, samples=99, private-window-title"}}
            """);
        Check(health == NeraNativeLifecycleHealth.Unknown, "Malformed numbers/bools or unknown enum text was accepted.");
        foreach (string unknown in new[] { "UNKNOWN", "UNRECOGNIZED_HOST_REASON", "host_timeout", "", "private-window-title" })
            Check(new NeraNativeLifecycleHealth { ReasonCode = unknown }.ReasonCode == "UNKNOWN",
                "Unknown native reason must not become an arbitrary audit string.");
        string serialized = JsonSerializer.Serialize(health with
        {
            ReasonCode = "C:\\private\\runtime.dll", LifecycleStage = "private-window-title",
            StartupRetryReason = "secret-text"
        });
        Check(!serialized.Contains("private", StringComparison.Ordinal) && !serialized.Contains("secret", StringComparison.Ordinal) &&
            !serialized.Contains("blackProbe", StringComparison.Ordinal),
            "Native health must not retain private strings or manufacture unavailable black-probe statistics.");
        return Task.CompletedTask;
    }

    internal static Task AgesAndExitNeedEvidence()
    {
        NeraNativeLifecycleHealth paused = Read("""
            {"host":{"reasonCode":"NONE","lifecycleStage":"FIRST_PRESENT_FRAME",
             "hostHeartbeatAgeMilliseconds":0,"successfulFrameAgeMilliseconds":0,
             "processingConfirmed":false,"lastSuccessfulFrameId":900,
             "processExitConfirmed":false,"processExitCode":259,
             "failSafeHresult":0,"failSafeNativeError":0,"lastWin32Error":0}}
            """);
        Check(paused.HeartbeatAgeMs == 0 && paused.LastSuccessfulFrameAgeMs is null &&
            paused.ProcessExitCode is null && paused.NativeHResult is null && paused.NativeError is null,
            "Paused progress-clock reset/STILL_ACTIVE/default error zero must not look like fresh frame/exit proof.");
        Check(Read("""{"host":{"processingConfirmed":true,"lastSuccessfulFrameId":0,"successfulFrameAgeMilliseconds":4}}""")
            .LastSuccessfulFrameAgeMs is null, "Before the first success there is no successful-frame age.");
        Check(Read("""{"host":{"processExitConfirmed":true,"processExitCode":0}}""").ProcessExitCode == 0,
            "Confirmed zero exit code is valid evidence, not unavailable.");
        return Task.CompletedTask;
    }

    internal static async Task ObservationAndAuditKeepOneTypedSnapshot()
    {
        var backend = new FakeControlBackend();
        var audit = new AuditSink();
        await using var control = new NeraControlService(backend, NeraStateSnapshot.CreateInitial(), new()
        {
            ActiveObservationInterval = TimeSpan.FromMilliseconds(20),
            IdleObservationInterval = TimeSpan.FromMilliseconds(20)
        }, audit);
        var observed = new NeraNativeLifecycleHealth
        {
            ReasonCode = "FIRST_FRAME_TIMEOUT", LifecycleStage = "FIRST_PRESENT_FRAME",
            HeartbeatAgeMs = 9, StartupRetryPending = false, StartupRetryAttempts = 0,
            StartupAutoRetryEnabled = false
        };
        backend.AuthoritativeObservation = state => state with { NativeLifecycleHealth = observed };
        NeraStateSnapshot state = await control.WaitForStateAsync(
            value => value.NativeLifecycleHealth == observed, TimeSpan.FromSeconds(2));
        Check(state.OffGateSatisfied && !state.DldrOn && !state.DldrRequested &&
            state.DldrActualState == NeraDldrActualState.Disabled, "Diagnostics must never become lifecycle authority.");
        Check(audit.Records.Any(value => value.Event == "StateTransition" && value.NativeLifecycleHealth == observed),
            "A diagnostic-only native transition was lost from the canonical observation/audit path.");
        int transitions = audit.Records.Count(value => value.Event == "StateTransition");
        backend.AuthoritativeObservation = value => value with
        { NativeLifecycleHealth = value.NativeLifecycleHealth with { HeartbeatAgeMs = value.NativeLifecycleHealth.HeartbeatAgeMs + 1 } };
        await control.WaitForStateAsync(value => value.NativeLifecycleHealth.HeartbeatAgeMs >= 12, TimeSpan.FromSeconds(2));
        Check(audit.Records.Count(value => value.Event == "StateTransition") == transitions,
            "Age-only samples must be visible in status without a transition audit entry per poll.");
        backend.AuthoritativeObservation = value => value with { NativeLifecycleHealth = NeraNativeLifecycleHealth.Unknown };
        await control.WaitForStateAsync(value => value.NativeLifecycleHealth == NeraNativeLifecycleHealth.Unknown, TimeSpan.FromSeconds(2));
        Check(audit.Records.Any(value => value.Event == "StateTransition" && value.NativeLifecycleHealth == NeraNativeLifecycleHealth.Unknown),
            "Missing new-session diagnostics must clear the old diagnostic snapshot.");
    }

    private static NeraNativeLifecycleHealth Read(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return NeraNativeLifecycleHealth.FromGlobalDisplay(document.RootElement);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed class AuditSink : INeraControlAuditSink
    {
        internal ConcurrentQueue<NeraControlAuditRecord> Records { get; } = new();
        public bool TryWrite(NeraControlAuditRecord record) { Records.Enqueue(record); return true; }
    }
}
