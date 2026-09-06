using System.Buffers.Binary;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChipsStudio.Nera.Control;

namespace ChipsStudio.Nera.Control.Tests;

internal static class Program
{
    private static readonly TestCase[] Tests =
    [
        new("initial_state_is_fail_closed", InitialStateIsFailClosed),
        new("phase14_native_health_exact_fields", Phase14NativeLifecycleHealthTests.ExactNativeFields),
        new("phase14_native_health_unknown_and_missing", Phase14NativeLifecycleHealthTests.UnknownAndMissingStayUnavailable),
        new("phase14_native_health_age_and_exit_proof", Phase14NativeLifecycleHealthTests.AgesAndExitNeedEvidence),
        new("phase14_native_health_observation_and_audit", Phase14NativeLifecycleHealthTests.ObservationAndAuditKeepOneTypedSnapshot),
        new("hud_live_present_timing_and_unknown_states", HudTelemetryFormatterTests.RunAll),
        new("global_pipeline_uses_strict_order_and_first_frame_gate", GlobalPipelineUsesStrictOrder),
        new("broker_and_host_evidence_are_independent", BrokerAndHostEvidenceAreIndependent),
        new("owned_broker_without_readiness_cannot_skip_failsafe", OwnedBrokerWithoutReadinessCannotSkipFailSafe),
        new("first_frame_rejects_missing_feature_dldr_presenter_or_foreground", FirstFrameRejectsMissingEvidence),
        new("first_frame_default_failure_message_is_neutral", FirstFrameDefaultFailureMessageIsNeutral),
        new("display_is_required", DisplayIsRequired),
        new("windows_hdr_is_required", WindowsHdrIsRequired),
        new("rtx_evidence_is_required", RtxEvidenceIsRequired),
        new("runtime_identity_is_required", RuntimeIdentityIsRequired),
        new("broker_start_failure_preserves_native_diagnostic_and_verified_identity", BrokerStartFailurePreservesDiagnostic),
        new("runtime_configuration_requires_strict_off", RuntimeConfigurationRequiresStrictOff),
        new("runtime_configuration_commits_verified_state_and_observes_cleanly", RuntimeConfigurationCommitsVerifiedState),
        new("failed_runtime_selection_clears_session_identity", FailedRuntimeSelectionClearsSessionIdentity),
        new("capture_failure_restores_bypass", CaptureFailureRestoresBypass),
        new("failed_failsafe_preserves_unreleased_ownership", FailedFailSafePreservesOwnership),
        new("disable_releases_global_ownership", DisableReleasesGlobalOwnership),
        new("failed_emergency_stop_preserves_unreleased_ownership", FailedEmergencyStopPreservesOwnership),
        new("authoritative_observer_reconciles_processing_to_bypass", AuthoritativeObserverReconcilesBypass),
        new("authoritative_observer_reconciles_processing_to_failed", AuthoritativeObserverReconcilesFailed),
        new("observation_exception_never_fabricates_release", ObservationExceptionPreservesOwnership),
        new("queued_disable_supersedes_inflight_observation_failure", QueuedDisableSupersedesObservationFailure),
        new("queued_disable_supersedes_inflight_observation_timeout", QueuedDisableSupersedesObservationTimeout),
        new("quiescent_observation_timeout_does_not_create_dldr_error", QuiescentObservationTimeoutDoesNotCreateDldrError),
        new("nonrelease_command_cannot_suppress_observation_failure", NonReleaseCommandCannotSuppressObservationFailure),
        new("stale_disable_cannot_suppress_observation_failure", StaleDisableCannotSuppressObservationFailure),
        new("display_change_disables_before_switch", DisplayChangeDisablesBeforeSwitch),
        new("performance_modes_have_exact_neural_dimensions", PerformanceModesHaveExactDimensions),
        new("ui_protection_removed_fail_closed", UiProtectionIsAuthoritative),
        new("canonical_recipe_and_independent_parameter_commands", CanonicalRecipeAndParameters),
        new("dldr_tuning_ranges_and_hot_ack", DldrTuningRangesAndHotAck),
        new("wire_canonical_bundles_and_removed_ui_protection", WireCanonicalBundles),
        new("feature18_tuning_is_strict_and_mode_calibrated", Feature18TuningIsStrictAndModeCalibrated),
        new("feature18_tuning_hot_exact_ack", Feature18TuningHotExactAck),
        new("hot_missing_stale_rejected_ack_never_commits_parameters", HotBadAcknowledgementDoesNotCommit),
        new("hot_timeout_or_exception_releases_uncertain_runtime", HotTimeoutOrExceptionFailsClosed),
        new("recipe_hot_bundle_preserves_feature_ownership", RecipeHotBundlePreservesOwnership),
        new("warm_off_retains_ownership_but_never_on", WarmOffRetainsOwnership),
        new("warm_off_missing_native_proof_fails_closed", WarmOffRequiresNativeEvidence),
        new("warm_on_requires_new_frame_without_second_create", WarmResumeRequiresFreshFrame),
        new("warm_on_rejects_stale_frame", WarmResumeRejectsStaleFrame),
        new("retained_session_transitions_require_exact_proof", RetainedSessionTransitionRequiresProof),
        new("overlay_waiting_transition_preserves_watermark_but_not_on", OverlayWaitingTransitionRequiresProof),
        new("overlay_observer_waits_for_fresh_frame_before_on", OverlayObserverWaitsForFreshFrame),
        new("warm_emergency_stop_always_cold_releases", WarmEmergencyAlwaysCold),
        new("performance_change_recreates_safely_and_regates_first_frame", PerformanceChangeRecreatesSafely),
        new("duplicate_request_is_idempotent", DuplicateRequestIsIdempotent),
        new("request_id_reuse_is_rejected", RequestIdReuseIsRejected),
        new("revision_conflict_is_rejected", RevisionConflictIsRejected),
        new("payload_shape_is_strict", PayloadShapeIsStrict),
        new("production_command_surface_is_global_only", ProductionCommandSurfaceIsGlobalOnly),
        new("wire_surface_is_global_only", WireSurfaceIsGlobalOnly),
        new("wire_rejects_legacy_and_unknown_commands", WireRejectsLegacyAndUnknownCommands),
        new("wire_round_trip_is_length_prefixed", WireRoundTripIsLengthPrefixed),
        new("wire_processing_mode_uses_public_clear_token", WireProcessingModeUsesPublicClearToken),
        new("wire_v3_strict_client_receives_protocol_mismatch", WireV3StrictClientReceivesProtocolMismatch),
        new("wire_handshake_defaults_to_deny", WireHandshakeDefaultsToDeny),
        new("wire_mutations_require_revision_and_permission", WireMutationsRequireRevisionAndPermission),
        new("all_sources_share_one_revision_lane", AllSourcesShareOneRevisionLane),
        new("windows_registerhotkey_registers_and_unregisters", ManagedHotkeyContractTests.WindowsPlatformRegistersAndUnregisters),
        new("hotkey_candidate_fallback_uses_no_repeat", ManagedHotkeyContractTests.CandidateFallbackUsesNoRepeat),
        new("hotkey_cleanup_attempts_every_registered_id", ManagedHotkeyContractTests.CleanupAttemptsEveryRegisteredId),
        new("hotkey_hold_original_fails_closed", ManagedHotkeyContractTests.RevealHoldFailsClosed),
        new("hotkey_router_uses_unified_commands", ManagedHotkeyContractTests.RouterUsesUnifiedCommands),
        new("hotkey_emergency_bypasses_normal_queue", ManagedHotkeyContractTests.RegistrationActivationAndEmergencyPriority),
        new("phase13_keyboard_rebind_atomic_persistence_and_fixed_escape", ManagedHotkeyContractTests.Phase13KeyboardRebindContracts),
        new("phase13_registered_recorder_candidates_never_execute_and_conflicts_preserve_bindings", ManagedHotkeyContractTests.RegisteredChordsReachRecorderWithoutCommands),
        new("phase13_command_origin_rejects_mouse_without_emergency_side_effect", Phase13ControlAuditTests.InputOriginCannotToggleDisplay),
        new("phase13_command_and_observation_audit_are_private_and_truthful", Phase13ControlAuditTests.CommandAndObservationAudit),
        new("phase13_rolling_audit_is_bounded_and_failure_isolated", Phase13ControlAuditTests.RollingAuditIsBounded),
        new("phase14_sampled_yield_and_host_recovery_require_new_proof", Phase14LifecycleTests.RecoveryRequiresProof),
        new("phase14_warm_expiry_uses_new_session_first_frame_proof", Phase14LifecycleTests.WarmExpiryUsesNewSessionProof),
        new("phase14_observation_failure_cause_survives_safe_restore", Phase14LifecycleTests.ObservationCause),
        new("phase14_explicit_show_is_not_short_circuited_by_visible_flag", Phase14LifecycleTests.ExplicitShow)
    ];

    public static async Task<int> Main()
    {
        var failures = new List<string>();
        foreach (TestCase test in Tests)
        {
            DateTimeOffset started = DateTimeOffset.UtcNow;
            try
            {
                await test.Body().ConfigureAwait(false);
                Console.WriteLine($"PASS {test.Name} {(DateTimeOffset.UtcNow - started).TotalMilliseconds:F0}ms");
            }
            catch (Exception error)
            {
                failures.Add(test.Name + ": " + error.Message);
                Console.WriteLine($"FAIL {test.Name}: {error}");
            }
        }
        Console.WriteLine($"TOTAL={Tests.Length}; PASS={Tests.Length - failures.Count}; FAIL={failures.Count}");
        if (failures.Count == 0)
        {
            Console.WriteLine("CONTROL_PLANE_CONTRACT_TESTS=PASS");
            return 0;
        }
        foreach (string failure in failures)
            Console.WriteLine("  " + failure);
        return 1;
    }

    private static Task InitialStateIsFailClosed()
    {
        NeraStateSnapshot state = NeraStateSnapshot.CreateInitial();
        NeraStatePolicy.Validate(state);
        Assert(!state.DldrOn && !state.DldrRequested && !state.ProcessingActive,
            "Initial state must expose normal Windows output.");
        AssertEqual("0.4.1-global-alpha.1", state.AppVersion);
        AssertEqual("0.4.1", state.RuntimeAdapterVersion);
        return Task.CompletedTask;
    }

    private static Task FirstFrameDefaultFailureMessageIsNeutral()
    {
        var outcome = new NeraFirstFrameGateOutcome
        {
            Feature18ProcessSucceeded = false,
            DldrSucceeded = false,
            PresenterSucceeded = false,
            ForegroundPreserved = false,
            FrameId = 0
        };
        Assert(!outcome.GateSatisfied, "Default failure fixture must not pass the first-frame gate.");
        Assert(!outcome.UserMessage.Contains("已恢复原画", StringComparison.Ordinal) &&
               outcome.UserMessage.Contains("正在撤销 DLDR ON", StringComparison.Ordinal),
            "Backend default must not claim restoration before strict cleanup evidence exists.");
        return Task.CompletedTask;
    }

    private static async Task GlobalPipelineUsesStrictOrder()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        var observed = new List<NeraStateSnapshot>();
        control.StateChanged += (_, state) => observed.Add(state);

