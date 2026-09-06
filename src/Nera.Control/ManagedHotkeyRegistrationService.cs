using System.Collections.Concurrent;
using ChipsStudio.Nera.Localization;

namespace ChipsStudio.Nera.Control;

/// <summary>
/// Owns OS registration evidence. Rebinding stages a new ID, persists the accepted chord,
/// activates it, and only then removes the previous ID. Failed stages preserve the old key.
/// </summary>
public sealed class ManagedHotkeyRegistrationService : IAsyncDisposable
{
    private const int RegistrationIdBase = 0x4E10;
    private const int AlternateIdBase = 0x4E30;
    private readonly IGlobalHotkeyPlatform platform_;
    private readonly INeraHotkeyPreferenceStore? preferencesStore_;
    private readonly bool ownsPlatform_;
    private readonly SemaphoreSlim mutationGate_ = new(1, 1);
    private readonly object evidenceGate_ = new();
    private readonly Dictionary<NeraHotkeyAction, NeraHotkeyRegistrationEvidence> evidence_ = [];
    private readonly Dictionary<NeraHotkeyAction, int> currentIds_ = [];
    private readonly Dictionary<int, NeraHotkeyChord> ownedIds_ = [];
    private readonly ConcurrentDictionary<int, NeraHotkeyAction> activeIds_ = new();
    private Dictionary<NeraHotkeyAction, NeraHotkeyChord?> preferences_ = [];
    private bool initialized_;
    private int disposed_;

    public ManagedHotkeyRegistrationService(IGlobalHotkeyPlatform platform, bool ownsPlatform = true,
        INeraHotkeyPreferenceStore? preferencesStore = null)
    {
        platform_ = platform ?? throw new ArgumentNullException(nameof(platform));
        ownsPlatform_ = ownsPlatform;
        preferencesStore_ = preferencesStore;
        platform_.Activated += OnPlatformActivated;
    }

    public event EventHandler<NeraHotkeyActivatedEventArgs>? Activated;
    public event EventHandler<NeraHotkeyRegistrationSet>? RegistrationsChanged;
    public string? InitializationErrorCode { get; private set; }
    public bool FixedEmergencyRegistered => Current.Registrations.Any(value =>
        value.Action == NeraHotkeyAction.EmergencyStopFallback && value.Registered &&
        value.ActualChord == NeraHotkeyPolicy.FixedEmergencyChord);
    public NeraHotkeyRegistrationSet Current
    {
        get { lock (evidenceGate_) return SnapshotLocked(); }
    }
    public static int RegistrationIdFor(NeraHotkeyAction action) => RegistrationIdBase + (int)action;

