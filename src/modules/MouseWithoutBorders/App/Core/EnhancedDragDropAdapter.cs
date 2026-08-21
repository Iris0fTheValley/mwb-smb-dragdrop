// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.Core;

#pragma warning disable SA1107, SA1132, SA1134, SA1501, SA1502, SA1503, SA1513, SA1516, SA1520

/// <summary>
/// Source-side bridge for enhanced Explorer drag/drop. Only a JSON manifest is
/// sent through MWB; the receiver later reads the files over SMB.
/// </summary>
internal static class EnhancedDragDropAdapter
{
    private const int ChunkBytes = 20;
    private static EnhancedDragManifest lastManifest;

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

            _ = Task.Run(() => PushToTargetAsync(targetMachine, request.TargetDirectory, lastManifest));
            Logger.LogDebug($"RemoteDrag push requested: DragId={request.DragId}, TargetMachine={targetMachine}, TargetDirectory={request.TargetDirectory}");
        }
        catch (Exception ex)
        {
            Logger.Log("RemoteDrag push request rejected: " + ex.Message);
        }
    }

    private static async Task PushToTargetAsync(string targetMachine, string targetDirectory, EnhancedDragManifest manifest)
    {
        var copied = 0;
        try
        {
            foreach (var item in manifest.Items)
            {
                var destination = ResolveTargetPath(targetMachine, targetDirectory, Path.GetFileName(Path.TrimEndingDirectorySeparator(item.LocalPath)));
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    throw new IOException("Destination already exists: " + destination);
                }

                if (item.IsDirectory)
                {
                    await CopyDirectoryAsync(item.LocalPath, destination, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await CopyFileAsync(item.LocalPath, destination, CancellationToken.None).ConfigureAwait(false);
                }

                copied++;
                Logger.LogDebug("RemoteDrag push completed: " + item.LocalPath + " -> " + destination);
            }

            Common.ShowToolTip($"Remote drag sent ({copied} item(s)).", 3000, System.Windows.Forms.ToolTipIcon.Info, true);
        }
        catch (Exception ex)
        {
            Logger.Log("RemoteDrag push failed: " + ex.Message);
            Common.ShowToolTip("Remote drag send failed: " + ex.Message, 4000, System.Windows.Forms.ToolTipIcon.Warning, true);
        }
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

    private static string ResolveTargetPath(string machine, string targetDirectory, string name)
    {
        var fullPath = Path.GetFullPath(targetDirectory);
        var root = Path.GetPathRoot(fullPath)?.TrimEnd('\\') ?? throw new IOException("Target path has no drive root.");
        if (root.Length != 2 || root[1] != ':') throw new IOException("Only local drive targets are supported.");
        var address = Common.GetConnectedIPv4AddressFor(machine);
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
    }

    private sealed record EnhancedDragPushRequest
    {
        public int Version { get; init; }
        public Guid DragId { get; init; }
        public string TargetDirectory { get; init; } = string.Empty;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
