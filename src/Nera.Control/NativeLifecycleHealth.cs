using System.Text.Json;

namespace ChipsStudio.Nera.Control;

/// <summary>
/// Diagnostic observations only; never ON/OFF authority. Numeric values come from exact
/// native snapshot members, not error text. Missing or unmeasured values remain null.
/// </summary>
public sealed record NeraNativeLifecycleHealth
{
    private string reasonCode_ = "UNKNOWN";
    private string lifecycleStage_ = "UNKNOWN";
    private string startupRetryReason_ = "UNKNOWN";

    public static NeraNativeLifecycleHealth Unknown { get; } = new();
    public string ReasonCode { get => reasonCode_; init => reasonCode_ = KnownReason(value); }
    public string LifecycleStage { get => lifecycleStage_; init => lifecycleStage_ = KnownStage(value); }
    public ulong? HeartbeatAgeMs { get; init; }
    public ulong? LastSuccessfulFrameAgeMs { get; init; }
    /// <summary>Unsigned HRESULT bits as emitted by the native protocol; null when no nonzero code was supplied.</summary>
    public uint? NativeHResult { get; init; }
    public uint? NativeError { get; init; }
    public uint? LastWin32Error { get; init; }
    /// <summary>Available only when native processExitConfirmed is true; zero then means confirmed normal exit.</summary>
    public uint? ProcessExitCode { get; init; }
    public uint? StartupRetryAttempts { get; init; }
    public bool? StartupRetryPending { get; init; }
    public bool? StartupAutoRetryEnabled { get; init; }
    public string StartupRetryReason { get => startupRetryReason_; init => startupRetryReason_ = KnownReason(value); }

    /// <summary>
    /// Reads only globalDisplay and its host object's fixed members. The current native
    /// snapshot has no black-probe statistics; final-report text and extra fields are ignored.
    /// </summary>
    public static NeraNativeLifecycleHealth FromGlobalDisplay(JsonElement global)
    {
        JsonElement host = Member(global, "host");
        bool successfulProcessing = Boolean(host, "processingConfirmed") == true &&
            Unsigned64(host, "lastSuccessfulFrameId") is > 0;
        return new()
        {
            ReasonCode = Text(host, "reasonCode"),
            LifecycleStage = Text(host, "lifecycleStage"),
            HeartbeatAgeMs = Unsigned64(host, "hostHeartbeatAgeMilliseconds"),
            // The native watchdog resets its progress clock while paused/starting too.
            // Do not relabel those resets as a newly successful frame.
            LastSuccessfulFrameAgeMs = successfulProcessing
                ? Unsigned64(host, "successfulFrameAgeMilliseconds") : null,
            NativeHResult = Nonzero32(host, "failSafeHresult"),
            NativeError = Nonzero32(host, "failSafeNativeError"),
            LastWin32Error = Nonzero32(host, "lastWin32Error"),
            ProcessExitCode = Boolean(host, "processExitConfirmed") == true
                ? Unsigned32(host, "processExitCode") : null,
            StartupRetryAttempts = Unsigned32(global, "startupRetryAttempts"),
            StartupRetryPending = Boolean(global, "startupRetryPending"),
            StartupAutoRetryEnabled = Boolean(global, "startupAutoRetryEnabled"),
            StartupRetryReason = Text(global, "startupRetryReason")
        };
    }

    /// <summary>Age-only sampling must update status but must not flood transition logs.</summary>
    internal static bool HasDiagnosticTransition(NeraNativeLifecycleHealth? before,
        NeraNativeLifecycleHealth? after) =>
        (before ?? Unknown) with { HeartbeatAgeMs = null, LastSuccessfulFrameAgeMs = null } !=
        (after ?? Unknown) with { HeartbeatAgeMs = null, LastSuccessfulFrameAgeMs = null };

    private static string KnownReason(string? value) => value switch
    {
        "NONE" or "HOST_CRASH" or "HOST_TIMEOUT" or "FEATURE_PROCESS_TIMEOUT" or
        "DISPLAY_UNAVAILABLE" or "DISPLAY_CHANGED" or "HDR_DISABLED" or "DEVICE_REMOVED" or
        "RUNTIME_IDENTITY_CHANGED" or "CAPTURE_START_FAILED" or "PROTECTED_CONTENT" or
        "BLACK_FRAME_GUARD" or "HOST_PIPELINE_FAILED" or "EMERGENCY_STOP" or
        "HOST_START_FAILED" or "HOST_PIPE_DISCONNECTED" or "HOST_PROTOCOL_REJECTED" or
        "CAPTURE_DENIED" or "FIRST_FRAME_TIMEOUT" or "HOST_CONTROL_FAILED" or
        "USER_CANCELLED" or "WARM_IDLE_RELEASE" or "HOST_EXITED" => value,
        _ => "UNKNOWN"
    };

    private static string KnownStage(string? value) => value switch
    {
        "DISABLED" or "HOST_START" or "RUNTIME_CHECK" or "FEATURE_CREATE" or
        "CAPTURE_START" or "FIRST_FEATURE18_FRAME" or "FIRST_DLDR_FRAME" or
        "FIRST_PRESENT_FRAME" or "PROCESSING" => value,
        _ => "UNKNOWN"
    };

    private static JsonElement Member(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value)
            ? value : default;
    private static string Text(JsonElement parent, string name) => Member(parent, name) is var value &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "UNKNOWN" : "UNKNOWN";
    private static bool? Boolean(JsonElement parent, string name) => Member(parent, name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
    private static ulong? Unsigned64(JsonElement parent, string name) => Member(parent, name) is var value &&
        value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong number) ? number : null;
    private static uint? Unsigned32(JsonElement parent, string name) => Member(parent, name) is var value &&
        value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint number) ? number : null;
    private static uint? Nonzero32(JsonElement parent, string name) => Unsigned32(parent, name) is > 0 and var value
        ? value : null;
}
