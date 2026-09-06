#pragma once

#include <bit>
#include <cstdint>

namespace nera::phase11
{
    inline constexpr std::uint32_t ProtectedTileColumns = 9;
    inline constexpr std::uint32_t ProtectedTileRows = 5;
    inline constexpr std::uint32_t ProtectedMinimumRectangleTiles = 12;
    // A single non-black probe can be a transition frame at the boundary of
    // protected/UAC content.  Keep normal Windows composition visible until
    // several independently completed probes agree that capture is usable.
    inline constexpr std::uint32_t ProtectedRecoveryClearSampleHysteresis = 3;
    inline constexpr std::uint64_t ProtectedAllTiles = (1ULL << 45U) - 1ULL;
    inline constexpr std::uint32_t ProtectedMinimumObservableTiles = 9;
    inline constexpr std::uint64_t ProtectedMinimumBlackDurationMs = 600;

    struct ObservableCaptureTiles final
    {
        std::uint64_t observableMask{};
        std::uint64_t blackMask{};
        std::uint64_t ownedExcludedMask{};
        std::uint32_t observableCount{};
        bool sufficient{};
        bool allBlack{};
    };

    [[nodiscard]] constexpr ObservableCaptureTiles ClassifyObservableTiles(
        const std::uint64_t rawBlackMask, const std::uint64_t ownedControlMask,
        const std::uint64_t rawExactBlackMask) noexcept
    {
        // WDA17 can expose valid underlying desktop pixels instead of a black
        // window hole. Never discard those observed pixels merely because an
        // owned control HWND covers their monitor coordinates. Only exact zero
        // is ambiguous in that region; near-black content remains observable.
        const auto ownedExcludedMask = ProtectedAllTiles & ownedControlMask &
            rawBlackMask & rawExactBlackMask;
        const auto visible = ProtectedAllTiles & ~ownedExcludedMask;
        const auto count = static_cast<std::uint32_t>(std::popcount(visible));
        std::uint32_t rows{}, columns{};
        for (std::uint32_t y = 0; y < ProtectedTileRows; ++y)
            if (visible & (0x1FFULL << (y * ProtectedTileColumns))) ++rows;
        for (std::uint32_t x = 0; x < ProtectedTileColumns; ++x)
        {
            bool present{};
            for (std::uint32_t y = 0; y < ProtectedTileRows; ++y)
                present = present || ((visible & (1ULL << (y * ProtectedTileColumns + x))) != 0);
            if (present) ++columns;
        }
        const bool sufficient = count >= ProtectedMinimumObservableTiles && rows >= 2 && columns >= 2;
        return {visible, rawBlackMask & visible, ownedExcludedMask, count, sufficient,
            sufficient && (rawBlackMask & visible) == visible};
    }

    [[nodiscard]] constexpr bool SustainedBlackWindow(const std::uint64_t count,
        const std::uint32_t warningRun, const std::uint64_t elapsedMs) noexcept
    {
        return count >= warningRun && elapsedMs >= ProtectedMinimumBlackDurationMs;
    }

    // Conservative low-frequency classifier for an exact-black rectangular
    // region. Requiring at least 3x3 avoids ordinary one/two-row letterbox
    // bars; false positives intentionally bypass instead of covering content.
    [[nodiscard]] constexpr bool HasLargeBlackTileRegion(
        const std::uint64_t blackTileMask) noexcept
    {
        for (std::uint32_t top = 0; top < ProtectedTileRows; ++top)
        {
            for (std::uint32_t bottom = top + 2;
                bottom < ProtectedTileRows; ++bottom)
            {
                for (std::uint32_t left = 0; left < ProtectedTileColumns; ++left)
                {
                    for (std::uint32_t right = left + 2;
                        right < ProtectedTileColumns; ++right)
                    {
                        const std::uint32_t area = (bottom - top + 1U) *
                            (right - left + 1U);
                        if (area < ProtectedMinimumRectangleTiles) continue;
                        bool allBlack = true;
                        for (std::uint32_t y = top; y <= bottom && allBlack; ++y)
                        {
                            for (std::uint32_t x = left; x <= right; ++x)
                            {
                                const std::uint32_t bit =
                                    y * ProtectedTileColumns + x;
                                if ((blackTileMask & (1ULL << bit)) == 0U)
                                {
                                    allBlack = false;
                                    break;
                                }
                            }
                        }
                        if (allBlack) return true;
                    }
                }
            }
        }
        return false;
    }

    [[nodiscard]] constexpr bool HasStableLargeBlackTileRegion(
        const std::uint64_t previousMask,
        const std::uint64_t currentMask) noexcept
    {
        return HasLargeBlackTileRegion(previousMask & currentMask);
    }

    struct ProtectedContentObservation final
    {
        bool globalDisplay{};
        bool diagnosticEnabled{};
        bool suspectedBlackSequence{};
        bool lastSampleWasBlack{};
        std::uint64_t completedSamples{};
        bool suspectedProtectedRegionSequence{};
        bool lastSampleHadLargeBlackRegion{};
        std::uint64_t consecutiveClearSamples{};
        bool observableCoverageSufficient{true};
    };

    enum class ProtectedContentDecision : std::uint32_t
    {
        WaitWithPresenterHidden = 0,
        AllowPresenterActivation = 1,
        RestoreNormalWindowsOutput = 2,
    };

    // WGC does not identify DRM content. A sustained full-screen or stable
    // large exact-black tile region is therefore only a conservative heuristic
    // for protected, unavailable, or otherwise unsafe capture. Nera never
    // attempts to circumvent it. Legitimate black content may intentionally
    // bypass.
    [[nodiscard]] constexpr ProtectedContentDecision
    EvaluateProtectedContentObservation(
        const ProtectedContentObservation& observation) noexcept
    {
        if (!observation.globalDisplay || !observation.diagnosticEnabled)
            return ProtectedContentDecision::AllowPresenterActivation;
        if (!observation.observableCoverageSufficient)
            return ProtectedContentDecision::WaitWithPresenterHidden;
        if (observation.suspectedBlackSequence ||
            observation.suspectedProtectedRegionSequence)
            return ProtectedContentDecision::RestoreNormalWindowsOutput;
        if (observation.completedSamples == 0 ||
            observation.lastSampleWasBlack ||
            observation.lastSampleHadLargeBlackRegion)
            return ProtectedContentDecision::WaitWithPresenterHidden;
        if (observation.consecutiveClearSamples <
            ProtectedRecoveryClearSampleHysteresis)
            return ProtectedContentDecision::WaitWithPresenterHidden;
        return ProtectedContentDecision::AllowPresenterActivation;
    }

    [[nodiscard]] constexpr bool MustHideActiveGlobalPresenter(
        const ProtectedContentObservation& observation,
        const bool presenterActivated) noexcept
    {
        return presenterActivated &&
            EvaluateProtectedContentObservation(observation) ==
                ProtectedContentDecision::RestoreNormalWindowsOutput;
    }
}
