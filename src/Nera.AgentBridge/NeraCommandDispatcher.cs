using System.Text.Json;
using ChipsStudio.Nera.Localization;
using System.Text.Json.Nodes;

namespace ChipsStudio.Nera.AgentBridge;

public sealed class NeraCommandDispatcher
{
    private readonly INeraControlTransport transport_;

    public NeraCommandDispatcher(INeraControlTransport transport) =>
        transport_ = transport ?? throw new ArgumentNullException(nameof(transport));

    public async Task<NeraCommandResult> InvokeToolAsync(
        string toolName,
        JsonElement arguments,
        string source,
        CancellationToken cancellationToken)
    {
        if (!ToolCatalog.TryGet(toolName, out var definition))
            throw new ToolArgumentException($"未知工具：{toolName}");
        if (source is not (NeraControlProtocol.AgentSource or NeraControlProtocol.CliSource or
            NeraControlProtocol.OrchestratorSource))
            throw new ArgumentOutOfRangeException(nameof(source));

        var normalized = ToolArgumentValidator.ValidateAndNormalize(
            definition, arguments, out var expectedRevision);

        return toolName switch
        {
            "nera_get_displays" => await SendAsync(
                "getDisplays", new(), null, source, cancellationToken).ConfigureAwait(false),
            "nera_get_performance" => AddData(await SendAsync(
                "getStatus", new(), null, source, cancellationToken).ConfigureAwait(false),
                state => new { performance = state.Performance }),
            "nera_recommend_settings" => AddData(await SendAsync(
                "getStatus", new(), null, source, cancellationToken).ConfigureAwait(false),
                BuildRecommendation),
            "nera_get_diagnostics" => AddData(await SendAsync(
                "getStatus", new(), null, source, cancellationToken).ConfigureAwait(false),
                state => new
                {
                    diagnostics = new
                    {
                        state.RuntimeVerified,
                        state.GpuVerified,
                        state.BrokerProcessOwned,
                        state.BrokerConnected,
                        state.HostConnected,
                        state.MonitorCaptureCreated,
                        state.FeatureCreated,
                        state.FirstFeature18FrameSucceeded,
                        state.DldrSucceeded,
                        state.PresenterSucceeded,
                        state.ForegroundPreserved,
                        state.ProcessingActive,
                        state.OffGateSatisfied,
                        state.DldrActualState,
                        state.DldrOn,
                        state.LastSuccessfulFrameId,
                        state.LastError,
                        state.RecoveryState
                    }
                }),
            _ => await SendAsync(WireCommand(toolName, normalized),
                WirePayload(toolName, normalized), expectedRevision, source,
                cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<NeraCommandResult> SendAsync(
        string command,
        JsonObject payload,
        long? expectedRevision,
        string source,
        CancellationToken cancellationToken)
    {
        var envelope = new NeraCommandEnvelope
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Source = source,
            ExpectedRevision = expectedRevision,
            Timestamp = DateTimeOffset.UtcNow,
            Command = command,
            Payload = payload
        };
        var result = await transport_.SendAsync(envelope,
            TimeSpan.FromMilliseconds(NeraControlProtocol.MaximumTimeoutMilliseconds),
            cancellationToken).ConfigureAwait(false);
        if (result.State is { } state && NeraLocalizer.IsValidPreference(state.LanguagePreference))
            NeraLocalizer.SetLanguage(state.LanguagePreference, state.Language);
        string localized = NeraLocalizer.LocalizeMessage(result.UserMessage,
            LocalizedErrorCode(result.Code), result.Ok);
        return result with { UserMessage = localized, LocalizedMessage = localized };
    }

    internal static string LocalizedErrorCode(string code) => code switch
    {
        NeraErrorCodes.RevisionConflict => "STATE_CHANGED",
        NeraErrorCodes.RuntimeUnavailable => "RUNTIME_MISSING",
        _ => code.StartsWith("NERA_", StringComparison.Ordinal) ? code[5..] : code
    };

    private static string WireCommand(string toolName, JsonObject arguments) => toolName switch
    {
        "nera_get_status" => "getStatus",
        "nera_set_display" => "setDisplay",
        "nera_enable_dldr" => "enableDldr",
        "nera_disable_dldr" => "disableDldr",
        "nera_set_mode" => "setMode",
        "nera_set_strength" => "setStrength",
        "nera_set_performance_mode" => "setPerformanceMode",
        "nera_set_nr_parameters" => "setFeature18Tuning",
        "nera_set_dldr_parameters" => "setDldrTuning",
        "nera_set_hud" => arguments["enabled"]!.GetValue<bool>() ? "showHud" : "hideHud",
        "nera_run_self_test" => "runSelfTest",
        "nera_emergency_stop" => "emergencyStop",
        _ => throw new ToolArgumentException($"工具“{toolName}”没有受限的控制映射。")
    };

    private static JsonObject WirePayload(string toolName, JsonObject arguments) => toolName switch
    {
        "nera_set_display" => new() { ["displayId"] = arguments["display_id"]!.GetValue<string>() },
        "nera_set_mode" => new()
        {
            ["mode"] = arguments["mode"]!.GetValue<string>() switch
            {
                "natural" => "natural",
                "clear" => "clear",
                "cinema" => "cinema",
                _ => throw new ToolArgumentException("不支持此画面模式。")
            }
        },
        "nera_set_strength" => new()
        {
            ["strength"] = checked((int)arguments["strength"]!.GetValue<long>())
        },
        "nera_set_performance_mode" => new()
        {
            ["performanceMode"] = arguments["performance_mode"]!.GetValue<string>() switch
            {
                "quality" => "quality",
                "balanced" => "balanced",
                "smooth" => "smooth",
                _ => throw new ToolArgumentException("不支持此性能档位。")
            }
        },
        "nera_set_nr_parameters" => new()
        {
            ["feature18Tuning"] = new JsonObject
            {
                ["custom"] = true,
                ["style"] = arguments["style"]!.DeepClone(),
                ["intensity"] = arguments["intensity"]!.DeepClone(),
                ["localToneStrength"] = arguments["local_tone"]!.DeepClone(),
                ["localStructureStrength"] = arguments["local_structure"]!.DeepClone(),
                ["skinStructureStrength"] = arguments["skin_structure"]!.DeepClone(),
                ["useAutoMask"] = arguments["automatic_skin_mask"]!.DeepClone()
            }
        },
        "nera_set_dldr_parameters" => new()
        {
            ["dldrTuning"] = new JsonObject
            {
                ["highlightProtection"] = arguments["highlight_protection"]!.DeepClone(),
                ["shadowProtection"] = arguments["shadow_protection"]!.DeepClone(),
                ["chromaStrength"] = arguments["chroma_strength"]!.DeepClone(),
                ["temporalResponse"] = arguments["temporal_response"]!.DeepClone()
            }
        },
        "nera_run_self_test" when arguments["scope"] is not null => new()
        {
            ["options"] = new JsonObject { ["scope"] = arguments["scope"]!.GetValue<string>() }
        },
        _ => new()
    };

    private static NeraCommandResult AddData(
        NeraCommandResult result, Func<NeraStateSnapshot, object> projection) =>
        !result.Ok ? result : result with
        {
            Data = JsonSerializer.SerializeToElement(projection(result.State!), JsonSupport.Compact)
        };

    private static object BuildRecommendation(NeraStateSnapshot state)
    {
        double displayRefresh = state.TargetDisplay is { RefreshRateHz: > 0.0 }
            ? state.TargetDisplay.RefreshRateHz : 0.0;
        double targetFps = FirstPositive(displayRefresh, state.InputFps,
            state.Performance.InputFramesPerSecond, state.OutputFps, 60.0);
        double budgetMilliseconds = 1000.0 / Math.Clamp(targetFps, 1.0, 1000.0);
        double featureMilliseconds = Math.Max(0.0, state.Performance.Feature18Milliseconds);
        double gpuPercent = Math.Clamp(state.Performance.GpuPercent, 0.0, 100.0);
        double processingFps = Math.Max(0.0, state.Performance.ProcessingFramesPerSecond);
        int width = state.TargetDisplay is { Width: > 0 }
            ? state.TargetDisplay.Width : Math.Max(state.InputWidth, state.OutputWidth);
        int height = state.TargetDisplay is { Height: > 0 }
            ? state.TargetDisplay.Height : Math.Max(state.InputHeight, state.OutputHeight);
        bool hasTiming = featureMilliseconds > 0.0;
        bool hasGpu = gpuPercent > 0.0;
        bool hasThroughput = processingFps > 0.0;
        bool throughputBehind = hasThroughput && processingFps < targetFps * 0.98;
        bool highResolutionHighRefresh = width >= 3_200 && height >= 1_800 && targetFps >= 50.0;
        bool stronglyConstrained =
            (hasTiming && featureMilliseconds >= budgetMilliseconds * 0.85) ||
            (hasGpu && gpuPercent >= 85.0) || throughputBehind ||
            state.Performance.DroppedFrames > 0 || state.Performance.BypassFrames > 0;
        bool moderatelyConstrained =
            (hasTiming && featureMilliseconds >= budgetMilliseconds * 0.70) ||
            (hasGpu && gpuPercent >= 75.0);

        int neuralScale;
        string performanceMode;
        string expectedEffect;
        if (!hasTiming && !hasGpu && !hasThroughput)
        {
            neuralScale = state.NeuralScale is 80 or 90 or 100 ? state.NeuralScale : 100;
            performanceMode = neuralScale switch
            {
                80 => "smooth",
                90 => "balanced",
                _ => "quality"
            };
            expectedEffect = NeraLocalizer.Get("Recommendation.Wait");
        }
        else if (highResolutionHighRefresh && stronglyConstrained)
        {
            neuralScale = 80;
            performanceMode = "smooth";
            expectedEffect = NeraLocalizer.Get("Recommendation.Smooth");
        }
        else if (stronglyConstrained || (highResolutionHighRefresh && moderatelyConstrained))
        {
            neuralScale = 90;
            performanceMode = "balanced";
            expectedEffect = NeraLocalizer.Get("Recommendation.Balanced");
        }
        else
        {
            neuralScale = 100;
            performanceMode = "quality";
            expectedEffect = NeraLocalizer.Get("Recommendation.Quality");
        }

        string confidence = hasTiming && hasGpu && hasThroughput
            ? "high"
            : hasTiming || hasGpu || hasThroughput ? "medium" : "low";
        string reason = !hasTiming && !hasGpu && !hasThroughput
            ? NeraLocalizer.Get("Recommendation.NoData")
            : NeraLocalizer.Get("Recommendation.Reason", width, height, targetFps, budgetMilliseconds, featureMilliseconds, gpuPercent, processingFps);

        return new
        {
            recommendation = new
            {
                performanceMode,
                neuralScale,
                targetFps = Math.Round(targetFps, 2),
                mode = state.ProcessingMode,
                strength = state.Strength
            },
            reason,
            confidence,
            expectedEffect,
            evidence = new
            {
                width,
                height,
                frameBudgetMilliseconds = Math.Round(budgetMilliseconds, 3),
                feature18Milliseconds = Math.Round(featureMilliseconds, 3),
                gpuPercent = Math.Round(gpuPercent, 2),
                processingFramesPerSecond = Math.Round(processingFps, 2),
                droppedFrames = state.Performance.DroppedFrames,
                bypassFrames = state.Performance.BypassFrames
            },
            advisoryOnly = true
        };
    }

    private static double FirstPositive(params double[] values) =>
        values.First(value => value > 0.0);
}
