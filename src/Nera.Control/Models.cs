namespace ChipsStudio.Nera.Control;

public enum NeraDldrActualState
{
    Disabled,
    RuntimeMissing,
    RuntimeChecking,
    RuntimeVerified,
    HostStarting,
    FeatureCreating,
    WaitingForFirstFrame,
    Processing,
    Recovering,
    Bypass,
    Failed,
    DisplayChecking,
    HdrChecking,
    GpuChecking,
    CaptureCreating
}

public enum NeraProcessingMode
{
    Natural,
    Sharp,
    Cinema
}

public enum NeraPerformanceMode
{
    Quality,
    Balanced,
    Smooth
}

public enum NeraUiProtectionMode
{
    Auto,
    Off
}

/// <summary>
/// Product-safe Feature 18 controls verified for the signed 310.8.0.0 runtime.
/// Values outside these deliberately narrow domains are lab-only and cannot enter
/// the production control plane.
/// </summary>
public sealed record NeraFeature18Tuning
{
    public bool Custom { get; init; }
    public int Style { get; init; }
    public double Intensity { get; init; } = 1d;
    public double LocalToneStrength { get; init; } = 1d;
    public double LocalStructureStrength { get; init; } = 1d;
    /// <summary>
    /// Runtime 310.8 skin-region structure strength. -1 follows LocalStructureStrength;
    /// explicit values are meaningful only when automatic skin masking is enabled.
    /// </summary>
    public double SkinStructureStrength { get; init; } = -1d;
    public bool UseAutoMask { get; init; }

    /// <summary>
    /// Canonical product representation at the precision accepted by the native
    /// Runtime adapter. This prevents a valid high-precision JSON value from
    /// changing again when the float value is observed back from native state.
    /// </summary>
    public NeraFeature18Tuning CanonicalizedForNative() => this with
    {
        Intensity = (double)(float)Intensity,
        LocalToneStrength = (double)(float)LocalToneStrength,
        LocalStructureStrength = (double)(float)LocalStructureStrength,
        SkinStructureStrength = (double)(float)SkinStructureStrength
    };

    public static NeraFeature18Tuning Natural { get; } = new()
    {
        Style = 1,
        Intensity = 1d,
        LocalToneStrength = 1d,
        LocalStructureStrength = 1d,
        SkinStructureStrength = -1d,
        UseAutoMask = false
    };

    public static NeraFeature18Tuning Clear { get; } = new()
    {
        Style = 0,
        Intensity = 1d,
        LocalToneStrength = 1d,
        LocalStructureStrength = 1.5d,
        SkinStructureStrength = -1d,
        UseAutoMask = false
    };

    public static NeraFeature18Tuning Cinema { get; } = new()
    {
        Style = 2,
        Intensity = 1d,
        LocalToneStrength = 0.5d,
        LocalStructureStrength = 0.5d,
        SkinStructureStrength = -1d,
        UseAutoMask = true
    };

    public static NeraFeature18Tuning ForMode(NeraProcessingMode mode) => mode switch
    {
        NeraProcessingMode.Sharp => Clear,
        NeraProcessingMode.Cinema => Cinema,
        _ => Natural
    };
}

public enum NeraRecoveryState
{
    None,
    Recovering,
    BypassRestored,
    Failed
}

/// <summary>Canonical DLDR controls; the Host consumes these values without resolving recipes.</summary>
public sealed record NeraDldrTuning
{
    public static NeraDldrTuning FromState(NeraStateSnapshot state) => new()
    {
        HighlightProtection = state.DldrHighlightProtection,
        ShadowProtection = state.DldrShadowProtection,
        ChromaStrength = state.DldrChromaStrength,
        TemporalResponse = state.DldrTemporalResponse
    };
    public double HighlightProtection { get; init; } = (double)(float)0.90d;
    public double ShadowProtection { get; init; } = (double)(float)0.85d;
    public double ChromaStrength { get; init; } = 0.50d;
    public double TemporalResponse { get; init; } = (double)(float)0.35d;

    public NeraDldrTuning CanonicalizedForNative() => this with
    {
        HighlightProtection = (double)(float)HighlightProtection,
        ShadowProtection = (double)(float)ShadowProtection,
        ChromaStrength = (double)(float)ChromaStrength,
        TemporalResponse = (double)(float)TemporalResponse
    };

