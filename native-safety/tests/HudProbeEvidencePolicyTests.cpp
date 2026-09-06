#include "HudProbeEvidencePolicy.h"
#include <iostream>
#include <limits>

int main()
{
    using namespace Nera::HudProbeEvidencePolicy;
    const Rect monitor{-3840, 0, 0, 2160}, baseline{-2300, 20, -1540, 62};
    const bool checks[]{
        SameRoi(baseline, baseline, monitor),
        !SameRoi(baseline, {-2299, 20, -1539, 62}, monitor),
        !SameRoi(baseline, {-2300, 20, -1540, 63}, monitor),
        !SameRoi({0, 0, 0, 0}, {0, 0, 0, 0}, monitor),
        !SameRoi(baseline, baseline, {0, 0, 3840, 2160}),
        !SameRoi({-3841, 20, -1540, 62}, {-3841, 20, -1540, 62}, monitor),
        TextOnlyAlpha(1000, 900, 100, 0),
        !TextOnlyAlpha(0, 0, 0, 0),
        !TextOnlyAlpha(1000, 1000, 0, 0),
        !TextOnlyAlpha(1000, 900, 99, 0),
        !TextOnlyAlpha(1000, 900, 100, 1),
        !TextOnlyAlpha(1000, 700, 300, 0),
        TextOnlyAlpha(1000, 701, 299, 0),
        !TextOnlyAlpha(1000, 0, 1000, 0),
        !TextOnlyAlpha(1000, std::numeric_limits<std::uint64_t>::max(), 1001, 0),
        TestColorArrived(1000, 901, 0, true),
        !TestColorArrived(1000, 900, 0, true),
        !TestColorArrived(1000, 32, 967, true),
        TestColorArrived(1000, 32, 967, false),
        !TestColorArrived(1000, 1000, 0, false),
        !TestColorArrived(0, 0, 0, true),
        !TestColorArrived(1000, 1001, 0, true)
    };
    for (const auto check : checks) if (!check) { std::cerr << "HUD evidence policy FAIL\n"; return 1; }
    std::cout << "HUD evidence policy 22/22 PASS (pure; no window or WGC execution)\n";
}
