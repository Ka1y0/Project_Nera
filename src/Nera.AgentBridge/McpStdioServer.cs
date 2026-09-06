using System.Text;
using System.Text.Json;
using ChipsStudio.Nera.Localization;
using System.Text.Json.Nodes;

namespace ChipsStudio.Nera.AgentBridge;

public sealed class McpStdioServer
{
    public const string ModernProtocolVersion = "2026-07-28";
    public const string LegacyProtocolVersion = "2025-11-25";
    public const int MaximumLineCharacters = NeraControlProtocol.MaximumMessageBytes;
    private const string ServerName = "nera-agent-bridge";
    private const string ServerVersion = "0.4.1-global-alpha.1";

    private readonly NeraCommandDispatcher dispatcher_;
    private bool legacyInitializeSeen_;
    private bool legacyReady_;

    public McpStdioServer(NeraCommandDispatcher dispatcher) =>
        dispatcher_ = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public async Task<int> RunAsync(
        TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await ReadBoundedLineAsync(input, cancellationToken).ConfigureAwait(false);
            }
            catch (McpInputTooLargeException)
            {
                await WriteResponseAsync(output,
                Serialize(ErrorResponse(null, -32600, NeraLocalizer.Get("Mcp.Error.MessageLimit"),
                        new JsonObject { ["code"] = NeraErrorCodes.ResponseTooLarge })),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (line is null) return ExitCodes.Success;
            var response = await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
            if (response is not null)
                await WriteResponseAsync(output, response, cancellationToken).ConfigureAwait(false);
        }
        return ExitCodes.Success;
    }

