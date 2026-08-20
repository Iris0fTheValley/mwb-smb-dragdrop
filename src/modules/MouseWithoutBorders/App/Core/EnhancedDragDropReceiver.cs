using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MouseWithoutBorders.Core;

/// <summary>
/// Reassembles manifest chunks and owns the target-machine Explorer overlay.
/// The transfer starts only after a visible filesystem target receives MouseUp.
/// </summary>
internal static class EnhancedDragDropReceiver
{
    private const int PayloadOffset = Package.PACKAGE_SIZE_EX - 48;
    private const int ChunkHeaderBytes = 28;
    private const int MaxChunks = 100_000;
    private const int MaxManifestBytes = 2 * 1024 * 1024;
    private static readonly ConcurrentDictionary<Guid, ChunkAssembly> Assemblies = new();
    private static readonly object SessionLock = new();
    private static OverlaySession? session;
    internal static bool IsActive => !Assemblies.IsEmpty || session is not null;

    internal static void ReceiveChunk(DATA package)
    {
        if (package.Des != Common.MachineID && package.Des != ID.ALL)
        {
            return;
        }

        var bytes = package.Bytes;
        if (bytes.Length < PayloadOffset + ChunkHeaderBytes)
        {
            Logger.Log("RemoteDrag chunk was shorter than its header.");
            return;
        }

        var payload = bytes.AsSpan(PayloadOffset);
        var dragId = new Guid(payload[..16]);
        var index = BitConverter.ToInt32(payload[16..20]);
        var total = BitConverter.ToInt32(payload[20..24]);
        var length = BitConverter.ToInt32(payload[24..28]);
        if (dragId == Guid.Empty || index < 0 || total <= 0 || total > MaxChunks || index >= total || length < 0 || length > 20 || PayloadOffset + ChunkHeaderBytes + length > bytes.Length)
        {
            Logger.Log("RemoteDrag chunk header was invalid.");
            return;
        }

        var assembly = Assemblies.GetOrAdd(dragId, _ => new ChunkAssembly(total));
        if (!assembly.Add(index, total, payload.Slice(ChunkHeaderBytes, length).ToArray()))
        {
            return;
        }
        if (assembly.TotalBytes > MaxManifestBytes || !assembly.IsComplete)
        {
            if (assembly.TotalBytes > MaxManifestBytes)
            {
                Assemblies.TryRemove(dragId, out _);
                Logger.Log("RemoteDrag manifest exceeded the size limit.");
            }
            return;
        }

        Assemblies.TryRemove(dragId, out _);
        try
        {
            var manifest = JsonSerializer.Deserialize<RemoteDragManifest>(Encoding.UTF8.GetString(assembly.Combine()), JsonOptions);
            if (manifest is null || manifest.Version != 1 || manifest.DragId != dragId || string.IsNullOrWhiteSpace(manifest.SourceMachine) || manifest.Items.Count == 0)
            {
                throw new FormatException("RemoteDrag manifest validation failed.");
            }
            Common.DoSomethingInUIThread(() => BeginOverlay(manifest));
            Logger.LogDebug($"RemoteDrag manifest received: DragId={dragId}, SourceMachine={manifest.SourceMachine}, ItemCount={manifest.Items.Count}");
        }
        catch (Exception ex)
        {
            Logger.Log("RemoteDrag manifest rejected: " + ex.Message);
        }
    }

    internal static void Cancel()
    {
        Common.DoSomethingInUIThread(CloseOverlay);
        Assemblies.Clear();
    }

    private static void BeginOverlay(RemoteDragManifest manifest)
    {
        lock (SessionLock)
        {
            CloseOverlay();
            session = new OverlaySession(manifest);
            session.Show();
        }
    }

    private static void CloseOverlay()
    {
        lock (SessionLock)
        {
            session?.Dispose();
            session = null;
        }
    }

    private sealed class ChunkAssembly
    {
        private readonly byte[][] chunks;
        private readonly object sync = new();
        private int received;
        internal int ExpectedTotal { get; }
        internal int TotalBytes { get; private set; }
        internal bool IsComplete { get { lock (sync) return received == chunks.Length; } }
        internal ChunkAssembly(int total) { ExpectedTotal = total; chunks = new byte[total][]; }
        internal bool Add(int index, int total, byte[] value)
        {
            lock (sync)
            {
                if (total != ExpectedTotal || chunks[index] is not null) return false;
                chunks[index] = value;
                received++;
                TotalBytes += value.Length;
                return true;
            }
        }
        internal byte[] Combine()
        {
            lock (sync)
            {
                var result = new byte[TotalBytes];
                var offset = 0;
                foreach (var chunk in chunks)
                {
                    chunk.CopyTo(result, offset);
                    offset += chunk.Length;
                }
                return result;
            }
        }
    }

    private sealed class OverlaySession : IDisposable
    {
        private readonly RemoteDragManifest manifest;
        private readonly List<OverlayForm> forms = new();
        private readonly CancellationTokenSource transferCancellation = new();
        private bool completed;

        internal OverlaySession(RemoteDragManifest manifest) => this.manifest = manifest;

        internal void Show()
        {
            foreach (var target in DiscoverTargets())
            {
                var form = new OverlayForm(target, OnDrop, OnCancel);
                forms.Add(form);
                form.Show();
            }
            if (forms.Count == 0)
            {
                Common.ShowToolTip("Remote drag: no filesystem Explorer target found.", 3000, ToolTipIcon.Warning, true);
            }
        }