    public static NeraDldrTuning ForMode(NeraProcessingMode mode) => mode switch
    {
        NeraProcessingMode.Sharp => new() { HighlightProtection = .94, ShadowProtection = .88,
            ChromaStrength = .60, TemporalResponse = .28 },
        NeraProcessingMode.Cinema => new() { HighlightProtection = .96, ShadowProtection = .92,
            ChromaStrength = .40, TemporalResponse = .48 },
        _ => new()
    };
}

public static class NeraRecipe
{
    public static int StrengthForMode(NeraProcessingMode mode) => mode switch
    {
        NeraProcessingMode.Sharp => 71,
        NeraProcessingMode.Cinema => 56,
        _ => 65
    };

    // Schema <= 5 stored a pre-gain percentage. Convert once when migrating;
    // new preferences store the shader's actual effect strength directly.
    public static int MigrateLegacyStrength(NeraProcessingMode mode, int strength) =>
        Math.Clamp((int)Math.Round(strength * (mode switch
        {
            NeraProcessingMode.Sharp => 1.42d,
            NeraProcessingMode.Cinema => 1.12d,
            _ => 1.30d
        }), MidpointRounding.AwayFromZero), 0, 100);
}

public enum NeraHotkeyAction
{
    ToggleDldr,
    ToggleHud,
    ToggleAppVisibility,
    HoldOriginal,
    EmergencyStop,
    EmergencyStopFallback
}

public sealed record NeraHotkeyBinding
{
    public required NeraHotkeyAction Action { get; init; }
    public required uint Modifiers { get; init; }
    public required uint VirtualKey { get; init; }
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

    public static NeraAiControlPermissions Disabled { get; } = new();
}

public sealed record NeraPerformanceSnapshot
{
    // Availability is explicit. Zero is a measurement, not a substitute for unknown.
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

    public static NeraPerformanceSnapshot Empty { get; } = new();
}

