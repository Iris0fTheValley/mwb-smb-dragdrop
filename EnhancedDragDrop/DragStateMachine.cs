namespace MouseWithoutBorders.EnhancedDragDrop;

public enum RemoteDragState { Idle, DragBegin, EnterRemote, HoverTarget, Dropped, Cancelled }

public sealed class RemoteDragStateMachine
{
    public RemoteDragState State { get; private set; } = RemoteDragState.Idle;
    public Guid? DragId { get; private set; }
    public ExplorerTarget? HoveredTarget { get; private set; }

    public void Begin(DragManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        if (State != RemoteDragState.Idle)
            throw new InvalidOperationException("A remote drag is already active.");
        DragId = manifest.DragId;
        State = RemoteDragState.DragBegin;
    }

    public void EnterRemote() => Transition(RemoteDragState.DragBegin, RemoteDragState.EnterRemote);

    public void Hover(ExplorerTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (State is not RemoteDragState.EnterRemote and not RemoteDragState.HoverTarget)
            throw new InvalidOperationException("Cannot hover a target before entering the remote machine.");
        HoveredTarget = target;
        State = RemoteDragState.HoverTarget;
    }

    public ExplorerTarget Drop()
    {
        if (State != RemoteDragState.HoverTarget || HoveredTarget is null)
            throw new InvalidOperationException("A remote drag must hover a target before drop.");
        var result = HoveredTarget;
        State = RemoteDragState.Dropped;
        return result;
    }

    public void Cancel()
    {
        if (State == RemoteDragState.Idle)
            return;
        State = RemoteDragState.Cancelled;
        DragId = null;
        HoveredTarget = null;
    }

    public void Reset()
    {
        State = RemoteDragState.Idle;
        DragId = null;
        HoveredTarget = null;
    }

    private void Transition(RemoteDragState expected, RemoteDragState next)
    {
        if (State != expected)
            throw new InvalidOperationException($"Invalid remote drag transition {State} -> {next}.");
        State = next;
    }
}
