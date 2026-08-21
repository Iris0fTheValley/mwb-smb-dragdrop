// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.Core;

#pragma warning disable CA1068, SA1107, SA1122, SA1132, SA1134, SA1501, SA1502, SA1503, SA1513, SA1516, SA1520

/// <summary>
/// Source-side bridge for enhanced Explorer drag/drop. Only a JSON manifest is
/// sent through MWB; the receiver later reads the files over SMB.
/// </summary>
internal static class EnhancedDragDropAdapter
{
    private const int ChunkBytes = 20;
    private static EnhancedDragManifest lastManifest;
    private static CancellationTokenSource activeTransferCancellation;
    private static EnhancedDragProgressForm activeProgressForm;

    internal static Guid ActiveDragId { get; private set; }
    internal static bool IsActive => ActiveDragId != Guid.Empty;

    internal static void BeginLocalDrag(string sourceMachine, IReadOnlyList<string> paths, ID destination)
    {
        if (!Setting.Values.TransferFile || paths.Count == 0 || destination == ID.NONE)
        {
            return;
        }

        var manifest = new EnhancedDragManifest
        {
            Version = 1,
            DragId = Guid.NewGuid(),
            SourceMachine = sourceMachine,
            Items = paths.Select(path => new EnhancedDragItem
            {
                LocalPath = Path.GetFullPath(path),
                IsDirectory = Directory.Exists(path),
                SizeBytes = GetSizeBytes(path),
                FileCount = GetFileCount(path),
            }).ToArray(),
        };
        lastManifest = manifest;
        ActiveDragId = manifest.DragId;
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest));
        var total = Math.Max(1, (payload.Length + ChunkBytes - 1) / ChunkBytes);
        for (var index = 0; index < total; index++)
        {
            var start = index * ChunkBytes;
            var length = Math.Min(ChunkBytes, payload.Length - start);
            Common.SendEnhancedDragChunk(destination, ActiveDragId, index, total, payload.AsSpan(start, length).ToArray());
        }
        Logger.LogDebug($"RemoteDrag Begin: DragId={ActiveDragId}, SourceMachine={sourceMachine}, ItemCount={manifest.Items.Count}");
    }

    internal static void Cancel()
    {
        ActiveDragId = Guid.Empty;
        activeTransferCancellation?.Cancel();
        activeTransferCancellation = null;
        Common.DoSomethingInUIThread(() =>
        {
            activeProgressForm?.Complete("Cancelled / 已取消");
            activeProgressForm = null;
        });
    }

    internal static void ReceivePushRequest(ID sourceId, string targetMachine, string requestJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize<EnhancedDragPushRequest>(requestJson, JsonOptions);
            if (request is null || request.Version != 2 || request.DragId == Guid.Empty || request.DragId != lastManifest?.DragId || string.IsNullOrWhiteSpace(request.TargetDirectory))
            {
                throw new FormatException("Remote drag push request validation failed.");
            }

            activeTransferCancellation?.Cancel();
            activeTransferCancellation = new CancellationTokenSource();
            var cancellation = activeTransferCancellation;
            Common.DoSomethingInUIThread(() =>
            {
                activeProgressForm?.Complete("Cancelled / 已取消");
                activeProgressForm = new EnhancedDragProgressForm(cancellation.Cancel);
                activeProgressForm.Show();
            });
            _ = Task.Run(() => PushToTargetAsync(targetMachine, request.TargetDirectory, lastManifest, request.ConflictPolicy, cancellation.Token));
            Logger.LogDebug($"RemoteDrag push requested: DragId={request.DragId}, TargetMachine={targetMachine}, TargetDirectory={request.TargetDirectory}");
        }
        catch (Exception ex)
        {
            Logger.Log("RemoteDrag push request rejected: " + ex.Message);
        }
    }

    private static async Task PushToTargetAsync(string targetMachine, string targetDirectory, EnhancedDragManifest manifest, EnhancedDragConflictPolicy requestPolicy, CancellationToken cancellationToken)
    {
        var copied = 0;
        long transferred = 0;
        var totalBytes = manifest.Items.Sum(item => item.SizeBytes);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var effectivePolicy = requestPolicy;
            foreach (var item in manifest.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ResolveTargetPath(targetMachine, targetDirectory, Path.GetFileName(Path.TrimEndingDirectorySeparator(item.LocalPath)));
                string temporaryDestination = null;
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    var policy = effectivePolicy;
                    if (policy == EnhancedDragConflictPolicy.Ask)
                    {
                        var decision = (Policy: EnhancedDragConflictPolicy.Ask, ApplyToAll: false);
                        Common.DoSomethingInUIThread(() => decision = EnhancedDragConflictDialog.Show(Path.GetFileName(item.LocalPath), item.IsDirectory), true);
                        policy = decision.Policy;
                        if (decision.ApplyToAll) effectivePolicy = policy;
                    }
                    if (policy == EnhancedDragConflictPolicy.Cancel) throw new OperationCanceledException(cancellationToken);
                    if (policy == EnhancedDragConflictPolicy.Skip) { copied++; continue; }
                    if (policy == EnhancedDragConflictPolicy.KeepBoth) destination = MakeUniqueDestination(destination);
                    if (policy == EnhancedDragConflictPolicy.Replace) DeleteExisting(destination);
                }

                temporaryDestination = destination + ".mwb-partial-" + Guid.NewGuid().ToString("N");
                try
                {
                    if (item.IsDirectory)
                    {
                        await CopyDirectoryAsync(item.LocalPath, temporaryDestination, value => ReportProgress(totalBytes, transferred += value, copied, manifest.Items.Count, stopwatch, item.LocalPath), cancellationToken).ConfigureAwait(false);
                        Directory.Move(temporaryDestination, destination);
                    }
                    else
                    {
                        await CopyFileAsync(item.LocalPath, temporaryDestination, value => ReportProgress(totalBytes, transferred += value, copied, manifest.Items.Count, stopwatch, item.LocalPath), cancellationToken).ConfigureAwait(false);
                        File.Move(temporaryDestination, destination);
                    }
                    temporaryDestination = null;
                }
                catch
                {
                    if (temporaryDestination is not null) DeleteExisting(temporaryDestination);
                    throw;
                }

                copied++;
                Logger.LogDebug("RemoteDrag push completed: " + item.LocalPath + " -> " + destination);
            }

            ReportProgress(totalBytes, transferred, copied, manifest.Items.Count, stopwatch, "");
            Common.ShowToolTip($"Remote drag sent ({copied} item(s)) / 已发送 {copied} 个文件。", 3000, System.Windows.Forms.ToolTipIcon.Info, true);
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("RemoteDrag push cancelled.");
            Common.ShowToolTip("Remote drag cancelled. / 已取消文件传输。", 3000, System.Windows.Forms.ToolTipIcon.Warning, true);
        }
        catch (Exception ex)
        {
            Logger.Log("RemoteDrag push failed: " + ex.Message);
            Common.ShowToolTip("Remote drag send failed: " + ex.Message, 4000, System.Windows.Forms.ToolTipIcon.Warning, true);
        }
        finally
        {
            Common.DoSomethingInUIThread(() => { activeProgressForm?.Complete("Complete / 完成"); activeProgressForm = null; });
            activeTransferCancellation = null;
        }
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

    private static void ReportProgress(long totalBytes, long transferred, int completed, int totalItems, Stopwatch stopwatch, string item)
    {
        activeProgressForm?.Report(new EnhancedDragProgress(totalBytes, transferred, totalItems, completed, transferred / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds)), item);
    }

    private static long GetSizeBytes(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) : new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static int GetFileCount(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count() : 1; }
        catch { return 1; }
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

    private static string ResolveTargetPath(string machine, string targetDirectory, string name)
    {
        var fullPath = Path.GetFullPath(targetDirectory);
        var root = Path.GetPathRoot(fullPath)?.TrimEnd('\\') ?? throw new IOException("Target path has no drive root.");
        if (root.Length != 2 || root[1] != ':') throw new IOException("Only local drive targets are supported.");
        var address = Common.GetConnectedIPv4AddressFor(machine)
            ?? Dns.GetHostAddresses(machine).FirstOrDefault(candidate => candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        var host = address?.ToString() ?? machine;
        var share = machine + "_" + root[0];
        var relative = fullPath[Path.GetPathRoot(fullPath)!.Length..].TrimStart('\\');
        var basePath = string.IsNullOrEmpty(relative) ? $"\\\\{host}\\{share}" : $"\\\\{host}\\{share}\\{relative}";
        return Path.Combine(basePath, name);
    }

    private sealed record EnhancedDragManifest
    {
        public int Version { get; init; }
        public Guid DragId { get; init; }
        public required string SourceMachine { get; init; }
        public required IReadOnlyList<EnhancedDragItem> Items { get; init; }
    }

    private sealed record EnhancedDragItem
    {
        public required string LocalPath { get; init; }
        public required bool IsDirectory { get; init; }
        public long SizeBytes { get; init; }
        public int FileCount { get; init; }
    }

    private sealed record EnhancedDragPushRequest
    {
        public int Version { get; init; }
        public Guid DragId { get; init; }
        public string TargetDirectory { get; init; } = string.Empty;
        public EnhancedDragConflictPolicy ConflictPolicy { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
