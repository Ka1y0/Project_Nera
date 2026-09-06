using System.Globalization;
using ChipsStudio.Nera.Control;

namespace ChipsStudio.Nera.Control.Tests;

internal static class HudTelemetryFormatterTests
{
    internal static Task RunAll()
    {
        static NeraStateSnapshot Active(NeraPerformanceSnapshot performance) => NeraStateSnapshot.CreateInitial() with
        {
            DldrOn = true, ProcessingActive = true, PresenterSucceeded = true,
            DldrActualState = NeraDldrActualState.Processing, Performance = performance
        };
        static void Equal(string expected, string actual, string reason)
        {
            if (expected != actual) throw new InvalidOperationException($"{reason}: expected '{expected}', got '{actual}'");
        }
        static void NoProcessingNumbers(NeraStateSnapshot state, string reason) => Equal(
            "-- FPS   1% Low --   GPU --%   DLSS5 -- ms", HudTelemetryFormatter.Format(state), reason);

        var measured = new NeraPerformanceSnapshot
        {
            PresenterTimingFlags = 3, PresenterTimingSampleCount = 1799,
            PresentFramesPerSecond = 60, AverageFramesPerSecond = 59.4,
            OnePercentLowFramesPerSecond = 58, FrameTimeP99Milliseconds = 17.2,
            GpuUsageAvailable = true, GpuPercent = 72, Feature18Milliseconds = 10.1
        };
        Equal("60.0 FPS   1% Low 58.0   GPU 72%   DLSS5 10.1 ms",
            HudTelemetryFormatter.Format(Active(measured)), "HUD uses actual Present, not average/source/game FPS");
        Equal("60.0 FPS   1% Low --   GPU 72%   DLSS5 10.1 ms",
            HudTelemetryFormatter.Format(Active(measured with { PresenterTimingFlags = 1 })), "flags1 cannot fabricate rolling interval statistics");
        Equal("-- FPS   1% Low 58.0   GPU 72%   DLSS5 10.1 ms",
            HudTelemetryFormatter.Format(Active(measured with { PresenterTimingFlags = 2 })), "flags2 cannot fabricate current FPS");
        Equal("-- FPS   1% Low --   GPU --%   DLSS5 10.1 ms",
            HudTelemetryFormatter.Format(Active(measured with { PresenterTimingFlags = 0, GpuUsageAvailable = false, GpuPercent = 0 })),
            "unavailable zero-initialized telemetry must render dashes");
        Equal("0.0 FPS   1% Low 0.0   GPU 0%   DLSS5 10.1 ms",
            HudTelemetryFormatter.Format(Active(measured with { PresentFramesPerSecond = 0, OnePercentLowFramesPerSecond = 0, GpuPercent = 0 })),
            "an explicitly measured zero is preserved, never confused with missing data");

        var missingGpu = measured with { GpuUsageAvailable = false };
        NoProcessingNumbers(Active(missingGpu) with { DldrOn = false, ProcessingActive = false }, "OFF rejects stale previous-session timing");
        NoProcessingNumbers(Active(missingGpu) with { PresenterSucceeded = false }, "pre-Present cannot display active values");
        NoProcessingNumbers(Active(missingGpu) with { OverlayBypass = true }, "overlay-yield evidence outranks briefly stale ON flags");
        NoProcessingNumbers(Active(missingGpu) with { WarmStandby = true }, "warm OFF evidence outranks briefly stale ON flags");
        NoProcessingNumbers(Active(missingGpu with { PresenterTimingFlags = 4 }), "wire suspended flag outranks stale processing snapshot");
        NoProcessingNumbers(Active(measured with
        {
            PresentFramesPerSecond = double.NaN, OnePercentLowFramesPerSecond = double.PositiveInfinity,
            GpuPercent = double.NegativeInfinity, Feature18Milliseconds = double.NaN
        }), "NaN/Inf are never shown as telemetry");
        NoProcessingNumbers(Active(measured with
        {
            PresentFramesPerSecond = -1, OnePercentLowFramesPerSecond = -1,
            GpuPercent = -1, Feature18Milliseconds = 0
        }), "negative or unavailable measurements are rejected");
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Equal("60.0 FPS   1% Low 58.0   GPU 72%   DLSS5 10.1 ms",
                HudTelemetryFormatter.Format(Active(measured)), "fixed telemetry decimals are culture invariant");
        }
        finally { CultureInfo.CurrentCulture = original; }
        return Task.CompletedTask;
    }
}