    public async Task<string?> HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(line) > NeraControlProtocol.MaximumMessageBytes)
            return Serialize(ErrorResponse(null, -32600,
                    NeraLocalizer.Get("Mcp.Error.MessageLimit"),
                new JsonObject { ["code"] = NeraErrorCodes.ResponseTooLarge }));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        }
        catch (JsonException)
        {
            return Serialize(ErrorResponse(null, -32700, NeraLocalizer.Get("Mcp.Error.JsonParse")));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                HasUnknownProperties(root, "jsonrpc", "id", "method", "params") ||
                !root.TryGetProperty("jsonrpc", out var jsonRpc) ||
                jsonRpc.ValueKind != JsonValueKind.String || jsonRpc.GetString() != "2.0" ||
                !root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                return Serialize(ErrorResponse(RequestIdOrNull(root), -32600,
                NeraLocalizer.Get("Mcp.Error.RequestInvalid")));
            }

            var hasId = root.TryGetProperty("id", out var idElement);
            object? id = null;
            if (hasId)
            {
                if (!TryReadRequestId(idElement, out id))
            return Serialize(ErrorResponse(null, -32600, NeraLocalizer.Get("Mcp.Error.IdInvalid")));
            }
            var method = methodElement.GetString()!;
            var isNotification = !hasId;
            var paramsElement = root.TryGetProperty("params", out var suppliedParams)
                ? suppliedParams : default;

            if (method == "notifications/initialized")
            {
                if (!isNotification || !legacyInitializeSeen_ ||
                    !ValidateLegacyInitializedParams(paramsElement))
                    return isNotification ? null : Serialize(ErrorResponse(id, -32600,
                NeraLocalizer.Get("Mcp.Error.InitializedInvalid")));
                legacyReady_ = true;
                return null;
            }
            if (isNotification)
            {
                // JSON-RPC notifications never receive a response. Unknown notifications
                // cannot expand Nera authority and are deliberately ignored.
                return null;
            }

            if (method == "initialize")
                return Serialize(HandleLegacyInitialize(id, paramsElement));

            var modern = TryValidateModernMetadata(paramsElement, out var metadataError,
                out var requestedVersion);
            if (requestedVersion is not null && requestedVersion != ModernProtocolVersion)
            {
                return Serialize(ErrorResponse(id, -32022,
                NeraLocalizer.Get("Mcp.Error.UnsupportedVersion"), new JsonObject
                    {
                        ["supported"] = new JsonArray(ModernProtocolVersion, LegacyProtocolVersion),
                        ["requested"] = requestedVersion
                    }));
            }
            if (!modern && !legacyReady_)
                return Serialize(ErrorResponse(id, -32602,
                metadataError ?? NeraLocalizer.Get("Mcp.Error.MetadataRequired")));

            return method switch
            {
                "server/discover" => modern
                    ? Serialize(HandleDiscover(id, paramsElement))
                    : Serialize(ErrorResponse(id, -32601,
                    NeraLocalizer.Get("Mcp.Error.DiscoverMetadata"))),
                "ping" => Serialize(SuccessResponse(id,
                    CompleteResult(modern, new JsonObject()))),
                "tools/list" => Serialize(HandleToolsList(id, paramsElement, modern)),
                "tools/call" => await HandleToolCallAsync(id, paramsElement, modern,
                    cancellationToken).ConfigureAwait(false),
            _ => Serialize(ErrorResponse(id, -32601, NeraLocalizer.Get("Mcp.Error.MethodMissing", method)))
            };
        }
    }

    private JsonObject HandleLegacyInitialize(object? id, JsonElement parameters)
    {
        if (legacyInitializeSeen_ || !ValidateLegacyInitializeParams(parameters))
            return ErrorResponse(id, -32602, NeraLocalizer.Get("Mcp.Error.LegacyInvalid") );
        legacyInitializeSeen_ = true;
        return SuccessResponse(id, new JsonObject
        {
            ["protocolVersion"] = LegacyProtocolVersion,
            ["capabilities"] = ServerCapabilities(legacy: true),
            ["serverInfo"] = ServerInfo(),
            ["instructions"] = Instructions()
        });
    }

    private JsonObject HandleDiscover(object? id, JsonElement parameters)
    {
        if (!HasOnlyProperties(parameters, "_meta"))
            return ErrorResponse(id, -32602, NeraLocalizer.Get("Mcp.Error.DiscoverOnlyMeta") );
        var result = CompleteResult(true, new JsonObject
        {
            ["supportedVersions"] = new JsonArray(ModernProtocolVersion, LegacyProtocolVersion),
            ["capabilities"] = ServerCapabilities(legacy: false),
            ["instructions"] = Instructions()
        });
        return SuccessResponse(id, result);
    }

    private JsonObject HandleToolsList(object? id, JsonElement parameters, bool modern)
    {
        var allowed = modern ? new[] { "cursor", "_meta" } : new[] { "cursor" };
        if (!IsObjectOrUndefined(parameters) || HasUnknownProperties(parameters, allowed))
            return ErrorResponse(id, -32602, NeraLocalizer.Get("Mcp.Error.ListInvalid") );
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("cursor", out var cursor))
        {
            if (cursor.ValueKind != JsonValueKind.String ||
                !string.IsNullOrEmpty(cursor.GetString()))
            return ErrorResponse(id, -32602, NeraLocalizer.Get("Mcp.Error.CursorInvalid") );
        }
        var tools = new JsonArray(ToolCatalog.All.Select(tool =>
            (JsonNode?)tool.ToMcpDefinition()).ToArray());
        return SuccessResponse(id, CompleteResult(modern,
            new JsonObject { ["tools"] = tools }));
    }

    private async Task<string> HandleToolCallAsync(
        object? id,
        JsonElement parameters,
        bool modern,
        CancellationToken cancellationToken)
    {
        var allowed = modern ? new[] { "name", "arguments", "_meta" } :
            new[] { "name", "arguments" };
        if (parameters.ValueKind != JsonValueKind.Object ||
            HasUnknownProperties(parameters, allowed) ||
            !parameters.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
            return Serialize(ErrorResponse(id, -32602, NeraLocalizer.Get("Mcp.Error.CallInvalid")));
        var name = nameElement.GetString()!;
        if (!ToolCatalog.TryGet(name, out _))
            return Serialize(ErrorResponse(id, -32602, NeraLocalizer.Get("Mcp.Error.UnknownTool", name)));
        var arguments = parameters.TryGetProperty("arguments", out var suppliedArguments)
            ? suppliedArguments : default;

        NeraCommandResult commandResult;
        try
        {
            commandResult = await dispatcher_.InvokeToolAsync(name, arguments,
                NeraControlProtocol.AgentSource, cancellationToken).ConfigureAwait(false);
        }
        catch (ToolArgumentException error)
        {
            AgentBridgeErrorPresentation.TraceException("MCP_TOOL_ARGUMENT_REJECTED", error);
            return Serialize(ErrorResponse(id, -32602, NeraLocalizer.Get("Mcp.Error.ToolArgumentsInvalid"),
                TechnicalErrorData(NeraErrorCodes.InvalidArgument, error)));
        }
        catch (NeraTransportException error)
        {
            commandResult = FailureResult(error.StableCode, error);
        }
        catch (Exception error)
        {
            commandResult = FailureResult(NeraErrorCodes.OperationFailed, error);
        }

        var structured = JsonSerializer.SerializeToNode(commandResult, JsonSupport.Compact)
            as JsonObject ?? new JsonObject();
        var message = !string.IsNullOrWhiteSpace(commandResult.UserMessage)
            ? commandResult.UserMessage
            : commandResult.Ok
                ? ChipsStudio.Nera.Localization.NeraLocalizer.Get("Common.Applied")
                : AgentBridgeErrorPresentation.UserMessage(commandResult.Code);
        var result = CompleteResult(modern, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = message
            }),
            ["structuredContent"] = structured,
            ["isError"] = !commandResult.Ok
        });
        return Serialize(SuccessResponse(id, result));
    }

    private static NeraCommandResult FailureResult(string code, Exception error)
    {
        AgentBridgeErrorPresentation.TraceException("MCP_TOOL_CALL_FAILED", error);
        return new NeraCommandResult
        {
            ProtocolVersion = NeraControlProtocol.Version,
            RequestId = "transport-failure",
            Ok = false,
            Code = code,
            UserMessage = AgentBridgeErrorPresentation.UserMessage(code),
            TechnicalMessage = AgentBridgeErrorPresentation.TechnicalMessage(error),
            RollbackPerformed = false
        };
    }

    private static JsonObject TechnicalErrorData(string code, Exception error) => new()
    {
        ["code"] = code,
        ["technicalMessage"] = AgentBridgeErrorPresentation.TechnicalMessage(error)
    };

    private static JsonObject CompleteResult(bool modern, JsonObject body)
    {
        if (modern)
        {
            body["resultType"] = "complete";
            body["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/serverInfo"] = ServerInfo()
            };
        }
        return body;
    }

    private static JsonObject SuccessResponse(object? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = IdNode(id),
        ["result"] = result
    };

    private static JsonObject ErrorResponse(
        object? id, int code, string message, JsonObject? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) error["data"] = data;
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = IdNode(id),
            ["error"] = error
        };
    }

    private static JsonNode? IdNode(object? id) => id switch
    {
        null => null,
        string text => JsonValue.Create(text),
        long number => JsonValue.Create(number),
        _ => null
    };

    private static JsonObject ServerInfo() => new()
    {
        ["name"] = ServerName,
        ["version"] = ServerVersion,
        ["description"] = NeraLocalizer.Get("Mcp.ServerDescription")
    };

    private static JsonObject ServerCapabilities(bool legacy) => new()
    {
        ["tools"] = legacy ? new JsonObject { ["listChanged"] = false } : new JsonObject()
    };

    private static string Instructions() => NeraLocalizer.Get("Mcp.Instructions");

    private static bool TryValidateModernMetadata(
        JsonElement parameters, out string? error, out string? requestedVersion)
    {
        error = null;
        requestedVersion = null;
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("_meta", out var metadata) ||
            metadata.ValueKind != JsonValueKind.Object)
        {
            error = NeraLocalizer.Get("Mcp.Error.ParamsMetaRequired");
            return false;
        }
        if (!metadata.TryGetProperty("io.modelcontextprotocol/protocolVersion", out var version) ||
            version.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(requestedVersion = version.GetString()))
        {
            error = NeraLocalizer.Get("Mcp.Error.ProtocolMetaRequired");
            return false;
        }
        if (!metadata.TryGetProperty("io.modelcontextprotocol/clientCapabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object)
        {
            error = NeraLocalizer.Get("Mcp.Error.CapabilitiesObject");
            return false;
        }
        if (metadata.TryGetProperty("io.modelcontextprotocol/clientInfo", out var clientInfo) &&
            !ValidateImplementation(clientInfo))
        {
            error = NeraLocalizer.Get("Mcp.Error.InfoInvalid");
            return false;
        }
        return requestedVersion == ModernProtocolVersion;
    }

    private static bool ValidateLegacyInitializeParams(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object &&
        HasOnlyProperties(parameters, "protocolVersion", "capabilities", "clientInfo", "_meta") &&
        parameters.TryGetProperty("protocolVersion", out var version) &&
        version.ValueKind == JsonValueKind.String &&
        version.GetString() == LegacyProtocolVersion &&
        parameters.TryGetProperty("capabilities", out var capabilities) &&
        capabilities.ValueKind == JsonValueKind.Object &&
        parameters.TryGetProperty("clientInfo", out var clientInfo) &&
        ValidateImplementation(clientInfo);

    private static bool ValidateImplementation(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(name.GetString()) &&
        value.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(version.GetString());

    private static bool ValidateLegacyInitializedParams(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Undefined ||
        (parameters.ValueKind == JsonValueKind.Object &&
         HasOnlyProperties(parameters, "_meta"));

    private static bool TryReadRequestId(JsonElement value, out object? id)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            id = value.GetString();
            return id is string text && text.Length <= 256;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            id = number;
            return true;
        }
        id = null;
        return false;
    }

    private static object? RequestIdOrNull(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id) &&
        TryReadRequestId(id, out var value) ? value : null;

    private static bool IsObjectOrUndefined(JsonElement value) =>
        value.ValueKind is JsonValueKind.Object or JsonValueKind.Undefined;

    private static bool HasOnlyProperties(JsonElement value, params string[] allowed) =>
        value.ValueKind == JsonValueKind.Object && !HasUnknownProperties(value, allowed);

    private static bool HasUnknownProperties(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        return value.EnumerateObject().Any(property => !names.Contains(property.Name));
    }

    private static string Serialize(JsonObject response) =>
        response.ToJsonString(JsonSupport.Compact);

    private static async Task WriteResponseAsync(
        TextWriter output, string response, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadBoundedLineAsync(
        TextReader input, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(4096, MaximumLineCharacters));
        var one = new char[1];
        var oversized = false;
        while (true)
        {
            var count = await input.ReadAsync(one.AsMemory(0, 1), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                if (oversized) throw new McpInputTooLargeException();
                return builder.Length == 0 ? null : builder.ToString();
            }
            if (one[0] == '\n')
            {
                if (oversized) throw new McpInputTooLargeException();
                if (builder.Length > 0 && builder[^1] == '\r') --builder.Length;
                return builder.ToString();
            }
            if (builder.Length < MaximumLineCharacters) builder.Append(one[0]);
            else oversized = true;
        }
    }

    private sealed class McpInputTooLargeException : Exception { }
}
