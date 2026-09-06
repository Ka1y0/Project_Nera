namespace ChipsStudio.Nera.Control;

/// <summary>
/// Win32 modifier values accepted by RegisterHotKey. Persisted chords contain only
/// the four user-facing modifier bits; MOD_NOREPEAT is always added at registration time.
/// </summary>
public static class NeraHotkeyModifiers
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Win = 0x0008;
    public const uint NoRepeat = 0x4000;
    public const uint Supported = Alt | Control | Shift | Win;

    public static uint Persisted(uint modifiers) => modifiers & Supported;
    public static uint ForRegistration(uint modifiers) => Persisted(modifiers) | NoRepeat;
}

public readonly record struct NeraHotkeyChord(uint Modifiers, uint VirtualKey)
{
    public uint PersistedModifiers => NeraHotkeyModifiers.Persisted(Modifiers);
    public uint RegistrationModifiers => NeraHotkeyModifiers.ForRegistration(Modifiers);

    public bool IsValid => NeraHotkeyPolicy.Validate(this) == NeraControlCodes.Ok;

    public NeraHotkeyBinding ToBinding(NeraHotkeyAction action, bool registered, string? errorCode = null) => new()
    {
        Action = action,
        Modifiers = PersistedModifiers,
        VirtualKey = VirtualKey,
        Registered = registered,
        ErrorCode = errorCode
    };
}

public enum NeraHotkeyRegistrationStatus
{
    Registered,
    PreviousBindingRestored,
    Conflict,
    Invalid,
    Unavailable,
    HoldSemanticsUnsupported,
    Unregistered
}

/// <summary>Keyboard-only format and OS-reserved-key policy. No mouse, wheel or input hooks.</summary>
public static class NeraHotkeyPolicy
{
    public static readonly NeraHotkeyChord FixedEmergencyChord = new(
        NeraHotkeyModifiers.Control | NeraHotkeyModifiers.Alt, 0x08);
    public static readonly IReadOnlyList<NeraHotkeyAction> EditableActions =
    [NeraHotkeyAction.ToggleDldr, NeraHotkeyAction.ToggleHud,
     NeraHotkeyAction.ToggleAppVisibility, NeraHotkeyAction.EmergencyStop];
    public static readonly IReadOnlyList<NeraHotkeyAction> ProductActions =
    [NeraHotkeyAction.ToggleDldr, NeraHotkeyAction.ToggleHud,
     NeraHotkeyAction.ToggleAppVisibility, NeraHotkeyAction.EmergencyStop,
     NeraHotkeyAction.EmergencyStopFallback];

    public static bool IsClear(NeraHotkeyBinding binding) =>
        binding.Modifiers == 0 && binding.VirtualKey == 0;

    public static string Validate(NeraHotkeyChord chord)
    {
        uint key = chord.VirtualKey;
        uint modifiers = chord.PersistedModifiers;
        if ((chord.Modifiers & ~(NeraHotkeyModifiers.Supported | NeraHotkeyModifiers.NoRepeat)) != 0)
            return NeraControlCodes.InvalidArgument;
        bool keyboard = key is 0x08 or 0x09 or 0x0D or 0x13 or 0x14 or 0x1B or 0x20 or
            >= 0x21 and <= 0x28 or 0x2D or 0x2E or >= 0x30 and <= 0x39 or
            >= 0x41 and <= 0x5A or >= 0x60 and <= 0x6F or >= 0x70 and <= 0x87 or
            >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDE or 0xE2;
        if (!keyboard) return NeraControlCodes.HotkeyKeyboardOnly;
        // F12 is reserved for the debugger; Win combinations and standard shell escape
        // gestures are deliberately not available to a global display tool.
        if (key == 0x7B || (modifiers & NeraHotkeyModifiers.Win) != 0 ||
            (modifiers & NeraHotkeyModifiers.Alt) != 0 && key is 0x73 or 0x09 or 0x1B or 0x20 ||
            (modifiers & NeraHotkeyModifiers.Control) != 0 && key == 0x1B ||
            (modifiers & (NeraHotkeyModifiers.Control | NeraHotkeyModifiers.Alt)) ==
                (NeraHotkeyModifiers.Control | NeraHotkeyModifiers.Alt) && key == 0x2E)
            return NeraControlCodes.HotkeyReserved;
        if (modifiers == 0 && !(key is >= 0x70 and <= 0x87 && key != 0x79))
            return NeraControlCodes.HotkeyModifierRequired;
        return NeraControlCodes.Ok;
    }

