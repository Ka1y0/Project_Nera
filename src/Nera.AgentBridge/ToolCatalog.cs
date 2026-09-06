using ChipsStudio.Nera.Localization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChipsStudio.Nera.AgentBridge;

public enum ToolArgumentKind
{
    String,
    Integer,
    Number,
    Boolean
}

public sealed record ToolArgumentRule(
    string Name,
    ToolArgumentKind Kind,
    bool Required = false,
    long? Minimum = null,
    long? Maximum = null,
    IReadOnlyList<string>? AllowedValues = null,
    int MaximumLength = 256,
    string? Description = null);

public sealed record NeraToolDefinition(
    string Name,
    string Command,
    string Description,
    bool Mutating,
    bool LongRunning,
    IReadOnlyList<ToolArgumentRule> Arguments)
{
    public JsonObject ToMcpDefinition()
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var rule in Arguments)
        {
            var schema = new JsonObject
            {
                ["type"] = rule.Kind switch
                {
                    ToolArgumentKind.Integer => "integer",
                    ToolArgumentKind.Number => "number",
                    ToolArgumentKind.Boolean => "boolean",
                    _ => "string"
                }
            };
            if (!string.IsNullOrWhiteSpace(rule.Description))
                schema["description"] = NeraLocalizer.Get(rule.Description);
            if (rule.Minimum.HasValue) schema["minimum"] = rule.Minimum.Value;
            if (rule.Maximum.HasValue) schema["maximum"] = rule.Maximum.Value;
            if (rule.Kind == ToolArgumentKind.String)
                schema["maxLength"] = rule.MaximumLength;
            if (rule.AllowedValues is not null)
                schema["enum"] = new JsonArray(rule.AllowedValues.Select(
                    value => (JsonNode?)JsonValue.Create(value)).ToArray());
            properties[rule.Name] = schema;
            if (rule.Required) required.Add(rule.Name);
        }

        var input = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
        if (required.Count > 0) input["required"] = required;

        return new JsonObject
        {
            ["name"] = Name,
            ["title"] = ToolCatalog.TitleFor(Name),
            ["description"] = NeraLocalizer.Get(Description),
            ["inputSchema"] = input,
            ["outputSchema"] = ToolCatalog.OutputSchema().DeepClone(),
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = !Mutating,
                ["destructiveHint"] = Name == "nera_emergency_stop",
                ["idempotentHint"] = !Mutating || Name is
                    "nera_set_display" or "nera_enable_dldr" or "nera_disable_dldr" or
                    "nera_set_mode" or "nera_set_strength" or "nera_set_performance_mode" or
                    "nera_set_nr_parameters" or "nera_set_dldr_parameters" or "nera_set_hud" or "nera_emergency_stop"
            }
        };
    }
}

public static class ToolCatalog
{
    private static readonly ToolArgumentRule ExpectedRevision = new(
        "expected_revision", ToolArgumentKind.Integer, true, 0, long.MaxValue,
        Description: "Mcp.ExpectedRevision");

