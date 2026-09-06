namespace ChipsStudio.Nera.Control;

/// <summary>
/// The single in-process authority for product state. Only NeraControlService may mutate it.
/// </summary>
public sealed class NeraControlStateMachine
{
    private readonly object gate_ = new();
    private NeraStateSnapshot current_;

    internal NeraControlStateMachine(NeraStateSnapshot initialState)
    {
        current_ = NeraStatePolicy.WithComputedDldrGate(initialState);
        NeraStatePolicy.Validate(current_);
    }

    internal event Action<NeraStateSnapshot>? Changed;

    public NeraStateSnapshot Current
    {
        get
        {
            lock (gate_)
            {
                return current_;
            }
        }
    }

    internal NeraStateSnapshot Commit(Func<NeraStateSnapshot, NeraStateSnapshot> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        NeraStateSnapshot next;
        lock (gate_)
        {
            var previous = current_;
            next = update(previous) with
            {
                Revision = checked(previous.Revision + 1),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            next = NeraStatePolicy.WithComputedDldrGate(next);
            if (!NeraStatePolicy.CanTransition(previous, next))
            {
                throw new NeraStateTransitionException(previous.DldrActualState, next.DldrActualState);
            }
            NeraStatePolicy.Validate(next);
            current_ = next;
        }

        Publish(next);
        return next;
    }

    private void Publish(NeraStateSnapshot snapshot)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }
        foreach (Action<NeraStateSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(snapshot);
            }
            catch
            {
                // An observer must never destabilize the authoritative state machine.
            }
        }
    }
}

public sealed class NeraStateTransitionException : InvalidOperationException
{
    public NeraDldrActualState PreviousState { get; }
    public NeraDldrActualState NextState { get; }

    public NeraStateTransitionException(NeraDldrActualState previous, NeraDldrActualState next)
        : base($"非法 DLDR 状态变化：{previous} → {next}。")
    { PreviousState = previous; NextState = next; }
}
