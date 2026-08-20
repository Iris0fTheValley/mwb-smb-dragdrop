using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseWithoutBorders.EnhancedDragDrop;

/// <summary>Metadata for one cross-machine Explorer drag. File bytes never appear in this object.</summary>
public sealed record DragManifest
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    [JsonPropertyName("dragId")]
    public Guid DragId { get; init; } = Guid.NewGuid();

    [JsonPropertyName("sourceMachine")]
    public required string SourceMachine { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<DragItem> Items { get; init; }

    public static DragManifest Create(string sourceMachine, IEnumerable<string> paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMachine);
        ArgumentNullException.ThrowIfNull(paths);
        var items = paths.Select(path =>
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A drag item path cannot be empty.", nameof(paths));
            var fullPath = Path.GetFullPath(path);
            return new DragItem { LocalPath = fullPath, IsDirectory = Directory.Exists(fullPath) };
        }).ToArray();
        if (items.Length == 0)
            throw new ArgumentException("A drag must contain at least one item.", nameof(paths));
        return new DragManifest { SourceMachine = sourceMachine.Trim(), Items = items };
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static DragManifest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FormatException("Drag manifest is empty.");
        var manifest = JsonSerializer.Deserialize<DragManifest>(json, JsonOptions)
            ?? throw new FormatException("Drag manifest is null.");
        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (Version != CurrentVersion)
            throw new FormatException($"Unsupported drag manifest version {Version}.");
        if (DragId == Guid.Empty)
            throw new FormatException("DragId must be non-empty.");
        if (string.IsNullOrWhiteSpace(SourceMachine))
            throw new FormatException("SourceMachine is required.");
        if (Items is null || Items.Count == 0)
            throw new FormatException("At least one drag item is required.");
        foreach (var item in Items)
        {
            if (string.IsNullOrWhiteSpace(item.LocalPath) || !Path.IsPathFullyQualified(item.LocalPath))
                throw new FormatException("Every drag item must contain a fully-qualified local path.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>One local source item in a <see cref="DragManifest"/>.</summary>
public sealed record DragItem
{
    [JsonPropertyName("localPath")]
    public required string LocalPath { get; init; }

    [JsonPropertyName("isDirectory")]
    public required bool IsDirectory { get; init; }
}

/// <summary>Length-prefixed UTF-8 transport framing that prevents fixed MWB packet truncation.</summary>
public static class DragManifestFraming
{
    public static byte[] Encode(DragManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var payload = System.Text.Encoding.UTF8.GetBytes(manifest.ToJson());
        var framed = new byte[sizeof(int) + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(framed, 0);
        payload.CopyTo(framed, sizeof(int));
        return framed;
    }

    public static DragManifest Decode(ReadOnlySpan<byte> framed)
    {
        if (framed.Length < sizeof(int))
            throw new FormatException("Manifest frame is shorter than its length prefix.");
        var length = BitConverter.ToInt32(framed[..sizeof(int)]);
        if (length <= 0 || length != framed.Length - sizeof(int))
            throw new FormatException("Manifest frame length does not match payload length.");
        return DragManifest.Parse(System.Text.Encoding.UTF8.GetString(framed[sizeof(int)..]));
    }
}
