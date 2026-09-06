using System.Text.Json;

namespace ChipsStudio.Nera.Control;

public enum NeraCommandSource
{
    Ui,
    Hotkey,
    Agent,
    Cli,
    Orchestrator,
    Test
}

/// <summary>
/// Production commands for the Global DLDR Display Mode. Window, media, profile,
/// fullscreen, and Windows-HDR mutation commands deliberately do not exist here.
/// </summary>
public enum NeraCommandKind
{
    SetDisplay,
    EnableDldr,
    DisableDldr,
    SetMode,
    SetStrength,
    SetPerformanceMode,
    SetUiProtection,
    SetFeature18Tuning,
    SetDldrTuning,
    SetHoldOriginal,
    ShowHud,
    HideHud,
    ShowApp,
    HideApp,
    ConfigureRuntimeFolder,
    SetHotkey,
    RestoreHotkeys,
    SetAiPermissions,
    RunSelfTest,
    EmergencyStop,
    InitializeHotkeys,
    SetLanguage
}

public sealed record NeraCommandPayload
{
    public static NeraCommandPayload Empty { get; } = new();

    public string? DisplayId { get; init; }
    public string? LanguagePreference { get; init; }
    public NeraProcessingMode? Mode { get; init; }
    public NeraPerformanceMode? PerformanceMode { get; init; }
    public NeraUiProtectionMode? UiProtection { get; init; }
    public NeraFeature18Tuning? Feature18Tuning { get; init; }
    public NeraDldrTuning? DldrTuning { get; init; }
    public int? Strength { get; init; }
    public bool? HoldOriginal { get; init; }
    public string? RuntimeFolder { get; init; }
    public NeraHotkeyBinding? Hotkey { get; init; }
    public NeraAiControlPermissions? AiPermissions { get; init; }
    public JsonElement? Options { get; init; }

    public static NeraCommandPayload ForDisplay(string displayId) => new() { DisplayId = displayId };
    public static NeraCommandPayload ForMode(NeraProcessingMode mode) => new() { Mode = mode };
    public static NeraCommandPayload ForPerformanceMode(NeraPerformanceMode mode) =>
        new() { PerformanceMode = mode };
    public static NeraCommandPayload ForUiProtection(NeraUiProtectionMode mode) =>
        new() { UiProtection = mode };
    public static NeraCommandPayload ForStrength(int strength) => new() { Strength = strength };
    public static NeraCommandPayload ForFeature18Tuning(NeraFeature18Tuning tuning) =>
        new() { Feature18Tuning = tuning };
    public static NeraCommandPayload ForDldrTuning(NeraDldrTuning tuning) =>
        new() { DldrTuning = tuning };
    public static NeraCommandPayload ForHoldOriginal(bool held) => new() { HoldOriginal = held };
    public static NeraCommandPayload ForRuntimeFolder(string absolutePath) =>
        new() { RuntimeFolder = absolutePath };
}

