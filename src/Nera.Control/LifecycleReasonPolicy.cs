namespace ChipsStudio.Nera.Control;

/// <summary>Stable, payload-free lifecycle classification. Unknown causes remain unknown.</summary>
public static class NeraLifecycleReasonPolicy
{
    public static string ObservationFailure(Exception error) => error switch
    {
        TimeoutException => "OBSERVATION_TIMEOUT",
        System.Text.Json.JsonException => "OBSERVATION_INVALID_JSON",
        InvalidDataException => "OBSERVATION_INVALID_EVIDENCE",
        NeraStateTransitionException => "STATE_TRANSITION_REJECTED",
        InvalidOperationException => "OBSERVATION_INVALID_OPERATION",
        IOException => "OBSERVATION_IO_FAILED",
        _ => "UNKNOWN"
    };

    internal static string Transition(NeraCommandEnvelope? command, NeraStateSnapshot before,
        NeraStateSnapshot after, NeraCommandResult? result)
    {
        if (result?.Ok == false) return ErrorCode(result.Code);
        if (after.LastError is { } error && error != before.LastError)
            return error.ReasonCode != "UNKNOWN" ? NeraControlAuditPolicy.Token(error.ReasonCode) : ErrorCode(error.Code);
        if (command?.Kind == NeraCommandKind.EmergencyStop) return "EMERGENCY_STOP";
        if (command?.Kind == NeraCommandKind.DisableDldr)
            return command.Source == NeraCommandSource.Hotkey ? "HOTKEY_TOGGLE" : "USER_CANCELLED";
        if (after.OverlayBypass) return "OVERLAY_YIELD";
        if (after.ProtectedRecoveryPending) return "PROTECTED_CONTENT_RECOVERY";
        if (before.WarmStandby && !after.WarmStandby && after.OffGateSatisfied) return "HOST_IDLE_RELEASE";
        if (command?.Kind == NeraCommandKind.EnableDldr || before.DldrActualState != after.DldrActualState)
            return after.DldrActualState switch
            {
                NeraDldrActualState.DisplayChecking => "DISPLAY_CHECK",
                NeraDldrActualState.HdrChecking => "HDR_CHECK",
                NeraDldrActualState.GpuChecking => "GPU_CHECK",
                NeraDldrActualState.RuntimeChecking => "RUNTIME_CHECK",
                NeraDldrActualState.RuntimeVerified => "RUNTIME_VERIFIED",
                NeraDldrActualState.HostStarting => "HOST_START",
                NeraDldrActualState.CaptureCreating => "CAPTURE_START",
                NeraDldrActualState.FeatureCreating => "FEATURE_CREATE",
                NeraDldrActualState.WaitingForFirstFrame => "FIRST_FRAME_PENDING",
                NeraDldrActualState.Processing => "PROCESSING",
                NeraDldrActualState.Bypass or NeraDldrActualState.Disabled when !before.DldrRequested => "RESTORE_CONFIRMED",
                NeraDldrActualState.Recovering => "RECOVERY_PENDING",
                _ => "UNKNOWN"
            };
        return command is null ? "STATE_OBSERVED" : "CONTROL_COMMAND";
    }

    public static string ErrorCode(string code) => code switch
    {
        "DISPLAY_REQUIRED" or "DISPLAY_DISCONNECTED" or "DISPLAY_UNAVAILABLE" => "DISPLAY_UNAVAILABLE",
        "HDR_SYSTEM_DISABLED" => "HDR_DISABLED",
        "RUNTIME_MISSING" => "RUNTIME_MISSING",
        "RUNTIME_UNVERIFIED" => "RUNTIME_IDENTITY_CHANGED",
        "HOST_START_FAILED" => "HOST_START_FAILED",
        "BROKER_START_FAILED" => "BROKER_START_FAILED",
        "FEATURE_CREATE_FAILED" => "FEATURE_CREATE_FAILED",
        "MONITOR_CAPTURE_FAILED" => "CAPTURE_START_FAILED",
        "PRESENTER_FAILED" => "PRESENTER_FAILED",
        "FIRST_FRAME_GATE_FAILED" => "FIRST_FRAME_GATE_REJECTED",
        "INTERRUPTED_BY_EMERGENCY_STOP" => "EMERGENCY_STOP",
        "TIMEOUT" => "OPERATION_TIMEOUT",
        "STATE_CHANGED" => "STATE_CHANGED",
        "FAIL_SAFE_FAILED" => "RESTORE_UNCONFIRMED",
        "INPUT_ORIGIN_REJECTED" => "INPUT_ORIGIN_REJECTED",
        _ => "UNKNOWN"
    };
}