    private static readonly IReadOnlyList<NeraToolDefinition> Definitions =
    [
        Read("nera_get_status", "get_status", "Mcp.GetStatus"),
        Read("nera_get_displays", "get_displays", "Mcp.GetDisplays"),
        Mutate("nera_set_display", "set_display", "Mcp.SetDisplay", false,
            new ToolArgumentRule("display_id", ToolArgumentKind.String, true, MaximumLength: 512,
                Description: "Mcp.DisplayId")),
        Mutate("nera_enable_dldr", "enable_dldr",
            "Mcp.Enable", true),
        Mutate("nera_disable_dldr", "disable_dldr",
            "Mcp.Disable"),
        Mutate("nera_set_mode", "set_mode", "Mcp.SetMode", false,
            new ToolArgumentRule("mode", ToolArgumentKind.String, true,
                AllowedValues: ["natural", "clear", "cinema"])),
        Mutate("nera_set_strength", "set_strength", "Mcp.SetStrength", false,
            new ToolArgumentRule("strength", ToolArgumentKind.Integer, true, 0, 100)),
        Mutate("nera_set_performance_mode", "set_performance_mode", "Mcp.SetPerformance", false,
            new ToolArgumentRule("performance_mode", ToolArgumentKind.String, true,
                AllowedValues: ["quality", "balanced", "smooth"])),
        Mutate("nera_set_nr_parameters", "set_feature18_tuning", "Mcp.SetNr", false,
            new ToolArgumentRule("style", ToolArgumentKind.Integer, true, 0, 2),
            new ToolArgumentRule("intensity", ToolArgumentKind.Number, true, 0, 1),
            new ToolArgumentRule("local_tone", ToolArgumentKind.Number, true, 0, 2),
            new ToolArgumentRule("local_structure", ToolArgumentKind.Number, true, 0, 2),
            new ToolArgumentRule("skin_structure", ToolArgumentKind.Number, true, -1, 2),
            new ToolArgumentRule("automatic_skin_mask", ToolArgumentKind.Boolean, true)),
        Mutate("nera_set_dldr_parameters", "set_dldr_tuning", "Mcp.SetDldr", false,
            new ToolArgumentRule("highlight_protection", ToolArgumentKind.Number, true, 0, 1),
            new ToolArgumentRule("shadow_protection", ToolArgumentKind.Number, true, 0, 1),
            new ToolArgumentRule("chroma_strength", ToolArgumentKind.Number, true, 0, 1),
            new ToolArgumentRule("temporal_response", ToolArgumentKind.Number, true, 0, 1)),
        Mutate("nera_set_hud", "set_hud", "Mcp.SetHud", false,
            new ToolArgumentRule("enabled", ToolArgumentKind.Boolean, true)),
        Read("nera_get_performance", "get_performance", "Mcp.GetPerformance"),
        Read("nera_recommend_settings", "recommend_settings",
            "Mcp.Recommend"),
        Mutate("nera_run_self_test", "run_self_test", "Mcp.SelfTest", true,
            new ToolArgumentRule("scope", ToolArgumentKind.String, false,
                AllowedValues: ["quick", "full"])),
        Read("nera_get_diagnostics", "get_diagnostics", "Mcp.Diagnostics",
            new ToolArgumentRule("include_recent_errors", ToolArgumentKind.Boolean, false)),
        Mutate("nera_emergency_stop", "emergency_stop",
            "Mcp.Emergency")
    ];

    private static NeraToolDefinition Read(string name, string command, string description,
        params ToolArgumentRule[] arguments) =>
        new(name, command, description, false, false, arguments);

    private static NeraToolDefinition Mutate(string name, string command, string description,
        bool longRunning = false, params ToolArgumentRule[] arguments) =>
        new(name, command, description, true, longRunning,
            arguments.Concat([ExpectedRevision]).ToArray());

    public static IReadOnlyList<NeraToolDefinition> All => Definitions;

    internal static string TitleFor(string name) => NeraLocalizer.Get("Mcp.Title." + name);

