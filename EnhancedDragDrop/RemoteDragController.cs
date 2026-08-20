namespace MouseWithoutBorders.EnhancedDragDrop;

public interface IRemoteDragTransport
{
    Task SendAsync(DragControlMessage message, CancellationToken cancellationToken = default);
}

public sealed record DragControlMessage(DragControlKind Kind, DragManifest? Manifest, Guid DragId, string? TargetPath = null);
public enum DragControlKind { Begin, Cancel, Drop }

/// <summary>Coordinates MWB mouse-crossing events with target selection and SMB transfer.</summary>
public sealed class RemoteDragController
{
    private readonly IRemoteDragTransport transport;
    private readonly IRemotePathResolver pathResolver;
    private readonly IFileTransferBackend transfer;
    private readonly RemoteDragStateMachine state = new();
    private DragManifest? manifest;

    public RemoteDragController(IRemoteDragTransport transport, IRemotePathResolver pathResolver, IFileTransferBackend transfer)
    {
        this.transport = transport;
        this.pathResolver = pathResolver;
        this.transfer = transfer;
    }

    public async Task BeginAsync(DragManifest manifest, CancellationToken cancellationToken = default)
    {
        state.Begin(manifest);
        this.manifest = manifest;
        await transport.SendAsync(new DragControlMessage(DragControlKind.Begin, manifest, manifest.DragId), cancellationToken).ConfigureAwait(false);
    }

    public void EnterRemote() => state.EnterRemote();
    public void Hover(ExplorerTarget target) => state.Hover(target);

    public async Task<TransferReport> DropAsync(CancellationToken cancellationToken = default)
    {
        var target = state.Drop();
        var activeManifest = manifest ?? throw new InvalidOperationException("No active drag manifest.");
        var resolvedSources = activeManifest.Items
            .Select(item => pathResolver.Resolve(activeManifest.SourceMachine, item.LocalPath))
            .ToArray();
        await transport.SendAsync(new DragControlMessage(DragControlKind.Drop, null, activeManifest.DragId, target.FolderPath), cancellationToken).ConfigureAwait(false);
        return await transfer.CopyAsync(resolvedSources, target.FolderPath, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (state.DragId is Guid id)
            await transport.SendAsync(new DragControlMessage(DragControlKind.Cancel, null, id), cancellationToken).ConfigureAwait(false);
        state.Cancel();
        manifest = null;
    }
}
