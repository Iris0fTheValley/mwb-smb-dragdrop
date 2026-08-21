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
            _ = Task.Run(() => PushToTargetAsync(targetMachine, request.TargetDirectory, lastManifest, cancellation.Token));
            Logger.LogDebug($"RemoteDrag push requested: DragId={request.DragId}, TargetMachine={targetMachine}, TargetDirectory={request.TargetDirectory}");
        }
        catch (Exception ex)
        {
            Logger.Log("RemoteDrag push request rejected: " + ex.Message);
        }
    }

    private static async Task PushToTargetAsync(string targetMachine, string targetDirectory, EnhancedDragManifest manifest, CancellationToken cancellationToken)
    {
        try
        {
            var destinationDirectory = ResolveTargetDirectory(targetMachine, targetDirectory);
            await ShellFileOperation.CopyAsync(manifest.Items.Select(item => item.LocalPath).ToArray(), destinationDirectory, Common.MainForm?.Handle ?? 0, cancellationToken).ConfigureAwait(false);
            Common.ShowToolTip($"Remote drag sent ({manifest.Items.Count} item(s)) / 已发送 {manifest.Items.Count} 个文件。", 3000, System.Windows.Forms.ToolTipIcon.Info, true);
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
            if (activeTransferCancellation is not null && activeTransferCancellation.Token == cancellationToken)
            {
                activeTransferCancellation = null;
            }
        }
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

    private static string ResolveTargetDirectory(string machine, string targetDirectory)
    {
        var fullPath = Path.GetFullPath(targetDirectory);
        var root = Path.GetPathRoot(fullPath)?.TrimEnd('\\') ?? throw new IOException("Target path has no drive root.");
        if (root.Length != 2 || root[1] != ':') throw new IOException("Only local drive targets are supported.");
        var address = Common.GetConnectedIPv4AddressFor(machine)
            ?? Dns.GetHostAddresses(machine).FirstOrDefault(candidate => candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        var host = address?.ToString() ?? machine;
        var share = machine + "_" + root[0];
        var relative = fullPath[Path.GetPathRoot(fullPath)!.Length..].TrimStart('\\');
        return string.IsNullOrEmpty(relative) ? $"\\\\{host}\\{share}" : $"\\\\{host}\\{share}\\{relative}";
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
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
