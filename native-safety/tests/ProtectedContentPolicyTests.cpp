#include <nera/safety/ProtectedContentPolicy.h>
#include "TestChecks.h"

using namespace nera::phase11;

constexpr std::uint64_t Rectangle(unsigned left, unsigned top, unsigned width, unsigned height)
{
    std::uint64_t result{};
    for (unsigned y = top; y < top + height; ++y)
        for (unsigned x = left; x < left + width; ++x)
            result |= 1ULL << (y * ProtectedTileColumns + x);
    return result;
}

int main()
{
    TestChecks checks;
    constexpr auto all = ProtectedAllTiles;
    constexpr auto hole = Rectangle(1, 1, 4, 3);
    constexpr auto moved = Rectangle(5, 1, 4, 3);
    auto tiles = ClassifyObservableTiles(0, all, 0);
    checks.Require(tiles.observableCount == 45 && tiles.sufficient && !tiles.allBlack &&
        tiles.ownedExcludedMask == 0, "full-screen owned window retains valid underlying pixels");
    tiles = ClassifyObservableTiles(all, all, all);
    checks.Require(tiles.observableCount == 0 && !tiles.sufficient && !tiles.allBlack,
        "full-screen ambiguous black is not invented valid capture");
    tiles = ClassifyObservableTiles(hole, all, hole);
    checks.Require(tiles.observableCount == 33 && tiles.sufficient &&
        tiles.blackMask == 0 && tiles.ownedExcludedMask == hole, "partial exact-black hole");
    tiles = ClassifyObservableTiles(all, all, 0);
    checks.Require(tiles.observableCount == 45 && tiles.sufficient && tiles.allBlack &&
        tiles.ownedExcludedMask == 0, "nonzero near-black content remains guarded");
    tiles = ClassifyObservableTiles(all, 0, all);
    checks.Require(tiles.allBlack && tiles.observableCount == 45, "unowned all-black remains guarded");
    tiles = ClassifyObservableTiles(hole, hole, hole);
    checks.Require(tiles.ownedExcludedMask == hole, "original verified owned region");
    tiles = ClassifyObservableTiles(hole, moved, hole);
    checks.Require(tiles.ownedExcludedMask == 0 && tiles.blackMask == hole,
        "moved window must not reuse previous mask");
    tiles = ClassifyObservableTiles(hole, 0, hole);
    checks.Require(tiles.ownedExcludedMask == 0 && HasLargeBlackTileRegion(tiles.blackMask),
        "hidden window leaves unowned black region observable");
    tiles = ClassifyObservableTiles(moved, hole, moved);
    checks.Require(HasLargeBlackTileRegion(tiles.blackMask), "independent protected-looking region not ignored");
    tiles = ClassifyObservableTiles(all, all ^ Rectangle(0, 0, 9, 1), all);
    checks.Require(tiles.observableCount == 9 && !tiles.sufficient, "one row is insufficient");
    tiles = ClassifyObservableTiles(all, all ^ Rectangle(0, 0, 3, 3), all);
    checks.Require(tiles.observableCount == 9 && tiles.sufficient && tiles.allBlack,
        "nine spread samples satisfy coverage but still detect all-black");
    checks.Require(!HasLargeBlackTileRegion(Rectangle(0, 0, 9, 2)), "two-row letterbox is not a 3D region");
    checks.Require(HasLargeBlackTileRegion(hole), "4x3 rectangle detected");
    checks.Require(!HasStableLargeBlackTileRegion(hole, moved), "moving disjoint regions not stable");
    checks.Require(HasStableLargeBlackTileRegion(hole, hole), "repeated region stable");
    checks.Require(!SustainedBlackWindow(3, 3, 599), "count alone insufficient");
    checks.Require(!SustainedBlackWindow(2, 3, 600), "duration alone insufficient");
    checks.Require(SustainedBlackWindow(3, 3, 600), "count and minimum time satisfied");
    ProtectedContentObservation observation;
    observation.globalDisplay = true;
    observation.diagnosticEnabled = true;
    checks.Require(EvaluateProtectedContentObservation(observation) ==
        ProtectedContentDecision::WaitWithPresenterHidden, "first sample has not arrived");
    observation.completedSamples = 3;
    observation.consecutiveClearSamples = 3;
    checks.Require(EvaluateProtectedContentObservation(observation) ==
        ProtectedContentDecision::AllowPresenterActivation, "repeated clear samples permit activation");
    observation.observableCoverageSufficient = false;
    checks.Require(EvaluateProtectedContentObservation(observation) ==
        ProtectedContentDecision::WaitWithPresenterHidden, "insufficient coverage never becomes valid");
    observation.observableCoverageSufficient = true;
    observation.suspectedBlackSequence = true;
    checks.Require(EvaluateProtectedContentObservation(observation) ==
        ProtectedContentDecision::RestoreNormalWindowsOutput, "sustained unavailable capture restores output");
    checks.Require(MustHideActiveGlobalPresenter(observation, true), "active output hides on restore decision");
    checks.Require(!MustHideActiveGlobalPresenter(observation, false), "inactive output stays inactive");
    observation.suspectedBlackSequence = false;
    observation.consecutiveClearSamples = 2;
    checks.Require(EvaluateProtectedContentObservation(observation) ==
        ProtectedContentDecision::WaitWithPresenterHidden, "clear-sample hysteresis required");
    observation.lastSampleHadLargeBlackRegion = true;
    observation.consecutiveClearSamples = 3;
    checks.Require(EvaluateProtectedContentObservation(observation) ==
        ProtectedContentDecision::WaitWithPresenterHidden, "current black region vetoes activation");
    observation.suspectedProtectedRegionSequence = true;
    checks.Require(EvaluateProtectedContentObservation(observation) ==
        ProtectedContentDecision::RestoreNormalWindowsOutput, "persistent black region restores output");
    return checks.Finish();
}