        private void OnDrop(ExplorerTarget target)
        {
            if (completed) return;
            completed = true;
            CloseForms();
            _ = Task.Run(() => TransferAsync(target.FolderPath));
        }

        private void OnCancel()
        {
            if (completed) return;
            completed = true;
            CloseForms();
            transferCancellation.Cancel();
            Logger.LogDebug("RemoteDrag cancelled.");
        }

        private async Task TransferAsync(string targetDirectory)
        {
            var copied = 0;
            var failures = new List<string>();
            foreach (var item in manifest.Items)
            {
                try
                {
                    var source = ResolveSourcePath(manifest.SourceMachine, item.LocalPath);
                    var destination = Path.Combine(targetDirectory, Path.GetFileName(Path.TrimEndingDirectorySeparator(source)));
                    if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Destination already exists: " + destination);
                    if (File.Exists(source))
                    {
                    await CopyFileAsync(source, destination, transferCancellation.Token).ConfigureAwait(false);
                    }
                    else if (Directory.Exists(source))
                    {
                        await CopyDirectoryAsync(source, destination, transferCancellation.Token).ConfigureAwait(false);
                    }
                    else throw new FileNotFoundException("Source item is unavailable over SMB.", source);
                    copied++;
                    Logger.LogDebug("Transfer completed: " + source + " -> " + destination);
                }
                catch (Exception ex)
                {
                    failures.Add(ex.Message);
                    Logger.Log("Transfer failed: " + ex.Message);
                }
            }
            Common.ShowToolTip(failures.Count == 0 ? $"Remote drag complete ({copied} item(s))." : $"Remote drag: {copied} copied, {failures.Count} failed.", 4000, failures.Count == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning, true);
        }

        private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CopyFileAsync(file, Path.Combine(destination, Path.GetRelativePath(source, file)), cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        }

        private static string ResolveSourcePath(string machine, string localPath)
        {
            var fullPath = Path.GetFullPath(localPath);
            var root = Path.GetPathRoot(fullPath)?.TrimEnd('\\') ?? throw new IOException("Source path has no drive root.");
            if (root.Length != 2 || root[1] != ':') throw new IOException("Only local drive paths are supported.");
            var share = machine + "_" + root[0];
            var relative = fullPath[Path.GetPathRoot(fullPath)!.Length..].TrimStart('\\');
            return string.IsNullOrEmpty(relative) ? $"\\\\{machine}\\{share}" : $"\\\\{machine}\\{share}\\{relative}";
        }

        private void CloseForms()
        {
            foreach (var form in forms) form.Close();
            forms.Clear();
        }

        public void Dispose()
        {
            transferCancellation.Cancel();
            transferCancellation.Dispose();
            CloseForms();
        }

        private static IReadOnlyList<ExplorerTarget> DiscoverTargets()
        {
            var targets = new List<ExplorerTarget>();
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
                foreach (var window in shell.Windows())
                {
                    var hwnd = (nint)(long)window.HWND;
                    if (hwnd == 0 || !IsWindowVisible(hwnd) || IsIconic(hwnd) || !GetWindowRect(hwnd, out var rect)) continue;
                    var url = (string)window.LocationURL;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile || !Directory.Exists(uri.LocalPath)) continue;
                    targets.Add(new ExplorerTarget(hwnd, rect.ToRectangle(), uri.LocalPath));
                }
            }
            catch (Exception ex) { Logger.Log("Explorer target enumeration failed: " + ex.Message); }
            var desktop = GetDesktopWindow();
            if (desktop != 0 && GetWindowRect(desktop, out var desktopRect))
                targets.Insert(0, new ExplorerTarget(desktop, desktopRect.ToRectangle(), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));
            return targets;
        }

        private sealed record ExplorerTarget(nint Hwnd, Rectangle Bounds, string FolderPath);

        private sealed class OverlayForm : Form
        {
            private readonly ExplorerTarget target;
            private readonly Action<ExplorerTarget> onDrop;
            private readonly Action onCancel;
            internal OverlayForm(ExplorerTarget target, Action<ExplorerTarget> onDrop, Action onCancel)
            {
                this.target = target;
                this.onDrop = onDrop;
                this.onCancel = onCancel;
                Bounds = target.Bounds;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                KeyPreview = true;
                BackColor = Color.DeepSkyBlue;
                Opacity = 0.18;
                MouseEnter += (_, _) => { BackColor = Color.LimeGreen; Opacity = 0.32; Invalidate(); };
                MouseLeave += (_, _) => { BackColor = Color.DeepSkyBlue; Opacity = 0.18; };
                MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) onDrop(target); else if (e.Button == MouseButtons.Right) onCancel(); };
                KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) onCancel(); };
                Paint += (_, e) => { using var pen = new Pen(Color.White, 3); e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3); TextRenderer.DrawText(e.Graphics, target.FolderPath, Font, new Point(12, 12), Color.White, Color.FromArgb(160, 20, 20, 20)); };
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; internal Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    }

    private sealed record RemoteDragManifest
    {
        public int Version { get; init; }
        public Guid DragId { get; init; }
        public string SourceMachine { get; init; } = string.Empty;
        public List<RemoteDragItem> Items { get; init; } = new();
    }

    private sealed record RemoteDragItem
    {
        public string LocalPath { get; init; } = string.Empty;
        public bool IsDirectory { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
