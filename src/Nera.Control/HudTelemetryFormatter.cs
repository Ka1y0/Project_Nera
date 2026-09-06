using System.Globalization;
namespace ChipsStudio.Nera.Control;
public static class HudTelemetryFormatter
{
    public static string Format(NeraStateSnapshot state)
    {
        var p = state.Performance;
        // Native telemetry and authority snapshots can arrive independently.
        // A pause/yield observation revokes stale processing numbers even
        // before the next canonical ON/OFF projection has been published.
        bool active = state.DldrOn && state.ProcessingActive && state.PresenterSucceeded &&
            !state.WarmStandby && !state.OverlayBypass && (p.PresenterTimingFlags & 4) == 0;
        return $"{Number(p.PresentFramesPerSecond, active && (p.PresenterTimingFlags & 1) != 0)} FPS   " +
            $"1% Low {Number(p.OnePercentLowFramesPerSecond, active && (p.PresenterTimingFlags & 2) != 0)}   " +
            $"GPU {Number(p.GpuPercent, p.GpuUsageAvailable, "0")}%   " +
            $"DLSS5 {Number(p.Feature18Milliseconds, active && p.Feature18Milliseconds > 0)} ms";
    }
    private static string Number(double value, bool available, string format = "0.0") =>
        available && double.IsFinite(value) && value >= 0 ? value.ToString(format, CultureInfo.InvariantCulture) : "--";
}
