using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using ChipsStudio.Nera.Localization;

[assembly: InternalsVisibleTo("Nera.AgentBridge.Tests")]

namespace ChipsStudio.Nera.AgentBridge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false, true);
        Console.OutputEncoding = new UTF8Encoding(false, true);
        try { NeraLocalizer.SetLanguage(NeraLanguagePreferenceStore.Read(Path.Combine(AppContext.BaseDirectory, "Data"))); }
        catch (Exception error) { AgentBridgeErrorPresentation.TraceException("LANGUAGE_PREFERENCE_READ_FAILED", error); }

        if (args.Length == 0)
        {
            CliApplication.WriteUsage(Console.Error);
            return ExitCodes.Usage;
        }

        try
        {
            await using var transport = new NamedPipeNeraControlTransport();
            var dispatcher = new NeraCommandDispatcher(transport);
            switch (args[0])
            {
                case "mcp" when args.Length == 1:
                    return await new McpStdioServer(dispatcher).RunAsync(
                        Console.In, Console.Out, CancellationToken.None).ConfigureAwait(false);
                case "cli":
                    return await new CliApplication(dispatcher, Console.Out, Console.Error)
                        .RunAsync(args[1..], CancellationToken.None).ConfigureAwait(false);
                default:
                    CliApplication.WriteUsage(Console.Error);
                    return ExitCodes.Usage;
            }
        }
        catch (Exception error)
        {
            return ReportFatalError(error, Console.Error);
        }
    }

    internal static int ReportFatalError(Exception error, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(writer);
        AgentBridgeErrorPresentation.TraceException("AGENT_BRIDGE_FATAL", error);
        writer.WriteLine(NeraLocalizer.Get("Cli.Error", NeraErrorCodes.OperationFailed,
            NeraLocalizer.Get("Error.OPERATION_FAILED")));
        return ExitCodes.OperationFailed;
    }
}

internal static class AgentBridgeErrorPresentation
{
    internal static string UserMessage(string? stableCode) => NeraLocalizer.LocalizeMessage(null,
        NeraCommandDispatcher.LocalizedErrorCode(stableCode ?? NeraErrorCodes.OperationFailed));

    internal static string TechnicalMessage(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return $"{error.GetType().FullName}: {error.Message}";
    }

    internal static void TraceException(string context, Exception error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentNullException.ThrowIfNull(error);
        Trace.WriteLine($"{context}: {error}");
    }
}
