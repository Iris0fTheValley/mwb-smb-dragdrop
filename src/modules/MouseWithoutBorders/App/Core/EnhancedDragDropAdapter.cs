using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MouseWithoutBorders.Core;

/// <summary>
/// Source-side bridge for enhanced Explorer drag/drop. Only a JSON manifest is
/// sent through MWB; the receiver later reads the files over SMB.
/// </summary>
internal static class EnhancedDragDropAdapter
{
    private const int ChunkBytes = 20;

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
}
