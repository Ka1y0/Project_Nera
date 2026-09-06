using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using ChipsStudio.Nera.Localization;

namespace ChipsStudio.Nera.AgentBridge;

public sealed class CliApplication
{
    private readonly NeraCommandDispatcher dispatcher_;
    private readonly TextWriter output_;
    private readonly TextWriter error_;

    public CliApplication(NeraCommandDispatcher dispatcher, TextWriter output, TextWriter error)
    {
        dispatcher_ = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        output_ = output ?? throw new ArgumentNullException(nameof(output));
        error_ = error ?? throw new ArgumentNullException(nameof(error));
    }

    public async Task<int> RunAsync(
        IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0 || args[0] is "--help" or "-h")
        {
            WriteUsage(args.Count == 0 ? error_ : output_);
            return args.Count == 0 ? ExitCodes.Usage : ExitCodes.Success;
        }

        try
        {
            return args[0] switch
            {
                "status" => await RunJsonReadAsync(args, "nera_get_status", cancellationToken),
                "displays" => await RunJsonReadAsync(args, "nera_get_displays", cancellationToken),
                "display" => await RunDisplayAsync(args, cancellationToken),
                "dldr" => await RunToggleMutationAsync(args,
                    "nera_enable_dldr", "nera_disable_dldr", cancellationToken),
                "mode" => await RunEnumMutationAsync(args, "nera_set_mode", "mode",
                    ["natural", "clear", "cinema"], cancellationToken),
                "strength" => await RunStrengthAsync(args, cancellationToken),
                "performance" when args.Count == 1 || args is [_, "--json"] =>
                    await RunJsonReadAsync(args, "nera_get_performance", cancellationToken),
                "performance" => await RunEnumMutationAsync(args,
                    "nera_set_performance_mode", "performance_mode",
                    ["quality", "balanced", "smooth"], cancellationToken),
                "nr" => await RunParameterBundleAsync(args, neural: true, cancellationToken),
                "dldr-parameters" => await RunParameterBundleAsync(args, neural: false, cancellationToken),
                "hud" => await RunBooleanMutationAsync(args, "nera_set_hud", cancellationToken),
                "recommend" => await RunJsonReadAsync(args,
                    "nera_recommend_settings", cancellationToken),
                "self-test" => await RunSelfTestAsync(args, cancellationToken),
                "diagnostics" => await RunJsonReadAsync(args,
                    "nera_get_diagnostics", cancellationToken),
                "emergency-stop" => await RunMutationAsync(
                    args.Count == 1 ? "nera_emergency_stop" : null, new(), cancellationToken),
                _ => UsageFailure(NeraLocalizer.Get("Cli.UnknownCommand", args[0]))
            };
        }
        catch (ToolArgumentException error)
        {
            AgentBridgeErrorPresentation.TraceException("CLI_TOOL_ARGUMENT_REJECTED", error);
            return UsageFailure(NeraLocalizer.Get("Error.INVALID_ARGUMENT"));
        }
        catch (NeraTransportException error)
        {
            return await WriteExceptionFailureAsync(args, error.StableCode, error)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await error_.WriteLineAsync(
                NeraLocalizer.Get("Cli.Error", NeraErrorCodes.Cancelled, NeraLocalizer.Get("Error.CANCELLED"))).ConfigureAwait(false);
            return ExitCodes.TimeoutOrCancelled;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            return await WriteExceptionFailureAsync(args, NeraErrorCodes.OperationFailed, error)
                .ConfigureAwait(false);
        }
    }

    private async Task<int> RunJsonReadAsync(
        IReadOnlyList<string> args, string tool, CancellationToken cancellationToken)
    {
        if (args.Count > 2 || (args.Count == 2 && args[1] != "--json"))
            return UsageFailure(NeraLocalizer.Get("Cli.OptionalJson", args[0]));
        var result = await InvokeAsync(tool, new(), cancellationToken).ConfigureAwait(false);
        if (args.Count == 2)
        {
            await output_.WriteLineAsync(JsonSerializer.Serialize(result, JsonSupport.Indented))
                .ConfigureAwait(false);
            return ResultExitCode(result);
        }
        return await WriteHumanReadResultAsync(tool, result).ConfigureAwait(false);
    }

    private async Task<int> RunDisplayAsync(
        IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count != 2 || string.IsNullOrWhiteSpace(args[1]))
            return UsageFailure(NeraLocalizer.Get("Cli.DisplayArgument"));

        var listed = await InvokeAsync("nera_get_displays", new(), cancellationToken)
            .ConfigureAwait(false);
        if (!listed.Ok) return await WriteHumanResultAsync(listed).ConfigureAwait(false);
        IReadOnlyList<NeraDisplaySummary> displays = listed.Displays ?? [];
        string displayId;
        if (int.TryParse(args[1], out int index))
        {
            if (index < 1 || index > displays.Count)
                return UsageFailure(NeraLocalizer.Get("Cli.DisplayRange", displays.Count));
            displayId = displays[index - 1].DisplayId;
        }
        else
        {
            NeraDisplaySummary? display = displays.FirstOrDefault(candidate =>
                string.Equals(candidate.DisplayId, args[1], StringComparison.Ordinal));
            if (display is null)
                return UsageFailure(NeraLocalizer.Get("Cli.DisplayMissing"));
            displayId = display.DisplayId;
        }

        return await RunMutationAsync("nera_set_display",
            new() { ["display_id"] = displayId }, cancellationToken).ConfigureAwait(false);
    }

    private Task<int> RunToggleMutationAsync(
        IReadOnlyList<string> args, string onTool, string offTool,
        CancellationToken cancellationToken)
    {
        if (args.Count != 2 || args[1] is not ("on" or "off"))
            return Task.FromResult(UsageFailure(NeraLocalizer.Get("Cli.OnOff", args[0])));
        return RunMutationAsync(args[1] == "on" ? onTool : offTool, new(), cancellationToken);
    }

    private Task<int> RunBooleanMutationAsync(
        IReadOnlyList<string> args, string tool, CancellationToken cancellationToken)
    {
        if (args.Count != 2 || args[1] is not ("on" or "off"))
            return Task.FromResult(UsageFailure(NeraLocalizer.Get("Cli.OnOff", args[0])));
        return RunMutationAsync(tool,
            new() { ["enabled"] = args[1] == "on" }, cancellationToken);
    }

    private Task<int> RunEnumMutationAsync(
        IReadOnlyList<string> args, string tool, string property,
        IReadOnlyList<string> allowed, CancellationToken cancellationToken)
    {
        if (args.Count != 2 || !allowed.Contains(args[1], StringComparer.Ordinal))
            return Task.FromResult(UsageFailure(
                NeraLocalizer.Get("Cli.Enum", args[0], string.Join('|', allowed))));
        return RunMutationAsync(tool, new() { [property] = args[1] }, cancellationToken);
    }

    private Task<int> RunStrengthAsync(
        IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count != 2 || !int.TryParse(args[1], out int strength) ||
            strength is < 0 or > 100)
            return Task.FromResult(UsageFailure(
                NeraLocalizer.Get("Cli.StrengthArgument")));
        return RunMutationAsync("nera_set_strength",
            new() { ["strength"] = strength }, cancellationToken);
    }

    private Task<int> RunSelfTestAsync(
        IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count > 2)
            return Task.FromResult(UsageFailure(NeraLocalizer.Get("Cli.SelfTestArgument")));
        var payload = new JsonObject();
        if (args.Count == 2) payload["scope"] = args[1];
        return RunMutationAsync("nera_run_self_test", payload, cancellationToken);
    }

    private async Task<int> RunMutationAsync(
        string? tool, JsonObject payload, CancellationToken cancellationToken)
    {
        if (tool is null) return UsageFailure(NeraLocalizer.Get("Error.INVALID_ARGUMENT"));

        var status = await InvokeAsync("nera_get_status", new(), cancellationToken)
            .ConfigureAwait(false);
        if (!status.Ok) return await WriteHumanResultAsync(status).ConfigureAwait(false);
        if (!status.CurrentRevision.HasValue)
            throw new NeraTransportException(NeraErrorCodes.MalformedResponse,
                "The successful response omitted its revision.");

        payload["expected_revision"] = status.CurrentRevision.Value;
        var result = await InvokeAsync(tool, payload, cancellationToken).ConfigureAwait(false);
        return await WriteHumanResultAsync(result).ConfigureAwait(false);
    }

    private async Task<int> WriteHumanResultAsync(NeraCommandResult result)
    {
        string message = result.LocalizedMessage ?? NeraLocalizer.LocalizeMessage(result.UserMessage,
            NeraCommandDispatcher.LocalizedErrorCode(result.Code), result.Ok);
        if (result.Ok)
            await output_.WriteLineAsync(NeraLocalizer.Get("Cli.Success", result.CurrentRevision, message)).ConfigureAwait(false);
        else
            await error_.WriteLineAsync(NeraLocalizer.Get("Cli.Error", result.Code, message)).ConfigureAwait(false);
        return ResultExitCode(result);
    }

    private Task<int> RunParameterBundleAsync(
        IReadOnlyList<string> args, bool neural, CancellationToken cancellationToken)
    {
        int expectedCount = neural ? 7 : 5;
        if (args.Count != expectedCount)
            return Task.FromResult(UsageFailure(neural
                ? NeraLocalizer.Get("Cli.NrArgument")
                : NeraLocalizer.Get("Cli.DldrArgument")));
        var payload = new JsonObject();
        string[] fields = neural
            ? ["intensity", "local_tone", "local_structure", "skin_structure"]
            : ["highlight_protection", "shadow_protection", "chroma_strength", "temporal_response"];
        if (neural)
        {
            if (!int.TryParse(args[1], out int style) || style is < 0 or > 2 ||
                args[6] is not ("on" or "off"))
                return Task.FromResult(UsageFailure(NeraLocalizer.Get("Cli.StyleArgument")));
            payload["style"] = style;
            payload["automatic_skin_mask"] = args[6] == "on";
        }
        for (int index = 0; index < fields.Length; index++)
        {
            if (!double.TryParse(args[index + (neural ? 2 : 1)], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
                return Task.FromResult(UsageFailure(NeraLocalizer.Get("Cli.FiniteArgument")));
            payload[fields[index]] = value;
        }
        return RunMutationAsync(neural ? "nera_set_nr_parameters" : "nera_set_dldr_parameters",
            payload, cancellationToken);
    }

    private async Task<int> WriteExceptionFailureAsync(
        IReadOnlyList<string> args,
        string code,
        Exception error)
    {
        AgentBridgeErrorPresentation.TraceException("CLI_COMMAND_FAILED", error);
        var result = new NeraCommandResult
        {
            ProtocolVersion = NeraControlProtocol.Version,
            RequestId = "cli-failure",
            Ok = false,
            Code = code,
            UserMessage = AgentBridgeErrorPresentation.UserMessage(code),
            TechnicalMessage = AgentBridgeErrorPresentation.TechnicalMessage(error),
            RollbackPerformed = false
        };
        if (args.Contains("--json", StringComparer.Ordinal))
        {
            await output_.WriteLineAsync(JsonSerializer.Serialize(result, JsonSupport.Indented))
                .ConfigureAwait(false);
        }
        else
        {
            await error_.WriteLineAsync(NeraLocalizer.Get("Cli.Error", code, result.UserMessage))
                .ConfigureAwait(false);
        }
        return ExitCodes.FromStableCode(code);
    }

    private async Task<int> WriteHumanReadResultAsync(string tool, NeraCommandResult result)
    {
        if (!result.Ok) return await WriteHumanResultAsync(result).ConfigureAwait(false);
        NeraStateSnapshot? state = result.State;
        string Bool(bool value) => NeraLocalizer.Get(value ? "Common.Yes" : "Common.No");
        string Metric(double value, bool valid = true) => valid && double.IsFinite(value) && value > 0
            ? value.ToString("0.#", CultureInfo.InvariantCulture) : "--";
        switch (tool)
        {
            case "nera_get_status" when state is not null:
                await output_.WriteLineAsync(NeraLocalizer.Get("Cli.Status", state.DldrOn ? "ON" : "OFF",
                    DldrStateText(state.DldrActualState), result.CurrentRevision)).ConfigureAwait(false);
                if (state.TargetDisplay is { } display)
                    await output_.WriteLineAsync(NeraLocalizer.Get("Cli.Display", display.DisplayName,
                        display.Width, display.Height, display.RefreshRateHz, display.HdrEnabled ? "ON" : "OFF")).ConfigureAwait(false);
                await output_.WriteLineAsync(NeraLocalizer.Get("Cli.Visual",
                    state.RecipeCustom ? NeraLocalizer.Get("Common.Custom") : ProcessingModeText(state.ProcessingMode),
                    state.Strength, PerformanceModeText(state.PerformanceMode), state.NeuralScale,
                    state.Feature18Intensity, state.HudVisible ? "ON" : "OFF")).ConfigureAwait(false);
                await output_.WriteLineAsync(NeraLocalizer.Get("Cli.Ownership", Bool(state.BrokerProcessOwned),
                    Bool(state.BrokerConnected), Bool(state.OffGateSatisfied))).ConfigureAwait(false);
                break;
            case "nera_get_displays":
                var displays = result.Displays ?? [];
                await output_.WriteLineAsync(NeraLocalizer.Get("Cli.DisplayCount", displays.Count)).ConfigureAwait(false);
                for (int i = 0; i < displays.Count; i++)
                {
                    var listed = displays[i];
                    await output_.WriteLineAsync($"{i + 1}. {(listed.Selected ? "* " : string.Empty)}" +
                        NeraLocalizer.Get("Cli.Display", listed.DisplayName, listed.Width, listed.Height,
                            listed.RefreshRateHz, listed.HdrEnabled ? "ON" : "OFF") + $" · {listed.DisplayId}").ConfigureAwait(false);
                }
                break;
            case "nera_get_performance" when state is not null:
                var performance = state.Performance;
                bool active = state.DldrOn && state.ProcessingActive && !state.WarmStandby && !state.OverlayBypass;
                await output_.WriteLineAsync(NeraLocalizer.Get("Cli.Performance",
                    Metric(performance.PresentFramesPerSecond, active), Metric(performance.AverageFramesPerSecond, active),
                    Metric(performance.OnePercentLowFramesPerSecond, active))).ConfigureAwait(false);
                await output_.WriteLineAsync(NeraLocalizer.Get("Cli.Pipeline",
                    Metric(performance.CaptureFramesPerSecond, active), Metric(performance.ProcessingFramesPerSecond, active),
                    Metric(performance.PresentFramesPerSecond, active), performance.DroppedFrames)).ConfigureAwait(false);
                await output_.WriteLineAsync($"DLSS5 {Metric(performance.Feature18Milliseconds, active)} ms · " +
                    $"DLDR {Metric(performance.DldrMilliseconds, active)} ms · " +
                    $"GPU {Metric(performance.GpuPercent, performance.GpuUsageAvailable)}% · " +
                    $"CPU {Metric(performance.CpuPercent, performance.CpuUsageAvailable)}%").ConfigureAwait(false);
                break;
            case "nera_recommend_settings" when result.Data is JsonElement data &&
                data.TryGetProperty("recommendation", out var recommendation):
                string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()! : NeraLocalizer.Get("Common.Unknown");
                await output_.WriteLineAsync(NeraLocalizer.Get("Cli.Recommendation",
                    PerformanceModeText(Text(recommendation, "performanceMode")), recommendation.GetProperty("neuralScale").GetInt32(),
                    ProcessingModeText(Text(recommendation, "mode")), recommendation.GetProperty("strength").GetInt32())).ConfigureAwait(false);
                string confidence = Text(data, "confidence");
                await output_.WriteLineAsync(NeraLocalizer.Get("Cli.Confidence", NeraLocalizer.Get(confidence switch
                    { "high" => "Common.High", "medium" => "Common.Medium", _ => "Common.Low" }))).ConfigureAwait(false);
                await output_.WriteLineAsync(Text(data, "reason")).ConfigureAwait(false);
                break;
            case "nera_get_diagnostics" when state is not null:
                await output_.WriteLineAsync(NeraLocalizer.Get("Cli.Diagnostics", result.CurrentRevision,
                    DldrStateText(state.DldrActualState), RecoveryStateText(state.RecoveryState))).ConfigureAwait(false);
                await output_.WriteLineAsync(NeraLocalizer.Get("Cli.DiagnosticGates", Bool(state.GpuVerified),
                    Bool(state.RuntimeVerified), Bool(state.HostConnected), Bool(state.FeatureCreated),
                    Bool(state.PresenterSucceeded), Bool(state.ForegroundPreserved))).ConfigureAwait(false);
                if (state.LastError is { } error)
                    await output_.WriteLineAsync(NeraLocalizer.Get("Cli.LastError", error.Code,
                        NeraLocalizer.LocalizeMessage(error.UserMessage, NeraCommandDispatcher.LocalizedErrorCode(error.Code)))).ConfigureAwait(false);
                break;
            default:
                return await WriteHumanResultAsync(result).ConfigureAwait(false);
        }
        return ExitCodes.Success;
    }

    private Task<NeraCommandResult> InvokeAsync(
        string tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        JsonElement element = JsonSerializer.SerializeToElement(arguments, JsonSupport.Compact);
        return dispatcher_.InvokeToolAsync(tool, element, NeraControlProtocol.CliSource,
            cancellationToken);
    }

    private static int ResultExitCode(NeraCommandResult result) =>
        result.Ok ? ExitCodes.Success : ExitCodes.FromStableCode(result.Code);

    private static string DldrStateText(string state) => NeraLocalizer.Get(state.ToLowerInvariant() switch
    {
        "disabled" => "Common.Disabled", "runtimemissing" => "Runtime.Required",
        "runtimechecking" => "State.RuntimeChecking", "runtimeverified" => "State.RuntimeVerified",
        "hoststarting" => "Dldr.Starting", "featurecreating" or "waitingforfirstframe" or "capturecreating" => "Dldr.Preparing",
        "processing" => "Common.Enabled", "recovering" => "Ui.Restoring", "bypass" => "Osd.Restored",
        "displaychecking" => "Display.Checking", "hdrchecking" => "Hdr.Checking", "gpuchecking" => "State.CheckGpu",
        _ => "State.Problem"
    });
    private static string ProcessingModeText(string mode) => NeraLocalizer.Get(mode.ToLowerInvariant() switch
    {
        "sharp" or "clear" => "Common.Clear", "cinema" => "Common.Cinema", _ => "Common.Natural"
    });
    private static string PerformanceModeText(string mode) => NeraLocalizer.Get(mode.ToLowerInvariant() switch
    {
        "balanced" => "Performance.Balanced", "smooth" => "Performance.Smooth", _ => "Performance.Quality"
    });
    private static string RecoveryStateText(string state) => NeraLocalizer.Get(state.ToLowerInvariant() switch
    {
        "recovering" => "Ui.Restoring", "bypassrestored" => "Osd.Restored", "failed" => "State.RecoveryFailed", _ => "State.Normal"
    });

    private int UsageFailure(string message)
    {
        error_.WriteLine(NeraLocalizer.Get("Cli.Error", NeraErrorCodes.InvalidArgument, message));
        WriteUsage(error_);
        return ExitCodes.Usage;
    }

    public static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine(NeraLocalizer.Get("Cli.Usage") + " Nera.AgentBridge.exe mcp");
        writer.WriteLine("       Nera.AgentBridge.exe cli status [--json]");
        writer.WriteLine("       Nera.AgentBridge.exe cli displays [--json]");
        writer.WriteLine("       Nera.AgentBridge.exe cli display <number|display-id>");
        writer.WriteLine("       Nera.AgentBridge.exe cli dldr on|off");
        writer.WriteLine("       Nera.AgentBridge.exe cli mode natural|clear|cinema");
        writer.WriteLine("       Nera.AgentBridge.exe cli strength 0..100");
        writer.WriteLine("       Nera.AgentBridge.exe cli performance [--json]|quality|balanced|smooth");
        writer.WriteLine("       Nera.AgentBridge.exe cli nr <style:0/1/2> <intensity:0..1> <tone:0..2> <detail:0..2> <skin:-1..2> <auto-mask:on|off>");
        writer.WriteLine("       Nera.AgentBridge.exe cli dldr-parameters <highlight:0..1> <shadow:0..1> <chroma:0..1> <temporal:0..1>");
        writer.WriteLine("       Nera.AgentBridge.exe cli hud on|off");
        writer.WriteLine("       Nera.AgentBridge.exe cli recommend [--json]");
        writer.WriteLine("       Nera.AgentBridge.exe cli self-test [scope]");
        writer.WriteLine("       Nera.AgentBridge.exe cli diagnostics [--json]");
        writer.WriteLine("       Nera.AgentBridge.exe cli emergency-stop");
    }
}
