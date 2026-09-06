#pragma once

namespace nera::safety
{
    // Value-only extraction of the owned-process cleanup invariant. This file
    // cannot observe or terminate a process, hide a window, or release a GPU.
    // The integration must supply coherent evidence for one owned incarnation.
    struct HostReleaseEvidence final
    {
        bool controllerActive{};
        bool processOwned{};
        bool processEverStarted{};
        // Must originate from the retained handle of the exact owned process,
        // a signaled exit, and a successful exit-code query. PID/name absence or
        // a disconnected pipe is insufficient. All relevant resources must be
        // process-local; this proof does not cover externally owned resources.
        bool processExitConfirmed{};
        bool presenterHidden{};
        bool captureStopped{};
        bool cleanReleaseAcknowledged{};
    };

    struct OffGates final
    {
        bool presenterHidden{};
        bool captureStopped{};
        bool cleanReleaseAcknowledged{};
        bool ownershipReclaimedByExit{};
        bool hostExited{};
        bool normalDisplayRestored{};

        [[nodiscard]] constexpr bool Passed() const noexcept
        {
            return presenterHidden && captureStopped &&
                (cleanReleaseAcknowledged || ownershipReclaimedByExit) &&
                hostExited && normalDisplayRestored;
        }
    };

    [[nodiscard]] constexpr OffGates EvaluateRelease(
        const HostReleaseEvidence& evidence) noexcept
    {
        const bool neverStarted = !evidence.processEverStarted && !evidence.processOwned;
        const bool exited = !evidence.controllerActive && !evidence.processOwned &&
            (neverStarted || evidence.processExitConfirmed);
        // Preserve an absent clean Release acknowledgement after a crash. The
        // separate exit proof accounts for OS-reclaimed process-local resources.
        return {evidence.presenterHidden || exited, evidence.captureStopped || exited,
            evidence.cleanReleaseAcknowledged, exited, exited,
            (evidence.presenterHidden || exited) && exited};
    }
}
