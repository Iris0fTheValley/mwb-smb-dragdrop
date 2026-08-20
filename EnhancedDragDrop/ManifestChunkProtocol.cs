namespace MouseWithoutBorders.EnhancedDragDrop;

/// <summary>Chunk framing used when a manifest does not fit in one MWB packet.</summary>
public sealed record ManifestChunk(Guid DragId, int Index, int Total, byte[] Payload);

/// <summary>Splits and reassembles manifest bytes while rejecting duplicates and malformed input.</summary>
public static class ManifestChunkProtocol
{
    public const int PayloadBytes = 20;
    public const int MaxChunks = 100_000;

    public static IReadOnlyList<ManifestChunk> Split(DragManifest manifest)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(manifest.ToJson());
        var total = Math.Max(1, (bytes.Length + PayloadBytes - 1) / PayloadBytes);
        return Enumerable.Range(0, total)
            .Select(index => new ManifestChunk(manifest.DragId, index, total, bytes.AsSpan(index * PayloadBytes, Math.Min(PayloadBytes, bytes.Length - index * PayloadBytes)).ToArray()))
            .ToArray();
    }

    public static DragManifest Reassemble(IEnumerable<ManifestChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var list = chunks.ToArray();
        if (list.Length == 0 || list.Any(chunk => chunk.DragId == Guid.Empty || chunk.Total <= 0 || chunk.Total > MaxChunks || chunk.Index < 0 || chunk.Index >= chunk.Total || chunk.Payload.Length > PayloadBytes))
            throw new FormatException("Manifest chunks are invalid.");
        var dragId = list[0].DragId;
        var total = list[0].Total;
        if (list.Any(chunk => chunk.DragId != dragId || chunk.Total != total) || list.Select(chunk => chunk.Index).Distinct().Count() != list.Length || list.Length != total)
            throw new FormatException("Manifest chunks are incomplete or duplicated.");
        var bytes = list.OrderBy(chunk => chunk.Index).SelectMany(chunk => chunk.Payload).ToArray();
        var manifest = DragManifest.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        if (manifest.DragId != dragId)
            throw new FormatException("Manifest DragId does not match chunk DragId.");
        return manifest;
    }
}
