#pragma once

#include <cstdint>

namespace Nera::HudProbeEvidencePolicy
{
    struct Rect
    {
        std::int64_t left{}, top{}, right{}, bottom{};
    };

    constexpr bool Valid(Rect value) noexcept
    {
        return value.right > value.left && value.bottom > value.top;
    }

    constexpr bool SameRoi(Rect baseline, Rect shown, Rect monitor) noexcept
    {
        return Valid(baseline) && Valid(monitor) && baseline.left == shown.left &&
            baseline.top == shown.top && baseline.right == shown.right && baseline.bottom == shown.bottom &&
            baseline.left >= monitor.left && baseline.top >= monitor.top &&
            baseline.right <= monitor.right && baseline.bottom <= monitor.bottom;
    }

    // Counts come from the actual alpha surface, not a declared background color.
    constexpr bool TextOnlyAlpha(std::uint64_t surface, std::uint64_t transparent,
        std::uint64_t glyph, std::uint64_t backgroundAlpha) noexcept
    {
        return backgroundAlpha == 0 && surface > 0 && transparent < surface && glyph > 0 &&
            glyph == surface - transparent && static_cast<double>(transparent) / static_cast<double>(surface) > 0.70;
    }

    constexpr bool TestColorArrived(std::uint64_t pixels, std::uint64_t bright,
        std::uint64_t dark, bool expectedWhite) noexcept
    {
        if (pixels == 0 || bright > pixels || dark > pixels) return false;
        return static_cast<double>(expectedWhite ? bright : dark) / static_cast<double>(pixels) > 0.90;
    }
}