        NeraCommandResult result = await EnableAsync(control, 0);
        Assert(result.Ok && result.State.DldrOn, result.TechnicalMessage);
        AssertEqual(NeraDldrActualState.Processing, result.State.DldrActualState);
        Assert(result.State.ForegroundPreserved && result.State.PresenterSucceeded,
            "Presenter and foreground evidence are required.");
        Assert(result.State.BrokerProcessOwned && !result.State.OffGateSatisfied,
            "ON must preserve auditable Broker ownership and an incomplete OFF gate.");
        AssertEqual(result.State.LastSuccessfulFrameId, result.State.LastFeature18FrameId);
        AssertEqual(result.State.LastSuccessfulFrameId, result.State.LastDldrFrameId);
        AssertEqual(result.State.LastSuccessfulFrameId, result.State.LastPresentedFrameId);
        Assert(observed.All(state => !state.DldrOn || NeraStatePolicy.IsFirstFrameGateSatisfied(state)),
            "No transition may expose ON before the complete first-frame gate.");
        AssertSubsequence(backend.Calls,
            "validateDisplay", "verifyRtxGpu", "verifyRuntime", "startBroker", "startHost",
            "createMonitorCapture", "createFeature18", "waitFirstFrame");
    }

    private static async Task RuntimeConfigurationRequiresStrictOff()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        NeraCommandResult enabled = await EnableAsync(control, 0);
        Assert(enabled.Ok && enabled.State.DldrOn, "Fixture must reach Processing before runtime change.");
        NeraCommandResult rejected = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.ConfigureRuntimeFolder, NeraCommandSource.Ui, enabled.CurrentRevision,
            NeraCommandPayload.ForRuntimeFolder(Path.GetTempPath())));
        Assert(!rejected.Ok && rejected.Code == NeraControlCodes.StateChanged &&
               rejected.State.DldrOn && backend.ExecuteCount(NeraCommandKind.ConfigureRuntimeFolder) == 0,
            "Runtime change while ON must be rejected without disabling or touching native runtime state.");
    }

    private static async Task FailedRuntimeSelectionClearsSessionIdentity()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        NeraCommandResult configured = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.ConfigureRuntimeFolder, NeraCommandSource.Ui, 0,
            NeraCommandPayload.ForRuntimeFolder(Path.GetTempPath())));
        Assert(configured.Ok && configured.State.RuntimeVerified,
            "Fixture must begin with a verified runtime identity.");

        backend.ConfigureRuntimeOutcome = NeraBackendOutcome.Failure(
            NeraControlCodes.RuntimeUnverified, "所选运行时无效", "native identity mismatch");
        NeraCommandResult failed = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.ConfigureRuntimeFolder, NeraCommandSource.Ui, configured.CurrentRevision,
            NeraCommandPayload.ForRuntimeFolder(Path.GetTempPath())));
        Assert(!failed.Ok && !failed.State.RuntimeVerified &&
               failed.State.RuntimeBinaryVersion is null && failed.State.RuntimeSha256 is null &&
               failed.State.RuntimePath is null && !failed.State.RuntimeSignatureValid &&
               failed.State.RuntimeSigner is null &&
               failed.State.DldrActualState == NeraDldrActualState.Failed,
            "A failed explicit selection must clear the old verified identity for this session.");
    }

    private static async Task RuntimeConfigurationCommitsVerifiedState()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend, fastObservation: true);
        NeraCommandResult configured = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.ConfigureRuntimeFolder, NeraCommandSource.Ui, 0,
            NeraCommandPayload.ForRuntimeFolder(Path.GetTempPath())));
        Assert(configured.Ok && configured.State.RuntimeVerified &&
               configured.State.DldrActualState == NeraDldrActualState.RuntimeVerified,
            "A successful explicit runtime configuration must atomically enter RuntimeVerified.");
        await Task.Delay(100).ConfigureAwait(false);
        NeraStateSnapshot observed = await control.GetSnapshotAsync().ConfigureAwait(false);
        Assert(observed.RuntimeVerified &&
               observed.DldrActualState == NeraDldrActualState.RuntimeVerified &&
               observed.LastError is null,
            "The authoritative observer must accept the post-configuration RuntimeVerified state.");
    }

    private static Task BrokerAndHostEvidenceAreIndependent()
    {
        NeraStateSnapshot brokerOnly = NeraStatePolicy.WithComputedDldrGate(ReadyState() with
        {
            DldrRequested = true,
            DldrActualState = NeraDldrActualState.HostStarting,
            GpuVerified = true,
            RuntimeVerified = true,
            RuntimeBinaryVersion = "310.8.0.0",
            RuntimeSha256 = FakeControlBackend.VerifiedSha256,
            RuntimeSignatureValid = true,
            RuntimeSigner = "NVIDIA Corporation",
            BrokerProcessOwned = true,
            BrokerConnected = true,
            HostConnected = false,
            OffGateSatisfied = false
        });
        NeraStatePolicy.Validate(brokerOnly);
        Assert(brokerOnly.BrokerConnected && !brokerOnly.HostConnected && !brokerOnly.DldrOn,
            "Broker ready before Host is a valid intermediate state and must remain OFF.");

        NeraStateSnapshot fullyGated = NeraStatePolicy.WithComputedDldrGate(brokerOnly with
        {
            DldrActualState = NeraDldrActualState.Processing,
            HostConnected = true,
            MonitorCaptureCreated = true,
            FeatureCreated = true,
            FirstFeature18FrameSucceeded = true,
            DldrSucceeded = true,
            PresenterSucceeded = true,
            ForegroundPreserved = true,
            ProcessingActive = true,
            LastSuccessfulFrameId = 1,
            LastFeature18FrameId = 1,
            LastDldrFrameId = 1,
            LastPresentedFrameId = 1,
            Feature18SuccessfulFrames = 1,
            DldrSuccessfulFrames = 1,
            PresentedFrames = 1
        });
        Assert(fullyGated.DldrOn, "The control policy test baseline must satisfy the complete gate.");

        NeraStateSnapshot hostWithoutBroker = NeraStatePolicy.WithComputedDldrGate(fullyGated with
        {
            BrokerConnected = false
        });
        Assert(hostWithoutBroker.HostConnected && !hostWithoutBroker.BrokerConnected &&
            !hostWithoutBroker.DldrOn &&
            !NeraStatePolicy.IsFirstFrameGateSatisfied(hostWithoutBroker),
            "Host evidence can never substitute for RuntimeBroker evidence.");
        return Task.CompletedTask;
    }

    private static async Task OwnedBrokerWithoutReadinessCannotSkipFailSafe()
    {
        var backend = new FakeControlBackend();
        NeraStateSnapshot retainedBroker = ReadyState() with
        {
            DldrRequested = false,
            DldrActualState = NeraDldrActualState.Failed,
            BrokerProcessOwned = true,
            BrokerConnected = false,
            OffGateSatisfied = true,
            RecoveryState = NeraRecoveryState.Failed,
            LastError = new NeraErrorInfo
            {
                Code = NeraControlCodes.FailSafeFailed,
                UserMessage = "RuntimeBroker 清理未确认"
            }
        };
        await using var control = Service(backend, retainedBroker);
        NeraCommandResult result = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.DisableDldr, NeraCommandSource.Ui, retainedBroker.Revision));
        Assert(result.Ok && result.RollbackPerformed,
            "A later confirmed cleanup should restore quiescent bypass.");
        Assert(backend.Calls.Contains("disableDldr:False"),
            "Owned RuntimeBroker process must prevent the OFF idempotent shortcut.");
        Assert(!result.State.BrokerProcessOwned && !result.State.BrokerConnected &&
            result.State.OffGateSatisfied,
            "Confirmed cleanup must clear ownership and satisfy the OFF gate.");
    }

    private static async Task FirstFrameRejectsMissingEvidence()
    {
        NeraFirstFrameGateOutcome[] cases =
        [
            Gate(feature: false), Gate(dldr: false), Gate(presenter: false), Gate(foreground: false)
        ];
        foreach (NeraFirstFrameGateOutcome gate in cases)
        {
            var backend = new FakeControlBackend { FirstFrameOutcome = gate };
            await using var control = Service(backend);
            NeraCommandResult result = await EnableAsync(control, 0);
            Assert(!result.Ok && result.RollbackPerformed, "Missing evidence must fail closed.");
            AssertEqual(NeraControlCodes.FirstFrameGateFailed, result.Code);
            AssertEqual(NeraDldrActualState.Bypass, result.State.DldrActualState);
            Assert(!result.State.DldrOn && !result.State.ProcessingActive,
                "A rejected frame cannot expose DLDR ON.");
        }
    }

    private static async Task DisplayIsRequired()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend, NeraStateSnapshot.CreateInitial());
        NeraCommandResult result = await EnableAsync(control, 0);
        AssertEqual(NeraControlCodes.DisplayRequired, result.Code);
        Assert(!backend.Calls.Any(), "Backend must not run without a selected display.");
    }

    private static async Task WindowsHdrIsRequired()
    {
        NeraDisplaySummary noHdr = FakeControlBackend.Display(selected: true, hdrEnabled: false);
        var backend = new FakeControlBackend
        {
            ValidateDisplayOutcome = NeraBackendOutcome.Success() with
            {
                Display = noHdr,
                Displays = [noHdr]
            }
        };
        await using var control = Service(backend, ReadyState(noHdr));
        NeraCommandResult result = await EnableAsync(control, 0);
        AssertEqual(NeraControlCodes.HdrSystemDisabled, result.Code);
        Assert(!backend.Calls.Contains("verifyRtxGpu"), "GPU validation must not run after HDR failure.");
    }

    private static async Task RtxEvidenceIsRequired()
    {
        var backend = new FakeControlBackend { GpuOutcome = NeraBackendOutcome.Success() };
        await using var control = Service(backend);
        NeraCommandResult result = await EnableAsync(control, 0);
        AssertEqual(NeraControlCodes.RtxGpuRequired, result.Code);
        Assert(!backend.Calls.Contains("verifyRuntime"), "Runtime must not run without affirmative RTX evidence.");
    }

    private static async Task RuntimeIdentityIsRequired()
    {
        var backend = new FakeControlBackend
        {
            RuntimeOutcome = NeraRuntimeVerificationOutcome.Verified(
                "310.8.0.0", new string('0', 64), FakeControlBackend.FakeRuntimePath, "Unexpected Signer")
        };
        await using var control = Service(backend);
        NeraCommandResult result = await EnableAsync(control, 0);
        AssertEqual(NeraControlCodes.RuntimeUnverified, result.Code);
        Assert(!backend.Calls.Contains("startBroker"), "Broker must not start for an invalid identity.");
    }

    private static async Task CaptureFailureRestoresBypass()
    {
        var backend = new FakeControlBackend
        {
            CaptureOutcome = NeraBackendOutcome.Failure(
                NeraControlCodes.MonitorCaptureFailed, "显示器捕获不可用")
        };
        await using var control = Service(backend);
        NeraCommandResult result = await EnableAsync(control, 0);
        Assert(!result.Ok && result.RollbackPerformed, "Capture failure must restore normal output.");
        AssertEqual(NeraDldrActualState.Bypass, result.State.DldrActualState);
        Assert(backend.Calls.Contains("failSafe:MONITOR_CAPTURE_FAILED"), "Fail-safe was not called.");
    }

    private static async Task FailedFailSafePreservesOwnership()
    {
        var backend = new FakeControlBackend
        {
            CaptureOutcome = NeraBackendOutcome.Failure(
                NeraControlCodes.MonitorCaptureFailed, "显示器捕获不可用"),
            FailSafeOutcome = NeraBackendOutcome.Failure(
                NeraControlCodes.FailSafeFailed, "无法确认普通 Windows 显示已恢复")
        };
        await using var control = Service(backend);
        NeraCommandResult result = await EnableAsync(control, 0);
        Assert(!result.Ok && !result.RollbackPerformed,
            "An unconfirmed fail-safe must not report rollback success.");
        AssertEqual(NeraDldrActualState.Failed, result.State.DldrActualState);
        AssertEqual(NeraRecoveryState.Failed, result.State.RecoveryState);
        Assert(result.State.BrokerProcessOwned && result.State.BrokerConnected && result.State.HostConnected &&
            !result.State.OffGateSatisfied,
            "Observed Host ownership must remain visible after an unconfirmed release.");
        Assert(!result.State.ProcessingActive && !result.State.DldrOn,
            "An unconfirmed release must revoke ON without fabricating cleanup.");
        Assert(result.UserMessage.Contains("清理尚未完成", StringComparison.Ordinal) &&
            !result.UserMessage.Contains("已恢复原画", StringComparison.Ordinal),
            "A failed cleanup must never claim that original output restoration is confirmed.");
    }

    private static async Task DisableReleasesGlobalOwnership()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        NeraCommandResult enabled = await EnableAsync(control, 0);
        NeraCommandResult disabled = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.DisableDldr, NeraCommandSource.Ui, enabled.CurrentRevision));
        Assert(disabled.Ok && disabled.RollbackPerformed, disabled.TechnicalMessage);
        AssertEqual(NeraDldrActualState.Bypass, disabled.State.DldrActualState);
        Assert(!disabled.State.DldrRequested && !disabled.State.DldrOn &&
            !disabled.State.BrokerProcessOwned && !disabled.State.BrokerConnected &&
            !disabled.State.HostConnected && disabled.State.OffGateSatisfied &&
            !disabled.State.MonitorCaptureCreated && !disabled.State.FeatureCreated &&
            !disabled.State.PresenterSucceeded,
            "OFF must release every global owner.");
    }

    private static async Task FailedEmergencyStopPreservesOwnership()
    {
        var backend = new FakeControlBackend
        {
            DisableOutcome = NeraBackendOutcome.Failure(
                NeraControlCodes.FailSafeFailed, "紧急停止未确认")
        };
        await using var control = Service(backend);
        NeraCommandResult enabled = await EnableAsync(control, 0);
        NeraCommandResult stopped = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.EmergencyStop, NeraCommandSource.Ui, enabled.CurrentRevision));
        Assert(!stopped.Ok && !stopped.RollbackPerformed,
            "Failed EmergencyStop must not claim cleanup.");
        AssertEqual(NeraDldrActualState.Failed, stopped.State.DldrActualState);
        Assert(stopped.State.BrokerProcessOwned && stopped.State.BrokerConnected && stopped.State.HostConnected &&
            stopped.State.MonitorCaptureCreated && stopped.State.FeatureCreated &&
            stopped.State.PresenterSucceeded && !stopped.State.OffGateSatisfied,
            "Failed EmergencyStop must preserve every last-observed ownership flag.");
        Assert(!stopped.State.ProcessingActive && !stopped.State.DldrOn,
            "Failed EmergencyStop must still revoke ON immediately.");
    }

    private static async Task AuthoritativeObserverReconcilesBypass()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend, fastObservation: true);
        NeraCommandResult enabled = await EnableAsync(control, 0);
        Assert(enabled.Ok && enabled.State.DldrOn, enabled.TechnicalMessage);

        backend.AuthoritativeObservation = current => current with
        {
            DldrRequested = false,
            DldrActualState = NeraDldrActualState.Bypass,
            ProcessingActive = false,
            BrokerProcessOwned = false,
            BrokerConnected = false,
            HostConnected = false,
            MonitorCaptureCreated = false,
            FeatureCreated = false,
            FirstFeature18FrameSucceeded = false,
            DldrSucceeded = false,
            PresenterSucceeded = false,
            ForegroundPreserved = false,
            OffGateSatisfied = true,
            LastSuccessfulFrameId = 0,
            LastFeature18FrameId = 0,
            LastDldrFrameId = 0,
            LastPresentedFrameId = 0,
            Feature18SuccessfulFrames = 0,
            DldrSuccessfulFrames = 0,
            PresentedFrames = 0,
            RecoveryState = NeraRecoveryState.BypassRestored,
            LastError = new NeraErrorInfo
            {
                Code = NeraControlCodes.OperationFailed,
                UserMessage = "处理出现问题，已恢复原画",
                TechnicalMessage = "native failClosed"
            }
        };

        NeraStateSnapshot observed = await control.WaitForStateAsync(
            state => state.DldrActualState == NeraDldrActualState.Bypass,
            TimeSpan.FromSeconds(2));
        Assert(!observed.DldrOn && !observed.ProcessingActive,
            "Native bypass must revoke managed ON.");
        Assert(!observed.BrokerProcessOwned && !observed.BrokerConnected && !observed.HostConnected &&
            !observed.MonitorCaptureCreated && !observed.FeatureCreated &&
            !observed.PresenterSucceeded && observed.OffGateSatisfied,
            "A native bypass observation may clear ownership only when its evidence does.");
        AssertEqual("native failClosed", observed.LastError?.TechnicalMessage);
    }

    private static async Task AuthoritativeObserverReconcilesFailed()
    {
        var backend = new FakeControlBackend
        {
            FailSafeOutcome = NeraBackendOutcome.Failure(
                NeraControlCodes.FailSafeFailed,
                "清理仍未确认",
                "injected retained ownership")
        };
        await using var control = Service(backend, fastObservation: true);
        NeraCommandResult enabled = await EnableAsync(control, 0);
        Assert(enabled.Ok && enabled.State.DldrOn, enabled.TechnicalMessage);

        backend.AuthoritativeObservation = current => current with
        {
            DldrRequested = false,
            DldrActualState = NeraDldrActualState.Failed,
            ProcessingActive = false,
            RecoveryState = NeraRecoveryState.Failed,
            LastError = new NeraErrorInfo
            {
                Code = NeraControlCodes.OperationFailed,
                UserMessage = "处理出现问题",
                TechnicalMessage = "native release unconfirmed"
            }
        };

        NeraStateSnapshot observed = await control.WaitForStateAsync(
            state => state.DldrActualState == NeraDldrActualState.Failed &&
                state.LastError?.Code == NeraControlCodes.FailSafeFailed,
            TimeSpan.FromSeconds(2));
        Assert(!observed.DldrOn && !observed.ProcessingActive,
            "Native failure must revoke managed ON.");
        Assert(observed.BrokerProcessOwned && observed.BrokerConnected && observed.HostConnected &&
            observed.MonitorCaptureCreated && observed.FeatureCreated &&
            observed.PresenterSucceeded && !observed.OffGateSatisfied,
            "Unreleased native ownership must remain visible in the authoritative state.");
        Assert(backend.Calls.Contains("failSafe:" + NeraControlCodes.OperationFailed),
            "The authoritative observer must retry fail-safe when native cleanup remains owned.");
    }

    private static async Task ObservationExceptionPreservesOwnership()
    {
        var backend = new FakeControlBackend
        {
            FailSafeOutcome = NeraBackendOutcome.Failure(
                NeraControlCodes.FailSafeFailed,
                "无法确认普通 Windows 显示已恢复",
                "injected fail-safe failure")
        };
        await using var control = Service(backend, fastObservation: true);
        NeraCommandResult enabled = await EnableAsync(control, 0);
        Assert(enabled.Ok && enabled.State.DldrOn, enabled.TechnicalMessage);
        backend.ObservationException = new IOException("injected observation failure");

        NeraStateSnapshot observed = await control.WaitForStateAsync(
            state => state.DldrActualState == NeraDldrActualState.Failed &&
                state.LastError?.Code == NeraControlCodes.FailSafeFailed,
            TimeSpan.FromSeconds(2));
        Assert(!observed.DldrOn && !observed.ProcessingActive,
            "Loss of authoritative health evidence must revoke ON.");
        Assert(observed.BrokerProcessOwned && observed.BrokerConnected && observed.HostConnected &&
            observed.MonitorCaptureCreated && observed.FeatureCreated &&
            observed.PresenterSucceeded && !observed.OffGateSatisfied,
            "An observation exception and failed recovery are not release evidence.");
        Assert(observed.LastError?.TechnicalMessage.Contains(
            "injected observation failure", StringComparison.Ordinal) == true,
            "The observation failure must remain diagnosable.");
        Assert(backend.Calls.Contains("failSafe:" + NeraControlCodes.OperationFailed),
            "Loss of authoritative health evidence must request fail-safe recovery.");
    }

    private static async Task DisplayChangeDisablesBeforeSwitch()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        NeraCommandResult enabled = await EnableAsync(control, 0);
        while (backend.Calls.TryDequeue(out _)) { }
        NeraCommandResult changed = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetDisplay, NeraCommandSource.Ui, enabled.CurrentRevision,
            NeraCommandPayload.ForDisplay("display:2")));
        Assert(changed.Ok && changed.RollbackPerformed, changed.TechnicalMessage);
        AssertEqual("display:2", changed.State.TargetDisplay?.DisplayId);
        AssertSubsequence(backend.Calls, "disableDldr:True", "execute:SetDisplay");
    }

    private static async Task PerformanceModesHaveExactDimensions()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        NeraStateSnapshot state = await control.GetSnapshotAsync();
        foreach ((NeraPerformanceMode mode, int scale, int width, int height) in new[]
        {
            (NeraPerformanceMode.Balanced, 90, 3456, 1944),
            (NeraPerformanceMode.Smooth, 80, 3072, 1728),
            (NeraPerformanceMode.Quality, 100, 3840, 2160)
        })
        {
            NeraCommandResult result = await control.ExecuteAsync(NeraCommandEnvelope.Create(
                NeraCommandKind.SetPerformanceMode, NeraCommandSource.Ui, state.Revision,
                NeraCommandPayload.ForPerformanceMode(mode)));
            Assert(result.Ok, result.TechnicalMessage);
            AssertEqual(scale, result.State.NeuralScale);
            AssertEqual(width, result.State.NeuralInputWidth);
            AssertEqual(height, result.State.NeuralInputHeight);
            AssertEqual(3840, result.State.OutputWidth);
            AssertEqual(2160, result.State.OutputHeight);
            state = result.State;
        }
    }

    private static async Task UiProtectionIsAuthoritative()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        NeraCommandResult result = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetUiProtection, NeraCommandSource.Ui, 0,
            NeraCommandPayload.ForUiProtection(NeraUiProtectionMode.Off)));
        Assert(!result.Ok && result.Code == NeraControlCodes.InvalidArgument, result.TechnicalMessage);
        AssertEqual(NeraUiProtectionMode.Off, result.State.UiProtection);
        AssertEqual(0, backend.ExecuteCount(NeraCommandKind.SetUiProtection));
    }

    private static async Task CanonicalRecipeAndParameters()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        long revision = 0;
        foreach (NeraProcessingMode mode in Enum.GetValues<NeraProcessingMode>())
        {
            var recipe = await control.ExecuteAsync(NeraCommandEnvelope.Create(
                NeraCommandKind.SetMode, NeraCommandSource.Ui, revision, NeraCommandPayload.ForMode(mode)));
            Assert(recipe.Ok && !recipe.State.RecipeCustom && !recipe.State.Feature18Custom,
                "A recipe must write canonical values without retaining Custom.");
            AssertEqual(NeraRecipe.StrengthForMode(mode), recipe.State.Strength);
            AssertEqual(NeraDldrTuning.ForMode(mode).CanonicalizedForNative(), NeraDldrTuning.FromState(recipe.State));
            revision = recipe.CurrentRevision;
        }
        var before = await control.GetSnapshotAsync();
        var strength = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetStrength, NeraCommandSource.Cli, revision, NeraCommandPayload.ForStrength(33)));
        Assert(strength.Ok && strength.State.RecipeCustom && strength.State.Strength == 33 &&
            strength.State.Feature18Intensity == before.Feature18Intensity &&
            strength.State.Feature18Style == before.Feature18Style &&
            NeraDldrTuning.FromState(strength.State) == NeraDldrTuning.FromState(before),
            "DLDR strength must not resolve mode or overwrite NR/DLDR advanced fields.");
        var dldr = new NeraDldrTuning { HighlightProtection = .123456789, ShadowProtection = .2,
            ChromaStrength = .3, TemporalResponse = 0 };
        var tuned = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetDldrTuning, NeraCommandSource.Agent, strength.CurrentRevision,
            NeraCommandPayload.ForDldrTuning(dldr)));
        Assert(tuned.Ok && tuned.State.RecipeCustom && tuned.State.Strength == 33 &&
            tuned.State.Feature18Style == before.Feature18Style &&
            NeraDldrTuning.FromState(tuned.State) == dldr.CanonicalizedForNative(),
            "DLDR bundle must use native float precision without touching NR or strength.");
        var stale = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetDldrTuning, NeraCommandSource.Ui, strength.CurrentRevision,
            NeraCommandPayload.ForDldrTuning(new())));
        Assert(!stale.Ok && stale.Code == NeraControlCodes.StateChanged,
            "A debounced stale whole bundle must not overwrite an Agent change.");
        var repeat = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetDldrTuning, NeraCommandSource.Agent, tuned.CurrentRevision,
            NeraCommandPayload.ForDldrTuning(dldr)));
        Assert(repeat.Ok && repeat.CurrentRevision == tuned.CurrentRevision &&
            backend.ExecuteCount(NeraCommandKind.SetDldrTuning) == 1, "Canonical bundle is idempotent.");
        string json = JsonSerializer.Serialize(repeat.State, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert(!json.Contains("uiProtection", StringComparison.Ordinal) &&
            json.Contains("dldrHighlightProtection", StringComparison.Ordinal) &&
            json.Contains("recipeCustom", StringComparison.Ordinal),
            "State files and IPC JSON share the canonical fields and omit retired UI protection.");
    }

    private static async Task BrokerStartFailurePreservesDiagnostic()
    {
        const string diagnostic = "stage=native_start_returned_false;runtimeBroker={\"processExitCode\":22,\"lastWin32Error\":2}";
        var backend = new FakeControlBackend
        {
            BrokerOutcome = NeraBackendOutcome.Failure(NeraControlCodes.BrokerStartFailed,
                "DLDR 未能开启，正在恢复原画。请稍后重试。", diagnostic)
        };
        await using var control = Service(backend);
        NeraCommandResult result = await EnableAsync(control, 0);
        AssertEqual(NeraControlCodes.BrokerStartFailed, result.Code);
        AssertEqual(diagnostic, result.TechnicalMessage);
        AssertEqual(diagnostic, result.State.LastError?.TechnicalMessage);
        Assert(result.State.RuntimeVerified, "IPC startup failure must not revoke verified Runtime identity.");
        Assert(!result.Ok && result.RollbackPerformed && !result.State.DldrOn,
            "Broker failure must remain OFF and retain normal strict fail-safe cleanup.");
        Assert(backend.Calls.Contains("failSafe:BROKER_START_FAILED") &&
               !backend.Calls.Contains("startHost"),
            "Broker failure must be cleaned up without starting CompatHost.");
    }

    private static async Task DldrTuningRangesAndHotAck()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        foreach (NeraDldrTuning invalid in new[] {
            new NeraDldrTuning { HighlightProtection = -0.1 },
            new NeraDldrTuning { ShadowProtection = 1.1 },
            new NeraDldrTuning { ChromaStrength = double.NaN },
            new NeraDldrTuning { TemporalResponse = double.PositiveInfinity } })
        {
            var result = await control.ExecuteAsync(NeraCommandEnvelope.Create(
                NeraCommandKind.SetDldrTuning, NeraCommandSource.Test, 0, NeraCommandPayload.ForDldrTuning(invalid)));
            Assert(!result.Ok && result.Code == NeraControlCodes.InvalidArgument, "Unsafe DLDR value rejected.");
        }
        var enabled = await EnableAsync(control, 0);
        NeraStateSnapshot previous = enabled.State;
        foreach ((NeraCommandKind kind, NeraCommandPayload payload) in new[] {
            (NeraCommandKind.SetDldrTuning, NeraCommandPayload.ForDldrTuning(new() { ChromaStrength = .75 })),
            (NeraCommandKind.SetStrength, NeraCommandPayload.ForStrength(42)) })
        {
            var applied = await control.ExecuteAsync(NeraCommandEnvelope.Create(
                kind, NeraCommandSource.Test, previous.Revision, payload));
            Assert(applied.Ok && applied.State.DldrOn &&
                applied.State.AppliedParameterRevision > previous.AppliedParameterRevision &&
                applied.State.AppliedParameterFrameId > previous.LastSuccessfulFrameId,
                "HOT mutation requires a newer applied revision and proven presented frame.");
            previous = applied.State;
        }
        AssertEqual(1, backend.ExecuteCount(NeraCommandKind.SetDldrTuning));
        AssertEqual(1, backend.ExecuteCount(NeraCommandKind.SetStrength));
        AssertEqual(1, backend.Calls.Count(call => call == "createFeature18"));
        Assert(!backend.Calls.Any(call => call.StartsWith("disableDldr:", StringComparison.Ordinal)),
            "HOT changes must not hide/cold-stop the running media session.");
    }

    private static async Task WireCanonicalBundles()
    {
        await using var control = Service(new FakeControlBackend(), ReadyState() with { AiPermissions = AllPermissions() });
        var handler = new NeraControlWireHandler(control, new AllowAuthorizer(), "0.4.1");
        var session = new NeraWireSession();
        Assert((await handler.HandleAsync(Handshake("canonical-handshake"), session)).Ok, "Handshake failed.");
        var nr = await handler.HandleAsync(WireMutation("canonical-nr", NeraWireCommand.SetFeature18Tuning, 0) with
        { Payload = NeraCommandPayload.ForFeature18Tuning(NeraFeature18Tuning.Clear with { Custom = true, Intensity = .4 }) }, session);
        Assert(nr.Ok && nr.State.Feature18Intensity == (double)(float).4 && nr.State.RecipeCustom, "Wire NR canonical path.");
        var dldr = await handler.HandleAsync(WireMutation("canonical-dldr", NeraWireCommand.SetDldrTuning, nr.CurrentRevision) with
        { Payload = NeraCommandPayload.ForDldrTuning(new() { ChromaStrength = .25 }) }, session);
        Assert(dldr.Ok && dldr.State.DldrChromaStrength == .25 && dldr.State.Feature18Intensity == nr.State.Feature18Intensity,
            "NR and DLDR wire commands must stay independent.");
        var removed = await handler.HandleAsync(WireMutation("canonical-retired", NeraWireCommand.SetUiProtection,
            dldr.CurrentRevision) with { Payload = NeraCommandPayload.ForUiProtection(NeraUiProtectionMode.Auto) }, session);
        Assert(!removed.Ok && removed.Code == NeraControlCodes.InvalidArgument &&
            removed.State.Revision == dldr.CurrentRevision, "Removed wire command must never mutate state.");
    }

    private static async Task Feature18TuningIsStrictAndModeCalibrated()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        var custom = new NeraFeature18Tuning
        {
            Custom = true,
            Style = 1,
            Intensity = 0.75,
            LocalToneStrength = 1.25,
            LocalStructureStrength = 1.5,
            SkinStructureStrength = 0.75,
            UseAutoMask = true
        };
        NeraCommandResult tuned = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetFeature18Tuning, NeraCommandSource.Ui, 0,
            NeraCommandPayload.ForFeature18Tuning(custom)));
        Assert(tuned.Ok && tuned.State.Feature18Custom && tuned.State.Feature18Style == 1 &&
               tuned.State.Feature18Intensity == 0.75 &&
               tuned.State.Feature18LocalToneStrength == 1.25 &&
               tuned.State.Feature18LocalStructureStrength == 1.5 &&
               tuned.State.Feature18SkinStructureStrength == 0.75 &&
               tuned.State.Feature18UseAutoMask,
            "A custom tuning command must atomically update the authoritative snapshot.");

        NeraFeature18Tuning highPrecision = custom with
        {
            Intensity = 0.123456789,
            LocalToneStrength = 1.234567891,
            LocalStructureStrength = 1.876543219,
            SkinStructureStrength = 0.456789123
        };
        NeraCommandResult canonical = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetFeature18Tuning, NeraCommandSource.Ui, tuned.CurrentRevision,
            NeraCommandPayload.ForFeature18Tuning(highPrecision)));
        Assert(canonical.Ok &&
               canonical.State.Feature18Intensity == (double)(float)highPrecision.Intensity &&
               canonical.State.Feature18LocalToneStrength ==
                   (double)(float)highPrecision.LocalToneStrength &&
               canonical.State.Feature18LocalStructureStrength ==
                   (double)(float)highPrecision.LocalStructureStrength &&
               canonical.State.Feature18SkinStructureStrength ==
                   (double)(float)highPrecision.SkinStructureStrength,
            "Feature 18 tuning must be canonicalized to the native float precision.");
        NeraCommandResult canonicalRepeat = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetFeature18Tuning, NeraCommandSource.Ui, canonical.CurrentRevision,
            NeraCommandPayload.ForFeature18Tuning(highPrecision)));
        Assert(canonicalRepeat.Ok && canonicalRepeat.CurrentRevision == canonical.CurrentRevision &&
               backend.ExecuteCount(NeraCommandKind.SetFeature18Tuning) == 2,
            "A repeated high-precision value must remain idempotent after native canonicalization.");

        NeraCommandResult cinema = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetMode, NeraCommandSource.Ui, canonical.CurrentRevision,
            NeraCommandPayload.ForMode(NeraProcessingMode.Cinema)));
        Assert(cinema.Ok && !cinema.State.Feature18Custom &&
               cinema.State.Feature18Style == NeraFeature18Tuning.Cinema.Style &&
               cinema.State.Feature18Intensity == NeraFeature18Tuning.Cinema.Intensity &&
               cinema.State.Feature18LocalToneStrength == NeraFeature18Tuning.Cinema.LocalToneStrength &&
               cinema.State.Feature18LocalStructureStrength ==
                   NeraFeature18Tuning.Cinema.LocalStructureStrength &&
               cinema.State.Feature18SkinStructureStrength ==
                   NeraFeature18Tuning.Cinema.SkinStructureStrength &&
               cinema.State.Feature18UseAutoMask == NeraFeature18Tuning.Cinema.UseAutoMask,
            "Selecting a named mode must clear Custom and load its measured calibration.");

        NeraCommandResult invalid = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetFeature18Tuning, NeraCommandSource.Ui, cinema.CurrentRevision,
            NeraCommandPayload.ForFeature18Tuning(custom with { Intensity = 1.01 })));
        Assert(!invalid.Ok && invalid.Code == NeraControlCodes.InvalidArgument &&
               backend.ExecuteCount(NeraCommandKind.SetFeature18Tuning) == 2,
            "Out-of-domain tuning must be rejected before the backend boundary.");
        NeraCommandResult invalidSkin = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetFeature18Tuning, NeraCommandSource.Ui, cinema.CurrentRevision,
            NeraCommandPayload.ForFeature18Tuning(custom with { SkinStructureStrength = 2.01 })));
        Assert(!invalidSkin.Ok && invalidSkin.Code == NeraControlCodes.InvalidArgument &&
               backend.ExecuteCount(NeraCommandKind.SetFeature18Tuning) == 2,
            "Out-of-domain skin tuning must be rejected before the backend boundary.");
    }

    private static async Task Feature18TuningHotExactAck()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        NeraCommandResult enabled = await EnableAsync(control, 0);
        var custom = NeraFeature18Tuning.Natural with { Custom = true, Intensity = 0.5 };
        NeraCommandResult applied = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetFeature18Tuning, NeraCommandSource.Ui, enabled.CurrentRevision,
            NeraCommandPayload.ForFeature18Tuning(custom)));
        Assert(applied.Ok && applied.State.DldrOn && applied.State.Feature18Intensity == .5 &&
               applied.State.AppliedParameterRevision == 1 && applied.State.AppliedParameterFrameId == 2 &&
               backend.ExecuteCount(NeraCommandKind.SetFeature18Tuning) == 1,
            "Feature 18 HOT tuning must commit only with its actual new revision/frame ACK.");
        AssertEqual(1, backend.Calls.Count(call => call == "createFeature18"));
    }

    private static async Task HotBadAcknowledgementDoesNotCommit()
    {
        NeraBackendOutcome[] outcomes =
        [
            NeraBackendOutcome.Success() with { AppliedParameterFrameId = 2 },
            NeraBackendOutcome.Success() with { AppliedParameterRevision = 1 },
            NeraBackendOutcome.Success() with { AppliedParameterRevision = 0, AppliedParameterFrameId = 2 },
            NeraBackendOutcome.Success() with { AppliedParameterRevision = 1, AppliedParameterFrameId = 1 },
            NeraBackendOutcome.Failure(NeraControlCodes.OperationFailed, "参数未应用", "native rejected bundle")
                with { AppliedParameterRevision = 1, AppliedParameterFrameId = 2 }
        ];
        foreach (NeraBackendOutcome outcome in outcomes)
        {
            var backend = new FakeControlBackend { HotUpdateOutcome = outcome };
            await using var control = Service(backend);
            var enabled = await EnableAsync(control, 0);
            var before = enabled.State;
            var rejected = await control.ExecuteAsync(NeraCommandEnvelope.Create(
                NeraCommandKind.SetStrength, NeraCommandSource.Agent, enabled.CurrentRevision,
                NeraCommandPayload.ForStrength(42)));
            Assert(!rejected.Ok && rejected.State.Strength == before.Strength &&
                rejected.State.AppliedParameterRevision == before.AppliedParameterRevision &&
                rejected.State.AppliedParameterFrameId == before.AppliedParameterFrameId,
                "Rejected, missing or stale native apply evidence must not commit requested parameters.");
            AssertEqual(1, backend.Calls.Count(call => call == "createFeature18"));
            Assert(!rejected.State.DldrOn && rejected.RollbackPerformed &&
                backend.Calls.Any(call => call.StartsWith("failSafe:", StringComparison.Ordinal)),
                "Rejected or unconfirmed HOT updates must revoke ON and run the shared fail-safe; " +
                "the Runtime may already have consumed a bundle whose completion was not observed.");
        }
    }

    private static async Task RecipeHotBundlePreservesOwnership()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        var enabled = await EnableAsync(control, 0);
        var applied = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetMode, NeraCommandSource.Cli, enabled.CurrentRevision,
            NeraCommandPayload.ForMode(NeraProcessingMode.Cinema)));
        Assert(applied.Ok && applied.State.DldrOn && !applied.State.RecipeCustom &&
            applied.State.Strength == NeraRecipe.StrengthForMode(NeraProcessingMode.Cinema) &&
            NeraDldrTuning.FromState(applied.State) == NeraDldrTuning.ForMode(NeraProcessingMode.Cinema).CanonicalizedForNative() &&
            applied.State.Feature18Style == NeraFeature18Tuning.Cinema.Style &&
            applied.State.Feature18UseAutoMask == NeraFeature18Tuning.Cinema.UseAutoMask &&
            applied.State.AppliedParameterRevision == 1 && applied.State.AppliedParameterFrameId == 2,
            "A named recipe is one canonical NR/DLDR bundle acknowledged by one new frame.");
        AssertEqual(1, backend.Calls.Count(call => call == "createFeature18"));
        AssertEqual(1, backend.Calls.Count(call => call == "startBroker"));
        AssertEqual(1, backend.ColdHostStartCount);
    }

    private static async Task HotTimeoutOrExceptionFailsClosed()
    {
        foreach (bool timeout in new[] { true, false })
        {
            var backend = new FakeControlBackend
            {
                GenericDelay = timeout ? TimeSpan.FromSeconds(2) : TimeSpan.Zero,
                HotUpdateException = timeout ? null : new IOException("Simulated apply ACK pipe disconnect")
            };
            await using var control = Service(backend);
            var enabled = await EnableAsync(control, 0);
            var failed = await control.ExecuteAsync(NeraCommandEnvelope.Create(
                NeraCommandKind.SetStrength, NeraCommandSource.Agent, enabled.CurrentRevision,
                NeraCommandPayload.ForStrength(42)));
            Assert(!failed.Ok && failed.Code == (timeout ? NeraControlCodes.Timeout : NeraControlCodes.OperationFailed) &&
                !failed.State.DldrOn && failed.State.OffGateSatisfied && !failed.State.HostConnected &&
                failed.State.Strength == enabled.State.Strength &&
                failed.State.AppliedParameterRevision == enabled.State.AppliedParameterRevision &&
                failed.RollbackPerformed && backend.Calls.Any(call => call.StartsWith("failSafe:", StringComparison.Ordinal)),
                $"A {(timeout ? "timeout" : "disconnect exception")} can hide a completed GPU update; " +
                "preserve the confirmed canonical bundle but retire uncertain Runtime ownership.");
        }
    }

    private static Task<NeraCommandResult> DisableAsync(NeraControlService control, long revision) =>
        control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.DisableDldr, NeraCommandSource.Ui, revision));

    private static async Task WarmOffRetainsOwnership()
    {
        var backend = new FakeControlBackend { UseWarmStandby = true };
        await using var control = Service(backend);
        var enabled = await EnableAsync(control, 0);
        var paused = await DisableAsync(control, enabled.CurrentRevision);
        Assert(paused.Ok && paused.State.WarmStandby && paused.State.WarmOffGateSatisfied &&
            !paused.State.DldrOn && !paused.State.DldrRequested && !paused.State.ProcessingActive &&
            !paused.State.PresenterSucceeded && !paused.State.OffGateSatisfied &&
            paused.State.HostConnected && paused.State.FeatureCreated && paused.State.BrokerProcessOwned,
            "Warm OFF proves normal desktop while truthfully retaining native ownership.");
        NeraStatePolicy.Validate(paused.State);
        Assert(paused.State.LastSuccessfulFrameId == enabled.State.LastSuccessfulFrameId &&
            paused.State.Feature18SuccessfulFrames == enabled.State.Feature18SuccessfulFrames &&
            paused.State.DldrSuccessfulFrames == enabled.State.DldrSuccessfulFrames &&
            paused.State.PresentedFrames == enabled.State.PresentedFrames &&
            !NeraStatePolicy.IsFirstFrameGateSatisfied(paused.State),
            "Historical frame watermarks survive warm OFF but are never current ON evidence.");
        var repeated = await DisableAsync(control, paused.CurrentRevision);
        Assert(repeated.Ok && repeated.CurrentRevision == paused.CurrentRevision,
            "Repeated OFF must be idempotent and cannot extend the Host idle timer by replaying Pause.");
        AssertEqual(1, backend.Calls.Count(call => call == "disableDldr:False"));
    }

    private static async Task WarmOffRequiresNativeEvidence()
    {
        var backend = new FakeControlBackend { UseWarmStandby = true, WarmEvidenceValid = false };
        await using var control = Service(backend);
        var enabled = await EnableAsync(control, 0);
        var stopped = await DisableAsync(control, enabled.CurrentRevision);
        Assert(!stopped.State.WarmStandby && !stopped.State.DldrOn &&
            stopped.State.OffGateSatisfied && !stopped.State.HostConnected &&
            backend.Calls.Any(call => call.StartsWith("failSafe:", StringComparison.Ordinal)),
            "A native warm hint alone must not imply retained-session safety; missing observation falls back cold.");
    }

    private static async Task WarmResumeRequiresFreshFrame()
    {
        var backend = new FakeControlBackend { UseWarmStandby = true };
        await using var control = Service(backend);
        var enabled = await EnableAsync(control, 0);
        var paused = await DisableAsync(control, enabled.CurrentRevision);
        var transitions = new List<NeraStateSnapshot>();
        control.StateChanged += (_, state) => transitions.Add(state);
        var resumed = await EnableAsync(control, paused.CurrentRevision);
        Assert(resumed.Ok && resumed.State.DldrOn && !resumed.State.WarmStandby &&
            resumed.State.LastSuccessfulFrameId > enabled.State.LastSuccessfulFrameId,
            $"Warm ON requires a strictly newer successful captured/processed/presented frame. " +
            $"ok={resumed.Ok}; code={resumed.Code}; detail={resumed.TechnicalMessage}; " +
            $"frame={resumed.State.LastSuccessfulFrameId}; calls={string.Join(',', backend.Calls)}");
        Assert(transitions.Any(state => state.DldrActualState == NeraDldrActualState.WaitingForFirstFrame &&
                !state.DldrOn && !state.PresenterSucceeded) &&
            transitions.All(state => !state.DldrOn || NeraStatePolicy.IsFirstFrameGateSatisfied(state)),
            "Old first-frame flags must be revoked before warm resume.");
        AssertEqual(1, backend.WarmResumeCount);
        AssertEqual(1, backend.ColdHostStartCount);
        AssertEqual(1, backend.Calls.Count(call => call == "createFeature18"));
        AssertEqual(1, backend.Calls.Count(call => call == "startBroker"));
    }

    private static async Task WarmResumeRejectsStaleFrame()
    {
        var backend = new FakeControlBackend { UseWarmStandby = true, FreshWarmResumeFrame = false };
        await using var control = Service(backend);
        var enabled = await EnableAsync(control, 0);
        var paused = await DisableAsync(control, enabled.CurrentRevision);
        var rejected = await EnableAsync(control, paused.CurrentRevision);
        Assert(!rejected.Ok && rejected.Code == NeraControlCodes.FirstFrameGateFailed &&
            !rejected.State.DldrOn && rejected.State.OffGateSatisfied &&
            !rejected.State.HostConnected && backend.Calls.Contains("failSafe:FIRST_FRAME_GATE_FAILED"),
            $"A previous successful frame cannot unlock warm ON; use the shared cold fail-safe. " +
            $"ok={rejected.Ok}; code={rejected.Code}; detail={rejected.TechnicalMessage}; " +
            $"frame={rejected.State.LastSuccessfulFrameId}; calls={string.Join(',', backend.Calls)}");
    }

    private static async Task WarmEmergencyAlwaysCold()
    {
        var backend = new FakeControlBackend { UseWarmStandby = true };
        await using var control = Service(backend);
        var enabled = await EnableAsync(control, 0);
        var paused = await DisableAsync(control, enabled.CurrentRevision);
        var stopped = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.EmergencyStop, NeraCommandSource.Hotkey, paused.CurrentRevision));
        Assert(stopped.Ok && !stopped.State.WarmStandby && !stopped.State.WarmOffGateSatisfied &&
            stopped.State.OffGateSatisfied && !stopped.State.FeatureCreated &&
            !stopped.State.HostConnected && !stopped.State.BrokerConnected &&
            !stopped.State.BrokerProcessOwned && backend.Calls.Contains("disableDldr:True"),
            "Emergency Stop must release warm ownership rather than merely hide output.");
    }

    private static async Task RetainedSessionTransitionRequiresProof()
    {
        var backend = new FakeControlBackend { UseWarmStandby = true };
        await using var control = Service(backend);
        var enabled = await EnableAsync(control, 0);
        var paused = await DisableAsync(control, enabled.CurrentRevision);
        var waiting = NeraStatePolicy.WithComputedDldrGate(paused.State with
        {
            DldrRequested = true,
            WarmStandby = false,
            WarmOffGateSatisfied = false,
            DldrActualState = NeraDldrActualState.WaitingForFirstFrame,
            FirstFeature18FrameSucceeded = false,
            DldrSucceeded = false,
            PresenterSucceeded = false,
            ProcessingActive = false
        });
        Assert(NeraStatePolicy.CanTransition(paused.State, waiting),
            "Only a retained, paused, identity-matched session may skip cold creation.");
        Assert(!NeraStatePolicy.CanTransition(paused.State.DldrActualState, waiting.DldrActualState),
            "The legacy enum-only API must not grant a generic Bypass-to-first-frame shortcut.");
        NeraStateSnapshot[] invalidWarmSources =
        [
            paused.State with { WarmStandby = false, WarmOffGateSatisfied = false },
            paused.State with { WarmOffGateSatisfied = false },
            paused.State with { HostConnected = false },
            paused.State with { FeatureCreated = false },
            paused.State with { BrokerProcessOwned = false }
        ];
        foreach (var invalid in invalidWarmSources)
            Assert(!NeraStatePolicy.CanTransition(invalid, waiting),
                "Missing warm ownership/normal-desktop proof cannot authorize a retained-session transition.");
        foreach (var invalid in new[]
        {
            waiting with { RuntimeSha256 = new string('0', 64) },
            waiting with { RuntimeBinaryVersion = "unverified" },
            waiting with { TargetDisplay = waiting.TargetDisplay! with { DisplayId = "display:other" } },
            waiting with { FirstFeature18FrameSucceeded = true },
            waiting with { PresenterSucceeded = true }
        })
            Assert(!NeraStatePolicy.CanTransition(paused.State, invalid),
                "Identity changes or old first-frame proof cannot shortcut warm resume.");

        var overlay = NeraStatePolicy.WithComputedDldrGate(enabled.State with
        {
            DldrActualState = NeraDldrActualState.Recovering,
            OverlayBypass = true,
            ProcessingActive = false,
            FirstFeature18FrameSucceeded = false,
            DldrSucceeded = false,
            PresenterSucceeded = false
        });
        ulong newFrame = enabled.State.LastSuccessfulFrameId + 1;
        var restored = NeraStatePolicy.WithComputedDldrGate(enabled.State with
        {
            OverlayBypass = false,
            LastSuccessfulFrameId = newFrame,
            LastFeature18FrameId = newFrame,
            LastDldrFrameId = newFrame,
            LastPresentedFrameId = newFrame,
            Feature18SuccessfulFrames = newFrame,
            DldrSuccessfulFrames = newFrame,
            PresentedFrames = newFrame
        });
        Assert(NeraStatePolicy.CanTransition(overlay, restored),
            "Overlay return requires a fresh complete capture/Feature/DLDR/Presenter proof.");
        Assert(!NeraStatePolicy.CanTransition(overlay, enabled.State),
            "A frame from before the Overlay suspension must not restore ON.");
        Assert(!NeraStatePolicy.CanTransition(overlay with { OverlayBypass = false }, restored),
            "Generic recovery cannot masquerade as an Overlay-specific retained-session return.");
        foreach (var invalid in new[]
        {
            restored with { RuntimeSha256 = new string('0', 64) },
            restored with { TargetDisplay = restored.TargetDisplay! with { DisplayId = "display:other" } },
            NeraStatePolicy.WithComputedDldrGate(restored with { PresenterSucceeded = false }),
            NeraStatePolicy.WithComputedDldrGate(restored with { LastDldrFrameId = newFrame - 1 })
        })
            Assert(!NeraStatePolicy.CanTransition(overlay, invalid),
                "Overlay return rejects identity changes and partial or mismatched frame proof.");
    }

    private static async Task PerformanceChangeRecreatesSafely()
    {
        var backend = new FakeControlBackend { UseWarmStandby = true };
        await using var control = Service(backend);
        var enabled = await EnableAsync(control, 0);
        var transitions = new List<NeraStateSnapshot>();
        control.StateChanged += (_, state) => transitions.Add(state);
        var changed = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetPerformanceMode, NeraCommandSource.Ui, enabled.CurrentRevision,
            NeraCommandPayload.ForPerformanceMode(NeraPerformanceMode.Smooth)));
        Assert(changed.Ok && changed.State.DldrOn && changed.State.NeuralScale == 80 &&
            changed.State.NeuralInputWidth == 3072 && changed.State.OutputWidth == 3840,
            "Scale changes keep final FP16 dimensions and honestly recreate rather than masquerading as HOT.");
        AssertSubsequence(backend.Calls, "disableDldr:True", "execute:SetPerformanceMode",
            "startBroker", "startHost", "createFeature18", "waitFirstFrame");
        AssertEqual(2, backend.ColdHostStartCount);
        AssertEqual(2, backend.Calls.Count(call => call == "createFeature18"));
        Assert(transitions.Any(state => !state.DldrOn && state.OffGateSatisfied) &&
            transitions.All(state => !state.DldrOn || NeraStatePolicy.IsFirstFrameGateSatisfied(state)),
            "WARM-class scale recreation must pass through safe OFF and re-prove its first frame.");
    }

    private static async Task OverlayWaitingTransitionRequiresProof()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        var enabled = await EnableAsync(control, 0);
        var overlay = NeraStatePolicy.WithComputedDldrGate(enabled.State with
        {
            DldrActualState = NeraDldrActualState.Recovering,
            OverlayBypass = true,
            ProcessingActive = false,
            FirstFeature18FrameSucceeded = false,
            DldrSucceeded = false,
            PresenterSucceeded = false
        });
        var waiting = overlay with
        {
            DldrActualState = NeraDldrActualState.WaitingForFirstFrame,
            OverlayBypass = false
        };
        NeraStatePolicy.Validate(overlay);
        NeraStatePolicy.Validate(waiting);
        Assert(NeraStatePolicy.CanTransition(overlay, waiting) &&
            !NeraStatePolicy.CanTransition(overlay.DldrActualState, waiting.DldrActualState),
            "Only exact retained Overlay ownership permits the safe intermediate waiting transition.");
        Assert(waiting.LastSuccessfulFrameId == enabled.State.LastSuccessfulFrameId &&
            waiting.Feature18SuccessfulFrames == enabled.State.Feature18SuccessfulFrames &&
            waiting.DldrSuccessfulFrames == enabled.State.DldrSuccessfulFrames &&
            waiting.PresentedFrames == enabled.State.PresentedFrames && !waiting.DldrOn &&
            !NeraStatePolicy.IsFirstFrameGateSatisfied(waiting),
            "Historical successful counters are watermarks, never proof that the hidden Presenter is ON.");
        foreach (var invalidSource in new[]
        {
            overlay with { OverlayBypass = false },
            overlay with { DldrRequested = false },
            overlay with { BrokerProcessOwned = false },
            overlay with { HostConnected = false },
            overlay with { FeatureCreated = false }
        })
            Assert(!NeraStatePolicy.CanTransition(invalidSource, waiting),
                "Generic recovery or lost retained ownership cannot use the Overlay waiting shortcut.");
        foreach (var invalidDestination in new[]
        {
            waiting with { RuntimeSha256 = new string('0', 64) },
            waiting with { TargetDisplay = waiting.TargetDisplay! with { DisplayId = "display:other" } },
            waiting with { DldrRequested = false },
            waiting with { ProcessingActive = true },
            waiting with { DldrOn = true },
            waiting with { OverlayBypass = true }
        })
            Assert(!NeraStatePolicy.CanTransition(overlay, invalidDestination),
                "Overlay waiting must be identity-matched and requested without claiming Processing or ON.");

        var partialPresent = NeraStatePolicy.WithComputedDldrGate(waiting with
        {
            FirstFeature18FrameSucceeded = true,
            DldrSucceeded = true,
            PresenterSucceeded = true
        });
        NeraStatePolicy.Validate(partialPresent);
        Assert(NeraStatePolicy.CanTransition(overlay, partialPresent) && !partialPresent.DldrOn &&
            !partialPresent.ProcessingActive && !NeraStatePolicy.IsFirstFrameGateSatisfied(partialPresent),
            "A restored Present may precede the consecutive-frame confirmation without failing recovery or claiming ON.");
        Assert(!NeraStatePolicy.CanTransition(partialPresent, enabled.State),
            "Partial restored Present evidence must still not reuse a pre-Overlay frame to claim ON.");

        Assert(!NeraStatePolicy.CanTransition(waiting, enabled.State),
            "The old pre-Overlay frame cannot be reused to transition Waiting back to Processing.");
        ulong nextFrame = enabled.State.LastSuccessfulFrameId + 1;
        var fresh = NeraStatePolicy.WithComputedDldrGate(enabled.State with
        {
            LastSuccessfulFrameId = nextFrame,
            LastFeature18FrameId = nextFrame,
            LastDldrFrameId = nextFrame,
            LastPresentedFrameId = nextFrame,
            Feature18SuccessfulFrames = nextFrame,
            DldrSuccessfulFrames = nextFrame,
            PresentedFrames = nextFrame
        });
        Assert(NeraStatePolicy.CanTransition(waiting, fresh),
            "A fresh complete frame restores ON after the intermediate waiting state.");
        Assert(!NeraStatePolicy.CanTransition(waiting,
            NeraStatePolicy.WithComputedDldrGate(fresh with { LastDldrFrameId = nextFrame - 1 })),
            "A newer capture with stale DLDR output still cannot restore ON.");
    }

    private static async Task OverlayObserverWaitsForFreshFrame()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend, fastObservation: true);
        var enabled = await EnableAsync(control, 0);
        var states = new System.Collections.Concurrent.ConcurrentQueue<NeraStateSnapshot>();
        control.StateChanged += (_, state) => states.Enqueue(state);
        backend.AuthoritativeObservation = current => current with
        {
            DldrActualState = NeraDldrActualState.Recovering,
            OverlayBypass = true,
            ProcessingActive = false,
            FirstFeature18FrameSucceeded = false,
            DldrSucceeded = false,
            PresenterSucceeded = false
        };
        var suspended = await control.WaitForStateAsync(
            state => state.DldrActualState == NeraDldrActualState.Recovering && state.OverlayBypass,
            TimeSpan.FromSeconds(2));
        Assert(!suspended.DldrOn && suspended.DldrRequested && suspended.HostConnected &&
            suspended.LastSuccessfulFrameId == enabled.State.LastSuccessfulFrameId,
            "Overlay suspension retains the request and watermark without claiming ON.");
        backend.AuthoritativeObservation = current => current with
        {
            DldrActualState = NeraDldrActualState.WaitingForFirstFrame,
            OverlayBypass = false,
            FirstFeature18FrameSucceeded = true,
            DldrSucceeded = true,
            PresenterSucceeded = true
        };
        var waiting = await control.WaitForStateAsync(
            state => state.DldrActualState == NeraDldrActualState.WaitingForFirstFrame,
            TimeSpan.FromSeconds(2));
        Assert(!waiting.DldrOn && !waiting.ProcessingActive && waiting.PresenterSucceeded && waiting.HostConnected &&
            waiting.LastSuccessfulFrameId == suspended.LastSuccessfulFrameId && waiting.LastError is null,
            "The real observer path must accept Overlay-to-waiting without triggering unrelated fail-safe.");
        ulong nextFrame = waiting.LastSuccessfulFrameId + 1;
        backend.AuthoritativeObservation = current => current with
        {
            DldrActualState = NeraDldrActualState.Processing,
            ProcessingActive = true,
            FirstFeature18FrameSucceeded = true,
            DldrSucceeded = true,
            PresenterSucceeded = true,
            ForegroundPreserved = true,
            LastSuccessfulFrameId = nextFrame,
            LastFeature18FrameId = nextFrame,
            LastDldrFrameId = nextFrame,
            LastPresentedFrameId = nextFrame,
            Feature18SuccessfulFrames = nextFrame,
            DldrSuccessfulFrames = nextFrame,
            PresentedFrames = nextFrame
        };
        var restored = await control.WaitForStateAsync(state => state.DldrOn,
            TimeSpan.FromSeconds(2));
        Assert(restored.LastSuccessfulFrameId > enabled.State.LastSuccessfulFrameId &&
            restored.HostConnected && restored.FeatureCreated &&
            states.All(state => !state.DldrOn || NeraStatePolicy.IsFirstFrameGateSatisfied(state)) &&
            !backend.Calls.Any(call => call.StartsWith("failSafe:", StringComparison.Ordinal)),
            "Fresh Overlay recovery must not relaunch or falsely release the existing Runtime session.");
        AssertEqual(1, backend.Calls.Count(call => call == "startBroker"));
        AssertEqual(1, backend.Calls.Count(call => call == "createFeature18"));
    }

    private static async Task DuplicateRequestIsIdempotent()
    {
        var backend = new FakeControlBackend { GenericDelay = TimeSpan.FromMilliseconds(25) };
        await using var control = Service(backend);
        NeraCommandEnvelope command = NeraCommandEnvelope.Create(
            NeraCommandKind.ShowHud, NeraCommandSource.Ui, 0, requestId: "same-request");
        NeraCommandResult[] results = await Task.WhenAll(
            control.ExecuteAsync(command), control.ExecuteAsync(command));
        Assert(results[0] == results[1], "Duplicate request must return the cached result object.");
        AssertEqual(1, backend.ExecuteCount(NeraCommandKind.ShowHud));
    }

    private static async Task RequestIdReuseIsRejected()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        NeraCommandResult first = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.ShowHud, NeraCommandSource.Ui, 0, requestId: "reused"));
        NeraCommandResult second = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.HideHud, NeraCommandSource.Ui, first.CurrentRevision, requestId: "reused"));
        AssertEqual(NeraControlCodes.RequestIdReused, second.Code);
        AssertEqual(0, backend.ExecuteCount(NeraCommandKind.HideHud));
    }

    private static async Task RevisionConflictIsRejected()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        NeraCommandResult result = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetStrength, NeraCommandSource.Cli, 99,
            NeraCommandPayload.ForStrength(75)));
        AssertEqual(NeraControlCodes.StateChanged, result.Code);
        AssertEqual(0, backend.ExecuteCount(NeraCommandKind.SetStrength));
    }

    private static async Task PayloadShapeIsStrict()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        NeraCommandResult result = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.ShowHud, NeraCommandSource.Ui, 0,
            NeraCommandPayload.ForStrength(12)));
        AssertEqual(NeraControlCodes.InvalidArgument, result.Code);
        AssertEqual(0, backend.ExecuteCount(NeraCommandKind.ShowHud));

        NeraCommandResult undefined = await control.ExecuteAsync(new NeraCommandEnvelope
        {
            RequestId = "undefined-kind",
            Source = NeraCommandSource.Test,
            ExpectedRevision = 0,
            Timestamp = DateTimeOffset.UtcNow,
            Kind = (NeraCommandKind)999,
            Payload = NeraCommandPayload.Empty
        });
        AssertEqual(NeraControlCodes.InvalidArgument, undefined.Code);
    }

    private static Task ProductionCommandSurfaceIsGlobalOnly()
    {
        string[] expected =
        [
            "SetDisplay", "EnableDldr", "DisableDldr", "SetMode", "SetStrength",
            "SetPerformanceMode", "SetUiProtection", "SetFeature18Tuning", "SetDldrTuning", "SetHoldOriginal", "ShowHud", "HideHud",
            "ShowApp", "HideApp", "ConfigureRuntimeFolder", "SetHotkey", "RestoreHotkeys",
            "SetAiPermissions", "RunSelfTest", "EmergencyStop", "InitializeHotkeys", "SetLanguage"
        ];
        AssertSequence(Enum.GetNames<NeraCommandKind>(), expected);
        string[] payloadProperties = typeof(NeraCommandPayload)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        AssertSequence(payloadProperties,
            "AiPermissions", "DisplayId", "DldrTuning", "Feature18Tuning", "HoldOriginal", "Hotkey", "LanguagePreference", "Mode", "Options",
            "PerformanceMode", "RuntimeFolder", "Strength", "UiProtection");
        string joined = string.Join('|', expected) + '|' + string.Join('|', payloadProperties);
        Assert(!ContainsLegacyWorkflow(joined), "Production Control surface contains a legacy workflow.");
        return Task.CompletedTask;
    }

    private static Task WireSurfaceIsGlobalOnly()
    {
        string[] expected =
        [
            "Handshake", "GetStatus", "GetDisplays", "SetDisplay", "EnableDldr", "DisableDldr",
            "SetMode", "SetStrength", "SetPerformanceMode", "SetUiProtection", "SetFeature18Tuning", "SetDldrTuning", "ShowHud", "HideHud",
            "RunSelfTest", "EmergencyStop"
        ];
        AssertSequence(Enum.GetNames<NeraWireCommand>(), expected);
        Assert(!ContainsLegacyWorkflow(string.Join('|', expected)), "Stable wire contains a legacy workflow.");
        AssertEqual(7, NeraControlWireProtocol.CurrentProtocolVersion);
        return Task.CompletedTask;
    }

    private static Task WireRejectsLegacyAndUnknownCommands()
    {
        string Prefix(string command) =>
            "{\"protocolVersion\":7,\"requestId\":\"strict\",\"source\":\"agent\"," +
            "\"expectedRevision\":0,\"timestamp\":\"2026-09-01T00:00:00Z\"," +
            $"\"command\":\"{command}\",\"payload\":{{}}}}";
        foreach (string command in new[] { "openImage", "selectTarget", "selectWindow", "enableDlss5", "applyProfile" })
        {
            ExpectWireFailure(() => NeraControlWireProtocol.DeserializeRequest(Encoding.UTF8.GetBytes(Prefix(command))),
                NeraControlCodes.MalformedJson);
        }
        string unknownProperty = Prefix("getStatus").Replace("\"payload\":{}", "\"payload\":{},\"surprise\":true");
        ExpectWireFailure(() => NeraControlWireProtocol.DeserializeRequest(Encoding.UTF8.GetBytes(unknownProperty)),
            NeraControlCodes.MalformedJson);
        return Task.CompletedTask;
    }

    private static async Task WireRoundTripIsLengthPrefixed()
    {
        var request = new NeraWireRequest
        {
            RequestId = "wire-round-trip",
            Source = NeraCommandSource.Cli,
            ExpectedRevision = 7,
            Timestamp = DateTimeOffset.UtcNow,
            Command = NeraWireCommand.SetDisplay,
            Payload = NeraCommandPayload.ForDisplay("display:1")
        };
        byte[] json = NeraControlWireProtocol.SerializeRequest(request);
        await using var stream = new MemoryStream();
        await NeraControlWireProtocol.WriteFrameAsync(stream, json);
        byte[] framed = stream.ToArray();
        AssertEqual(json.Length, BinaryPrimitives.ReadInt32LittleEndian(framed.AsSpan(0, 4)));
        stream.Position = 0;
        byte[] read = await NeraControlWireProtocol.ReadFrameAsync(stream);
        NeraWireRequest decoded = NeraControlWireProtocol.DeserializeRequest(read);
        AssertEqual(request.RequestId, decoded.RequestId);
        AssertEqual("display:1", decoded.Payload.DisplayId);
    }

    private static async Task QueuedDisableSupersedesObservationFailure()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend, fastObservation: true);
        NeraCommandResult enabled = await EnableAsync(control, 0);
        Assert(enabled.Ok && enabled.State.DldrOn, enabled.TechnicalMessage);

        int observationsBefore = backend.Calls.Count(call =>
            string.Equals(call, "observeAuthoritativeState", StringComparison.Ordinal));
        backend.ObservationDelay = TimeSpan.FromMilliseconds(250);
        backend.ObservationException = new IOException("expected shutdown observation race");

        await WaitUntilAsync(() => backend.Calls.Count(call =>
                string.Equals(call, "observeAuthoritativeState", StringComparison.Ordinal)) > observationsBefore,
            TimeSpan.FromSeconds(2));
        NeraCommandResult disabled = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.DisableDldr, NeraCommandSource.Hotkey, enabled.CurrentRevision));

        Assert(disabled.Ok, disabled.TechnicalMessage);
        Assert(!disabled.State.DldrOn && !disabled.State.DldrRequested &&
               !disabled.State.ProcessingActive && disabled.State.OffGateSatisfied,
            "Disable must finish at the strict OFF gate after superseding the stale observation.");
        Assert(disabled.State.LastError is null,
            "An in-flight observation superseded by an explicit disable must not leave a false error.");
        AssertEqual(1, backend.Calls.Count(call =>
            string.Equals(call, "disableDldr:False", StringComparison.Ordinal)));
    }

    private static async Task QueuedDisableSupersedesObservationTimeout()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend, fastObservation: true);
        NeraCommandResult enabled = await EnableAsync(control, 0);
        Assert(enabled.Ok && enabled.State.DldrOn, enabled.TechnicalMessage);

        int observationsBefore = backend.Calls.Count(call =>
            string.Equals(call, "observeAuthoritativeState", StringComparison.Ordinal));
        // LegacyNativeControlBackend.StatusJson is synchronous: the timeout token
        // is observed only after the native call returns. Model that exact edge,
        // rather than a cooperatively-cancelled Task.Delay.
        backend.ObservationDelay = TimeSpan.FromMilliseconds(1250);
        backend.ObservationDelayIgnoresCancellation = true;

        await WaitUntilAsync(() => backend.Calls.Count(call =>
                string.Equals(call, "observeAuthoritativeState", StringComparison.Ordinal)) > observationsBefore,
            TimeSpan.FromSeconds(2));
        NeraCommandResult disabled = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.DisableDldr, NeraCommandSource.Ui, enabled.CurrentRevision));

        Assert(disabled.Ok, disabled.TechnicalMessage);
        Assert(!disabled.State.DldrOn && !disabled.State.DldrRequested &&
               !disabled.State.ProcessingActive && disabled.State.OffGateSatisfied,
            "A queued explicit disable must own release after the stale observation times out.");
        Assert(disabled.State.LastError is null,
            "A timed-out observation superseded by an explicit disable must not leave a false error.");
        AssertEqual(1, backend.Calls.Count(call =>
            string.Equals(call, "disableDldr:False", StringComparison.Ordinal)));
    }

    private static async Task QuiescentObservationTimeoutDoesNotCreateDldrError()
    {
        var backend = new FakeControlBackend
        {
            ObservationDelay = TimeSpan.FromMilliseconds(1250),
            ObservationDelayIgnoresCancellation = true
        };
        await using var control = Service(backend, fastObservation: true);

        await WaitUntilAsync(() => backend.Calls.Any(call =>
                string.Equals(call, "observeAuthoritativeState", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromMilliseconds(1400));

        NeraStateSnapshot state = await control.GetSnapshotAsync();
        Assert(!state.DldrRequested && !state.DldrOn && !state.ProcessingActive &&
               state.OffGateSatisfied,
            "An idle observation timeout must leave the strict OFF gate intact.");
        Assert(state.LastError is null,
            "An idle observation timeout must not fabricate a DLDR failure after release is proven.");
        Assert(state.RecoveryState is NeraRecoveryState.None or NeraRecoveryState.BypassRestored,
            "An idle observation timeout must not enter a new recovery state.");
    }

    private static async Task NonReleaseCommandCannotSuppressObservationFailure()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend, fastObservation: true);
        NeraCommandResult enabled = await EnableAsync(control, 0);
        Assert(enabled.Ok && enabled.State.DldrOn, enabled.TechnicalMessage);

        int observationsBefore = backend.Calls.Count(call =>
            string.Equals(call, "observeAuthoritativeState", StringComparison.Ordinal));
        backend.ObservationDelay = TimeSpan.FromMilliseconds(250);
        backend.ObservationException = new IOException("processing health lost");
        await WaitUntilAsync(() => backend.Calls.Count(call =>
                string.Equals(call, "observeAuthoritativeState", StringComparison.Ordinal)) > observationsBefore,
            TimeSpan.FromSeconds(2));

        NeraCommandResult command = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.ShowHud, NeraCommandSource.Ui, enabled.CurrentRevision));
        NeraStateSnapshot final = await control.GetSnapshotAsync();
        Assert(!command.Ok && command.Code == NeraControlCodes.StateChanged,
            "A queued non-release command must not outrun a failed Processing observation.");
        Assert(!final.DldrOn && !final.ProcessingActive && final.OffGateSatisfied,
            "A failed Processing observation must still revoke ON and complete fail-safe.");
        Assert(final.LastError?.Code == NeraControlCodes.OperationFailed,
            "The genuine observation failure must remain diagnosable.");
        Assert(backend.Calls.Contains("failSafe:" + NeraControlCodes.OperationFailed),
            "A non-release command must not suppress fail-safe.");
    }

    private static async Task StaleDisableCannotSuppressObservationFailure()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend, fastObservation: true);
        NeraCommandResult enabled = await EnableAsync(control, 0);
        Assert(enabled.Ok && enabled.State.DldrOn, enabled.TechnicalMessage);

        int observationsBefore = backend.Calls.Count(call =>
            string.Equals(call, "observeAuthoritativeState", StringComparison.Ordinal));
        backend.ObservationDelay = TimeSpan.FromMilliseconds(250);
        backend.ObservationException = new IOException("processing health lost before stale disable");
        await WaitUntilAsync(() => backend.Calls.Count(call =>
                string.Equals(call, "observeAuthoritativeState", StringComparison.Ordinal)) > observationsBefore,
            TimeSpan.FromSeconds(2));

        NeraCommandResult command = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.DisableDldr, NeraCommandSource.Ui, enabled.PreviousRevision));
        NeraStateSnapshot final = await control.GetSnapshotAsync();
        Assert(!command.Ok && command.Code == NeraControlCodes.StateChanged,
            "A stale-revision DisableDldr must not own the release transition.");
        Assert(!final.DldrOn && !final.ProcessingActive && final.OffGateSatisfied,
            "A stale disable must not suppress fail-safe after observation loss.");
        Assert(final.LastError?.Code == NeraControlCodes.OperationFailed,
            "The observation failure must remain visible after stale disable rejection.");
        Assert(backend.Calls.Contains("failSafe:" + NeraControlCodes.OperationFailed),
            "A stale disable must not suppress fail-safe.");
    }

    private static Task WireProcessingModeUsesPublicClearToken()
    {
        var request = new NeraWireRequest
        {
            RequestId = "wire-clear-mode",
            Source = NeraCommandSource.Agent,
            ExpectedRevision = 3,
            Timestamp = DateTimeOffset.UtcNow,
            Command = NeraWireCommand.SetMode,
            Payload = NeraCommandPayload.ForMode(NeraProcessingMode.Sharp)
        };
        byte[] requestJson = NeraControlWireProtocol.SerializeRequest(request);
        string requestText = Encoding.UTF8.GetString(requestJson);
        Assert(requestText.Contains("\"mode\":\"clear\"", StringComparison.Ordinal),
            "The public SetMode wire token for internal Sharp must be clear.");
        Assert(!requestText.Contains("\"mode\":\"sharp\"", StringComparison.Ordinal),
            "The internal Sharp enum name must not escape onto the public wire.");
        NeraWireRequest decodedRequest = NeraControlWireProtocol.DeserializeRequest(requestJson);
        AssertEqual(NeraProcessingMode.Sharp, decodedRequest.Payload.Mode);

        var response = new NeraWireResponse
        {
            Ok = true,
            RequestId = request.RequestId,
            PreviousRevision = 3,
            CurrentRevision = 4,
            State = NeraStateSnapshot.CreateInitial() with
            {
                Revision = 4,
                ProcessingMode = NeraProcessingMode.Sharp
            },
            Code = NeraControlCodes.Ok,
            UserMessage = "ok"
        };
        byte[] responseJson = NeraControlWireProtocol.SerializeResponse(response);
        string responseText = Encoding.UTF8.GetString(responseJson);
        Assert(responseText.Contains("\"processingMode\":\"clear\"", StringComparison.Ordinal),
            "The authoritative state snapshot must expose clear on the public wire.");
        Assert(!responseText.Contains("\"processingMode\":\"sharp\"", StringComparison.Ordinal),
            "The authoritative state snapshot must not expose the internal Sharp enum name.");
        NeraWireResponse decodedResponse = NeraControlWireProtocol.DeserializeResponse(responseJson);
        AssertEqual(NeraProcessingMode.Sharp, decodedResponse.State.ProcessingMode);

        byte[] legacySharp = Encoding.UTF8.GetBytes(
            requestText.Replace("\"mode\":\"clear\"", "\"mode\":\"sharp\"",
                StringComparison.Ordinal));
        ExpectWireFailure(() => NeraControlWireProtocol.DeserializeRequest(legacySharp),
            NeraControlCodes.MalformedJson);
        return Task.CompletedTask;
    }

    private static async Task WireV3StrictClientReceivesProtocolMismatch()
    {
        var authorizer = new CountingAllowAuthorizer();
        await using var control = Service(new FakeControlBackend(), ReadyState() with
        {
            AiPermissions = AllPermissions()
        });
        string pipeName = "Nera.Control.Tests." + Guid.NewGuid().ToString("N");
        await using var server = new NeraControlPipeServer(control, authorizer,
            new NeraControlPipeServerOptions
            {
                PipeName = pipeName,
                ExpectedAgentBridgeVersion = "0.4.1"
            });
        await server.StartAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);

        NeraWireRequest request = Handshake("legacy-v3") with { ProtocolVersion = 3 };
        await NeraControlWireProtocol.WriteFrameAsync(pipe,
            NeraControlWireProtocol.SerializeRequest(request), timeout.Token);
        byte[] responseBytes = await NeraControlWireProtocol.ReadFrameAsync(pipe, timeout.Token);
        string responseText = Encoding.UTF8.GetString(responseBytes);

        Assert(!responseText.Contains("feature18SkinStructureStrength", StringComparison.Ordinal),
            "The v3 negotiation reply must not include v4-only state fields.");
        Assert(!responseText.Contains("\"runtimePath\"", StringComparison.Ordinal),
            "The v3 negotiation reply must not expose the complete runtime state.");

        var strictV3Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            MaxDepth = 32
        };

        NeraWireResponse v4Response = NeraControlWireProtocol.DeserializeResponse(responseBytes);
        byte[] fullV4Response = NeraControlWireProtocol.SerializeResponse(v4Response);
        Assert(Encoding.UTF8.GetString(fullV4Response).Contains(
                "feature18SkinStructureStrength", StringComparison.Ordinal),
            "The regression negative control must contain the v4-only state member.");
        bool fullV4Rejected = false;
        try
        {
            _ = JsonSerializer.Deserialize<StrictV3WireResponse>(fullV4Response, strictV3Options);
        }
        catch (JsonException)
        {
            fullV4Rejected = true;
        }
        Assert(fullV4Rejected,
            "The frozen strict v3 DTO must reject a complete v4 state response.");

        StrictV3WireResponse? decoded = JsonSerializer.Deserialize<StrictV3WireResponse>(
            responseBytes, strictV3Options);
        Assert(decoded is not null, "The strict v3 response DTO must decode the negotiation reply.");
        AssertEqual(7, decoded!.ProtocolVersion);
        Assert(!decoded.Ok, "A protocol mismatch must fail closed.");
        AssertEqual("legacy-v3", decoded.RequestId);
        AssertEqual(NeraControlCodes.ProtocolMismatch, decoded.Code);
        AssertEqual(decoded.CurrentRevision, decoded.State!.Revision);
        AssertEqual(0, authorizer.CallCount);
    }

    private static async Task WireHandshakeDefaultsToDeny()
    {
        await using var control = Service(new FakeControlBackend(), ReadyState() with
        {
            AiPermissions = AllPermissions()
        });
        var handler = new NeraControlWireHandler(control, expectedAgentBridgeVersion: "0.4.1");
        NeraWireResponse response = await handler.HandleAsync(Handshake("default-deny"), new NeraWireSession());
        AssertEqual(NeraControlCodes.PermissionDenied, response.Code);
    }

    private static async Task WireMutationsRequireRevisionAndPermission()
    {
        NeraAiControlPermissions permissions = AllPermissions() with { ToggleDldr = false };
        await using var control = Service(new FakeControlBackend(), ReadyState() with
        {
            AiPermissions = permissions
        });
        var handler = new NeraControlWireHandler(control, new AllowAuthorizer(), "0.4.1");
        var session = new NeraWireSession();
        NeraWireResponse handshake = await handler.HandleAsync(Handshake("allowed"), session);
        Assert(handshake.Ok && session.HandshakeComplete, handshake.TechnicalMessage);

        NeraWireResponse denied = await handler.HandleAsync(WireMutation(
            "denied", NeraWireCommand.EnableDldr, 0), session);
        AssertEqual(NeraControlCodes.PermissionDenied, denied.Code);

        NeraWireResponse missingRevision = await handler.HandleAsync(WireMutation(
            "missing-revision", NeraWireCommand.ShowHud, null), session);
        AssertEqual(NeraControlCodes.InvalidArgument, missingRevision.Code);

        NeraWireResponse displays = await handler.HandleAsync(new NeraWireRequest
        {
            RequestId = "displays",
            Source = NeraCommandSource.Agent,
            Timestamp = DateTimeOffset.UtcNow,
            Command = NeraWireCommand.GetDisplays,
            AgentBridgeVersion = "0.4.1"
        }, session);
        Assert(displays.Ok && displays.Displays?.Count == 1, displays.TechnicalMessage);
    }

    private static async Task AllSourcesShareOneRevisionLane()
    {
        var backend = new FakeControlBackend();
        await using var control = Service(backend);
        NeraCommandResult ui = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.ShowHud, NeraCommandSource.Ui, 0));
        NeraCommandResult cli = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.HideHud, NeraCommandSource.Cli, ui.CurrentRevision));
        NeraCommandResult agent = await control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.SetMode, NeraCommandSource.Agent, cli.CurrentRevision,
            NeraCommandPayload.ForMode(NeraProcessingMode.Cinema)));
        Assert(ui.Ok && cli.Ok && agent.Ok, "UI/CLI/Agent commands must share one service.");
        AssertEqual(ui.CurrentRevision + 1, cli.CurrentRevision);
        AssertEqual(cli.CurrentRevision + 1, agent.CurrentRevision);
        AssertEqual(NeraProcessingMode.Cinema, agent.State.ProcessingMode);
    }

    private static NeraControlService Service(
        FakeControlBackend backend,
        NeraStateSnapshot? initial = null,
        bool fastObservation = false) => new(backend, initial ?? ReadyState(), new NeraControlOptions
        {
            BackendStepTimeout = TimeSpan.FromSeconds(1),
            FirstFrameTimeout = TimeSpan.FromSeconds(1),
            FailSafeTimeout = TimeSpan.FromSeconds(1),
            ActiveObservationInterval = fastObservation
                ? TimeSpan.FromMilliseconds(20) : TimeSpan.FromSeconds(10),
            IdleObservationInterval = fastObservation
                ? TimeSpan.FromMilliseconds(40) : TimeSpan.FromSeconds(10),
            ObservationTimeout = TimeSpan.FromSeconds(1)
        });

    private static NeraStateSnapshot ReadyState(NeraDisplaySummary? display = null)
    {
        display ??= FakeControlBackend.Display(selected: true);
        return NeraStatePolicy.WithComputedDldrGate(NeraStateSnapshot.CreateInitial() with
        {
            TargetDisplay = display,
            AvailableDisplays = [display],
            InputWidth = display.Width,
            InputHeight = display.Height,
            InputFps = display.RefreshRateHz,
            OutputWidth = display.Width,
            OutputHeight = display.Height,
            OutputFps = display.RefreshRateHz,
            NeuralInputWidth = display.Width,
            NeuralInputHeight = display.Height,
            HdrSystemEnabled = display.HdrEnabled,
            HdrPipelineEnabled = display.AdvancedColorEnabled,
            HdrInputDetected = display.HdrEnabled
        });
    }

    private static NeraAiControlPermissions AllPermissions() => new()
    {
        Enabled = true,
        ReadState = true,
        ReadDisplays = true,
        SetDisplay = true,
        ToggleDldr = true,
        ModifyDldrSettings = true,
        ControlHud = true,
        RunDiagnostics = true
    };

    private static Task<NeraCommandResult> EnableAsync(NeraControlService control, long revision) =>
        control.ExecuteAsync(NeraCommandEnvelope.Create(
            NeraCommandKind.EnableDldr, NeraCommandSource.Ui, revision));

    private static NeraFirstFrameGateOutcome Gate(
        bool feature = true,
        bool dldr = true,
        bool presenter = true,
        bool foreground = true) => new()
    {
        Feature18ProcessSucceeded = feature,
        DldrSucceeded = dldr,
        PresenterSucceeded = presenter,
        ForegroundPreserved = foreground,
        FrameId = 1,
        HostSessionGeneration = 1,
        Feature18SuccessfulFrames = feature ? 1UL : 0,
        DldrSuccessfulFrames = dldr ? 1UL : 0,
        PresentedFrames = presenter ? 1UL : 0
    };

    private static NeraWireRequest Handshake(string requestId) => new()
    {
        RequestId = requestId,
        Source = NeraCommandSource.Agent,
        Timestamp = DateTimeOffset.UtcNow,
        Command = NeraWireCommand.Handshake,
        AgentBridgeVersion = "0.4.1",
        ExpectedAppVersion = "0.4.1-global-alpha.1"
    };

    private static NeraWireRequest WireMutation(
        string requestId,
        NeraWireCommand command,
        long? revision) => new()
    {
        RequestId = requestId,
        Source = NeraCommandSource.Agent,
        ExpectedRevision = revision,
        Timestamp = DateTimeOffset.UtcNow,
        Command = command,
        AgentBridgeVersion = "0.4.1"
    };

    private static bool ContainsLegacyWorkflow(string value) =>
        new[] { "Target", "Window", "Image", "Video", "Profile", "Fullscreen", "Dlss", "Hdr" }
            .Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static void ExpectWireFailure(Action action, string expectedCode)
    {
        try
        {
            action();
            throw new InvalidOperationException("Expected a wire protocol failure.");
        }
        catch (NeraWireProtocolException error)
        {
            AssertEqual(expectedCode, error.Code);
        }
    }

    private static void AssertSubsequence(IEnumerable<string> actual, params string[] expected)
    {
        string[] values = actual.ToArray();
        int position = 0;
        foreach (string value in values)
        {
            if (position < expected.Length && string.Equals(value, expected[position], StringComparison.Ordinal))
                position++;
        }
        if (position != expected.Length)
            throw new InvalidOperationException(
                $"Expected subsequence=[{string.Join(',', expected)}]; actual=[{string.Join(',', values)}].");
    }

    private static void AssertSequence<T>(IEnumerable<T> actual, params T[] expected)
    {
        T[] values = actual.ToArray();
        if (!values.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"Expected=[{string.Join(',', expected)}]; actual=[{string.Join(',', values)}].");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the test condition.");
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected={expected}; actual={actual}.");
    }

    private sealed class AllowAuthorizer : INeraAgentConnectionAuthorizer
    {
        public ValueTask<NeraAgentConnectionDecision> AuthorizeAsync(
            NeraAgentConnectionRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(NeraAgentConnectionDecision.AllowOnce);
    }

    private sealed class CountingAllowAuthorizer : INeraAgentConnectionAuthorizer
    {
        public int CallCount { get; private set; }

        public ValueTask<NeraAgentConnectionDecision> AuthorizeAsync(
            NeraAgentConnectionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(NeraAgentConnectionDecision.AllowOnce);
        }
    }

    // Frozen protocol-v3 mirrors. They intentionally omit the v4 skin-structure
    // member and reject every unmapped member through the options in the test.
    private sealed record StrictV3WireResponse
    {
        public int ProtocolVersion { get; init; } = 3;
        public required bool Ok { get; init; }
        public required string RequestId { get; init; }
        public long? PreviousRevision { get; init; }
        public long? CurrentRevision { get; init; }
        public StrictV3StateSnapshot? State { get; init; }
        public required string Code { get; init; }
        public required string UserMessage { get; init; }
        public string TechnicalMessage { get; init; } = string.Empty;
        public bool RollbackPerformed { get; init; }
        public IReadOnlyList<StrictV3DisplaySummary>? Displays { get; init; }
        public string? OperationId { get; init; }
        public JsonElement? Data { get; init; }
    }

    private sealed record StrictV3StateSnapshot
    {
        public long Revision { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public required string AppVersion { get; init; }
        public required string RuntimeAdapterVersion { get; init; }
        public string? RuntimeBinaryVersion { get; init; }
        public string? RuntimeSha256 { get; init; }
        public string? RuntimePath { get; init; }
        public bool RuntimeSignatureValid { get; init; }
        public string? RuntimeSigner { get; init; }
        public bool RuntimeVerified { get; init; }
        public StrictV3DisplaySummary? TargetDisplay { get; init; }
        public IReadOnlyList<StrictV3DisplaySummary> AvailableDisplays { get; init; } = [];
        public int InputWidth { get; init; }
        public int InputHeight { get; init; }
        public double InputFps { get; init; }
        public int OutputWidth { get; init; }
        public int OutputHeight { get; init; }
        public double OutputFps { get; init; }
        public bool HdrSystemEnabled { get; init; }
        public bool HdrPipelineEnabled { get; init; }
        public bool HdrInputDetected { get; init; }
        public required string WorkingColorSpace { get; init; }
        public bool DldrRequested { get; init; }
        public required string DldrActualState { get; init; }
        public required string ProcessingMode { get; init; }
        public bool Feature18Custom { get; init; }
        public int Feature18Style { get; init; }
        public double Feature18Intensity { get; init; }
        public double Feature18LocalToneStrength { get; init; }
        public double Feature18LocalStructureStrength { get; init; }
        public bool Feature18UseAutoMask { get; init; }
        public int Strength { get; init; }
        public int NeuralScale { get; init; }
        public required string PerformanceMode { get; init; }
        public required string UiProtection { get; init; }
        public bool HudVisible { get; init; }
        public bool AppVisible { get; init; } = true;
        public bool HoldOriginal { get; init; }
        public int NeuralInputWidth { get; init; }
        public int NeuralInputHeight { get; init; }
        public IReadOnlyList<StrictV3HotkeyBinding> Hotkeys { get; init; } = [];
        public StrictV3AiControlPermissions AiPermissions { get; init; } = new();
        public StrictV3PerformanceSnapshot Performance { get; init; } = new();
        public ulong LastSuccessfulFrameId { get; init; }
        public ulong LastFeature18FrameId { get; init; }
        public ulong LastDldrFrameId { get; init; }
        public ulong LastPresentedFrameId { get; init; }
        public ulong Feature18SuccessfulFrames { get; init; }
        public ulong DldrSuccessfulFrames { get; init; }
        public ulong PresentedFrames { get; init; }
        public StrictV3ErrorInfo? LastError { get; init; }
        public required string RecoveryState { get; init; }
        public bool GpuVerified { get; init; }
        public bool BrokerProcessOwned { get; init; }
        public bool BrokerConnected { get; init; }
        public bool HostConnected { get; init; }
        public bool MonitorCaptureCreated { get; init; }
        public bool FeatureCreated { get; init; }
        public bool FirstFeature18FrameSucceeded { get; init; }
        public bool DldrSucceeded { get; init; }
        public bool PresenterSucceeded { get; init; }
        public bool ForegroundPreserved { get; init; }
        public bool ProcessingActive { get; init; }
        public bool OffGateSatisfied { get; init; } = true;
        public bool DldrOn { get; init; }
    }

    private sealed record StrictV3DisplaySummary
    {
        public required string DisplayId { get; init; }
        public required string DisplayName { get; init; }
        public string? SourceName { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public uint RefreshNumerator { get; init; }
        public uint RefreshDenominator { get; init; }
        public double RefreshRateHz { get; init; }
        public bool HdrSupported { get; init; }
        public bool HdrEnabled { get; init; }
        public bool AdvancedColorEnabled { get; init; }
        public double? SdrWhiteLevelNits { get; init; }
        public required string AdapterLuid { get; init; }
        public string? TargetId { get; init; }
        public bool Connected { get; init; }
        public bool Selected { get; init; }
        public bool Capturable { get; init; }
    }

    private sealed record StrictV3HotkeyBinding
    {
        public required string Action { get; init; }
        public uint Modifiers { get; init; }
        public uint VirtualKey { get; init; }
        public bool Registered { get; init; }
        public string? ErrorCode { get; init; }
    }

    private sealed record StrictV3AiControlPermissions
    {
        public bool Enabled { get; init; }
        public bool ReadState { get; init; }
        public bool ReadDisplays { get; init; }
        public bool SetDisplay { get; init; }
        public bool ToggleDldr { get; init; }
        public bool ModifyDldrSettings { get; init; }
        public bool ControlHud { get; init; }
        public bool RunDiagnostics { get; init; }
    }

    private sealed record StrictV3PerformanceSnapshot
    {
        public double FramesPerSecond { get; init; }
        public double AverageFramesPerSecond { get; init; }
        public double OnePercentLowFramesPerSecond { get; init; }
        public double CpuPercent { get; init; }
        public double GpuPercent { get; init; }
        public long MemoryBytes { get; init; }
        public long DedicatedVideoMemoryBytes { get; init; }
        public double InputFramesPerSecond { get; init; }
        public double CaptureFramesPerSecond { get; init; }
        public double ProcessingFramesPerSecond { get; init; }
        public double PresentFramesPerSecond { get; init; }
        public long DroppedFrames { get; init; }
        public long BypassFrames { get; init; }
        public double Feature18Milliseconds { get; init; }
        public double DldrMilliseconds { get; init; }
        public double EndToEndLatencyMilliseconds { get; init; }
    }

    private sealed record StrictV3ErrorInfo
    {
        public required string Code { get; init; }
        public required string UserMessage { get; init; }
        public string TechnicalMessage { get; init; } = string.Empty;
        public DateTimeOffset Timestamp { get; init; }
    }

    private sealed record TestCase(string Name, Func<Task> Body);
}
