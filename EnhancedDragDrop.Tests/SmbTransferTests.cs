using MouseWithoutBorders.EnhancedDragDrop;
using Xunit;

namespace EnhancedDragDrop.Tests;

public sealed class SmbTransferTests
{
    [Fact]
    public async Task Streaming_backend_copies_a_real_unc_source()
    {
        var source = Environment.GetEnvironmentVariable("MWB_SMB_SMOKE_SOURCE")
            ?? throw new InvalidOperationException("MWB_SMB_SMOKE_SOURCE is required.");
        var target = Environment.GetEnvironmentVariable("MWB_SMB_SMOKE_TARGET")
            ?? throw new InvalidOperationException("MWB_SMB_SMOKE_TARGET is required.");
        var backend = new StreamingFileTransferBackend();
        var report = await backend.CopyAsync([source], target);
        Assert.Empty(report.Failures);
        Assert.Single(report.Copied);
        Assert.True(File.Exists(report.Copied[0]));
    }
}
