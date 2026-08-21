// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MouseWithoutBorders.Core;

#pragma warning disable CA1068, SA1107, SA1132, SA1134, SA1501, SA1502, SA1503, SA1513, SA1516, SA1520

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
    private static OverlaySession session;
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
            var json = Encoding.UTF8.GetString(assembly.Combine());
            var manifest = JsonSerializer.Deserialize<RemoteDragManifest>(json, JsonOptions);
            if (manifest is not null && manifest.Version == 1 && manifest.DragId == dragId && !string.IsNullOrWhiteSpace(manifest.SourceMachine) && manifest.Items.Count > 0)
            {
                Common.DoSomethingInUIThread(() => BeginOverlay(package.Src, manifest));
                Logger.LogDebug($"RemoteDrag manifest received: DragId={dragId}, SourceMachine={manifest.SourceMachine}, ItemCount={manifest.Items.Count}");
                return;
            }

            var request = JsonSerializer.Deserialize<RemoteDragPushRequest>(json, JsonOptions);
            if (request is null || request.Version != 2 || request.DragId != dragId || string.IsNullOrWhiteSpace(request.TargetDirectory))
            {
                throw new FormatException("RemoteDrag payload validation failed.");
            }

            // Enhanced chunk payloads occupy the extended DATA tail, which is also
            // where the legacy fixed-width MachineName lives. Resolve the sender
            // from its trusted MWB ID instead of reading the overlapping field.
            var sourceMachine = MachineStuff.NameFromID(package.Src);
            if (string.IsNullOrWhiteSpace(sourceMachine))
            {
                throw new InvalidOperationException("Remote drag sender is not in the machine matrix.");
            }

            EnhancedDragDropAdapter.ReceivePushRequest(package.Src, sourceMachine, json);
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

    private static void BeginOverlay(ID sourceId, RemoteDragManifest manifest)
    {
        lock (SessionLock)
        {
            CloseOverlay();
            session = new OverlaySession(sourceId, manifest);
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
        private readonly ID sourceId;
        private readonly List<OverlayForm> forms = new();
        private readonly CancellationTokenSource transferCancellation = new();
        private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 250 };
        private readonly Stopwatch transferStopwatch = new();
        private EnhancedDragProgressForm progressForm;
        private EnhancedDragConflictPolicy conflictPolicy = EnhancedDragConflictPolicy.Ask;
        private long transferredBytes;
        private int completedItems;
        private bool completed;

        internal OverlaySession(ID sourceId, RemoteDragManifest manifest)
        {
            this.sourceId = sourceId;
            this.manifest = manifest;
        }

        internal void Show()
        {
            RefreshTargets();
            refreshTimer.Tick += (_, _) => RefreshTargets();
            refreshTimer.Start();
            if (forms.Count == 0)
            {
                Common.ShowToolTip("Remote drag: no filesystem Explorer target found.", 3000, ToolTipIcon.Warning, true);
            }
        }

        private void OnDrop(ExplorerTarget target)
        {
            if (completed) return;
            completed = true;
            refreshTimer.Stop();
            CloseForms();
            Common.DoSomethingInUIThread(() =>
            {
                progressForm?.Complete("Starting / 正在开始");
                progressForm = new EnhancedDragProgressForm(OnTransferCancel);
                progressForm.Show();
            });
            _ = Task.Run(() => TransferAsync(target.FolderPath));
        }

        private void OnTransferCancel()
        {
            if (transferCancellation.IsCancellationRequested) return;
            transferCancellation.Cancel();
            Common.SendPackage(sourceId, PackageType.EnhancedDragCancel);
            Logger.LogDebug("RemoteDrag transfer cancellation requested.");
        }

        private void OnCancel()
        {
            if (completed) return;
            completed = true;
            CloseForms();
            transferCancellation.Cancel();
            Common.SendPackage(sourceId, PackageType.EnhancedDragCancel);
            CloseOverlay();
            Logger.LogDebug("RemoteDrag cancelled.");
        }

        private async Task TransferAsync(string targetDirectory)
        {
            var copied = 0;
            var failures = new List<string>();
            var sourceAccessFailed = false;
            transferredBytes = 0;
            completedItems = 0;
            transferStopwatch.Restart();
            foreach (var item in manifest.Items)
            {
                var sourceProbeCompleted = false;
                string temporaryDestination = null;
                try
                {
                    var source = ResolveSourcePath(manifest.SourceMachine, item.LocalPath);
                    Logger.LogDebug("RemoteDrag source resolved: " + source);
                    var attributes = File.GetAttributes(source);

                    // Probe the source before creating the destination. Windows can report
                    // SMB authentication/share failures as IOException rather than
                    // UnauthorizedAccessException (for example, ERROR_LOGON_FAILURE).
                    // Those failures must use the source-side push fallback.
                    if ((attributes & FileAttributes.Directory) == 0)
                    {
                        await using var probe = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                    }
                    sourceProbeCompleted = true;
                    var destination = Path.Combine(targetDirectory, Path.GetFileName(Path.TrimEndingDirectorySeparator(source)));
                    if (File.Exists(destination) || Directory.Exists(destination))
                    {
                        var decision = ResolveConflict(Path.GetFileName(destination), item.IsDirectory);
                        if (decision == EnhancedDragConflictPolicy.Cancel) throw new OperationCanceledException(transferCancellation.Token);
                        if (decision == EnhancedDragConflictPolicy.Skip) continue;
                        if (decision == EnhancedDragConflictPolicy.KeepBoth) destination = MakeUniqueDestination(destination);
                        temporaryDestination = destination + ".mwb-partial-" + Guid.NewGuid().ToString("N");
                    }
                    else
                    {
                        temporaryDestination = destination + ".mwb-partial-" + Guid.NewGuid().ToString("N");
                    }
                    if ((attributes & FileAttributes.Directory) == 0)
                    {
                        await CopyFileAsync(source, temporaryDestination, value => ReportProgress(item, value), transferCancellation.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await CopyDirectoryAsync(source, temporaryDestination, value => ReportProgress(item, value), transferCancellation.Token).ConfigureAwait(false);
                    }
                    if (File.Exists(destination) || Directory.Exists(destination)) DeleteExisting(destination);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (item.IsDirectory)
                    {
                        Directory.Move(temporaryDestination, destination);
                    }
                    else
                    {
                        File.Move(temporaryDestination, destination);
                    }
                    temporaryDestination = null;
                    copied++;
                    completedItems++;
                    Logger.LogDebug("Transfer completed: " + source + " -> " + destination);
                }
                catch (UnauthorizedAccessException ex) when (!sourceProbeCompleted)
                {
                    sourceAccessFailed = true;
                    failures.Add("SMB access denied: " + ex.Message);
                    Logger.Log("Transfer failed: SMB access denied. " + ex.Message);
                }
                catch (FileNotFoundException ex) when (!sourceProbeCompleted)
                {
                    sourceAccessFailed = true;
                    failures.Add("Source item is unavailable over SMB: " + ex.FileName);
                    Logger.Log("Transfer failed: source item is unavailable over SMB. " + ex.FileName);
                }
                catch (IOException ex) when (!sourceProbeCompleted && IsLikelySourceAccessFailure(ex))
                {
                    sourceAccessFailed = true;
                    failures.Add("Source item is unavailable over SMB: " + ex.Message);
                    Logger.Log("Transfer failed: source item is unavailable over SMB. " + ex.Message);
                }
                catch (OperationCanceledException)
                {
                    if (temporaryDestination is not null) DeleteExisting(temporaryDestination);
                    Logger.LogDebug("RemoteDrag transfer cancelled.");
                    break;
                }
                catch (Exception ex)
                {
                    if (temporaryDestination is not null) DeleteExisting(temporaryDestination);
                    failures.Add(ex.Message);
                    Logger.Log("Transfer failed: " + ex.Message);
                }
            }
            if (copied == 0 && sourceAccessFailed)
            {
                RequestSourcePush(targetDirectory, conflictPolicy);
                Common.DoSomethingInUIThread(() => { progressForm?.Complete("Source push / 源端传输"); progressForm = null; });
                return;
            }

            Common.ShowToolTip(failures.Count == 0 ? $"Remote drag complete ({copied} item(s))." : $"Remote drag: {copied} copied, {failures.Count} failed.", 4000, failures.Count == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning, true);
            Common.DoSomethingInUIThread(() => { progressForm?.Complete("Complete / 完成"); progressForm = null; });
        }

        private EnhancedDragConflictPolicy ResolveConflict(string name, bool isDirectory)
        {
            if (conflictPolicy != EnhancedDragConflictPolicy.Ask) return conflictPolicy;
            var decision = (Policy: EnhancedDragConflictPolicy.Cancel, ApplyToAll: false);
            Common.DoSomethingInUIThread(() => decision = EnhancedDragConflictDialog.Show(name, isDirectory), true);
            if (decision.ApplyToAll) conflictPolicy = decision.Policy;
            return decision.Policy;
        }

        private void ReportProgress(RemoteDragItem item, long bytes)
        {
            transferredBytes += bytes;
            var total = manifest.Items.Sum(value => value.SizeBytes);
            progressForm?.Report(new EnhancedDragProgress(total, transferredBytes, manifest.Items.Count, completedItems, transferredBytes / Math.Max(0.001, transferStopwatch.Elapsed.TotalSeconds)), item.LocalPath);
        }

        private static bool IsLikelySourceAccessFailure(IOException exception)
        {
            // Network-path authentication and share errors are surfaced by .NET as
            // IOException. A destination conflict is rejected before the source probe,
            // so IOException from the probe is safe to classify as source access here.
            return exception is not DirectoryNotFoundException;
        }

        private void RequestSourcePush(string targetDirectory, EnhancedDragConflictPolicy policy)
        {
            var request = new RemoteDragPushRequest { Version = 2, DragId = manifest.DragId, TargetDirectory = Path.GetFullPath(targetDirectory), ConflictPolicy = policy };
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions));
            var total = Math.Max(1, (payload.Length + 19) / 20);
            for (var index = 0; index < total; index++)
            {
                var start = index * 20;
                var length = Math.Min(20, payload.Length - start);
                Common.SendEnhancedDragChunk(sourceId, manifest.DragId, index, total, payload.AsSpan(start, length).ToArray());
            }
            Logger.LogDebug($"RemoteDrag source SMB read denied; requested source push to {targetDirectory}.");
            Common.ShowToolTip("Remote drag: source is transferring via SMB.", 4000, ToolTipIcon.Info, true);
        }

        private static async Task CopyDirectoryAsync(string source, string destination, Action<long> onBytes, CancellationToken cancellationToken)
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
                await CopyFileAsync(file, Path.Combine(destination, Path.GetRelativePath(source, file)), onBytes, cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task CopyFileAsync(string source, string destination, Action<long> onBytes, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                onBytes(read);
            }
        }

        private static string MakeUniqueDestination(string destination)
        {
            var directory = Path.GetDirectoryName(destination)!;
            var name = Path.GetFileName(destination);
            for (var index = 2; ; index++)
            {
                var candidate = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)} ({index}){Path.GetExtension(name)}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        private static void DeleteExisting(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path);
        }

        private static string ResolveSourcePath(string machine, string localPath)
        {
            var fullPath = Path.GetFullPath(localPath);
            var pathForMapping = fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal) ? fullPath[4..] : fullPath;
            var root = Path.GetPathRoot(pathForMapping)?.TrimEnd('\\') ?? throw new IOException("Source path has no drive root.");
            if (root.Length != 2 || root[1] != ':') throw new IOException("Only local drive paths are supported.");
            var share = machine + "_" + root[0];
            var relative = pathForMapping[Path.GetPathRoot(pathForMapping)!.Length..].TrimStart('\\');
            return string.IsNullOrEmpty(relative) ? $"\\\\{machine}\\{share}" : $"\\\\{machine}\\{share}\\{relative}";
        }

        private void CloseForms()
        {
            foreach (var form in forms) form.Close();
            forms.Clear();
        }

        private void RefreshTargets()
        {
            if (completed) return;
            var targets = DiscoverTargets();
            var ignoredOverlays = forms.Where(form => form.IsHandleCreated).Select(form => form.Handle).ToHashSet();
            var desired = targets.SelectMany(target => GetVisibleRegions(target, ignoredOverlays).Select(bounds => (target, bounds))).ToArray();
            for (var index = forms.Count - 1; index >= 0; index--)
            {
                if (!desired.Any(value => value.target.Hwnd == forms[index].TargetHwnd && value.bounds == forms[index].TargetBounds))
                {
                    forms[index].Close();
                    forms.RemoveAt(index);
                }
            }
            foreach (var value in desired)
            {
                var form = forms.FirstOrDefault(candidate => candidate.TargetHwnd == value.target.Hwnd && candidate.TargetBounds == value.bounds);
                if (form is null)
                {
                    form = new OverlayForm(value.target, value.bounds, OnDrop, OnCancel);
                    forms.Add(form);
                    form.Show();
                }
                else
                {
                    form.RefreshTarget(value.target, value.bounds);
                }
            }
        }

        private static IReadOnlyList<Rectangle> GetVisibleRegions(ExplorerTarget target, IReadOnlySet<nint> ignoredWindows)
        {
            var regions = new List<Rectangle> { target.Bounds };
            const uint GW_HWNDPREV = 3;
            for (var current = GetWindow(target.Hwnd, GW_HWNDPREV); current != 0; current = GetWindow(current, GW_HWNDPREV))
            {
                if (ignoredWindows.Contains(current)) continue;
                if (!IsWindowVisible(current) || IsIconic(current) || !GetWindowRect(current, out var rect)) continue;
                var occluder = rect.ToRectangle();
                var next = new List<Rectangle>();
                foreach (var region in regions) Subtract(region, occluder, next);
                regions = next;
                if (regions.Count == 0) break;
            }
            return regions.Where(region => region.Width > 8 && region.Height > 8).ToArray();
        }

        private static void Subtract(Rectangle source, Rectangle occluder, List<Rectangle> output)
        {
            var intersection = Rectangle.Intersect(source, occluder);
            if (intersection.IsEmpty) { output.Add(source); return; }
            if (intersection.Top > source.Top) output.Add(Rectangle.FromLTRB(source.Left, source.Top, source.Right, intersection.Top));
            if (intersection.Bottom < source.Bottom) output.Add(Rectangle.FromLTRB(source.Left, intersection.Bottom, source.Right, source.Bottom));
            if (intersection.Left > source.Left) output.Add(Rectangle.FromLTRB(source.Left, intersection.Top, intersection.Left, intersection.Bottom));
            if (intersection.Right < source.Right) output.Add(Rectangle.FromLTRB(intersection.Right, intersection.Top, source.Right, intersection.Bottom));
        }

        public void Dispose()
        {
            refreshTimer.Stop();
            refreshTimer.Dispose();
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
                    var title = (string)window.LocationName;
                    targets.Add(new ExplorerTarget(hwnd, rect.ToRectangle(), uri.LocalPath, string.IsNullOrWhiteSpace(title) ? Path.GetFileName(uri.LocalPath) : title, GetZOrder(hwnd)));
                }
            }
            catch (Exception ex) { Logger.Log("Explorer target enumeration failed: " + ex.Message); }
            var desktop = GetDesktopWindow();
            if (desktop != 0 && GetWindowRect(desktop, out var desktopRect))
            {
                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                targets.Insert(0, new ExplorerTarget(desktop, desktopRect.ToRectangle(), desktopPath, "Desktop", int.MaxValue));
            }
            Logger.LogDebug("ExplorerTargets discovered: " + string.Join("; ", targets.Select(target => $"{target.Hwnd}:{target.DisplayName}:{target.FolderPath}:{target.Bounds}")));
            return targets;
        }

        private sealed record ExplorerTarget(nint Hwnd, Rectangle Bounds, string FolderPath, string DisplayName, int ZOrder);

        private sealed class OverlayForm : System.Windows.Forms.Form
        {
            private readonly Action<ExplorerTarget> onDrop;
            private readonly Action onCancel;
            private ExplorerTarget target;
            internal nint TargetHwnd => target.Hwnd;
            internal Rectangle TargetBounds { get; private set; }
            internal OverlayForm(ExplorerTarget target, Rectangle bounds, Action<ExplorerTarget> onDrop, Action onCancel)
            {
                this.target = target;
                this.onDrop = onDrop;
                this.onCancel = onCancel;
                TargetBounds = bounds;
                Bounds = bounds;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = false;
                KeyPreview = true;
                SetStyle(ControlStyles.Selectable, false);
                BackColor = Color.DeepSkyBlue;
                Opacity = 0.18;
                MouseEnter += (_, _) => { BackColor = Color.LimeGreen; Opacity = 0.32; Invalidate(); };
                MouseLeave += (_, _) => { BackColor = Color.DeepSkyBlue; Opacity = 0.18; };
                MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) onDrop(target); else if (e.Button == MouseButtons.Right) onCancel(); };
                KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) onCancel(); };
                Paint += (_, e) => { using var pen = new Pen(Color.White, 3); e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3); TextRenderer.DrawText(e.Graphics, $"{target.DisplayName} / 目录\r\n{target.FolderPath}", Font, new Rectangle(12, 12, Math.Max(20, Width - 24), Math.Max(20, Height - 24)), Color.White, Color.FromArgb(160, 20, 20, 20)); };
                Shown += (_, _) => PlaceAboveExplorer();
            }

            internal void RefreshTarget(ExplorerTarget next, Rectangle bounds)
            {
                target = next;
                TargetBounds = bounds;
                if (Bounds != bounds) Bounds = bounds;
                PlaceAboveExplorer();
                Invalidate();
            }

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    parameters.ExStyle |= 0x08000000;
                    parameters.ExStyle |= 0x00000080;
                    return parameters;
                }
            }

            private void PlaceAboveExplorer()
            {
                if (IsHandleCreated) _ = SetWindowPos(Handle, target.Hwnd, Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; internal Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
        [DllImport("user32.dll")] private static extern nint GetWindow(nint hWnd, uint uCmd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private static int GetZOrder(nint hwnd)
        {
            const uint GW_HWNDPREV = 3;
            var rank = 0;
            for (var current = GetWindow(hwnd, GW_HWNDPREV); current != 0 && rank < 10000; current = GetWindow(current, GW_HWNDPREV))
            {
                rank++;
            }
            return rank;
        }
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
        public long SizeBytes { get; init; }
        public int FileCount { get; init; }
    }

    private sealed record RemoteDragPushRequest
    {
        public int Version { get; init; }
        public Guid DragId { get; init; }
        public string TargetDirectory { get; init; } = string.Empty;
        public EnhancedDragConflictPolicy ConflictPolicy { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
