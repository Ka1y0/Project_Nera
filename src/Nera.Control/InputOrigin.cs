namespace ChipsStudio.Nera.Control;

public enum NeraInputKind
{
    Unspecified,
    UiButton,
    KeyboardHotkey,
    Keyboard,
    Mouse,
    Wheel,
    Pointer,
    RawMouse,
    InternalObservation,
    InternalLifecycle,
    SyntheticTest
}

/// <summary>
/// Diagnostic provenance, not an authorization credential. HWND/thread/PID identify the
/// Nera receiver when recorded from WM_HOTKEY, not an inferred foreground application.
/// A semantic UI button invocation is distinct from an arbitrary mouse window message.
/// </summary>
public sealed record NeraInputOrigin
{
    public uint? Message { get; init; }
    public int? RegistrationId { get; init; }
    public uint? Vk { get; init; }
    public uint? Modifiers { get; init; }
    public long? SourceHwnd { get; init; }
    public uint? SourceThreadId { get; init; }
    public int? SourcePid { get; init; }
    public NeraInputKind InputKind { get; init; }
    public string? ControlId { get; init; }

    public static NeraInputOrigin UiButton(string controlId, long? sourceHwnd = null,
        uint? sourceThreadId = null) => new()
    {
        InputKind = NeraInputKind.UiButton,
        ControlId = controlId,
        SourceHwnd = sourceHwnd,
        SourceThreadId = sourceThreadId,
        SourcePid = Environment.ProcessId
    };
}

public static class NeraInputOriginPolicy
{
    public static bool IsDisplayToggle(NeraCommandKind kind) => kind is
        NeraCommandKind.EnableDldr or NeraCommandKind.DisableDldr or NeraCommandKind.EmergencyStop;

    public static bool IsForbiddenDisplayToggle(NeraCommandEnvelope command) =>
        IsDisplayToggle(command.Kind) && command.InputOrigin is { } origin &&
        (origin.InputKind is NeraInputKind.Mouse or NeraInputKind.Wheel or
            NeraInputKind.Pointer or NeraInputKind.RawMouse ||
         origin.Message is { } message && message != 0x0312);

    // Raw input is never interpreted as a display command. WM_HOTKEY is the only
    // global keyboard activation route; buttons arrive through the explicit semantic UI route.
    public static bool IsNonCommandInputMessage(uint message) => message is
        0x0021 or >= 0x00A0 and <= 0x00AD or 0x00FF or 0x0119 or
        >= 0x0200 and <= 0x020E or >= 0x0238 and <= 0x023A or
        >= 0x0240 and <= 0x024F or 0x02A0 or 0x02A1 or 0x02A2 or 0x02A3;

    internal static void Validate(NeraInputOrigin? origin)
    {
        if (origin is null) return; // Existing Agent/CLI commands remain compatible.
        if (!Enum.IsDefined(origin.InputKind) || origin.SourcePid < 0 ||
            origin.RegistrationId is < 0 or > 0xBFFF ||
            origin.ControlId is { Length: > 96 } ||
            origin.ControlId?.Any(value => !(char.IsAsciiLetterOrDigit(value) || value is '_' or '.' or '-' or ':')) == true)
            throw new ArgumentException("Input origin metadata is invalid.", nameof(origin));
        if (origin.InputKind == NeraInputKind.KeyboardHotkey &&
            (origin.Message != 0x0312 || origin.RegistrationId is null || origin.Vk is null ||
             origin.Modifiers is null || !new NeraHotkeyChord(origin.Modifiers.Value, origin.Vk.Value).IsValid))
            throw new ArgumentException("Keyboard hotkey origin requires an actual WM_HOTKEY registration.", nameof(origin));
        if (origin.InputKind == NeraInputKind.UiButton && string.IsNullOrWhiteSpace(origin.ControlId))
            throw new ArgumentException("A semantic UI button requires its control ID.", nameof(origin));
    }
}
