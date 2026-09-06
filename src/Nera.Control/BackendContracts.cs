namespace ChipsStudio.Nera.Control;

public sealed record NeraBackendOutcome
{
    public required bool Succeeded { get; init; }
    public string Code { get; init; } = NeraControlCodes.Ok;
    public string UserMessage { get; init; } = string.Empty;
    public string TechnicalMessage { get; init; } = string.Empty;
    public NeraDisplaySummary? Display { get; init; }
    public IReadOnlyList<NeraDisplaySummary>? Displays { get; init; }
    public bool? GpuVerified { get; init; }
    public string? OperationId { get; init; }
    public bool? RuntimeVerified { get; init; }
    public string? RuntimeBinaryVersion { get; init; }
    public string? RuntimeSha256 { get; init; }
    public string? RuntimePath { get; init; }
    public bool? RuntimeSignatureValid { get; init; }
    public string? RuntimeSigner { get; init; }
    public IReadOnlyList<NeraHotkeyBinding>? Hotkeys { get; init; }
    public bool WarmStandby { get; init; }
    public ulong? AppliedParameterRevision { get; init; }
    public ulong? AppliedParameterFrameId { get; init; }

    public static NeraBackendOutcome Success(
        string userMessage = "操作已完成", string? operationId = null) => new()
    {
        Succeeded = true,
        UserMessage = userMessage,
        OperationId = operationId
    };

    public static NeraBackendOutcome Failure(
        string code, string userMessage, string technicalMessage = "") => new()
    {
        Succeeded = false,
        Code = code,
        UserMessage = userMessage,
        TechnicalMessage = technicalMessage
    };
}

public sealed record NeraRuntimeVerificationOutcome
{
    public required bool Succeeded { get; init; }
    public string Code { get; init; } = NeraControlCodes.Ok;
    public string UserMessage { get; init; } = string.Empty;
    public string TechnicalMessage { get; init; } = string.Empty;
    public string? RuntimeBinaryVersion { get; init; }
    public string? RuntimeSha256 { get; init; }
    public string? RuntimePath { get; init; }
    public bool RuntimeSignatureValid { get; init; }
    public string? RuntimeSigner { get; init; }

    public static NeraRuntimeVerificationOutcome Verified(
        string version, string sha256, string runtimePath,
        string signer = "NVIDIA Corporation") => new()
    {
        Succeeded = true,
        UserMessage = $"已连接实验运行时 {version}",
        RuntimeBinaryVersion = version,
        RuntimeSha256 = sha256,
        RuntimePath = runtimePath,
        RuntimeSignatureValid = true,
        RuntimeSigner = signer
    };

    public static NeraRuntimeVerificationOutcome Failure(
        string code, string userMessage, string technicalMessage = "") => new()
    {
        Succeeded = false,
        Code = code,
        UserMessage = userMessage,
        TechnicalMessage = technicalMessage
    };
}

public sealed record NeraFirstFrameGateOutcome
{
    public required bool Feature18ProcessSucceeded { get; init; }
    public required bool DldrSucceeded { get; init; }
    public required bool PresenterSucceeded { get; init; }
    public required bool ForegroundPreserved { get; init; }
    public required ulong FrameId { get; init; }
    /// <summary>Monotonic, controller-owned Host incarnation; frame IDs are local to this generation.</summary>
    public ulong HostSessionGeneration { get; init; }
    public ulong Feature18SuccessfulFrames { get; init; }
    public ulong DldrSuccessfulFrames { get; init; }
    public ulong PresentedFrames { get; init; }
    public string Code { get; init; } = NeraControlCodes.FirstFrameGateFailed;
    public string UserMessage { get; init; } = "处理未能启动，正在撤销 DLDR ON";
    public string TechnicalMessage { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool GateSatisfied => Feature18ProcessSucceeded && DldrSucceeded &&
        PresenterSucceeded && ForegroundPreserved && FrameId > 0 && HostSessionGeneration > 0 &&
        Feature18SuccessfulFrames > 0 && DldrSuccessfulFrames > 0 &&
        PresentedFrames > 0;

    public bool IsFreshFor(NeraStateSnapshot state) => GateSatisfied &&
        (HostSessionGeneration > state.HostSessionGeneration ||
         HostSessionGeneration == state.HostSessionGeneration && FrameId > state.LastSuccessfulFrameId);
}

/// <summary>
/// The only native boundary for the production Global DLDR Display Mode.
/// Every successful step must return observable evidence; unknown state fails closed.
/// </summary>
public interface INeraControlBackend
{
    /// <summary>
    /// Reads the backend's current native ownership, first-frame, recovery, display and
    /// performance evidence. The caller supplies the latest managed snapshot only as a
    /// merge seed; implementations must overwrite every field they own with observed
    /// evidence and must throw when that evidence cannot be read reliably.
    /// </summary>
    Task<NeraStateSnapshot> ObserveAuthoritativeStateAsync(
        NeraStateSnapshot current,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<NeraDisplaySummary>> ListDisplaysAsync(
        CancellationToken cancellationToken);
    Task<NeraBackendOutcome> ExecuteAsync(
        NeraCommandKind command,
        NeraCommandPayload payload,
        CancellationToken cancellationToken);
    Task<NeraBackendOutcome> ValidateCurrentDisplayAsync(CancellationToken cancellationToken);
    Task<NeraBackendOutcome> VerifyRtxGpuAsync(CancellationToken cancellationToken);
    Task<NeraRuntimeVerificationOutcome> VerifyRuntimeAsync(CancellationToken cancellationToken);
    Task<NeraBackendOutcome> StartRuntimeBrokerAsync(CancellationToken cancellationToken);
    Task<NeraBackendOutcome> StartCompatHostAsync(CancellationToken cancellationToken);
    Task<NeraBackendOutcome> CreateMonitorCaptureAsync(CancellationToken cancellationToken);
    Task<NeraBackendOutcome> CreateFeature18Async(CancellationToken cancellationToken);
    Task<NeraFirstFrameGateOutcome> WaitForFirstFrameAsync(CancellationToken cancellationToken);
    Task<NeraBackendOutcome> EnterFailSafeBypassAsync(
        string reasonCode, CancellationToken cancellationToken);
    Task<NeraBackendOutcome> DisableDldrAsync(
        bool emergency, CancellationToken cancellationToken);

    /// <summary>
    /// Thread-safe, non-blocking signal that interrupts in-flight work. Authoritative
    /// state still changes only when EmergencyStop runs on the serialized command lane.
    /// </summary>
    void SignalEmergencyStop();
}
