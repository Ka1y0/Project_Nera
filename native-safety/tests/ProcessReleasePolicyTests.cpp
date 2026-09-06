#include <nera/safety/ProcessReleasePolicy.h>
#include "TestChecks.h"

int main()
{
    using namespace nera::safety;
    TestChecks checks;
    for (unsigned bits = 0; bits != 64; ++bits)
    {
        const OffGates gates{(bits & 1) != 0, (bits & 2) != 0,
            (bits & 4) != 0, (bits & 8) != 0, (bits & 16) != 0, (bits & 32) != 0};
        checks.Require(gates.Passed() == ((bits & 1) != 0 && (bits & 2) != 0 &&
            (bits & 12) != 0 && (bits & 16) != 0 && (bits & 32) != 0),
            "all restoration gates required; release acknowledgement OR exit reclamation");
    }
    for (unsigned bits = 0; bits != 128; ++bits)
    {
        const HostReleaseEvidence evidence{(bits & 1) != 0, (bits & 2) != 0,
            (bits & 4) != 0, (bits & 8) != 0, (bits & 16) != 0,
            (bits & 32) != 0, (bits & 64) != 0};
        const auto gates = EvaluateRelease(evidence);
        const bool expectedExit = !evidence.controllerActive && !evidence.processOwned &&
            (!evidence.processEverStarted || evidence.processExitConfirmed);
        checks.Require(gates.Passed() == expectedExit, "coherent owned exit required");
        checks.Require(gates.cleanReleaseAcknowledged == evidence.cleanReleaseAcknowledged,
            "never fabricate clean release acknowledgement");
    }
    HostReleaseEvidence crash{false, false, true, true, false, false, false};
    const auto crashGates = EvaluateRelease(crash);
    checks.Require(crashGates.Passed() && !crashGates.cleanReleaseAcknowledged &&
        crashGates.ownershipReclaimedByExit, "crash exit proves safe OFF without clean Release");
    crash.processExitConfirmed = false;
    checks.Require(!EvaluateRelease(crash).Passed(), "pipe loss alone cannot prove process exit");
    crash.processExitConfirmed = true;
    crash.controllerActive = true;
    checks.Require(!EvaluateRelease(crash).Passed(), "active controller plus stale exit snapshot rejected");
    crash.controllerActive = false;
    crash.processOwned = true;
    checks.Require(!EvaluateRelease(crash).Passed(), "owned process still present rejected");
    auto incomplete = crashGates;
    incomplete.normalDisplayRestored = false;
    checks.Require(!incomplete.Passed(), "exit proof cannot bypass restoration gate");
    const auto notStarted = EvaluateRelease({});
    checks.Require(notStarted.Passed() && !notStarted.cleanReleaseAcknowledged,
        "never-started idle state needs no invented release acknowledgement");
    return checks.Finish();
}
