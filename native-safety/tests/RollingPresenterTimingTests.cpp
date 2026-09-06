// SPDX-License-Identifier: MIT
#include "RollingPresenterTiming.h"
#include <cmath>
#include <iostream>
using namespace nera::phase6;
int main()
{
    RollingPresenterTiming timing;
    timing.Reset(0, 60'000);
    if (timing.Snapshot(0).flags != 0) return 1;
    for (std::uint64_t frame = 1; frame <= 1'800; ++frame)
        timing.RecordSuccessfulPresent(frame * 1'000);
    auto value = timing.Snapshot(1'800'000);
    if (value.flags != 3 || value.presentFps != 60 ||
        std::abs(value.averagePresentFps - 60) > 0.001 ||
        std::abs(value.onePercentLowFps - 60) > 0.001 ||
        std::abs(value.frameTimeP99Ms - 1000.0 / 60) > 0.001) return 2;
    value = timing.Snapshot(1'920'000);
    if ((value.flags & 1) == 0 || value.presentFps != 0) return 3;
    timing.Reset(1'920'000, 60'000, true);
    timing.RecordSuccessfulPresent(2'000'000);
    value = timing.Snapshot(6'000'000);
    if (value.flags != 4 || value.sampleCount != 0) return 4;
    timing.Reset(6'000'000, 60'000);
    for (std::uint64_t frame = 1; frame <= 120; ++frame)
        timing.RecordSuccessfulPresent(6'000'000 + frame * 1'000);
    value = timing.Snapshot(6'120'000);
    if (value.flags != 3 || value.presentFps != 60 || value.sampleCount != 119) return 5;
    timing.Reset(0, 1'000'000);
    std::uint64_t now{};
    timing.RecordSuccessfulPresent(now);
    for (std::uint64_t frame = 1; frame <= 1'000; ++frame)
    { now += frame % 100 == 0 ? 100'000 : 10'000; timing.RecordSuccessfulPresent(now); }
    value = timing.Snapshot(now);
    if (std::abs(value.onePercentLowFps - 10) > 0.001 || value.sampleCount != 1'000) return 6;
    timing.Snapshot(now + 31'000'000);
    value = timing.Snapshot(now + 32'000'000);
    if (value.sampleCount != 0 || value.flags != 1) return 7;
    std::cout << "PASS: real successful-Present 1s rate/30s intervals, reciprocal slowest1%, P99, pause/yield reset, no stale lifetime FPS\n";
    return 0;
}