public sealed record NeraCommandEnvelope
{
    public required string RequestId { get; init; }
    public required NeraCommandSource Source { get; init; }
    public required long ExpectedRevision { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required NeraCommandKind Kind { get; init; }
    public NeraCommandPayload Payload { get; init; } = NeraCommandPayload.Empty;
    public NeraInputOrigin? InputOrigin { get; init; }

    public static NeraCommandEnvelope Create(
        NeraCommandKind kind,
        NeraCommandSource source,
        long expectedRevision,
        NeraCommandPayload? payload = null,
        string? requestId = null,
        NeraInputOrigin? inputOrigin = null) => new()
    {
        RequestId = requestId ?? Guid.NewGuid().ToString("D"),
        Source = source,
        ExpectedRevision = expectedRevision,
        Timestamp = DateTimeOffset.UtcNow,
        Kind = kind,
        Payload = payload ?? NeraCommandPayload.Empty,
        InputOrigin = inputOrigin
    };
}

public sealed record NeraCommandResult
{
    public required bool Ok { get; init; }
    public required string RequestId { get; init; }
    public required long PreviousRevision { get; init; }
    public required long CurrentRevision { get; init; }
    public required NeraStateSnapshot State { get; init; }
    public required string Code { get; init; }
    public required string UserMessage { get; init; }
    public string TechnicalMessage { get; init; } = string.Empty;
    public bool RollbackPerformed { get; init; }
    public string? OperationId { get; init; }
    public JsonElement? Data { get; init; }
}

public static class NeraControlCodes
{
    public const string Ok = "OK";
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string InvalidState = "INVALID_STATE";
    public const string StateChanged = "STATE_CHANGED";
    public const string RequestIdReused = "REQUEST_ID_REUSED";
    public const string DisplayRequired = "DISPLAY_REQUIRED";
    public const string DisplayUnavailable = "DISPLAY_UNAVAILABLE";
    public const string DisplayDisconnected = "DISPLAY_DISCONNECTED";
    public const string HdrSystemDisabled = "HDR_SYSTEM_DISABLED";
    public const string RtxGpuRequired = "RTX_GPU_REQUIRED";
    public const string RuntimeMissing = "RUNTIME_MISSING";
    public const string RuntimeUnverified = "RUNTIME_UNVERIFIED";
    public const string BrokerStartFailed = "BROKER_START_FAILED";
    public const string HostStartFailed = "HOST_START_FAILED";
    public const string FeatureCreateFailed = "FEATURE_CREATE_FAILED";
    public const string MonitorCaptureFailed = "MONITOR_CAPTURE_FAILED";
    public const string PresenterFailed = "PRESENTER_FAILED";
    public const string ForegroundChanged = "FOREGROUND_CHANGED";
    public const string FirstFrameGateFailed = "FIRST_FRAME_GATE_FAILED";
    public const string FailSafeFailed = "FAIL_SAFE_FAILED";
    public const string InterruptedByEmergencyStop = "INTERRUPTED_BY_EMERGENCY_STOP";
    public const string OperationFailed = "OPERATION_FAILED";
    public const string Timeout = "TIMEOUT";
    public const string PermissionDenied = "PERMISSION_DENIED";
    public const string ProtocolMismatch = "PROTOCOL_MISMATCH";
    public const string VersionMismatch = "VERSION_MISMATCH";
    public const string HandshakeRequired = "HANDSHAKE_REQUIRED";
    public const string MalformedJson = "MALFORMED_JSON";
    public const string MessageTooLarge = "MESSAGE_TOO_LARGE";
    public const string HotkeyConflict = "HOTKEY_CONFLICT";
    public const string HotkeyRegistrationFailed = "HOTKEY_REGISTRATION_FAILED";
    public const string HotkeyHoldUnsupported = "HOTKEY_HOLD_UNSUPPORTED";
    public const string HotkeyKeyboardOnly = "HOTKEY_KEYBOARD_ONLY";
    public const string HotkeyModifierRequired = "HOTKEY_MODIFIER_REQUIRED";
    public const string HotkeyReserved = "HOTKEY_RESERVED";
    public const string HotkeyInternalDuplicate = "HOTKEY_INTERNAL_DUPLICATE";
    public const string HotkeyPersistenceFailed = "HOTKEY_PERSISTENCE_FAILED";
    public const string HotkeyFixedEmergency = "HOTKEY_FIXED_EMERGENCY";
    public const string InputOriginRejected = "INPUT_ORIGIN_REJECTED";
}

public static class NeraCommandPolicy
{
    public static void Validate(NeraCommandEnvelope command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Enum.IsDefined(command.Kind) || !Enum.IsDefined(command.Source))
            throw new ArgumentException("命令类型和来源必须是已定义的枚举值。", nameof(command));
        if (string.IsNullOrWhiteSpace(command.RequestId) || command.RequestId.Length > 128 ||
            command.RequestId.Any(char.IsControl))
            throw new ArgumentException(
                "requestId 必须包含 1 到 128 个非控制字符。", nameof(command));
        if (command.ExpectedRevision < 0)
            throw new ArgumentException("expectedRevision 不能为负数。", nameof(command));
        if (command.Timestamp == default)
            throw new ArgumentException("必须提供 timestamp。", nameof(command));
        NeraInputOriginPolicy.Validate(command.InputOrigin);

