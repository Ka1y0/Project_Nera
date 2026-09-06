#include <nera/safety/StartupRecoveryPolicy.h>
#include "TestChecks.h"

int main()
{
    using Policy = ChipsStudio::Nera::GlobalStartupRetryPolicy;
    TestChecks checks;
    checks.Require(!Policy::AutomaticStartupRetryEnabled, "release default disables unqualified retry");
    constexpr std::wstring_view reasons[]{L"BLACK_FRAME_GUARD", L"HOST_TIMEOUT",
        L"FIRST_FRAME_TIMEOUT", L"CAPTURE_DENIED", L"IDENTITY_CHANGED", L""};
    for (const auto reason : reasons)
        for (unsigned attempt = 0; attempt != 3; ++attempt)
            for (unsigned requested = 0; requested != 2; ++requested)
                for (unsigned processed = 0; processed != 2; ++processed)
                    checks.Require(!Policy::Eligible(reason, attempt, requested != 0, processed != 0),
                        "no hidden automatic startup retry in any tested state");
    for (unsigned bits = 0; bits != 16; ++bits)
    {
        const bool active = (bits & 1) != 0, owned = (bits & 2) != 0;
        const bool exit = (bits & 4) != 0, clean = (bits & 8) != 0;
        checks.Require(Policy::CleanupAllowsRetry(active, owned, exit, clean) ==
            (!active && !owned && exit && clean), "cleanup requires all evidence");
    }
    return checks.Finish();
}
