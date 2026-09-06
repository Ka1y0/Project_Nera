namespace ChipsStudio.Nera.Control;

/// <summary>Only an exact registered keyboard WM_HOTKEY is allowed to become an activation.</summary>
public static class NeraHotkeyMessagePolicy
{
    public const uint WmHotkey = 0x0312;
    public static bool IsActivation(uint message, nint lParam, NeraHotkeyChord? registered)
    {
        if (message != WmHotkey || registered is not { } chord || !chord.IsValid) return false;
        ulong packed = unchecked((ulong)(long)lParam);
        uint modifiers = (uint)(packed & 0xFFFF);
        uint key = (uint)((packed >> 16) & 0xFFFF);
        return modifiers == chord.PersistedModifiers && key == chord.VirtualKey;
    }
}
