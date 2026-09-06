using System.Text.Json;

namespace ChipsStudio.Nera.Localization;

/// <summary>Only Data/language.json below the explicitly supplied Nera directory.</summary>
public static class NeraLanguagePreferenceStore
{
    public static string Read(string dataDirectory)
    {
        string path = Path.Combine(Path.GetFullPath(dataDirectory), "language.json");
        if (!File.Exists(path)) return "system";
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Language document exceeds its bound.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid language document.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name) || property.Name is not ("schemaVersion" or "preference"))
                throw new InvalidDataException("Unknown or duplicate language property.");
        if (!root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out int version) || version != 1 ||
            !root.TryGetProperty("preference", out var field) || field.ValueKind != JsonValueKind.String ||
            !NeraLocalizer.IsValidPreference(field.GetString())) throw new InvalidDataException("Unsupported language preference.");
        return field.GetString()!;
    }
}
