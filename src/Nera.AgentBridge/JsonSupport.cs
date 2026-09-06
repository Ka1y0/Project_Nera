using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChipsStudio.Nera.AgentBridge;

public static class JsonSupport
{
    public static JsonSerializerOptions CreateOptions(bool indented = false) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 32,
        WriteIndented = indented
    };

    public static readonly JsonSerializerOptions Compact = CreateOptions();
    public static readonly JsonSerializerOptions Indented = CreateOptions(true);
}
