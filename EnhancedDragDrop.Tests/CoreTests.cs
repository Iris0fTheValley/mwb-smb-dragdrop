using MouseWithoutBorders.EnhancedDragDrop;
using Xunit;

namespace EnhancedDragDrop.Tests;

public sealed class CoreTests
{
    [Fact]
    public void Manifest_round_trips_all_items_and_unicode()
    {
        var manifest = DragManifest.Create("IRIS0FTHEVALLEY", ["D:\\模型\\a.gguf", "E:\\资料\\dataset"]);
        var decoded = DragManifest.Parse(manifest.ToJson());
        Assert.Equal(manifest.SourceMachine, decoded.SourceMachine);
        Assert.Equal(manifest.Items.Select(item => item.LocalPath), decoded.Items.Select(item => item.LocalPath));
        Assert.Equal(manifest.Items.Select(item => item.IsDirectory), decoded.Items.Select(item => item.IsDirectory));
    }

    [Fact]
    public void Manifest_frame_rejects_truncation()
    {
        var frame = DragManifestFraming.Encode(DragManifest.Create("PC", ["C:\\a.txt"]));
        Assert.Throws<FormatException>(() => DragManifestFraming.Decode(frame[..^1]));
    }

    [Fact]
    public void Manifest_chunks_round_trip_many_items_and_reject_duplicate_index()
    {
        var paths = Enumerable.Range(0, 300).Select(index => $"D:\\长路径\\item-{index:D3}-资料.txt").ToArray();
        var manifest = DragManifest.Create("ID-BLUEBERRY", paths);
        var chunks = ManifestChunkProtocol.Split(manifest);
        Assert.True(chunks.Count > 1);
        Assert.Equal(manifest.DragId, ManifestChunkProtocol.Reassemble(chunks).DragId);
        Assert.Throws<FormatException>(() => ManifestChunkProtocol.Reassemble(chunks.Append(chunks[0])));
    }

    [Fact]
    public void Resolver_uses_source_drive_share_without_symmetric_drive_assumption()
    {
        var resolver = new SharePathResolver([
            new ShareRoot { LocalRoot = "D:\\", ShareName = "ID-BLUEBERRY_D" },
            new ShareRoot { LocalRoot = "E:\\", ShareName = "ID-BLUEBERRY_E" },
        ]);
        Assert.Equal(@"\\ID-BLUEBERRY\ID-BLUEBERRY_D\Models\a.gguf", resolver.Resolve("ID-BLUEBERRY", @"D:\Models\a.gguf"));
        Assert.Throws<IOException>(() => resolver.Resolve("ID-BLUEBERRY", @"C:\missing.txt"));
    }

    [Fact]
    public void State_machine_supports_drop_and_cancel_paths()
    {
        var manifest = DragManifest.Create("PC-A", [@"C:\a.txt"]);
        var target = new ExplorerTarget((nint)42, new(0, 0, 100, 100), @"D:\Target", 0, true);
        var state = new RemoteDragStateMachine();
        state.Begin(manifest);
        state.EnterRemote();
        state.Hover(target);
        Assert.Same(target, state.Drop());
        Assert.Equal(RemoteDragState.Dropped, state.State);

        state.Reset();
        state.Begin(manifest);
        state.EnterRemote();
        state.Cancel();
        Assert.Equal(RemoteDragState.Cancelled, state.State);
    }

    [Fact]
    public async Task Transfer_backend_copies_mixed_items_without_overwrite()
    {
        var root = Directory.CreateTempSubdirectory();
        var source = Directory.CreateDirectory(Path.Combine(root.FullName, "source"));
        var nested = Directory.CreateDirectory(Path.Combine(source.FullName, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source.FullName, "one.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "two.txt"), "two");
        var target = Directory.CreateDirectory(Path.Combine(root.FullName, "target"));
        var backend = new StreamingFileTransferBackend();
        var report = await backend.CopyAsync([Path.Combine(source.FullName, "one.txt"), source.FullName], target.FullName);
        Assert.Equal(2, report.Copied.Count);
        Assert.Empty(report.Failures);
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(target.FullName, "one.txt")));
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(target.FullName, "source", "nested", "two.txt")));
    }
}