        NeraCommandPayload payload = command.Payload ??
            throw new ArgumentException("必须提供 payload。", nameof(command));
        ValidatePayloadShape(command.Kind, payload);
        switch (command.Kind)
        {
            case NeraCommandKind.SetLanguage when command.Source is not (NeraCommandSource.Ui or NeraCommandSource.Test):
                throw new ArgumentException("Language selection is a local user preference.", nameof(command));
            case NeraCommandKind.SetLanguage when !ChipsStudio.Nera.Localization.NeraLocalizer.IsValidPreference(payload.LanguagePreference):
                throw new ArgumentException("Unsupported language preference.", nameof(command));
            case NeraCommandKind.SetDisplay:
                RequireText(payload.DisplayId, "displayId");
                break;
            case NeraCommandKind.SetMode when payload.Mode is null:
                throw new ArgumentException("必须提供 mode。", nameof(command));
            case NeraCommandKind.SetMode when !Enum.IsDefined(payload.Mode.Value):
                throw new ArgumentException("mode 无效。", nameof(command));
            case NeraCommandKind.SetPerformanceMode when payload.PerformanceMode is null:
                throw new ArgumentException("必须提供 performanceMode。", nameof(command));
            case NeraCommandKind.SetPerformanceMode when !Enum.IsDefined(payload.PerformanceMode.Value):
                throw new ArgumentException("performanceMode 无效。", nameof(command));
            case NeraCommandKind.SetUiProtection:
                throw new ArgumentException("界面保护已从正式产品移除。", nameof(command));
            case NeraCommandKind.SetDldrTuning:
                try
                {
                    NeraStatePolicy.ValidateDldrTuning(payload.DldrTuning!);
                }
                catch (InvalidDataException error)
                {
                    throw new ArgumentException("dldrTuning 无效。", nameof(command), error);
                }
                break;
            case NeraCommandKind.SetFeature18Tuning when payload.Feature18Tuning is null:
                throw new ArgumentException("必须提供 feature18Tuning。", nameof(command));
            case NeraCommandKind.SetFeature18Tuning:
                try
                {
                    NeraStatePolicy.ValidateFeature18Tuning(payload.Feature18Tuning!);
                }
                catch (InvalidDataException error)
                {
                    throw new ArgumentException("feature18Tuning 无效。", nameof(command), error);
                }
                if (!payload.Feature18Tuning!.Custom)
                {
                    throw new ArgumentException(
                        "SetFeature18Tuning 只接受明确的自定义值；校准值请使用 SetMode。",
                        nameof(command));
                }
                break;
            case NeraCommandKind.SetStrength when payload.Strength is < 0 or > 100 or null:
                throw new ArgumentException("strength 必须在 0 到 100 之间。", nameof(command));
            case NeraCommandKind.SetHoldOriginal when payload.HoldOriginal is null:
                throw new ArgumentException("必须提供 holdOriginal。", nameof(command));
            case NeraCommandKind.ConfigureRuntimeFolder:
                RequireExistingLocalDirectory(payload.RuntimeFolder, "runtimeFolder");
                break;
            case NeraCommandKind.InitializeHotkeys when command.Source is not (NeraCommandSource.Ui or NeraCommandSource.Test):
                throw new ArgumentException("快捷键初始化仅用于应用启动。", nameof(command));
            case NeraCommandKind.SetHotkey when payload.Hotkey is null:
                throw new ArgumentException(
                    "必须提供快捷键操作和组合；0/0 表示清除。", nameof(command));
            case NeraCommandKind.SetHotkey when !Enum.IsDefined(payload.Hotkey.Action):
                throw new ArgumentException("快捷键操作无效。", nameof(command));
            case NeraCommandKind.SetAiPermissions when payload.AiPermissions is null:
                throw new ArgumentException("必须提供 aiPermissions。", nameof(command));
        }
    }

