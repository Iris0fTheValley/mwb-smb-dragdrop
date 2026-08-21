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
    internal static bool IsActive => !Assemblies.IsEmpty || (session is not null && !session.IsCompleted);

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
        private bool completed;
        private bool transferStarted;

        internal OverlaySession(ID sourceId, RemoteDragManifest manifest)
        {
            this.sourceId = sourceId;
            this.manifest = manifest;
        }

        internal void Show()
        {
            try
            {
                RefreshTargets();
            }
            catch (Exception ex)
            {
                Logger.Log("RemoteDrag initial overlay refresh failed: " + ex.Message);
            }
            refreshTimer.Tick += (_, _) =>
            {
                try
                {
                    RefreshTargets();
                }
                catch (Exception ex)
                {
                    Logger.Log("RemoteDrag overlay refresh failed: " + ex.Message);
                }
            };
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
            transferStarted = true;
            _ = Task.Run(() => TransferAsync(target.FolderPath));
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

        internal bool IsCompleted => completed;

        private async Task TransferAsync(string targetDirectory)
        {
            try
            {
                await TransferCoreAsync(targetDirectory).ConfigureAwait(false);
            }
            finally
            {
                lock (SessionLock)
                {
                    if (ReferenceEquals(session, this))
                    {
                        session = null;
                    }
                }

                transferCancellation.Dispose();
            }
        }

        private async Task TransferCoreAsync(string targetDirectory)
        {
            var copied = 0;
            var failures = new List<string>();
            var sourceAccessFailed = false;
            var cancelled = false;
            foreach (var item in manifest.Items)
            {
                var sourceProbeCompleted = false;
                try
                {
                    var source = ResolveSourcePath(manifest.SourceMachine, item.LocalPath);
                    Logger.LogDebug("RemoteDrag source resolved: " + source);
                    var attributes = File.GetAttributes(source);

                    // Probe the source before creating the destination. Windows can report
                    // SMB authentication/share failures as IOException rather than
                    // UnauthorizedAccessException (for example, ERROR_LOGON_FAILURE).
                    // Those failures must use the source-side push fallback.
                    sourceProbeCompleted = true;
                    await ShellFileOperation.CopyAsync(new[] { source }, targetDirectory, Common.MainForm?.Handle ?? 0, transferCancellation.Token).ConfigureAwait(false);
                    copied++;
                    Logger.LogDebug("Transfer completed: " + source + " -> " + targetDirectory);
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
                    cancelled = true;
                    Logger.LogDebug("RemoteDrag transfer cancelled.");
                    break;
                }
                catch (Exception ex)
                {
                    failures.Add(ex.Message);
                    Logger.Log("Transfer failed: " + ex.Message);
                }
            }
            if (copied == 0 && sourceAccessFailed)
            {
                RequestSourcePush(targetDirectory);
                return;
            }

            var message = cancelled
                ? $"Remote drag cancelled ({copied} item(s) kept). / 已取消（保留 {copied} 项）"
                : failures.Count == 0
                    ? $"Remote drag complete ({copied} item(s))."
                    : $"Remote drag: {copied} copied, {failures.Count} failed.";
            Common.ShowToolTip(message, 4000, cancelled || failures.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info, true);
        }

        private static bool IsLikelySourceAccessFailure(IOException exception)
        {
            // Network-path authentication and share errors are surfaced by .NET as
            // IOException. A destination conflict is rejected before the source probe,
            // so IOException from the probe is safe to classify as source access here.
            return exception is not DirectoryNotFoundException;
        }

        private void RequestSourcePush(string targetDirectory)
        {
            var request = new RemoteDragPushRequest { Version = 2, DragId = manifest.DragId, TargetDirectory = Path.GetFullPath(targetDirectory) };
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
            var desired = targets
                .GroupBy(target => target.Hwnd)
                .ToDictionary(group => group.Key, group => group.First());
            for (var index = forms.Count - 1; index >= 0; index--)
            {
                if (!desired.ContainsKey(forms[index].TargetHwnd))
                {
                    forms[index].Close();
                    forms.RemoveAt(index);
                }
            }
            foreach (var target in desired.Values)
            {
                var form = forms.FirstOrDefault(candidate => candidate.TargetHwnd == target.Hwnd);
                if (form is null)
                {
                    try
                    {
                        form = new OverlayForm(target, target.Bounds, OnDrop, OnCancel);
                        forms.Add(form);
                        form.Show();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("RemoteDrag overlay creation failed: " + ex.Message);
                    }
                }
                else
                {
                    form.RefreshTarget(target, target.Bounds);
                }
            }
        }

        public void Dispose()
        {
            refreshTimer.Stop();
            refreshTimer.Dispose();
            transferCancellation.Cancel();
            if (!transferStarted)
            {
                transferCancellation.Dispose();
            }
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
                    try
                    {
                        var hwnd = (nint)(long)window.HWND;
                        if (hwnd == 0 || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd) || !TryGetActualWindowRect(hwnd, out var rect)) continue;
                        var bounds = rect.ToRectangle();
                        if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                        var url = (string)window.LocationURL;
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile || !Directory.Exists(uri.LocalPath)) continue;
                        var title = (string)window.LocationName;
                        targets.Add(new ExplorerTarget(hwnd, bounds, uri.LocalPath, string.IsNullOrWhiteSpace(title) ? Path.GetFileName(uri.LocalPath) : title, GetDpiForWindow(hwnd)));
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug("Explorer target skipped: " + ex.Message);
                    }
                }
            }
            catch (Exception ex) { Logger.Log("Explorer target enumeration failed: " + ex.Message); }
            Logger.LogDebug("ExplorerTargets discovered: " + string.Join("; ", targets.Select(target => $"{target.Hwnd}:{target.DisplayName}:{target.FolderPath}:{target.Bounds}")));
            return targets;
        }

        private sealed record ExplorerTarget(nint Hwnd, Rectangle Bounds, string FolderPath, string DisplayName, uint Dpi);

        private sealed class OverlayForm : System.Windows.Forms.Form
        {
            private readonly Action<ExplorerTarget> onDrop;
            private readonly Action onCancel;
            private ExplorerTarget target;
            private bool isHovered;
            private nint zOrderAnchor;
            internal nint TargetHwnd => target.Hwnd;
            internal Rectangle TargetBounds { get; private set; }
            internal OverlayForm(ExplorerTarget target, Rectangle bounds, Action<ExplorerTarget> onDrop, Action onCancel)
            {
                this.target = target;
                this.onDrop = onDrop;
                this.onCancel = onCancel;
                TargetBounds = bounds;
                zOrderAnchor = GetExplorerPredecessor(target.Hwnd);
                Bounds = bounds;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = false;
                KeyPreview = true;
                SetStyle(ControlStyles.Selectable, false);
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                BackColor = Color.DeepSkyBlue;
                Opacity = 0.18;
                MouseEnter += (_, _) => SetHoverState(true);
                MouseLeave += (_, _) => SetHoverState(false);
                MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) onDrop(target); else if (e.Button == MouseButtons.Right) onCancel(); };
                KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) onCancel(); };
                Paint += (_, e) => { using var pen = new Pen(Color.White, 3); e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3); TextRenderer.DrawText(e.Graphics, $"{target.DisplayName} / 目录\r\n{target.FolderPath}\r\nDPI {target.Dpi}", Font, new Rectangle(12, 12, Math.Max(20, Width - 24), Math.Max(20, Height - 24)), Color.White, Color.FromArgb(160, 20, 20, 20)); };
                Shown += (_, _) => PlaceAboveExplorer();
            }

            internal void RefreshTarget(ExplorerTarget next, Rectangle bounds)
            {
                var boundsChanged = TargetBounds != bounds;
                var contentChanged = target.DisplayName != next.DisplayName || target.FolderPath != next.FolderPath || target.Dpi != next.Dpi;
                var nextZOrderAnchor = GetExplorerPredecessor(next.Hwnd);
                var zOrderChanged = zOrderAnchor != nextZOrderAnchor;
                target = next;
                zOrderAnchor = nextZOrderAnchor;
                if (boundsChanged)
                {
                    TargetBounds = bounds;
                    Bounds = bounds;
                }
                if (boundsChanged || zOrderChanged)
                {
                    PlaceAboveExplorer();
                }
                if (contentChanged)
                {
                    Invalidate();
                }
            }

            private void SetHoverState(bool hovered)
            {
                if (isHovered == hovered) return;
                isHovered = hovered;
                BackColor = hovered ? Color.LimeGreen : Color.DeepSkyBlue;
                Opacity = hovered ? 0.32 : 0.18;
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
                if (IsHandleCreated)
                {
                    var insertAfter = zOrderAnchor;
                    if (insertAfter == Handle)
                    {
                        insertAfter = GetWindow(insertAfter, GW_HWNDPREV);
                    }
                    if (insertAfter == 0)
                    {
                        insertAfter = HWND_TOP;
                    }

                    _ = SetWindowPos(Handle, insertAfter, Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }
            }

            private nint GetExplorerPredecessor(nint explorerHwnd)
            {
                var predecessor = GetWindow(explorerHwnd, GW_HWNDPREV);
                return predecessor == Handle ? GetWindow(Handle, GW_HWNDPREV) : predecessor;
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; internal Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hWnd, uint attribute, out NativeRect value, int valueSize);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hWnd);
        [DllImport("user32.dll")] private static extern nint GetWindow(nint hWnd, uint command);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
        private const uint GW_HWNDPREV = 3;
        private static readonly nint HWND_TOP = 0;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        private static bool TryGetActualWindowRect(nint hwnd, out NativeRect rect)
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<NativeRect>()) == 0)
            {
                return true;
            }

            return GetWindowRect(hwnd, out rect);
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
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