    public static bool TryGet(string name, out NeraToolDefinition definition)
    {
        definition = Definitions.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.Ordinal))!;
        return definition is not null;
    }

    public static JsonObject OutputSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["protocolVersion"] = IntegerSchema(NeraControlProtocol.Version),
            ["requestId"] = new JsonObject { ["type"] = "string" },
            ["ok"] = new JsonObject { ["type"] = "boolean" },
            ["previousRevision"] = NullableIntegerSchema(0),
            ["currentRevision"] = NullableIntegerSchema(0),
            ["state"] = new JsonObject { ["type"] = TypeUnion("object", "null") },
            ["code"] = new JsonObject { ["type"] = "string" },
            ["userMessage"] = new JsonObject { ["type"] = "string" },
            ["localizedMessage"] = new JsonObject { ["type"] = TypeUnion("string", "null") },
            ["technicalMessage"] = new JsonObject { ["type"] = "string" },
            ["rollbackPerformed"] = new JsonObject { ["type"] = "boolean" },
            ["displays"] = new JsonObject { ["type"] = TypeUnion("array", "null") },
            ["operationId"] = new JsonObject { ["type"] = TypeUnion("string", "null") },
            ["data"] = new JsonObject { ["type"] = TypeUnion("object", "null") }
        },
        ["required"] = new JsonArray("protocolVersion", "requestId", "ok",
            "code", "userMessage",
            "technicalMessage", "rollbackPerformed"),
        ["additionalProperties"] = false
    };

    private static JsonObject IntegerSchema(long minimum) => new()
    {
        ["type"] = "integer",
        ["minimum"] = minimum
    };

    private static JsonObject NullableIntegerSchema(long minimum) => new()
    {
        ["type"] = TypeUnion("integer", "null"),
        ["minimum"] = minimum
    };

    private static JsonArray TypeUnion(params string[] values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}

public sealed class ToolArgumentException : Exception
{
    public ToolArgumentException(string message) : base(message) { }
}

public static class ToolArgumentValidator
{
    public static JsonObject ValidateAndNormalize(
        NeraToolDefinition definition, JsonElement arguments, out long? expectedRevision)
    {
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            using var empty = JsonDocument.Parse("{}");
            return ValidateAndNormalize(definition, empty.RootElement, out expectedRevision);
        }
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ToolArgumentException("参数必须是一个对象。");

        var rules = definition.Arguments.ToDictionary(rule => rule.Name, StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!rules.ContainsKey(property.Name))
                throw new ToolArgumentException($"未知参数“{property.Name}”。");
        }

        var normalized = new JsonObject();
        expectedRevision = null;
        foreach (var rule in definition.Arguments)
        {
            if (!arguments.TryGetProperty(rule.Name, out var value))
            {
                if (rule.Required)
                    throw new ToolArgumentException($"缺少必需参数“{rule.Name}”。");
                continue;
            }
            switch (rule.Kind)
            {
                case ToolArgumentKind.Number:
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) ||
                        !double.IsFinite(number) || (rule.Minimum.HasValue && number < rule.Minimum) ||
                        (rule.Maximum.HasValue && number > rule.Maximum))
                        throw new ToolArgumentException($"参数“{rule.Name}”必须是允许范围内的有限数值。");
                    normalized[rule.Name] = number;
                    break;
                case ToolArgumentKind.Boolean:
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new ToolArgumentException($"参数“{rule.Name}”必须是布尔值。");
                    normalized[rule.Name] = value.GetBoolean();
                    break;
                case ToolArgumentKind.Integer:
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var integer))
                        throw new ToolArgumentException($"参数“{rule.Name}”必须是整数。");
                    if ((rule.Minimum.HasValue && integer < rule.Minimum) ||
                        (rule.Maximum.HasValue && integer > rule.Maximum))
                        throw new ToolArgumentException($"参数“{rule.Name}”超出允许范围。");
                    if (rule.Name == "expected_revision") expectedRevision = integer;
                    else normalized[rule.Name] = integer;
                    break;
                default:
                    if (value.ValueKind != JsonValueKind.String)
                        throw new ToolArgumentException($"参数“{rule.Name}”必须是字符串。");
                    var text = value.GetString() ?? string.Empty;
                    if (text.Length == 0 || text.Length > rule.MaximumLength ||
                        text.Any(char.IsControl))
                        throw new ToolArgumentException($"参数“{rule.Name}”的长度或字符无效。");
                    if (rule.AllowedValues is not null &&
                        !rule.AllowedValues.Contains(text, StringComparer.Ordinal))
                        throw new ToolArgumentException($"参数“{rule.Name}”使用了不支持的值。");
                    normalized[rule.Name] = text;
                    break;
            }
        }
        if (definition.Mutating && !expectedRevision.HasValue)
            throw new ToolArgumentException("每个修改操作都必须提供 expected_revision。");
        return normalized;
    }

}
