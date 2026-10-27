using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class NativeProducerRegressionTests
{
    [Fact]
    public void InstalledConsumer_ClassifiesMalformedXmlJsonAndShapeFailuresAsContractFailures()
    {
        Assert.True(InstalledConsumerProducer.IsContractFailure(new XmlException("malformed")));
        Assert.True(InstalledConsumerProducer.IsContractFailure(new JsonException("malformed")));
        Assert.True(InstalledConsumerProducer.IsContractFailure(new KeyNotFoundException("missing property")));
        Assert.True(InstalledConsumerProducer.IsContractFailure(new FormatException("invalid value")));
        Assert.False(InstalledConsumerProducer.IsContractFailure(new NotSupportedException()));
        Assert.False(InstalledConsumerProducer.IsContractFailure(new OperationCanceledException()));
    }

    [Fact]
    public void InstalledConsumer_RedactsThenRetainsAtMostSixteenKiBUtf8Bytes()
    {
        var root = "/private/workspace";
        var source = root + "/secret/" + new string('é', 12_000);

        var safe = InstalledConsumerProducer.RedactAndBoundDiagnostic(source, root);

        Assert.DoesNotContain(root, safe, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(safe) <= 16 * 1024);
        Assert.EndsWith(new string('é', 12_000)[^8_000..], safe, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstalledConsumer_AssemblyVerificationChecksEveryStoreCandidate()
    {
        var root = Directory.CreateTempSubdirectory("native-store-proof-").FullName;
        try
        {
            var store = Path.Join(root, "tool", ".store", "package");
            Directory.CreateDirectory(store);
            var candidate = Path.Join(store, "ForgeTrust.AppSurface.Cli.dll");
            await File.WriteAllTextAsync(candidate, "wrong installed bytes");
            var decoy = Path.Join(root, "consumer", "ForgeTrust.AppSurface.Cli.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(decoy)!);
            await File.WriteAllTextAsync(decoy, "expected bytes");
            var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("expected bytes"))).ToLowerInvariant();

            Assert.False(await InstalledConsumerProducer.VerifyInstalledAssembliesAsync(Path.Join(root, "tool", ".store"), expected, CancellationToken.None));
            await File.WriteAllTextAsync(candidate, "expected bytes");
            var second = Path.Join(store, "nested", "ForgeTrust.AppSurface.Cli.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            await File.WriteAllTextAsync(second, "wrong duplicate");

            Assert.False(await InstalledConsumerProducer.VerifyInstalledAssembliesAsync(Path.Join(root, "tool", ".store"), expected, CancellationToken.None));
            await File.WriteAllTextAsync(second, "expected bytes");
            Assert.True(await InstalledConsumerProducer.VerifyInstalledAssembliesAsync(Path.Join(root, "tool", ".store"), expected, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PackedStorage_MalformedPackagePreflightReturnsFailureAndCleansOwnedWorkspace()
    {
        var root = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var parent = Directory.CreateTempSubdirectory("native-packed-preflight-").FullName;
        var packagePath = Path.Join(parent, "invalid.nupkg");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        await using (var writer = new StreamWriter(archive.CreateEntry("package.nuspec").Open()))
            await writer.WriteAsync("<package><metadata>");

        try
        {
            var settings = new ValidationSettings(root, packagePath, "1.2.3", "commit", "/missing/dotnet", parent, parent);
            var declaration = new EvidenceProducerDeclaration("packed-storage", "native-validation", "1", [], [], [], 900);
            var context = new EvidenceProducerContext(new EvidencePlan("1", "p", "pd", "diff", new("p", EvidenceProfileScope.Targeted, [], [declaration], []), [], [], "digest"), declaration, TimeProvider.System);
            await using var producer = new PackedStorageProducer(settings);

            var result = await producer.ProduceAsync(context, CancellationToken.None);

            Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
            Assert.Empty(result.SatisfiedAssertionIds);
            Assert.Equal([packagePath], Directory.EnumerateFileSystemEntries(parent));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }
}