    public async Task<NeraHotkeyRegistrationSet> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await mutationGate_.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized_) return PublishAndSnapshot();
            initialized_ = true;
            EnsureFixedEmergencyCore();
            try
            {
                preferences_ = new(preferencesStore_?.Load() ?? new Dictionary<NeraHotkeyAction, NeraHotkeyChord?>());
            }
            catch (Exception error) when (IsPreferenceError(error))
            {
                InitializationErrorCode = NeraControlCodes.HotkeyPersistenceFailed;
                foreach (var action in NeraHotkeyPolicy.EditableActions)
                    SetEvidence(Failure(action, null, NeraControlCodes.HotkeyPersistenceFailed, "Hotkey.PreferencesInvalid"));
                return PublishAndSnapshot(); // Never overwrite unreadable settings during startup.
            }
            foreach (var action in NeraHotkeyPolicy.EditableActions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool saved = preferences_.TryGetValue(action, out NeraHotkeyChord? chord);
                if (action == NeraHotkeyAction.EmergencyStop && (!saved || chord == NeraHotkeyPolicy.FixedEmergencyChord))
                    chord = null;
                else if (!saved)
                    chord = NeraHotkeyCandidates.For(action).FirstOrDefault();
                _ = ChangeCore(action, chord, useRecommendedFallbacks: true, persist: false);
            }
            return PublishAndSnapshot();
        }
        finally { mutationGate_.Release(); }
    }

    public async Task<NeraHotkeyRegistrationSet> RegisterRecommendedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await mutationGate_.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureFixedEmergencyCore();
            InitializationErrorCode = null;
            foreach (var action in NeraHotkeyPolicy.EditableActions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NeraHotkeyChord? chord = action == NeraHotkeyAction.EmergencyStop
                    ? null : NeraHotkeyCandidates.For(action).First();
                _ = ChangeCore(action, chord, useRecommendedFallbacks: true, persist: true);
            }
            return PublishAndSnapshot();
        }
        finally { mutationGate_.Release(); }
    }

    public async Task<NeraHotkeyRegistrationEvidence> RegisterOrUpdateAsync(NeraHotkeyBinding requested,
        bool useRecommendedFallbacks = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ThrowIfDisposed();
        await mutationGate_.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Enum.IsDefined(requested.Action)) throw new ArgumentOutOfRangeException(nameof(requested));
            NeraHotkeyChord? chord = NeraHotkeyPolicy.IsClear(requested)
                ? null : new(requested.Modifiers, requested.VirtualKey);
            NeraHotkeyRegistrationEvidence result;
            if (requested.Action == NeraHotkeyAction.HoldOriginal)
                result = Failure(requested.Action, chord, NeraControlCodes.HotkeyHoldUnsupported, "Hotkey.HoldUnsupported");
            else if (requested.Action == NeraHotkeyAction.EmergencyStopFallback)
                result = PreservePrevious(requested.Action, chord, NeraControlCodes.HotkeyFixedEmergency, "Hotkey.FixedEmergency");
            else
                result = ChangeCore(requested.Action, chord, useRecommendedFallbacks, persist: true);
            PublishAndSnapshot();
            return result;
        }
        finally { mutationGate_.Release(); }
    }

    public async Task<NeraHotkeyRegistrationSet> UnregisterAllAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await mutationGate_.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { UnregisterAllCore(); return PublishAndSnapshot(); }
        finally { mutationGate_.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed_, 1) != 0) return;
        platform_.Activated -= OnPlatformActivated;
        await mutationGate_.WaitAsync().ConfigureAwait(false);
        try { UnregisterAllCore(); }
        finally { mutationGate_.Release(); mutationGate_.Dispose(); }
        if (ownsPlatform_) await platform_.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureFixedEmergencyCore()
    {
        var action = NeraHotkeyAction.EmergencyStopFallback;
        if (currentIds_.ContainsKey(action)) return;
        int id = RegistrationIdFor(action);
        var chord = NeraHotkeyPolicy.FixedEmergencyChord;
        NeraHotkeyPlatformResult registered = platform_.TryRegister(id, chord.RegistrationModifiers, chord.VirtualKey);
        if (!registered.Succeeded)
        {
            SetEvidence(Failure(action, chord, NeraControlCodes.HotkeyRegistrationFailed,
                "Hotkey.FixedEmergencyUnavailable", registered.Win32Error));
            return;
        }
        ownedIds_[id] = chord;
        currentIds_[action] = id;
        activeIds_[id] = action;
        SetEvidence(Registered(action, chord, chord, 0));
    }

    private NeraHotkeyRegistrationEvidence ChangeCore(NeraHotkeyAction action, NeraHotkeyChord? requested,
        bool useRecommendedFallbacks, bool persist)
    {
        NeraHotkeyRegistrationEvidence? previous = Previous(action);
        int? oldId = currentIds_.TryGetValue(action, out int currentId) ? currentId : null;
        if (requested is { } value)
        {
            string code = NeraHotkeyPolicy.Validate(value);
            if (code != NeraControlCodes.Ok)
                return PreservePrevious(action, requested, code, ValidationResourceKey(code));
            requested = new NeraHotkeyChord(value.PersistedModifiers, value.VirtualKey);
            if (requested == NeraHotkeyPolicy.FixedEmergencyChord)
                return PreservePrevious(action, requested, NeraControlCodes.HotkeyFixedEmergency, "Hotkey.FixedEmergency");
        }
        if (requested == previous?.ActualChord && previous?.Registered == true)
        {
            if (!TryPersist(action, requested, persist))
                return PreservePrevious(action, requested, NeraControlCodes.HotkeyPersistenceFailed, "Hotkey.SaveFailed");
            var same = Registered(action, requested!.Value, requested.Value, 0);
            SetEvidence(same);
            return same;
        }
        if (requested is null)
        {
            var oldPreferences = new Dictionary<NeraHotkeyAction, NeraHotkeyChord?>(preferences_);
            if (!TryPersist(action, null, persist))
                return PreservePrevious(action, null, NeraControlCodes.HotkeyPersistenceFailed, "Hotkey.SaveFailed");
            if (oldId is int id && !TryRemoveOwned(id))
            {
                RestorePreferences(oldPreferences, persist);
                return PreservePrevious(action, null, NeraControlCodes.HotkeyRegistrationFailed, "Hotkey.CleanupFailed");
            }
            currentIds_.Remove(action);
            var cleared = Unregistered(action, "Hotkey.Cleared");
            SetEvidence(cleared);
            return cleared;
        }
        var candidates = new List<NeraHotkeyChord> { requested.Value };
        if (useRecommendedFallbacks)
            candidates.AddRange(NeraHotkeyCandidates.For(action).Where(value => !candidates.Contains(value)));
        int stagingId = oldId == RegistrationIdFor(action) ? AlternateIdBase + (int)action : RegistrationIdFor(action);
        if (ownedIds_.ContainsKey(stagingId))
            return PreservePrevious(action, requested, NeraControlCodes.HotkeyRegistrationFailed, "Hotkey.CleanupFailed");
        int lastError = 1409;
        string lastCode = NeraControlCodes.HotkeyConflict;
        string lastMessage = "Hotkey.Conflict";
        for (int index = 0; index < candidates.Count; index++)
        {
            NeraHotkeyChord candidate = candidates[index];
            if (!candidate.IsValid) continue;
            if (ownedIds_.Any(pair => pair.Key != oldId && pair.Value == candidate) ||
                candidate == NeraHotkeyPolicy.FixedEmergencyChord)
            {
                lastCode = NeraControlCodes.HotkeyInternalDuplicate;
                lastMessage = "Hotkey.InternalDuplicate";
                continue;
            }
            // Staged IDs cannot dispatch commands before persistence accepts the new chord.
            NeraHotkeyPlatformResult staged = platform_.TryRegister(stagingId,
                candidate.RegistrationModifiers, candidate.VirtualKey);
            if (!staged.Succeeded)
            {
                lastError = staged.Win32Error;
                lastCode = lastError == 1409 ? NeraControlCodes.HotkeyConflict : NeraControlCodes.HotkeyRegistrationFailed;
                lastMessage = lastError == 1409 ? "Hotkey.Conflict" : "Hotkey.RegistrationFailed";
                continue;
            }
            ownedIds_[stagingId] = candidate;
            var oldPreferences = new Dictionary<NeraHotkeyAction, NeraHotkeyChord?>(preferences_);
            if (!TryPersist(action, candidate, persist))
            {
                bool cleanup = TryRemoveOwned(stagingId);
                return PreservePrevious(action, requested, cleanup ? NeraControlCodes.HotkeyPersistenceFailed :
                    NeraControlCodes.HotkeyRegistrationFailed, cleanup ? "Hotkey.SaveFailed" : "Hotkey.CleanupFailed");
            }
            activeIds_[stagingId] = action;
            if (oldId is int old && !TryRemoveOwned(old))
            {
                activeIds_.TryRemove(stagingId, out _);
                _ = TryRemoveOwned(stagingId);
                RestorePreferences(oldPreferences, persist);
                return PreservePrevious(action, requested, NeraControlCodes.HotkeyRegistrationFailed, "Hotkey.CleanupFailed");
            }
            currentIds_[action] = stagingId;
            var accepted = Registered(action, requested.Value, candidate, index);
            SetEvidence(accepted);
            return accepted;
        }
        return PreservePrevious(action, requested, lastCode, lastMessage, lastError);
    }

    private bool TryPersist(NeraHotkeyAction action, NeraHotkeyChord? chord, bool persist)
    {
        var next = new Dictionary<NeraHotkeyAction, NeraHotkeyChord?>(preferences_) { [action] = chord };
        try
        {
            if (persist) preferencesStore_?.Save(next);
            preferences_ = next;
            return true;
        }
        catch (Exception error) when (IsPreferenceError(error)) { return false; }
    }
    private void RestorePreferences(Dictionary<NeraHotkeyAction, NeraHotkeyChord?> previous, bool persist)
    {
        try { if (persist) preferencesStore_?.Save(previous); }
        catch (Exception error) when (IsPreferenceError(error))
        { InitializationErrorCode = NeraControlCodes.HotkeyPersistenceFailed; }
        preferences_ = previous;
    }
    private bool TryRemoveOwned(int id)
    {
        var removed = platform_.TryUnregister(id);
        if (!removed.Succeeded) return false;
        activeIds_.TryRemove(id, out _);
        ownedIds_.Remove(id);
        return true;
    }
    private void UnregisterAllCore()
    {
        foreach (int id in ownedIds_.Keys.ToArray()) _ = TryRemoveOwned(id);
        foreach (var pair in currentIds_.ToArray())
        {
            if (!ownedIds_.ContainsKey(pair.Value))
            {
                currentIds_.Remove(pair.Key);
                SetEvidence(Unregistered(pair.Key, "Hotkey.NotRegistered"));
            }
            else
                _ = PreservePrevious(pair.Key, Previous(pair.Key)?.RequestedChord,
                    NeraControlCodes.HotkeyRegistrationFailed, "Hotkey.CleanupFailed");
        }
    }
    private NeraHotkeyRegistrationEvidence PreservePrevious(NeraHotkeyAction action,
        NeraHotkeyChord? requested, string code, string resourceKey, int error = 0)
    {
        var previous = Previous(action);
        var result = previous?.Registered == true
            ? previous with { RequestedChord = requested, Status = NeraHotkeyRegistrationStatus.PreviousBindingRestored,
                Code = code, UserMessage = NeraLocalizer.Get(resourceKey), Win32Error = error }
            : Failure(action, requested, code, resourceKey, error);
        if (NeraHotkeyPolicy.ProductActions.Contains(action)) SetEvidence(result);
        return result;
    }
    private static NeraHotkeyRegistrationEvidence Failure(NeraHotkeyAction action, NeraHotkeyChord? chord,
        string code, string resourceKey, int error = 0) => new()
    {
        Action = action, RequestedChord = chord,
        Status = code == NeraControlCodes.HotkeyHoldUnsupported ? NeraHotkeyRegistrationStatus.HoldSemanticsUnsupported :
            code is NeraControlCodes.HotkeyConflict or NeraControlCodes.HotkeyInternalDuplicate
                ? NeraHotkeyRegistrationStatus.Conflict : NeraHotkeyRegistrationStatus.Unavailable,
        Code = code, UserMessage = NeraLocalizer.Get(resourceKey), Win32Error = error
    };
    private static NeraHotkeyRegistrationEvidence Registered(NeraHotkeyAction action, NeraHotkeyChord requested,
        NeraHotkeyChord actual, int candidateIndex) => new()
    {
        Action = action, RequestedChord = requested, ActualChord = actual,
        Status = NeraHotkeyRegistrationStatus.Registered, Code = NeraControlCodes.Ok,
        CandidateIndex = candidateIndex, UsedFallbackCandidate = candidateIndex > 0, NoRepeatApplied = true,
        UserMessage = NeraLocalizer.Get(candidateIndex > 0 ? "Hotkey.FallbackSelected" : "Hotkey.Registered")
    };
    private static NeraHotkeyRegistrationEvidence Unregistered(NeraHotkeyAction action, string resourceKey) => new()
    {
        Action = action, Status = NeraHotkeyRegistrationStatus.Unregistered,
        Code = NeraControlCodes.Ok, UserMessage = NeraLocalizer.Get(resourceKey)
    };
    public static string ValidationResourceKey(string code) => code switch
    {
        NeraControlCodes.HotkeyKeyboardOnly => "Hotkey.KeyboardOnly",
        NeraControlCodes.HotkeyModifierRequired => "Hotkey.ModifierRequired",
        NeraControlCodes.HotkeyReserved => "Hotkey.Reserved",
        NeraControlCodes.HotkeyFixedEmergency => "Hotkey.FixedEmergency",
        NeraControlCodes.HotkeyInternalDuplicate => "Hotkey.InternalDuplicate",
        _ => "Hotkey.Invalid"
    };
    private NeraHotkeyRegistrationEvidence? Previous(NeraHotkeyAction action)
    { lock (evidenceGate_) return evidence_.GetValueOrDefault(action); }
    private void SetEvidence(NeraHotkeyRegistrationEvidence evidence)
    { lock (evidenceGate_) evidence_[evidence.Action] = evidence; }
    private NeraHotkeyRegistrationSet SnapshotLocked() => new()
    {
        Registrations = NeraHotkeyPolicy.ProductActions.Select(action => evidence_.GetValueOrDefault(action) ??
            Unregistered(action, "Hotkey.NotRegistered")).ToArray()
    };
    private NeraHotkeyRegistrationSet PublishAndSnapshot()
    {
        NeraHotkeyRegistrationSet snapshot = Current;
        RegistrationsChanged?.Invoke(this, snapshot);
        return snapshot;
    }
    private void OnPlatformActivated(NeraHotkeyPlatformActivation activation)
    {
        if (Volatile.Read(ref disposed_) == 0 && activeIds_.TryGetValue(activation.RegistrationId, out var action))
            Activated?.Invoke(this, new NeraHotkeyActivatedEventArgs(action, activation.Origin));
    }
    private static bool IsPreferenceError(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException;
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed_) != 0, this);
}
