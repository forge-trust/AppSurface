using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceOrdinaryPublicationTests
{
    [Fact]
    public void OrdinaryCaptureForwardsCancellationThroughTheNativeEnumerationCallChain()
    {
        using var directory = TestDirectory.Create();
        var tree = Path.Join(directory.Path, "reports");
        Directory.CreateDirectory(tree);
        for (var index = 0; index < 3; index++) File.WriteAllText(Path.Join(tree, $"report-{index}.txt"), "report");
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        IEnumerable<string> WindowsNames()
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(tree))
            {
                reads++;
                cancellation.Cancel();
                yield return path;
            }
        }
        var calls = new CoverageRunOutputLease.CoverageArtifactEnumerationCalls
        {
            EnumerateWindowsNames = _ => WindowsNames(),
            ReadDirectory = stream =>
            {
                reads++;
                cancellation.Cancel();
                return CoverageRunOutputLease.CoverageArtifactEnumerationCalls.Default.ReadDirectory(stream);
            }
        };
        var inventory = new CoverageArtifactInventory(directory.Path, TimeProvider.System, calls);
        inventory.EnableOrdinaryCapture();
        inventory.RecordFreshTree(tree);

        Assert.ThrowsAny<OperationCanceledException>(() => inventory.CaptureOrdinary(cancellation.Token));

        // Removing the token at either forwarding boundary enumerates the entire tree before the outer check throws.
        Assert.Equal(1, reads);
        Assert.Equal(3, Directory.GetFiles(tree).Length);
    }

    [Fact]
    public async Task OrdinaryCaptureIncludesOnlyRegisteredOwnedFilesAndIsIdempotentlyEnabled()
    {
        using var directory = TestDirectory.Create();
        var inventory = new CoverageArtifactInventory(directory.Path, TimeProvider.System);
        inventory.EnableOrdinaryCapture();
        inventory.EnableOrdinaryCapture();
        var bytes = Encoding.UTF8.GetBytes("owned report");
        var report = Path.Join(directory.Path, "report.txt");
        await File.WriteAllBytesAsync(report, bytes);
        await File.WriteAllTextAsync(Path.Join(directory.Path, "foreign.txt"), "unregistered");
        inventory.RecordFile(report);

        var captured = Assert.Single(inventory.CaptureOrdinary(CancellationToken.None));

        Assert.Equal("report.txt", captured.Path);
        Assert.Equal(bytes.Length, captured.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), captured.Sha256);
        Assert.Equal(captured, Assert.Single(inventory.Capture(CancellationToken.None)));
    }

    [Fact]
    public void OrdinaryCaptureRequiresOptInBeforeIncompleteRegistration()
    {
        using var directory = TestDirectory.Create();
        var inventory = new CoverageArtifactInventory(directory.Path, TimeProvider.System);
        Assert.Throws<InvalidDataException>(() => inventory.CaptureOrdinary(CancellationToken.None));
        inventory.RecordFile(Path.Join(directory.Path, "..", "outside.txt"));
        inventory.EnableOrdinaryCapture();
        Assert.Throws<InvalidDataException>(() => inventory.CaptureOrdinary(CancellationToken.None));
    }

    [Fact]
    public void OrdinaryCaptureHonorsCancellationEvenForAnEmptyPublicationSet()
    {
        using var directory = TestDirectory.Create();
        var inventory = new CoverageArtifactInventory(directory.Path, TimeProvider.System);
        inventory.EnableOrdinaryCapture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => inventory.CaptureOrdinary(cancellation.Token));
    }
}