    /// <summary>Recorder preview only; registration/persistence still performs the final transaction.</summary>
    public static string ValidateRecordingCandidate(NeraHotkeyAction action, NeraHotkeyChord chord,
        IReadOnlyList<NeraHotkeyBinding> bindings)
    {
        string code = Validate(chord);
        if (code != NeraControlCodes.Ok) return code;
        if (action == NeraHotkeyAction.EmergencyStopFallback ||
            new NeraHotkeyChord(chord.PersistedModifiers, chord.VirtualKey) == FixedEmergencyChord)
            return NeraControlCodes.HotkeyFixedEmergency;
        if (bindings.Any(binding => binding.Registered && binding.Action != action &&
            binding.VirtualKey == chord.VirtualKey &&
            (binding.Modifiers & NeraHotkeyModifiers.Supported) == chord.PersistedModifiers))
            return NeraControlCodes.HotkeyInternalDuplicate;
        return NeraControlCodes.Ok;
    }

    public static string FormatChord(NeraHotkeyChord chord)
    {
        var parts = new List<string>(5);
        if ((chord.PersistedModifiers & NeraHotkeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((chord.PersistedModifiers & NeraHotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((chord.PersistedModifiers & NeraHotkeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((chord.PersistedModifiers & NeraHotkeyModifiers.Win) != 0) parts.Add("Win");
        parts.Add(chord.VirtualKey switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)chord.VirtualKey).ToString(),
            >= 0x70 and <= 0x87 => $"F{chord.VirtualKey - 0x6F}",
            0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x1B => "Esc", 0x20 => "Space",
            0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End", 0x24 => "Home",
            0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
            0x2D => "Insert", 0x2E => "Delete", _ => $"VK 0x{chord.VirtualKey:X2}"
        });
        return string.Join(" + ", parts);
    }
}

/// <summary>Only Nera-owned shortcut preferences; a null chord is an explicit per-action clear.</summary>
public interface INeraHotkeyPreferenceStore
{
    IReadOnlyDictionary<NeraHotkeyAction, NeraHotkeyChord?> Load();
    void Save(IReadOnlyDictionary<NeraHotkeyAction, NeraHotkeyChord?> preferences);
}

/// <summary>
/// Runtime evidence for the requested and the actual chord. ActualChord is the value
/// that the operating system accepted in this process; it is never inferred from a saved value.
/// </summary>
public sealed record NeraHotkeyRegistrationEvidence
{
    public required NeraHotkeyAction Action { get; init; }
    public NeraHotkeyChord? RequestedChord { get; init; }
    public NeraHotkeyChord? ActualChord { get; init; }
    public NeraHotkeyRegistrationStatus Status { get; init; }
    public int? CandidateIndex { get; init; }
    public bool UsedFallbackCandidate { get; init; }
    public bool NoRepeatApplied { get; init; }
    public int Win32Error { get; init; }
    public required string Code { get; init; }
    public required string UserMessage { get; init; }

    public bool Registered => Status is NeraHotkeyRegistrationStatus.Registered or
        NeraHotkeyRegistrationStatus.PreviousBindingRestored;

    public NeraHotkeyBinding ToBinding() => ActualChord is { } actual
        ? actual.ToBinding(Action, Registered,
            Code == NeraControlCodes.Ok ? null : Code)
        : new NeraHotkeyBinding
        {
            Action = Action,
            Modifiers = RequestedChord?.PersistedModifiers ?? 0,
            VirtualKey = RequestedChord?.VirtualKey ?? 0,
            Registered = false,
            ErrorCode = Code
        };
}

public sealed record NeraHotkeyRegistrationSet
{
    public required IReadOnlyList<NeraHotkeyRegistrationEvidence> Registrations { get; init; }
    public IReadOnlyList<NeraHotkeyBinding> Bindings => Registrations.Select(value => value.ToBinding()).ToArray();
    public int RegisteredCount => Registrations.Count(value => value.Registered);

    /// <summary>
    /// HoldOriginal is deliberately excluded: RegisterHotKey has WM_HOTKEY key-down semantics
    /// and cannot supply the key-up edge required for a safe hold gesture.
    /// </summary>
    public bool AllRegisterHotKeyActionsRegistered => Registrations
        .All(value => value.Code == NeraControlCodes.Ok && (value.Registered ||
            value.Action != NeraHotkeyAction.EmergencyStopFallback &&
            value.Status == NeraHotkeyRegistrationStatus.Unregistered));
}

public readonly record struct NeraHotkeyPlatformResult(bool Succeeded, int Win32Error)
{
    public static NeraHotkeyPlatformResult Success { get; } = new(true, 0);
    public static NeraHotkeyPlatformResult Failure(int win32Error) => new(false, win32Error);
}

/// <summary>
/// Small operating-system seam so candidate selection and cleanup can be tested without
/// registering machine-global shortcuts in contract tests.
/// </summary>
public interface IGlobalHotkeyPlatform : IAsyncDisposable
{
    event Action<NeraHotkeyPlatformActivation>? Activated;

    NeraHotkeyPlatformResult TryRegister(int id, uint modifiers, uint virtualKey);
    NeraHotkeyPlatformResult TryUnregister(int id);
}

public sealed record NeraHotkeyPlatformActivation(int RegistrationId, NeraInputOrigin Origin);

public sealed class NeraHotkeyActivatedEventArgs : EventArgs
{
    public NeraHotkeyActivatedEventArgs(NeraHotkeyAction action, NeraInputOrigin? origin = null)
    { Action = action; Origin = origin; }
    public NeraHotkeyAction Action { get; }
    public NeraInputOrigin? Origin { get; }
}

public static class NeraHotkeyCandidates
{
    private const uint CtrlAlt = NeraHotkeyModifiers.Control | NeraHotkeyModifiers.Alt;
    private const uint CtrlShift = NeraHotkeyModifiers.Control | NeraHotkeyModifiers.Shift;
    private const uint AltShift = NeraHotkeyModifiers.Alt | NeraHotkeyModifiers.Shift;

    private static readonly IReadOnlyDictionary<NeraHotkeyAction, NeraHotkeyChord[]> Values =
        new Dictionary<NeraHotkeyAction, NeraHotkeyChord[]>
        {
            [NeraHotkeyAction.ToggleDldr] = Letter('D'),
            [NeraHotkeyAction.ToggleHud] = Letter('P'),
            [NeraHotkeyAction.ToggleAppVisibility] = Letter('N'),
            [NeraHotkeyAction.HoldOriginal] = [],
            [NeraHotkeyAction.EmergencyStop] =
            [
                new(CtrlShift, 0x08),
                new(CtrlAlt, 0x23),       // End
                new(CtrlShift, 0x23)
            ],
            [NeraHotkeyAction.EmergencyStopFallback] = [NeraHotkeyPolicy.FixedEmergencyChord]
        };

    public static IReadOnlyList<NeraHotkeyChord> For(NeraHotkeyAction action) =>
        Values.TryGetValue(action, out var values) ? values : [];

    private static NeraHotkeyChord[] Letter(char key) =>
    [
        new(CtrlAlt, key),
        new(CtrlShift, key),
        new(AltShift, key)
    ];
}
