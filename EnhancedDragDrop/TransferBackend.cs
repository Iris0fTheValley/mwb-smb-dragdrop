namespace MouseWithoutBorders.EnhancedDragDrop;

public interface IFileTransferBackend
{
    Task<TransferReport> CopyAsync(
        IReadOnlyList<string> sourcePaths,
        string targetDirectory,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record TransferProgress(string? CurrentPath, long BytesCopied, long? TotalBytes);

public sealed record TransferReport(IReadOnlyList<string> Copied, IReadOnlyList<TransferFailure> Failures, long BytesCopied);

public sealed record TransferFailure(string SourcePath, string Error);

/// <summary>Streaming managed copy backend. SMB handles the actual network transport for UNC sources.</summary>
/// <summary>
/// Deterministic test/fixture backend for the standalone net8 model. The
/// integrated Mouse Without Borders path does not use this backend; it uses
/// Windows Shell IFileOperation instead.
/// </summary>
public sealed class StreamingFileTransferBackend : IFileTransferBackend
{
    private const int BufferSize = 1024 * 1024;

    public async Task<TransferReport> CopyAsync(
        IReadOnlyList<string> sourcePaths,
        string targetDirectory,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        Directory.CreateDirectory(targetDirectory);
        var copied = new List<string>();
        var failures = new List<TransferFailure>();
        long totalCopied = 0;
        foreach (var source in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var destination = Path.Combine(targetDirectory, Path.GetFileName(Path.TrimEndingDirectorySeparator(source)));
                if (File.Exists(source))
                {
                    if (File.Exists(destination) || Directory.Exists(destination))
                        throw new IOException($"Destination already exists: {destination}");
                    totalCopied += await CopyFileAsync(source, destination, progress, totalCopied, cancellationToken).ConfigureAwait(false);
                    copied.Add(destination);
                }
                else if (Directory.Exists(source))
                {
                    if (File.Exists(destination) || Directory.Exists(destination))
                        throw new IOException($"Destination already exists: {destination}");
                    totalCopied += await CopyDirectoryAsync(source, destination, progress, totalCopied, cancellationToken).ConfigureAwait(false);
                    copied.Add(destination);
                }
                else
                {
                    throw new FileNotFoundException("Source item does not exist.", source);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failures.Add(new TransferFailure(source, ex.Message)); }
        }
        return new TransferReport(copied, failures, totalCopied);
    }

    private static async Task<long> CopyDirectoryAsync(string source, string destination, IProgress<TransferProgress>? progress, long alreadyCopied, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        long copied = 0;
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationFile = Path.Combine(destination, Path.GetRelativePath(source, file));
            copied += await CopyFileAsync(file, destinationFile, progress, alreadyCopied + copied, cancellationToken).ConfigureAwait(false);
        }
        return copied;
    }

    private static async Task<long> CopyFileAsync(string source, string destination, IProgress<TransferProgress>? progress, long alreadyCopied, CancellationToken cancellationToken)
    {
        long copied = 0;
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            progress?.Report(new TransferProgress(source, alreadyCopied + copied, input.Length));
        }
        return copied;
    }
}