    private static void ValidatePayloadShape(NeraCommandKind kind, NeraCommandPayload payload)
    {
        if (kind == NeraCommandKind.SetLanguage)
        {
            if (payload.LanguagePreference is null || payload.DisplayId is not null || payload.Mode is not null ||
                payload.PerformanceMode is not null || payload.UiProtection is not null ||
                payload.Feature18Tuning is not null || payload.DldrTuning is not null || payload.Strength is not null ||
                payload.HoldOriginal is not null || payload.RuntimeFolder is not null || payload.Hotkey is not null ||
                payload.AiPermissions is not null || payload.Options is not null)
                throw new ArgumentException("SetLanguage payload must contain only languagePreference.", nameof(payload));
            return;
        }
        if (payload.LanguagePreference is not null)
            throw new ArgumentException("This command does not accept languagePreference.", nameof(payload));
        if (kind == NeraCommandKind.SetDldrTuning)
        {
            if (payload.DldrTuning is null || payload.DisplayId is not null || payload.Mode is not null ||
                payload.PerformanceMode is not null || payload.UiProtection is not null ||
                payload.Feature18Tuning is not null || payload.Strength is not null ||
                payload.HoldOriginal is not null || payload.RuntimeFolder is not null ||
                payload.Hotkey is not null || payload.AiPermissions is not null || payload.Options is not null)
                throw new ArgumentException("SetDldrTuning 的 payload 结构无效。", nameof(payload));
            return;
        }
        if (payload.DldrTuning is not null)
            throw new ArgumentException($"{kind} 不接受 dldrTuning。", nameof(payload));
        bool displayId = payload.DisplayId is not null;
        bool mode = payload.Mode is not null;
        bool performanceMode = payload.PerformanceMode is not null;
        bool uiProtection = payload.UiProtection is not null;
        bool feature18Tuning = payload.Feature18Tuning is not null;
        bool strength = payload.Strength is not null;
        bool holdOriginal = payload.HoldOriginal is not null;
        bool runtimeFolder = payload.RuntimeFolder is not null;
        bool hotkey = payload.Hotkey is not null;
        bool aiPermissions = payload.AiPermissions is not null;
        bool options = payload.Options is not null;

        bool valid = kind switch
        {
            NeraCommandKind.SetDisplay => displayId &&
                !(mode || performanceMode || uiProtection || feature18Tuning || strength || holdOriginal || runtimeFolder ||
                  hotkey || aiPermissions || options),
            NeraCommandKind.SetMode => mode &&
                !(displayId || performanceMode || uiProtection || feature18Tuning || strength || holdOriginal || runtimeFolder ||
                  hotkey || aiPermissions || options),
            NeraCommandKind.SetPerformanceMode => performanceMode &&
                !(displayId || mode || uiProtection || feature18Tuning || strength || holdOriginal || runtimeFolder || hotkey ||
                  aiPermissions || options),
            NeraCommandKind.SetUiProtection => uiProtection &&
                !(displayId || mode || performanceMode || feature18Tuning || strength || holdOriginal || runtimeFolder || hotkey ||
                   aiPermissions || options),
            NeraCommandKind.SetFeature18Tuning => feature18Tuning &&
                !(displayId || mode || performanceMode || uiProtection || strength || holdOriginal ||
                  runtimeFolder || hotkey || aiPermissions || options),
            NeraCommandKind.SetStrength => strength &&
                !(displayId || mode || performanceMode || uiProtection || feature18Tuning || holdOriginal || runtimeFolder ||
                  hotkey || aiPermissions || options),
            NeraCommandKind.SetHoldOriginal => holdOriginal &&
                !(displayId || mode || performanceMode || uiProtection || feature18Tuning || strength || runtimeFolder || hotkey ||
                  aiPermissions || options),
            NeraCommandKind.ConfigureRuntimeFolder => runtimeFolder &&
                !(displayId || mode || performanceMode || uiProtection || feature18Tuning || strength || holdOriginal || hotkey ||
                  aiPermissions || options),
            NeraCommandKind.SetHotkey => hotkey &&
                !(displayId || mode || performanceMode || uiProtection || feature18Tuning || strength || holdOriginal ||
                  runtimeFolder || aiPermissions || options),
            NeraCommandKind.SetAiPermissions => aiPermissions &&
                !(displayId || mode || performanceMode || uiProtection || feature18Tuning || strength || holdOriginal ||
                  runtimeFolder || hotkey || options),
            NeraCommandKind.RunSelfTest => !(displayId || mode || performanceMode || uiProtection || feature18Tuning || strength ||
                holdOriginal || runtimeFolder || hotkey || aiPermissions),
            _ => !(displayId || mode || performanceMode || uiProtection || feature18Tuning || strength || holdOriginal ||
                runtimeFolder || hotkey || aiPermissions || options)
        };
        if (!valid)
            throw new ArgumentException($"{kind} 的 payload 结构无效。", nameof(payload));
    }

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException(
                $"{name} 必须包含 1 到 512 个非控制字符。");
    }

    private static void RequireExistingLocalDirectory(string? path, string name)
    {
        if (!NeraStatePolicy.IsLocalAbsolutePath(path) || !Directory.Exists(path))
            throw new ArgumentException(
                $"{name} 必须指向现有且明确的本地目录。");
    }
}