public sealed record NeraErrorInfo
{
    public required string Code { get; init; }
    public string ReasonCode { get; init; } = "UNKNOWN";
    public required string UserMessage { get; init; }
    public string TechnicalMessage { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record NeraStateSnapshot
{
    public string Language { get; init; } = ChipsStudio.Nera.Localization.NeraLocalizer.Language;
    public string LanguagePreference { get; init; } = ChipsStudio.Nera.Localization.NeraLocalizer.Preference;
    public const string DefaultWorkingColorSpace = "FP16 linear scRGB";

    public long Revision { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
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
    public string WorkingColorSpace { get; init; } = DefaultWorkingColorSpace;
    public bool DldrRequested { get; init; }
    public NeraDldrActualState DldrActualState { get; init; } = NeraDldrActualState.Disabled;
    public NeraProcessingMode ProcessingMode { get; init; } = NeraProcessingMode.Natural;
    public bool RecipeCustom { get; init; }
    public double DldrHighlightProtection { get; init; } = (double)(float).90d;
    public double DldrShadowProtection { get; init; } = (double)(float).85d;
    public double DldrChromaStrength { get; init; } = .50d;
    public double DldrTemporalResponse { get; init; } = (double)(float).35d;
    public bool Feature18Custom { get; init; }
    public int Feature18Style { get; init; } = 1;
    public double Feature18Intensity { get; init; } = 1d;
    public double Feature18LocalToneStrength { get; init; } = 1d;
    public double Feature18LocalStructureStrength { get; init; } = 1d;
    public double Feature18SkinStructureStrength { get; init; } = -1d;
    public bool Feature18UseAutoMask { get; init; }
    public int Strength { get; init; } = 65;
    public int NeuralScale { get; init; } = 100;
    public NeraPerformanceMode PerformanceMode => NeuralScale switch
    {
        80 => NeraPerformanceMode.Smooth,
        90 => NeraPerformanceMode.Balanced,
        _ => NeraPerformanceMode.Quality
    };
    // Read-compatible legacy field only. Heuristic UI protection is not a product control.
    [System.Text.Json.Serialization.JsonIgnore]
    public NeraUiProtectionMode UiProtection { get; init; } = NeraUiProtectionMode.Off;
    public bool HudVisible { get; init; }
    public bool AppVisible { get; init; } = true;
    public bool HoldOriginal { get; init; }
    public int NeuralInputWidth { get; init; }
    public int NeuralInputHeight { get; init; }
    public IReadOnlyList<NeraHotkeyBinding> Hotkeys { get; init; } = [];
    public NeraAiControlPermissions AiPermissions { get; init; } = NeraAiControlPermissions.Disabled;
    public NeraPerformanceSnapshot Performance { get; init; } = NeraPerformanceSnapshot.Empty;
    public ulong LastSuccessfulFrameId { get; init; }
    public ulong LastFeature18FrameId { get; init; }
    public ulong LastDldrFrameId { get; init; }
    public ulong LastPresentedFrameId { get; init; }
    public ulong Feature18SuccessfulFrames { get; init; }
    public ulong DldrSuccessfulFrames { get; init; }
    public ulong PresentedFrames { get; init; }
    public NeraErrorInfo? LastError { get; init; }
    public NeraRecoveryState RecoveryState { get; init; }
    public bool GpuVerified { get; init; }
    /// <summary>
    /// True while the native authority still owns the RuntimeBroker process.
    /// This is cleanup ownership, not readiness; BrokerConnected remains the
    /// strict transport/handshake/capability/heartbeat readiness projection.
    /// </summary>
    public bool BrokerProcessOwned { get; init; }
    public bool BrokerConnected { get; init; }
    public bool HostConnected { get; init; }
    // Monotonic within the native controller lifetime. Frame IDs are local to a Host incarnation.
    public ulong HostSessionGeneration { get; init; }
    public NeraNativeLifecycleHealth NativeLifecycleHealth { get; init; } = NeraNativeLifecycleHealth.Unknown;
    public bool ProtectedRecoveryPending { get; init; }
    public bool MonitorCaptureCreated { get; init; }
    public bool FeatureCreated { get; init; }
    public bool FirstFeature18FrameSucceeded { get; init; }
    public bool DldrSucceeded { get; init; }
    public bool PresenterSucceeded { get; init; }
    public bool ForegroundPreserved { get; init; }
    public bool ProcessingActive { get; init; }
    /// <summary>
    /// True only after the native Presenter/Capture/Feature/Host OFF gate passes.
    /// Normal desktop visibility alone is not complete release evidence.
    /// </summary>
    public bool OffGateSatisfied { get; init; } = true;
    // Warm OFF is normal desktop + paused Evaluate, not Feature Release.
    public bool WarmStandby { get; init; }
    public bool WarmOffGateSatisfied { get; init; }
    public bool OverlayBypass { get; init; }
    public ulong AppliedParameterRevision { get; init; }
    public ulong AppliedParameterFrameId { get; init; }
    public bool DldrOn { get; init; }

    public static NeraStateSnapshot CreateInitial(
        string appVersion = "0.4.1-global-alpha.1",
        string runtimeAdapterVersion = "0.4.1") => new()
    {
        AppVersion = appVersion,
        RuntimeAdapterVersion = runtimeAdapterVersion
    };
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056:URI-like properties should not be strings",
    Justification = "Display identifiers are opaque Windows values, not URIs.")]
public sealed record NeraDisplaySummary
{
    public required string DisplayId { get; init; }
    public required string DisplayName { get; init; }
    public string? SourceName { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public uint RefreshNumerator { get; init; }
    public uint RefreshDenominator { get; init; } = 1;
    public double RefreshRateHz => RefreshDenominator == 0
        ? 0
        : RefreshNumerator / (double)RefreshDenominator;
    public bool HdrSupported { get; init; }
    public bool HdrEnabled { get; init; }
    public bool AdvancedColorEnabled { get; init; }
    public double? SdrWhiteLevelNits { get; init; }
    public required string AdapterLuid { get; init; }
    public string? TargetId { get; init; }
    public bool Connected { get; init; } = true;
    public bool Selected { get; init; }
    public bool Capturable { get; init; }
}

public static class NeraStatePolicy
{
    public const string SupportedRuntimeVersion = "310.8.0.0";
    public const string SupportedRuntimeSha256 =
        "E16BCF15E16E13F527491CDF7845B2FE6521A738D8F7C9C721866A8496E1FC8E";
    public const string SupportedRuntimeSigner = "NVIDIA Corporation";

    public static bool IsFirstFrameGateSatisfied(NeraStateSnapshot state) =>
        state.DldrActualState == NeraDldrActualState.Processing &&
        state.DldrRequested &&
        state.TargetDisplay is { Connected: true, Capturable: true } &&
        state.HdrSystemEnabled &&
        state.GpuVerified &&
        state.RuntimeVerified &&
        state.BrokerProcessOwned &&
        state.BrokerConnected &&
        state.HostConnected &&
        state.MonitorCaptureCreated &&
        state.FeatureCreated &&
        state.FirstFeature18FrameSucceeded &&
        state.DldrSucceeded &&
        state.PresenterSucceeded &&
        state.ForegroundPreserved &&
        state.ProcessingActive &&
        state.LastSuccessfulFrameId > 0 &&
        state.LastFeature18FrameId == state.LastSuccessfulFrameId &&
        state.LastDldrFrameId == state.LastSuccessfulFrameId &&
        state.LastPresentedFrameId == state.LastSuccessfulFrameId &&
        state.Feature18SuccessfulFrames > 0 &&
        state.DldrSuccessfulFrames > 0 &&
        state.PresentedFrames > 0;

    public static NeraStateSnapshot WithComputedDldrGate(NeraStateSnapshot state) =>
        state with { DldrOn = IsFirstFrameGateSatisfied(state) };

    public static void Validate(NeraStateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Revision < 0)
        {
            throw new InvalidDataException("状态修订号不能为负数。");
        }
        if (string.IsNullOrWhiteSpace(state.AppVersion) || string.IsNullOrWhiteSpace(state.RuntimeAdapterVersion))
        {
            throw new InvalidDataException("必须提供应用版本和运行时适配器版本。");
        }
        if (state.InputWidth < 0 || state.InputHeight < 0 || state.OutputWidth < 0 || state.OutputHeight < 0 ||
            state.NeuralInputWidth < 0 || state.NeuralInputHeight < 0 ||
            !double.IsFinite(state.InputFps) || !double.IsFinite(state.OutputFps) ||
            state.InputFps < 0 || state.OutputFps < 0)
        {
            throw new InvalidDataException("尺寸和帧率不能为负数。");
        }
        if (state.Strength is < 0 or > 100)
        {
            throw new InvalidDataException("强度必须在 0 到 100 之间。");
        }
        ValidateFeature18Tuning(new NeraFeature18Tuning
        {
            Custom = state.Feature18Custom,
            Style = state.Feature18Style,
            Intensity = state.Feature18Intensity,
            LocalToneStrength = state.Feature18LocalToneStrength,
            LocalStructureStrength = state.Feature18LocalStructureStrength,
            SkinStructureStrength = state.Feature18SkinStructureStrength,
            UseAutoMask = state.Feature18UseAutoMask
        });
        ValidateDldrTuning(new NeraDldrTuning
        {
            HighlightProtection = state.DldrHighlightProtection,
            ShadowProtection = state.DldrShadowProtection,
            ChromaStrength = state.DldrChromaStrength,
            TemporalResponse = state.DldrTemporalResponse
        });
        if (state.NeuralScale is not (80 or 90 or 100))
        {
            throw new InvalidDataException("神经输入比例必须明确设为支持的值：80、90 或 100。");
        }
        if (!string.Equals(state.WorkingColorSpace, NeraStateSnapshot.DefaultWorkingColorSpace,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("全局显示管线仅支持 FP16 线性 scRGB。");
        }
        ValidateDisplays(state);
        if (state.RuntimeVerified &&
            !HasVerifiedRuntimeIdentity(state.RuntimeBinaryVersion, state.RuntimeSha256,
                state.RuntimeSignatureValid, state.RuntimeSigner))
        {
            throw new InvalidDataException("已验证的运行时必须具有版本和 64 字符 SHA-256。");
        }
        if (state.DldrActualState == NeraDldrActualState.Processing && !IsFirstFrameGateSatisfied(state))
        {
            throw new InvalidDataException(
                "同一帧通过 Feature 18、DLDR 和显示层之前，不允许进入处理状态。");
        }
        if (state.DldrActualState != NeraDldrActualState.Processing &&
            (state.ProcessingActive || state.DldrOn))
        {
            throw new InvalidDataException("非处理状态不能报告 processingActive 或 DLDR ON。");
        }
        if (state.DldrOn != IsFirstFrameGateSatisfied(state))
        {
            throw new InvalidDataException("DldrOn 必须完全由全局首帧门禁推导。");
        }
        if (state.WarmStandby && (state.DldrRequested || state.ProcessingActive || state.DldrOn ||
            !state.WarmOffGateSatisfied || !state.HostConnected || !state.FeatureCreated ||
            !state.BrokerConnected || !state.BrokerProcessOwned || state.PresenterSucceeded))
            throw new InvalidDataException("暖待机必须有暂停处理与原画恢复证据，不能报告 DLDR ON。");
        if (!state.WarmStandby && state.WarmOffGateSatisfied)
            throw new InvalidDataException("暖关闭门禁不能脱离暖待机状态。");
        if (!state.WarmStandby &&
            state.DldrActualState is (NeraDldrActualState.Disabled or NeraDldrActualState.RuntimeMissing or NeraDldrActualState.Bypass) &&
            (state.BrokerProcessOwned || state.BrokerConnected || state.HostConnected ||
             state.MonitorCaptureCreated || state.FeatureCreated || state.PresenterSucceeded || !state.OffGateSatisfied))
        {
            throw new InvalidDataException("静止状态必须完整释放资源，或通过显式暖待机门禁。");
        }
    }

    public static void ValidateFeature18Tuning(NeraFeature18Tuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        if (tuning.Style is < 0 or > 2)
        {
            throw new InvalidDataException("Feature 18 风格必须是 0、1 或 2。");
        }
        if (!double.IsFinite(tuning.Intensity) || tuning.Intensity is < 0d or > 1d)
        {
            throw new InvalidDataException("Feature 18 强度必须是 0 到 1 之间的有限数值。");
        }
        if (!double.IsFinite(tuning.LocalToneStrength) ||
            tuning.LocalToneStrength is < 0d or > 2d)
        {
            throw new InvalidDataException("Feature 18 局部明暗必须是 0 到 2 之间的有限数值。");
        }
        if (!double.IsFinite(tuning.LocalStructureStrength) ||
            tuning.LocalStructureStrength is < 0d or > 2d)
        {
            throw new InvalidDataException("Feature 18 局部细节必须是 0 到 2 之间的有限数值。");
        }
        if (!double.IsFinite(tuning.SkinStructureStrength) ||
            tuning.SkinStructureStrength is < -1d or > 2d)
        {
            throw new InvalidDataException("Feature 18 皮肤细节必须是 -1 到 2 之间的有限数值。");
        }
    }

    public static void ValidateDldrTuning(NeraDldrTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        if (!double.IsFinite(tuning.HighlightProtection) || tuning.HighlightProtection is < 0 or > 1 ||
            !double.IsFinite(tuning.ShadowProtection) || tuning.ShadowProtection is < 0 or > 1 ||
            !double.IsFinite(tuning.ChromaStrength) || tuning.ChromaStrength is < 0 or > 1 ||
            !double.IsFinite(tuning.TemporalResponse) || tuning.TemporalResponse is < 0 or > 1)
            throw new InvalidDataException("DLDR 高级参数必须是 0 到 1 之间的有限数值。");
    }

    public static bool CanTransition(NeraStateSnapshot from, NeraStateSnapshot to)
    {
        bool verifiedEnvironment = from.RuntimeVerified && to.RuntimeVerified &&
            from.RuntimeSha256 == to.RuntimeSha256 && from.RuntimeBinaryVersion == to.RuntimeBinaryVersion &&
            from.TargetDisplay?.DisplayId is not null && from.TargetDisplay.DisplayId == to.TargetDisplay?.DisplayId;
        bool retainedIdentity = verifiedEnvironment && from.HostSessionGeneration == to.HostSessionGeneration &&
            from.BrokerProcessOwned && from.BrokerConnected && from.HostConnected && from.FeatureCreated &&
            to.BrokerProcessOwned && to.BrokerConnected && to.HostConnected && to.FeatureCreated;
        // Observation is sampled, not an event stream: a short Overlay yield can start and finish
        // between polls. Revoking ON to await fresh evidence is safe and must not itself tear down
        // a healthy native session. It still cannot turn Processing back on using an old frame.
        if (retainedIdentity && from.DldrActualState == NeraDldrActualState.Processing &&
            from.DldrRequested && to.DldrRequested && !to.OverlayBypass &&
            to.DldrActualState == NeraDldrActualState.WaitingForFirstFrame &&
            !to.ProcessingActive && !to.DldrOn &&
            to.LastSuccessfulFrameId >= from.LastSuccessfulFrameId)
            return true;
        // Protected-content recovery creates a new, owned Host. A new incarnation may restart
        // FrameId at one; compare generations, not unrelated frame counters. No generic
        // Recovering -> Processing exemption is allowed for unknown recovery or changed identity.
        if (verifiedEnvironment && from.DldrRequested && to.DldrRequested &&
            from.DldrActualState == NeraDldrActualState.Recovering && from.ProtectedRecoveryPending &&
            !from.DldrOn && to.HostSessionGeneration > 0 &&
            (to.HostSessionGeneration > from.HostSessionGeneration ||
                to.HostSessionGeneration == from.HostSessionGeneration &&
                to.LastSuccessfulFrameId > from.LastSuccessfulFrameId) &&
            to.DldrActualState == NeraDldrActualState.Processing && !to.OverlayBypass &&
            !to.ProtectedRecoveryPending && IsFirstFrameGateSatisfied(to))
            return true;
        if (from.DldrActualState == NeraDldrActualState.WaitingForFirstFrame &&
            to.DldrActualState == NeraDldrActualState.Processing)
            // Cold startup and an expired warm Host both receive a new owned generation.
            // A frame from that generation may start at one; no zero/older incarnation or
            // same-generation stale watermark can unlock ON.
            return verifiedEnvironment && from.DldrRequested && to.DldrRequested &&
                from.BrokerProcessOwned && from.BrokerConnected &&
                !to.OverlayBypass && !to.ProtectedRecoveryPending &&
                IsFirstFrameGateSatisfied(to) && to.HostSessionGeneration > 0 &&
                (to.HostSessionGeneration > from.HostSessionGeneration ||
                 retainedIdentity && to.LastSuccessfulFrameId > from.LastSuccessfulFrameId);
        if (CanTransition(from.DldrActualState, to.DldrActualState)) return true;
        // Resource retention is a distinct lifecycle, not permission to skip a cold first-frame gate.
        if (retainedIdentity && from.WarmStandby && from.WarmOffGateSatisfied &&
            !from.DldrRequested && !from.ProcessingActive && !from.DldrOn &&
            from.DldrActualState is NeraDldrActualState.Disabled or NeraDldrActualState.Bypass &&
            to.DldrActualState == NeraDldrActualState.WaitingForFirstFrame &&
            to.DldrRequested && !to.WarmStandby && !to.WarmOffGateSatisfied &&
            !to.ProcessingActive && !to.FirstFeature18FrameSucceeded && !to.DldrSucceeded && !to.PresenterSucceeded)
            return true;
        if (retainedIdentity && from.DldrActualState == NeraDldrActualState.Recovering &&
            from.OverlayBypass && from.DldrRequested && !from.DldrOn &&
            to.DldrActualState == NeraDldrActualState.WaitingForFirstFrame &&
            to.DldrRequested && !to.OverlayBypass && !to.ProcessingActive &&
            !to.DldrOn)
            // The first restored Present can finish before the controller's consecutive
            // successful-frame gate. Partial Presenter proof is valid while still OFF;
            // Waiting -> Processing below retains the full gate and fresh watermark.
            return true;
        // An observed overlay return is allowed only on a new, fully confirmed display frame.
        return retainedIdentity && from.DldrActualState == NeraDldrActualState.Recovering &&
            from.OverlayBypass && from.DldrRequested && !from.DldrOn &&
            to.DldrActualState == NeraDldrActualState.Processing && !to.OverlayBypass &&
            IsFirstFrameGateSatisfied(to) && to.LastSuccessfulFrameId > from.LastSuccessfulFrameId;
    }

    public static bool CanTransition(NeraDldrActualState from, NeraDldrActualState to)
    {
        if (from == to)
        {
            return true;
        }
        if (to is NeraDldrActualState.Recovering or NeraDldrActualState.Failed)
        {
            return true;
        }
        return from switch
        {
            NeraDldrActualState.Disabled => to is NeraDldrActualState.DisplayChecking or
                NeraDldrActualState.RuntimeChecking or NeraDldrActualState.RuntimeVerified or
                NeraDldrActualState.Bypass,
            NeraDldrActualState.RuntimeMissing => to is NeraDldrActualState.DisplayChecking or
                NeraDldrActualState.RuntimeChecking or NeraDldrActualState.RuntimeVerified or
                NeraDldrActualState.Disabled,
            NeraDldrActualState.DisplayChecking => to is NeraDldrActualState.HdrChecking,
            NeraDldrActualState.HdrChecking => to is NeraDldrActualState.GpuChecking,
            NeraDldrActualState.GpuChecking => to is NeraDldrActualState.RuntimeChecking,
            NeraDldrActualState.RuntimeChecking => to is NeraDldrActualState.RuntimeMissing or
                NeraDldrActualState.RuntimeVerified,
            NeraDldrActualState.RuntimeVerified => to is NeraDldrActualState.HostStarting or
                NeraDldrActualState.Disabled,
            NeraDldrActualState.HostStarting => to is NeraDldrActualState.CaptureCreating or
                NeraDldrActualState.FeatureCreating,
            NeraDldrActualState.CaptureCreating => to is NeraDldrActualState.FeatureCreating,
            NeraDldrActualState.FeatureCreating => to is NeraDldrActualState.WaitingForFirstFrame,
            NeraDldrActualState.WaitingForFirstFrame => to is NeraDldrActualState.Processing,
            NeraDldrActualState.Processing => to is NeraDldrActualState.Bypass or NeraDldrActualState.Disabled,
            NeraDldrActualState.Recovering => to is NeraDldrActualState.Bypass or NeraDldrActualState.Disabled,
            NeraDldrActualState.Bypass => to is NeraDldrActualState.DisplayChecking or
                NeraDldrActualState.RuntimeChecking or NeraDldrActualState.RuntimeVerified or
                NeraDldrActualState.Disabled,
            NeraDldrActualState.Failed => to is NeraDldrActualState.DisplayChecking or
                NeraDldrActualState.RuntimeChecking or NeraDldrActualState.RuntimeVerified or
                NeraDldrActualState.Bypass or
                NeraDldrActualState.Disabled,
            _ => false
        };
    }

    public static void ValidateDisplay(NeraDisplaySummary display)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (string.IsNullOrWhiteSpace(display.DisplayId) || display.DisplayId.Length > 512 ||
            display.DisplayId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(display.DisplayName) || display.DisplayName.Length > 512 ||
            display.DisplayName.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(display.AdapterLuid) || display.AdapterLuid.Length > 64 ||
            display.AdapterLuid.Any(char.IsControl))
        {
            throw new InvalidDataException("显示器身份字段缺失或格式错误。");
        }
        if (display.Width <= 0 || display.Height <= 0 || display.RefreshDenominator == 0 ||
            !double.IsFinite(display.RefreshRateHz) || display.RefreshRateHz <= 0 ||
            display.SdrWhiteLevelNits is double white && (!double.IsFinite(white) || white <= 0))
        {
            throw new InvalidDataException("显示器尺寸、刷新率或 SDR 白电平无效。");
        }
        if (display.HdrEnabled && (!display.HdrSupported || !display.AdvancedColorEnabled))
        {
            throw new InvalidDataException("已开启 HDR 的显示器必须报告 HDR 支持和高级颜色。");
        }
    }

    private static void ValidateDisplays(NeraStateSnapshot state)
    {
        foreach (var display in state.AvailableDisplays)
        {
            ValidateDisplay(display);
        }
        if (state.AvailableDisplays.GroupBy(display => display.DisplayId, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
        {
            throw new InvalidDataException("显示器标识必须唯一。");
        }
        if (state.TargetDisplay is not null)
        {
            ValidateDisplay(state.TargetDisplay);
            if (!state.TargetDisplay.Selected)
            {
                throw new InvalidDataException("TargetDisplay 必须带有 selected=true。");
            }
            if (state.AvailableDisplays.Count > 0 && !state.AvailableDisplays.Any(display =>
                    string.Equals(display.DisplayId, state.TargetDisplay.DisplayId, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("TargetDisplay 必须存在于 AvailableDisplays 中。");
            }
        }
    }

    public static bool HasVerifiedRuntimeIdentity(
        string? version,
        string? sha256,
        bool signatureValid,
        string? signer) =>
        string.Equals(version, SupportedRuntimeVersion, StringComparison.Ordinal) &&
        string.Equals(sha256, SupportedRuntimeSha256, StringComparison.OrdinalIgnoreCase) &&
        signatureValid && string.Equals(signer, SupportedRuntimeSigner, StringComparison.Ordinal);

    public static bool IsLocalAbsolutePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
        !path.StartsWith("\\\\", StringComparison.Ordinal) &&
        !path.StartsWith("\\?\\", StringComparison.Ordinal) &&
        !path.StartsWith("\\.\\", StringComparison.Ordinal);

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => char.IsAsciiHexDigit(character));
}
