#pragma once

#include <cstdint>
#include <string_view>

namespace ChipsStudio::Nera
{
    struct GlobalStartupRetryPolicy final
    {
        // Unqualified for product use: managed/native startup waits do not yet
        // share one absolute deadline. Keep the implementation fail-closed and
        // require an explicit user retry rather than restart behind the UI.
        // This does not disable the separate bounded protected-content recovery.
        static constexpr bool AutomaticStartupRetryEnabled = false;
        static constexpr std::uint32_t MaximumRetries = 1;
        static constexpr std::uint64_t BackoffMilliseconds = 500;
        static constexpr std::uint64_t CleanupDeadlineMilliseconds = 15'000;

        [[nodiscard]] static constexpr bool Eligible(const std::wstring_view reason,
            const std::uint32_t retries, const bool requested, const bool everProcessing) noexcept
        {
            return AutomaticStartupRetryEnabled && requested && !everProcessing &&
                retries < MaximumRetries && reason == L"BLACK_FRAME_GUARD";
            // Timeout also includes communication failure, not a proven
            // transient. Capture denial and identity failures are never included.
        }

        [[nodiscard]] static constexpr bool CleanupAllowsRetry(const bool active,
            const bool processOwned, const bool processExitConfirmed,
            const bool temporaryCleanupSucceeded) noexcept
        {
            return !active && !processOwned && processExitConfirmed && temporaryCleanupSucceeded;
        }
    };
}
