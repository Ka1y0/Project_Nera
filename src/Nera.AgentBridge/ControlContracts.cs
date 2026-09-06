using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChipsStudio.Nera.AgentBridge;

public static class NeraControlProtocol
{
    public const int Version = 7;
    public const string AgentBridgeVersion = "0.4.1";
    public const string ExpectedAppVersion = "0.4.1-global-alpha.1";
    public const int MaximumMessageBytes = 1024 * 1024;
    public const int DefaultTimeoutMilliseconds = 5000;
    public const int MaximumTimeoutMilliseconds = 30000;
    public const string AgentSource = "agent";
    public const string CliSource = "cli";
    public const string OrchestratorSource = "orchestrator";

    public static string CurrentUserPipeName()
    {
        var sid = WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            throw new NeraTransportException(NeraErrorCodes.PermissionDenied,
                "无法获取当前 Windows 用户 SID。");
        }
        return PipeNameForSid(sid);
    }

    public static string PipeNameForSid(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sid));
        return "Nera.Control." + Convert.ToHexString(digest.AsSpan(0, 16));
    }
}

public sealed record NeraCommandEnvelope
{
    public int ProtocolVersion { get; init; } = NeraControlProtocol.Version;
    public required string RequestId { get; init; }
    public required string Source { get; init; }
    public long? ExpectedRevision { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Command { get; init; }
    public JsonObject Payload { get; init; } = new();
    public string? AgentBridgeVersion { get; init; }
    public string? ExpectedAppVersion { get; init; }
}

public sealed record NeraCommandResult
{
    public int ProtocolVersion { get; init; } = NeraControlProtocol.Version;
    public required bool Ok { get; init; }
    public required string RequestId { get; init; }
    public long? PreviousRevision { get; init; }
    public long? CurrentRevision { get; init; }
    public NeraStateSnapshot? State { get; init; }
    public required string Code { get; init; }
    public required string UserMessage { get; init; }
    public string? LocalizedMessage { get; init; }
    public string TechnicalMessage { get; init; } = string.Empty;
    public bool RollbackPerformed { get; init; }
    public IReadOnlyList<NeraDisplaySummary>? Displays { get; init; }
    public string? OperationId { get; init; }
    public JsonElement? Data { get; init; }
}

// Wire mirrors keep AgentBridge independent from the in-process control service.
public sealed record NeraStateSnapshot
{
    public string Language { get; init; } = "en-US";
    public string LanguagePreference { get; init; } = "system";
    public long Revision { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public required string AppVersion { get; init; }
    public required string RuntimeAdapterVersion { get; init; }
    public string? RuntimeBinaryVersion { get; init; }
    public string? RuntimeSha256 { get; init; }
    public string? RuntimePath { get; init; }
    public bool RuntimeSignatureValid { get; init; }
    public string? RuntimeSigner { get; init; }
    public bool RuntimeVerified { get; init; }
    public NeraDisplaySummary? TargetDisplay { get; init; }
    public IReadOnlyList<NeraDisplaySummary> AvailableDisplays { get; init; } = [];
    public int InputWidth { get; init; }
    public int InputHeight { get; init; }
    public double InputFps { get; init; }
    public int OutputWidth { get; init; }
    public int OutputHeight { get; init; }
    public double OutputFps { get; init; }
    public bool HdrSystemEnabled { get; init; }
    public bool HdrPipelineEnabled { get; init; }
    public bool HdrInputDetected { get; init; }
    public required string WorkingColorSpace { get; init; }
    public bool DldrRequested { get; init; }
    public required string DldrActualState { get; init; }
    public required string ProcessingMode { get; init; }
    public bool RecipeCustom { get; init; }
    public double DldrHighlightProtection { get; init; }
    public double DldrShadowProtection { get; init; }
    public double DldrChromaStrength { get; init; }
    public double DldrTemporalResponse { get; init; }
    public bool Feature18Custom { get; init; }
    public int Feature18Style { get; init; }
    public double Feature18Intensity { get; init; }
    public double Feature18LocalToneStrength { get; init; }
    public double Feature18LocalStructureStrength { get; init; }
    public double Feature18SkinStructureStrength { get; init; } = -1d;
    public bool Feature18UseAutoMask { get; init; }
    public int Strength { get; init; }
    public int NeuralScale { get; init; }
    public required string PerformanceMode { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string UiProtection { get; init; } = "off";
    public bool HudVisible { get; init; }
    public bool AppVisible { get; init; } = true;
    public bool HoldOriginal { get; init; }
    public int NeuralInputWidth { get; init; }
    public int NeuralInputHeight { get; init; }
    public IReadOnlyList<NeraHotkeyBinding> Hotkeys { get; init; } = [];
    public NeraAiControlPermissions AiPermissions { get; init; } = new();
    public NeraPerformanceSnapshot Performance { get; init; } = new();
    public ulong LastSuccessfulFrameId { get; init; }
    public ulong LastFeature18FrameId { get; init; }
    public ulong LastDldrFrameId { get; init; }
    public ulong LastPresentedFrameId { get; init; }
    public ulong Feature18SuccessfulFrames { get; init; }
    public ulong DldrSuccessfulFrames { get; init; }
    public ulong PresentedFrames { get; init; }
    public NeraErrorInfo? LastError { get; init; }
    public required string RecoveryState { get; init; }
    public bool GpuVerified { get; init; }
    public bool BrokerProcessOwned { get; init; }
    public bool BrokerConnected { get; init; }
    public bool HostConnected { get; init; }
    public ulong HostSessionGeneration { get; init; }
    public NeraNativeLifecycleHealth NativeLifecycleHealth { get; init; } = new();
    public bool ProtectedRecoveryPending { get; init; }
    public bool MonitorCaptureCreated { get; init; }
    public bool FeatureCreated { get; init; }
    public bool FirstFeature18FrameSucceeded { get; init; }
    public bool DldrSucceeded { get; init; }
    public bool PresenterSucceeded { get; init; }
    public bool ForegroundPreserved { get; init; }
    public bool ProcessingActive { get; init; }
    public bool OffGateSatisfied { get; init; } = true;
    public bool WarmStandby { get; init; }
    public bool WarmOffGateSatisfied { get; init; }
    public bool OverlayBypass { get; init; }
    public ulong AppliedParameterRevision { get; init; }
    public ulong AppliedParameterFrameId { get; init; }
    public bool DldrOn { get; init; }
}

public sealed record NeraDisplaySummary
{
    public required string DisplayId { get; init; }
    public required string DisplayName { get; init; }
    public string? SourceName { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public uint RefreshNumerator { get; init; }
    public uint RefreshDenominator { get; init; }
    public double RefreshRateHz { get; init; }
    public bool HdrSupported { get; init; }
    public bool HdrEnabled { get; init; }
    public bool AdvancedColorEnabled { get; init; }
    public double? SdrWhiteLevelNits { get; init; }
    public required string AdapterLuid { get; init; }
    public string? TargetId { get; init; }
    public bool Connected { get; init; }
    public bool Selected { get; init; }
    public bool Capturable { get; init; }
}

public sealed record NeraHotkeyBinding
{
    public required string Action { get; init; }
    public uint Modifiers { get; init; }
    public uint VirtualKey { get; init; }
    public bool Registered { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed record NeraAiControlPermissions
{
    public bool Enabled { get; init; }
    public bool ReadState { get; init; }
    public bool ReadDisplays { get; init; }
    public bool SetDisplay { get; init; }
    public bool ToggleDldr { get; init; }
    public bool ModifyDldrSettings { get; init; }
    public bool ControlHud { get; init; }
    public bool RunDiagnostics { get; init; }
}

public sealed record NeraPerformanceSnapshot
{
    public uint PresenterTimingFlags { get; init; }
    public uint PresenterTimingSampleCount { get; init; }
    public double FrameTimeP99Milliseconds { get; init; }
    public bool GpuUsageAvailable { get; init; }
    public bool CpuUsageAvailable { get; init; }
    public double FramesPerSecond { get; init; }
    public double AverageFramesPerSecond { get; init; }
    public double OnePercentLowFramesPerSecond { get; init; }
    public double CpuPercent { get; init; }
    public double GpuPercent { get; init; }
    public long MemoryBytes { get; init; }
    public long DedicatedVideoMemoryBytes { get; init; }
    public double InputFramesPerSecond { get; init; }
    public double CaptureFramesPerSecond { get; init; }
    public double ProcessingFramesPerSecond { get; init; }
    public double PresentFramesPerSecond { get; init; }
    public long DroppedFrames { get; init; }
    public long BypassFrames { get; init; }
    public double Feature18Milliseconds { get; init; }
    public double DldrMilliseconds { get; init; }
    public double EndToEndLatencyMilliseconds { get; init; }
}

public sealed record NeraErrorInfo
{
    public required string Code { get; init; }
    public string ReasonCode { get; init; } = "UNKNOWN";
    public required string UserMessage { get; init; }
    public string TechnicalMessage { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
}

// Exact v7 mirror; no arbitrary JSON extension data or error-text parsing.
public sealed record NeraNativeLifecycleHealth
{
    public string ReasonCode { get; init; } = "UNKNOWN";
    public string LifecycleStage { get; init; } = "UNKNOWN";
    public ulong? HeartbeatAgeMs { get; init; }
    public ulong? LastSuccessfulFrameAgeMs { get; init; }
    public uint? NativeHResult { get; init; }
    public uint? NativeError { get; init; }
    public uint? LastWin32Error { get; init; }
    public uint? ProcessExitCode { get; init; }
    public uint? StartupRetryAttempts { get; init; }
    public bool? StartupRetryPending { get; init; }
    public bool? StartupAutoRetryEnabled { get; init; }
    public string StartupRetryReason { get; init; } = "UNKNOWN";
}

public sealed class NeraTransportException : Exception
{
    public NeraTransportException(string stableCode, string message, Exception? inner = null)
        : base(message, inner) => StableCode = stableCode;

    public string StableCode { get; }
}
