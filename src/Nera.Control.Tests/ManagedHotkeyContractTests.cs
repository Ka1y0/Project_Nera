using System.Collections.Concurrent;
using ChipsStudio.Nera.Control;

namespace ChipsStudio.Nera.Control.Tests;

internal static class ManagedHotkeyContractTests
{
    internal static async Task WindowsPlatformRegistersAndUnregisters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var platform = new WindowsGlobalHotkeyPlatform();
        const int id = 0x4EFF;
        var candidates = new[]
        {
            new NeraHotkeyChord(NeraHotkeyModifiers.Control | NeraHotkeyModifiers.Alt |
                NeraHotkeyModifiers.Shift, 0x87), // F24
            new NeraHotkeyChord(NeraHotkeyModifiers.Control | NeraHotkeyModifiers.Alt |
                NeraHotkeyModifiers.Shift, 0x86), // F23
            new NeraHotkeyChord(NeraHotkeyModifiers.Control | NeraHotkeyModifiers.Alt |
                NeraHotkeyModifiers.Shift, 0x85)  // F22
        };
        NeraHotkeyPlatformResult registration = default;
        foreach (var candidate in candidates)
        {
            registration = platform.TryRegister(
                id, candidate.RegistrationModifiers, candidate.VirtualKey);
            if (registration.Succeeded)
            {
                break;
            }
        }
        Assert(registration.Succeeded,
            $"No isolated F22-F24 chord could be registered; last Win32 error={registration.Win32Error}.");
        var removed = platform.TryUnregister(id);
        Assert(removed.Succeeded,
            $"Registered smoke-test ID was not unregistered; Win32 error={removed.Win32Error}.");
    }

    internal static async Task CandidateFallbackUsesNoRepeat()
    {
        await using var platform = new FakeGlobalHotkeyPlatform();
        await using var service = new ManagedHotkeyRegistrationService(platform, ownsPlatform: false);
        var preferred = new NeraHotkeyChord(
            NeraHotkeyModifiers.Control | NeraHotkeyModifiers.Alt, 'D');
        platform.FailRegistration(preferred, 1409);

        var evidence = await service.RegisterOrUpdateAsync(new NeraHotkeyBinding
        {
            Action = NeraHotkeyAction.ToggleDldr,
            Modifiers = preferred.Modifiers,
            VirtualKey = preferred.VirtualKey
        }, useRecommendedFallbacks: true);

        Assert(evidence.Registered, "A fallback candidate should register after the preferred chord conflicts.");
        Assert(evidence.UsedFallbackCandidate, "The actual evidence must identify fallback selection.");
        AssertEqual(1, evidence.CandidateIndex);
        AssertEqual(NeraHotkeyModifiers.Control | NeraHotkeyModifiers.Shift,
            evidence.ActualChord!.Value.Modifiers);
        Assert(platform.RegisterAttempts.All(attempt =>
                (attempt.Modifiers & NeraHotkeyModifiers.NoRepeat) != 0),
            "Every RegisterHotKey call must include MOD_NOREPEAT.");
        AssertEqual(NeraHotkeyModifiers.Control | NeraHotkeyModifiers.Shift,
            evidence.ToBinding().Modifiers);
    }

    internal static async Task CleanupAttemptsEveryRegisteredId()
    {
        await using var platform = new FakeGlobalHotkeyPlatform();
        await using var service = new ManagedHotkeyRegistrationService(platform, ownsPlatform: false);
        var registered = await service.RegisterRecommendedAsync();
        AssertEqual(4, registered.RegisteredCount);
        Assert(registered.AllRegisterHotKeyActionsRegistered, "All actions with key-down semantics should register.");
        Assert(!registered.Registrations.Any(value => value.Action == NeraHotkeyAction.HoldOriginal),
            "Hold Original must not be listed as a product shortcut.");
        Assert(service.FixedEmergencyRegistered, "The fixed escape shortcut must be actually registered.");

        var firstId = platform.ActiveIds.Order().First();
        platform.FailUnregister(firstId, 5);
        await service.UnregisterAllAsync();

        var attemptedIds = platform.UnregisterAttempts.Distinct().ToArray();
        AssertEqual(4, attemptedIds.Length);
        Assert(attemptedIds.Contains(firstId), "Cleanup must attempt even the injected failing ID.");
        Assert(platform.ActiveIds.Contains(firstId),
            "The fake retains a failed ID so disposal can retry; success must not be fabricated.");
    }

    internal static async Task RevealHoldFailsClosed()
    {
        await using var platform = new FakeGlobalHotkeyPlatform();
        await using var registration = new ManagedHotkeyRegistrationService(platform, ownsPlatform: false);
        var evidence = await registration.RegisterOrUpdateAsync(new NeraHotkeyBinding
        {
            Action = NeraHotkeyAction.HoldOriginal,
            Modifiers = NeraHotkeyModifiers.Control | NeraHotkeyModifiers.Win,
            VirtualKey = 'O'
        });
        AssertEqual(NeraHotkeyRegistrationStatus.HoldSemanticsUnsupported, evidence.Status);
        AssertEqual(0, platform.RegisterAttempts.Count);

        var client = new RecordingControlClient(ReadyState());
        await using var router = new HotkeyCommandRouter(client);
        var result = await router.RouteAsync(NeraHotkeyAction.HoldOriginal);
        Assert(!result.Ok, "A press-only global hotkey must not turn reveal-original on indefinitely.");
        AssertEqual(NeraControlCodes.HotkeyHoldUnsupported, result.Code);
        AssertEqual(0, client.Commands.Count);
    }

    internal static async Task RouterUsesUnifiedCommands()
    {
        var client = new RecordingControlClient(ReadyState());
        await using var router = new HotkeyCommandRouter(client);

        var hud = await router.RouteAsync(NeraHotkeyAction.ToggleHud);
        var visibility = await router.RouteAsync(NeraHotkeyAction.ToggleAppVisibility);
        var dlss = await router.RouteAsync(NeraHotkeyAction.ToggleDldr);

        Assert(hud.Ok && visibility.Ok && dlss.Ok, "Mapped hotkey commands should complete.");
        var commands = client.Commands.ToArray();
        AssertSequence(commands.Select(command => command.Kind),
            NeraCommandKind.ShowHud,
            NeraCommandKind.HideApp,
            NeraCommandKind.EnableDldr);
        Assert(commands.All(command => command.Source == NeraCommandSource.Hotkey),
            "Every routed activation must enter Control with source HOTKEY.");
        Assert(commands.Zip(commands.Skip(1)).All(pair =>
                pair.Second.ExpectedRevision == pair.First.ExpectedRevision + 1),
            "Router must read and submit the authoritative revision for each toggle.");
    }

    internal static async Task RegistrationActivationAndEmergencyPriority()
    {
        await using var platform = new FakeGlobalHotkeyPlatform();
        await using var registration = new ManagedHotkeyRegistrationService(platform, ownsPlatform: false);
        await registration.RegisterRecommendedAsync();

        var client = new RecordingControlClient(ReadyState())
        {
            BlockingKind = NeraCommandKind.ShowHud
        };
        await using var router = new HotkeyCommandRouter(client);
        registration.Activated += (_, activation) => router.TryPost(activation.Action, activation.Origin);

        platform.Activate(ManagedHotkeyRegistrationService.RegistrationIdFor(NeraHotkeyAction.ToggleHud));
        await client.BlockingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        platform.Activate(ManagedHotkeyRegistrationService.RegistrationIdFor(NeraHotkeyAction.EmergencyStopFallback));
        await client.EmergencyObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(!client.ReleaseBlocking.Task.IsCompleted,
            "Emergency evidence must arrive while the normal hotkey command is still blocked.");
        Assert(client.Commands.Any(command => command.Kind == NeraCommandKind.EmergencyStop),
            "EmergencyStop must enter the authoritative control command lane.");
        Assert(client.Commands.All(command => command.InputOrigin is
            { Message: 0x0312, SourceThreadId: 123, InputKind: NeraInputKind.SyntheticTest } &&
            command.InputOrigin.SourcePid == Environment.ProcessId && command.InputOrigin.RegistrationId > 0),
            "Recorded receiver/message/chord provenance must reach the exact canonical command.");

        client.ReleaseBlocking.TrySetResult();
        await WaitUntilAsync(() => client.Commands.Count >= 2, TimeSpan.FromSeconds(2));
    }

    internal static async Task Phase13KeyboardRebindContracts()
    {
        uint ctrlAlt = NeraHotkeyModifiers.Control | NeraHotkeyModifiers.Alt;
        foreach (uint key in new uint[] { 1, 2, 3, 4, 5, 6, 0x10, 0x11, 0x12, 0x5B, 0xA0, 0xAD, 0xFF, 0x020A })
            Assert(!new NeraHotkeyChord(ctrlAlt, key).IsValid, "Mouse, modifier-only, media and message IDs are not keyboard chords.");
        foreach (NeraHotkeyChord chord in new[]
        {
            new NeraHotkeyChord(0, 'A'), new NeraHotkeyChord(0, 0x79),
            new NeraHotkeyChord(ctrlAlt, 0x2E), new NeraHotkeyChord(NeraHotkeyModifiers.Alt, 0x73),
            new NeraHotkeyChord(ctrlAlt, 0x7B), new NeraHotkeyChord(NeraHotkeyModifiers.Win, 'D')
        }) Assert(!chord.IsValid, "Unsafe or OS-reserved chords must fail format validation.");
        Assert(new NeraHotkeyChord(ctrlAlt, 'D').IsValid && new NeraHotkeyChord(0, 0x87).IsValid,
            "A keyboard chord and an allowed function key should pass.");
        var packedChord = new NeraHotkeyChord(ctrlAlt, 'D');
        nint packed = (nint)((packedChord.VirtualKey << 16) | ctrlAlt);
        foreach (uint message in new uint[] { 0x0200, 0x0201, 0x0202, 0x0204, 0x0205, 0x0207, 0x0208,
                     0x020A, 0x020B, 0x020C, 0x020E, 0x0240, 0x0245, 0x0246, 0x0247, 0x0119, 0x00FF, 0x0319 })
            Assert(!NeraHotkeyMessagePolicy.IsActivation(message, packed, packedChord),
                $"Message 0x{message:X} must not activate a DLDR shortcut.");
        Assert(NeraHotkeyMessagePolicy.IsActivation(0x0312, packed, packedChord) &&
               !NeraHotkeyMessagePolicy.IsActivation(0x0312, packed + 1, packedChord) &&
               !NeraHotkeyMessagePolicy.IsActivation(0x0312, packed, null),
            "WM_HOTKEY requires exact current chord evidence.");
        foreach (NeraCommandSource source in new[] { NeraCommandSource.Agent, NeraCommandSource.Cli,
                     NeraCommandSource.Orchestrator, NeraCommandSource.Hotkey })
        {
            bool rejectedInitialization = false;
            try { NeraCommandPolicy.Validate(NeraCommandEnvelope.Create(NeraCommandKind.InitializeHotkeys, source, 0)); }
            catch (ArgumentException) { rejectedInitialization = true; }
            Assert(rejectedInitialization, "External sources cannot invoke internal hotkey initialization.");
        }
        var recordingClient = new RecordingControlClient(ReadyState());
        await using (var recordingRouter = new HotkeyCommandRouter(recordingClient))
        {
            recordingRouter.SetRecording(true);
            Assert(!recordingRouter.TryPost(NeraHotkeyAction.ToggleDldr), "Recording suppresses normal registered hotkeys.");
            var ignored = await recordingRouter.RouteAsync(NeraHotkeyAction.ToggleHud);
            Assert(!ignored.Ok && recordingClient.Commands.IsEmpty, "A pending normal shortcut cannot escape the recorder gate.");
            var escape = await recordingRouter.RouteAsync(NeraHotkeyAction.EmergencyStopFallback);
            Assert(escape.Ok && recordingClient.Commands.Single().Kind == NeraCommandKind.EmergencyStop,
                "Fixed emergency remains active while recording.");
            recordingRouter.SetRecording(false);
            await recordingRouter.RouteAsync(NeraHotkeyAction.ToggleHud);
            AssertEqual(2, recordingClient.Commands.Count);
        }

        var store = new FakeHotkeyPreferenceStore();
        await using var platform = new FakeGlobalHotkeyPlatform();
        await using var service = new ManagedHotkeyRegistrationService(platform, ownsPlatform: false, preferencesStore: store);
        await service.InitializeAsync();
        AssertEqual(4, platform.ActiveIds.Count);
        AssertEqual(0, store.SaveCount);
        Assert(service.FixedEmergencyRegistered && !service.Current.Bindings.Any(binding => binding.Action == NeraHotkeyAction.HoldOriginal),
            "Startup must register fixed escape and omit the deferred hold action.");
        var oldChord = service.Current.Registrations.Single(value => value.Action == NeraHotkeyAction.ToggleDldr).ActualChord!.Value;
        int oldId = platform.IdFor(oldChord);
        var newChord = new NeraHotkeyChord(ctrlAlt, 'K');
        int activated = 0;
        service.Activated += (_, _) => activated++;
        store.BeforeSave = _ =>
        {
            Assert(platform.ActiveIds.Contains(oldId) && platform.IdFor(newChord) != oldId,
                "New OS registration must be staged while the old registration is still intact at persistence.");
            platform.Activate(oldId);
            platform.Activate(platform.IdFor(newChord));
            AssertEqual(1, activated); // The staged key cannot issue a command before durable commit.
        };
        var changed = await service.RegisterOrUpdateAsync(newChord.ToBinding(NeraHotkeyAction.ToggleDldr, false));
        store.BeforeSave = null;
        Assert(changed.Code == NeraControlCodes.Ok && changed.ActualChord == newChord, "Rebind must accept exactly the recorded chord.");
        Assert(!platform.ActiveIds.Contains(oldId) && store.Saved[NeraHotkeyAction.ToggleDldr] == newChord,
            "Old ID is removed only after the new preference was saved.");
        platform.Activate(platform.IdFor(newChord));
        AssertEqual(2, activated);

        int activeId = platform.IdFor(newChord);
        var unavailable = new NeraHotkeyChord(ctrlAlt, 'J');
        platform.FailRegistration(unavailable, 1409);
        int removals = platform.UnregisterAttempts.Count;
        var rejected = await service.RegisterOrUpdateAsync(unavailable.ToBinding(NeraHotkeyAction.ToggleDldr, false));
        Assert(rejected.Code == NeraControlCodes.HotkeyConflict && rejected.Registered && rejected.ActualChord == newChord,
            "Conflict must retain the actual old shortcut and report failure.");
        AssertEqual(removals, platform.UnregisterAttempts.Count);
        Assert(platform.ActiveIds.Contains(activeId), "The old shortcut cannot be unregistered before testing replacement.");
        var hudChord = service.Current.Registrations.Single(value => value.Action == NeraHotkeyAction.ToggleHud).ActualChord!.Value;
        var duplicate = await service.RegisterOrUpdateAsync(hudChord.ToBinding(NeraHotkeyAction.ToggleDldr, false));
        AssertEqual(NeraControlCodes.HotkeyInternalDuplicate, duplicate.Code);
        AssertEqual(newChord, duplicate.ActualChord!.Value);
        var invalid = await service.RegisterOrUpdateAsync(new NeraHotkeyChord(0, 'A').ToBinding(NeraHotkeyAction.ToggleDldr, false));
        Assert(invalid.Registered && invalid.ActualChord == newChord, "Invalid input must not erase actual binding evidence.");

        var saveFailureChord = new NeraHotkeyChord(ctrlAlt, 'L');
        store.FailSave = true;
        var notSaved = await service.RegisterOrUpdateAsync(saveFailureChord.ToBinding(NeraHotkeyAction.ToggleDldr, false));
        store.FailSave = false;
        Assert(notSaved.Code == NeraControlCodes.HotkeyPersistenceFailed && notSaved.ActualChord == newChord &&
               platform.ActiveIds.Contains(activeId) && platform.ActiveIds.Count == 4,
            "Save failure must unregister only the staged key, retaining the old key and disk value.");
        platform.FailUnregister(activeId, 5);
        var notRemoved = await service.RegisterOrUpdateAsync(saveFailureChord.ToBinding(NeraHotkeyAction.ToggleDldr, false));
        platform.ClearUnregisterFailure(activeId);
        Assert(notRemoved.Code == NeraControlCodes.HotkeyRegistrationFailed && notRemoved.ActualChord == newChord &&
               store.Saved[NeraHotkeyAction.ToggleDldr] == newChord && platform.ActiveIds.Count == 4,
            "Old-ID cleanup failure must roll back saved preference and quarantine/remove staged registration.");
        store.FailSave = true;
        var rejectedClear = await service.RegisterOrUpdateAsync(new NeraHotkeyBinding
        { Action = NeraHotkeyAction.ToggleDldr, Modifiers = 0, VirtualKey = 0 });
        store.FailSave = false;
        Assert(rejectedClear.Code == NeraControlCodes.HotkeyPersistenceFailed && platform.ActiveIds.Contains(activeId),
            "A failed clear persistence cannot remove the live key.");
        var cleared = await service.RegisterOrUpdateAsync(new NeraHotkeyBinding
        { Action = NeraHotkeyAction.ToggleDldr, Modifiers = 0, VirtualKey = 0 });
        Assert(cleared.Code == NeraControlCodes.Ok && !cleared.Registered &&
               !platform.ActiveIds.Contains(activeId) && store.Saved[NeraHotkeyAction.ToggleDldr] is null,
            "Per-action clear must be saved and actually unregistered.");
        int saveCount = store.SaveCount;
        var deniedFixedClear = await service.RegisterOrUpdateAsync(new NeraHotkeyBinding
        { Action = NeraHotkeyAction.EmergencyStopFallback, Modifiers = 0, VirtualKey = 0 });
        Assert(deniedFixedClear.Code == NeraControlCodes.HotkeyFixedEmergency && service.FixedEmergencyRegistered &&
               store.SaveCount == saveCount, "The fixed escape shortcut cannot be removed or persisted as a user override.");
        var customEmergency = new NeraHotkeyChord(ctrlAlt, 'E');
        await service.RegisterOrUpdateAsync(customEmergency.ToBinding(NeraHotkeyAction.EmergencyStop, false));
        await service.RegisterOrUpdateAsync(new NeraHotkeyBinding
        { Action = NeraHotkeyAction.EmergencyStop, Modifiers = 0, VirtualKey = 0 });
        Assert(service.FixedEmergencyRegistered, "Clearing custom EmergencyStop must leave fixed fallback registered.");

        await using var restartedPlatform = new FakeGlobalHotkeyPlatform();
        await using var restarted = new ManagedHotkeyRegistrationService(restartedPlatform, ownsPlatform: false, preferencesStore: store);
        var restartedSet = await restarted.InitializeAsync();
        Assert(!restartedSet.Bindings.Single(value => value.Action == NeraHotkeyAction.ToggleDldr).Registered &&
               restarted.FixedEmergencyRegistered, "Restart must honor a cleared action without clearing fixed escape.");
        await restarted.RegisterRecommendedAsync();
        Assert(restarted.Current.AllRegisterHotKeyActionsRegistered && restarted.Current.RegisteredCount == 4,
            "Restore recommendations retains fixed escape and restores three normal keys, leaving custom escape optional.");

        // The store test is intentionally limited to one newly-created scratch directory and exact files.
        string scratch = Path.Combine(Path.GetTempPath(), "NeraPhase13HotkeyStore-" + Guid.NewGuid().ToString("N"));
        var jsonStore = new JsonHotkeyPreferenceStore(scratch);
        try
        {
            jsonStore.Save(new Dictionary<NeraHotkeyAction, NeraHotkeyChord?>
            { [NeraHotkeyAction.ToggleDldr] = newChord, [NeraHotkeyAction.ToggleHud] = null });
            var loaded = jsonStore.Load();
            Assert(loaded[NeraHotkeyAction.ToggleDldr] == newChord && loaded[NeraHotkeyAction.ToggleHud] is null,
                "Atomic local JSON must round-trip actual accepted bindings and explicit clear.");
            AssertEqual(1, Directory.GetFiles(scratch).Length);
            string exactFile = Path.Combine(scratch, "hotkeys.json");
            byte[] malformed = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"bindings\":[{\"action\":false,\"modifiers\":0,\"virtualKey\":0}]}");
            File.WriteAllBytes(exactFile, malformed);
            await using var invalidPlatform = new FakeGlobalHotkeyPlatform();
            await using var invalidService = new ManagedHotkeyRegistrationService(invalidPlatform,
                ownsPlatform: false, preferencesStore: jsonStore);
            await invalidService.InitializeAsync();
            Assert(invalidService.FixedEmergencyRegistered && invalidService.InitializationErrorCode == NeraControlCodes.HotkeyPersistenceFailed &&
                   File.ReadAllBytes(exactFile).SequenceEqual(malformed),
                "Malformed preference input must not be overwritten, and fixed emergency is still attempted.");
        }
        finally
        {
            string exactFile = Path.Combine(scratch, "hotkeys.json");
            if (File.Exists(exactFile)) File.Delete(exactFile);
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: false);
        }
    }

    internal static async Task RegisteredChordsReachRecorderWithoutCommands()
    {
        await using var platform = new FakeGlobalHotkeyPlatform();
        var store = new FakeHotkeyPreferenceStore();
        await using var registration = new ManagedHotkeyRegistrationService(
            platform, ownsPlatform: false, preferencesStore: store);
        await registration.InitializeAsync();
        var client = new RecordingControlClient(ReadyState());
        await using var router = new HotkeyCommandRouter(client);
        registration.Activated += (_, activation) => router.TryPost(activation.Action, activation.Origin);
        var candidates = new List<NeraHotkeyRecordingCandidateEventArgs>();
        router.RecordingCandidate += (_, candidate) => candidates.Add(candidate);
        router.SetRecording(true);

        var originalDldr = registration.Current.Registrations.Single(
            value => value.Action == NeraHotkeyAction.ToggleDldr).ActualChord!.Value;
        var hudChord = registration.Current.Registrations.Single(
            value => value.Action == NeraHotkeyAction.ToggleHud).ActualChord!.Value;
        int originalDldrId = platform.IdFor(originalDldr);
        int hudId = platform.IdFor(hudChord);
        int originalSaves = store.SaveCount;
        int registrationsBefore = platform.RegisterAttempts.Count;
        int removalsBefore = platform.UnregisterAttempts.Count;
        platform.Activate(hudId);
        platform.Activate(hudId);
        platform.Activate(originalDldrId);
        Assert(candidates.Count == 3 && candidates.Take(2).All(value =>
                   value.Action == NeraHotkeyAction.ToggleHud && value.Chord == hudChord &&
                   value.Origin is { Message: 0x0312, RegistrationId: > 0, InputKind: NeraInputKind.SyntheticTest }) &&
               candidates[2].Chord == originalDldr && client.Commands.IsEmpty,
            "Repeated registered chords must reach the recorder without executing HUD or DLDR.");
        Assert(NeraHotkeyPolicy.ValidateRecordingCandidate(NeraHotkeyAction.ToggleDldr,
                   hudChord, registration.Current.Bindings) == NeraControlCodes.HotkeyInternalDuplicate &&
               NeraHotkeyPolicy.ValidateRecordingCandidate(NeraHotkeyAction.ToggleHud,
                   hudChord, registration.Current.Bindings) == NeraControlCodes.Ok,
            "Recorder previews internal duplicates, but permits the current action's own binding.");
        Assert(NeraHotkeyPolicy.ValidateRecordingCandidate(NeraHotkeyAction.ToggleDldr,
                   NeraHotkeyPolicy.FixedEmergencyChord, registration.Current.Bindings) ==
               NeraControlCodes.HotkeyFixedEmergency,
            "The fixed emergency chord remains unavailable as a rebind candidate.");

        var saveConflict = await registration.RegisterOrUpdateAsync(
            hudChord.ToBinding(NeraHotkeyAction.ToggleDldr, false));
        Assert(saveConflict.Code == NeraControlCodes.HotkeyInternalDuplicate &&
               saveConflict.Registered && saveConflict.ActualChord == originalDldr &&
               platform.ActiveIds.Contains(originalDldrId) && platform.ActiveIds.Contains(hudId) &&
               store.SaveCount == originalSaves && platform.RegisterAttempts.Count == registrationsBefore &&
               platform.UnregisterAttempts.Count == removalsBefore && client.Commands.IsEmpty,
            "Even if Save bypasses preview, an internal collision must preserve both live bindings and preferences.");
        router.TryPost(NeraHotkeyAction.ToggleHud, new NeraInputOrigin
        { InputKind = NeraInputKind.Mouse, Message = 0x020A, Vk = 'P', Modifiers = 3, RegistrationId = hudId });
        router.TryPost(NeraHotkeyAction.ToggleHud);
        Assert(candidates.Count == 3 && client.Commands.IsEmpty,
            "Mouse or originless actions cannot manufacture registered recorder candidates.");

        platform.Activate(ManagedHotkeyRegistrationService.RegistrationIdFor(NeraHotkeyAction.EmergencyStopFallback));
        await client.EmergencyObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(candidates.Count == 3 && client.Commands.Count == 1 &&
               client.Commands.Single().Kind == NeraCommandKind.EmergencyStop,
            "Fixed emergency bypasses recording immediately, never becoming a harmless candidate.");
        var customEmergency = new NeraHotkeyChord(NeraHotkeyModifiers.Control | NeraHotkeyModifiers.Alt, 'E');
        await registration.RegisterOrUpdateAsync(customEmergency.ToBinding(NeraHotkeyAction.EmergencyStop, false));
        platform.Activate(platform.IdFor(customEmergency));
        await WaitUntilAsync(() => client.Commands.Count == 2, TimeSpan.FromSeconds(2));
        Assert(candidates.Count == 3 && client.Commands.All(value => value.Kind == NeraCommandKind.EmergencyStop),
            "Custom emergency also retains its safety action during recording.");
        router.SetRecording(false);
        platform.Activate(hudId);
        await WaitUntilAsync(() => client.Commands.Count == 3, TimeSpan.FromSeconds(2));
        Assert(client.Commands.Last().Kind == NeraCommandKind.ShowHud && candidates.Count == 3,
            "Closing the recorder restores normal registered action routing.");
    }

    private static NeraStateSnapshot ReadyState() => NeraStateSnapshot.CreateInitial() with
    {
        TargetDisplay = FakeControlBackend.Display(selected: true),
        AvailableDisplays = [FakeControlBackend.Display(selected: true)],
        InputWidth = 3840,
        InputHeight = 2160,
        OutputWidth = 3840,
        OutputHeight = 2160,
        NeuralInputWidth = 3840,
        NeuralInputHeight = 2160,
        HdrSystemEnabled = true,
        HdrPipelineEnabled = true,
        AppVisible = true
    };

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!predicate())
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected={expected}; Actual={actual}.");
        }
    }

    private static void AssertSequence<T>(IEnumerable<T> actual, params T[] expected)
    {
        var values = actual.ToArray();
        if (!values.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"Expected=[{string.Join(',', expected)}]; Actual=[{string.Join(',', values)}].");
        }
    }

    private sealed class FakeGlobalHotkeyPlatform : IGlobalHotkeyPlatform
    {
        private readonly Dictionary<NeraHotkeyChord, int> registrationFailures_ = [];
        private readonly Dictionary<int, int> unregisterFailures_ = [];
        private readonly Dictionary<int, NeraHotkeyChord> active_ = [];

        public event Action<NeraHotkeyPlatformActivation>? Activated;
        public List<(int Id, uint Modifiers, uint VirtualKey)> RegisterAttempts { get; } = [];
        public List<int> UnregisterAttempts { get; } = [];
        public IReadOnlyCollection<int> ActiveIds => active_.Keys;
        public int IdFor(NeraHotkeyChord chord) => active_.Single(pair => pair.Value == chord).Key;

        public void FailRegistration(NeraHotkeyChord chord, int error) =>
            registrationFailures_[new NeraHotkeyChord(chord.PersistedModifiers, chord.VirtualKey)] = error;

        public void FailUnregister(int id, int error) => unregisterFailures_[id] = error;
        public void ClearUnregisterFailure(int id) => unregisterFailures_.Remove(id);

        public NeraHotkeyPlatformResult TryRegister(int id, uint modifiers, uint virtualKey)
        {
            RegisterAttempts.Add((id, modifiers, virtualKey));
            var chord = new NeraHotkeyChord(NeraHotkeyModifiers.Persisted(modifiers), virtualKey);
            if (registrationFailures_.TryGetValue(chord, out var error))
            {
                return NeraHotkeyPlatformResult.Failure(error);
            }
            if (active_.Any(pair => pair.Key != id && pair.Value == chord))
            {
                return NeraHotkeyPlatformResult.Failure(1409);
            }
            active_[id] = chord;
            return NeraHotkeyPlatformResult.Success;
        }

        public NeraHotkeyPlatformResult TryUnregister(int id)
        {
            UnregisterAttempts.Add(id);
            if (unregisterFailures_.TryGetValue(id, out var error))
            {
                return NeraHotkeyPlatformResult.Failure(error);
            }
            return active_.Remove(id)
                ? NeraHotkeyPlatformResult.Success
                : NeraHotkeyPlatformResult.Failure(1419);
        }

        public void Activate(int id)
        {
            if (active_.TryGetValue(id, out NeraHotkeyChord chord))
            {
                Activated?.Invoke(new(id, new NeraInputOrigin
                {
                    InputKind = NeraInputKind.SyntheticTest, Message = 0x0312,
                    RegistrationId = id, Vk = chord.VirtualKey, Modifiers = chord.PersistedModifiers,
                    SourcePid = Environment.ProcessId, SourceThreadId = 123
                }));
            }
        }

        public ValueTask DisposeAsync()
        {
            active_.Clear();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHotkeyPreferenceStore : INeraHotkeyPreferenceStore
    {
        public Dictionary<NeraHotkeyAction, NeraHotkeyChord?> Saved { get; private set; } = [];
        public int SaveCount { get; private set; }
        public bool FailSave { get; set; }
        public Action<IReadOnlyDictionary<NeraHotkeyAction, NeraHotkeyChord?>>? BeforeSave { get; set; }
        public IReadOnlyDictionary<NeraHotkeyAction, NeraHotkeyChord?> Load() => new Dictionary<NeraHotkeyAction, NeraHotkeyChord?>(Saved);
        public void Save(IReadOnlyDictionary<NeraHotkeyAction, NeraHotkeyChord?> preferences)
        {
            BeforeSave?.Invoke(preferences);
            if (FailSave) throw new IOException("Injected preference-save failure.");
            Saved = new(preferences);
            SaveCount++;
        }
    }

    private sealed class RecordingControlClient(NeraStateSnapshot initial) : INeraControlClient
    {
        private readonly object gate_ = new();
        private NeraStateSnapshot state_ = initial;

        public event EventHandler<NeraStateSnapshot>? StateChanged;
        public ConcurrentQueue<NeraCommandEnvelope> Commands { get; } = new();
        public NeraCommandKind? BlockingKind { get; init; }
        public TaskCompletionSource BlockingStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBlocking { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EmergencyObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<NeraStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate_)
            {
                return ValueTask.FromResult(state_);
            }
        }

        public async Task<NeraCommandResult> ExecuteAsync(
            NeraCommandEnvelope command,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command);
            if (command.Kind == NeraCommandKind.EmergencyStop)
            {
                EmergencyObserved.TrySetResult();
            }
            if (command.Kind == BlockingKind)
            {
                BlockingStarted.TrySetResult();
                await ReleaseBlocking.Task.WaitAsync(cancellationToken);
            }

            NeraStateSnapshot previous;
            NeraStateSnapshot current;
            lock (gate_)
            {
                previous = state_;
                current = Apply(previous, command) with
                {
                    Revision = previous.Revision + 1,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                state_ = current;
            }
            StateChanged?.Invoke(this, current);
            return new NeraCommandResult
            {
                Ok = true,
                RequestId = command.RequestId,
                PreviousRevision = previous.Revision,
                CurrentRevision = current.Revision,
                State = current,
                Code = NeraControlCodes.Ok,
                UserMessage = "OK"
            };
        }

        public Task<IReadOnlyList<NeraDisplaySummary>> ListDisplaysAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(state_.AvailableDisplays);

        private static NeraStateSnapshot Apply(NeraStateSnapshot state, NeraCommandEnvelope command) =>
            command.Kind switch
            {
                NeraCommandKind.ShowHud => state with { HudVisible = true },
                NeraCommandKind.HideHud => state with { HudVisible = false },
                NeraCommandKind.ShowApp => state with { AppVisible = true },
                NeraCommandKind.HideApp => state with { AppVisible = false },
                NeraCommandKind.EnableDldr => state with { DldrRequested = true },
                NeraCommandKind.DisableDldr => state with { DldrRequested = false },
                NeraCommandKind.EmergencyStop => state with
                {
                    DldrRequested = false,
                    HudVisible = false,
                    AppVisible = true
                },
                _ => state
            };
    }
}
