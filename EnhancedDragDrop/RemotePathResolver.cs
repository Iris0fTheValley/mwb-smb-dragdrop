namespace MouseWithoutBorders.EnhancedDragDrop;

/// <summary>Resolves a source-local Windows path to an existing UNC share.</summary>
public interface IRemotePathResolver
{
    string Resolve(string sourceMachine, string localPath);
}

/// <summary>Maps local drive roots to named machine shares without assuming symmetric drive letters.</summary>
public sealed class SharePathResolver : IRemotePathResolver
{
    private readonly IReadOnlyDictionary<string, string> sharesByRoot;

    public SharePathResolver(IEnumerable<ShareRoot> shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        sharesByRoot = shares.ToDictionary(
            share => NormalizeRoot(share.LocalRoot),
            share => share.ShareName,
            StringComparer.OrdinalIgnoreCase);
    }

    public string Resolve(string sourceMachine, string localPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMachine);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var fullPath = Path.GetFullPath(localPath);
        if (!Path.IsPathFullyQualified(fullPath) || fullPath.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException($"Only source-local drive paths can be resolved: {localPath}");
        var root = NormalizeRoot(Path.GetPathRoot(fullPath) ?? throw new IOException($"No drive root in {localPath}"));
        if (!sharesByRoot.TryGetValue(root, out var shareName))
            throw new IOException($"No configured SMB share for source drive {root}.");
        var relative = fullPath[root.Length..].TrimStart('\\');
        return string.IsNullOrEmpty(relative)
            ? $"\\\\{sourceMachine}\\{shareName}"
            : $"\\\\{sourceMachine}\\{shareName}\\{relative}";
    }

    private static string NormalizeRoot(string root) => root.Trim().TrimEnd('\\') + "\\";
}

/// <summary>Local source volume and the share name visible to its peer.</summary>
public sealed record ShareRoot
{
    public required string LocalRoot { get; init; }
    public required string ShareName { get; init; }
}
