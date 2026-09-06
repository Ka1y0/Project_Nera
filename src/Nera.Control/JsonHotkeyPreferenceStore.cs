using System.Text.Json;

namespace ChipsStudio.Nera.Control;

/// <summary>Atomic, bounded Nera-only shortcut preferences. No external application settings.</summary>
public sealed class JsonHotkeyPreferenceStore : INeraHotkeyPreferenceStore
{
    private const int MaximumBytes = 16 * 1024;
    private readonly string directory_;
    private readonly string path_;
    public JsonHotkeyPreferenceStore(string dataDirectory)
    {
        if (!NeraStatePolicy.IsLocalAbsolutePath(dataDirectory))
            throw new ArgumentException("A local absolute Nera data directory is required.", nameof(dataDirectory));
        directory_ = Path.GetFullPath(dataDirectory);
        path_ = Path.Combine(directory_, "hotkeys.json");
    }

    public IReadOnlyDictionary<NeraHotkeyAction, NeraHotkeyChord?> Load()
    {
        try { return LoadCore(); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("Malformed hotkey preference values.", error); }
    }

    private IReadOnlyDictionary<NeraHotkeyAction, NeraHotkeyChord?> LoadCore()
    {
        RejectReparse(directory_);
        RejectReparse(path_);
        if (!File.Exists(path_)) return new Dictionary<NeraHotkeyAction, NeraHotkeyChord?>();
        if (new FileInfo(path_).Length > MaximumBytes) throw new InvalidDataException("Hotkey preferences exceed the limit.");
        using var stream = new FileStream(path_, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Hotkey preferences exceed the limit.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 6 });
        JsonElement root = document.RootElement;
        RequireProperties(root, "schemaVersion", "bindings");
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported hotkey preferences version.");
        JsonElement bindings = root.GetProperty("bindings");
        if (bindings.ValueKind != JsonValueKind.Array || bindings.GetArrayLength() > 4)
            throw new InvalidDataException("Invalid hotkey bindings.");
        var result = new Dictionary<NeraHotkeyAction, NeraHotkeyChord?>();
        foreach (JsonElement binding in bindings.EnumerateArray())
        {
            RequireProperties(binding, "action", "modifiers", "virtualKey");
            if (!Enum.TryParse(binding.GetProperty("action").GetString(), false, out NeraHotkeyAction action) ||
                !NeraHotkeyPolicy.EditableActions.Contains(action) || result.ContainsKey(action))
                throw new InvalidDataException("Unknown or duplicate hotkey action.");
            uint modifiers = binding.GetProperty("modifiers").GetUInt32();
            uint key = binding.GetProperty("virtualKey").GetUInt32();
            var chord = new NeraHotkeyChord(modifiers, key);
            if (modifiers == 0 && key == 0) result.Add(action, null);
            else if (chord.IsValid) result.Add(action, new(chord.PersistedModifiers, key));
            else throw new InvalidDataException("Unsupported saved keyboard shortcut.");
        }
        return result;
    }

    public void Save(IReadOnlyDictionary<NeraHotkeyAction, NeraHotkeyChord?> preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.Keys.Any(action => !NeraHotkeyPolicy.EditableActions.Contains(action)))
            throw new InvalidDataException("Fixed escape bindings cannot be persisted or overridden.");
        foreach (NeraHotkeyChord? chord in preferences.Values)
            if (chord is { IsValid: false }) throw new InvalidDataException("Invalid hotkey chord.");
        RejectReparse(directory_);
        RejectReparse(path_);
        Directory.CreateDirectory(directory_);
        string temporary = Path.Combine(directory_, $".hotkeys-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schemaVersion", 1);
                    writer.WriteStartArray("bindings");
                    foreach (var pair in preferences.OrderBy(pair => (int)pair.Key))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("action", pair.Key.ToString());
                        writer.WriteNumber("modifiers", pair.Value?.PersistedModifiers ?? 0);
                        writer.WriteNumber("virtualKey", pair.Value?.VirtualKey ?? 0);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.Flush();
                }
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path_, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary); // This invocation's exact private temp file only.
        }
    }

    private static void RequireProperties(JsonElement element, params string[] properties)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected an object.");
        string[] actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != properties.Length || !actual.Order().SequenceEqual(properties.Order()))
            throw new InvalidDataException("Unknown, missing or duplicate hotkey preference fields.");
    }
    private static void RejectReparse(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Nera shortcut preferences cannot follow a reparse point.");
    }
}
