using System.Text;
using ForgeTrust.AppSurface.Evidence.Validation;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class NativeValidationJsonTests
{
    [Fact]
    public void ReadJsonFile_AcceptsValidJsonAndPreservesBytes()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Join(directory.Root, "candidate.json");
        var expected = Encoding.UTF8.GetBytes("{\"passed\":true}");
        File.WriteAllBytes(path, expected);

        using var document = ValidationProof.ReadJsonFile(path, 1024, CancellationToken.None);

        Assert.True(document.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReadJsonFile_RejectsOversizedAndEmptyFilesWithoutChangingBytes()
    {
        using var directory = new TemporaryDirectory();
        var oversizedPath = System.IO.Path.Join(directory.Root, "oversized.json");
        var oversized = Encoding.UTF8.GetBytes("{\"value\":\"123456\"}");
        File.WriteAllBytes(oversizedPath, oversized);
        var emptyPath = System.IO.Path.Join(directory.Root, "empty.json");
        File.WriteAllBytes(emptyPath, []);

        Assert.Throws<InvalidDataException>(() => ValidationProof.ReadJsonFile(oversizedPath, 8, CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => ValidationProof.ReadJsonFile(emptyPath, 1024, CancellationToken.None));
        Assert.Equal(oversized, File.ReadAllBytes(oversizedPath));
        Assert.Empty(File.ReadAllBytes(emptyPath));
    }

    [Fact]
    public void ReadJsonFile_RejectsOneByteAboveTheGateAndRunManifestCaps()
    {
        using var directory = new TemporaryDirectory();
        var gatePath = System.IO.Path.Join(directory.Root, "gate.json");
        var manifestPath = System.IO.Path.Join(directory.Root, "manifest.json");
        foreach (var (path, maximum) in new[] { (gatePath, 1 * 1024 * 1024), (manifestPath, 16 * 1024 * 1024) })
        {
            var exact = "{}" + new string(' ', maximum - 2);
            File.WriteAllText(path, exact, new UTF8Encoding(false));
            using (var accepted = ValidationProof.ReadJsonFile(path, maximum, CancellationToken.None))
                Assert.Equal(System.Text.Json.JsonValueKind.Object, accepted.RootElement.ValueKind);
            File.AppendAllText(path, " ", new UTF8Encoding(false));
            Assert.Equal(maximum + 1, new FileInfo(path).Length);

            var exception = Assert.Throws<InvalidDataException>(() => ValidationProof.ReadJsonFile(path, maximum, CancellationToken.None));

            Assert.Contains("size limit", exception.Message, StringComparison.Ordinal);
            Assert.Equal(exact + " ", File.ReadAllText(path));
        }
    }

    [Fact]
    public void ReadJsonFile_RejectsInvalidJsonAndHonorsCancellation()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Join(directory.Root, "invalid.json");
        File.WriteAllText(path, "{invalid");

        Assert.Throws<InvalidDataException>(() => ValidationProof.ReadJsonFile(path, 1024, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ValidationProof.ReadJsonFile(path, 1024, cancellation.Token));
    }

    [Fact]
    public void GateReportContract_AcceptsBoundedValidCandidateReportAndPreservesBytes()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Join(directory.Root, "coverage-gate.json");
        var json = """
            {"passed":true,"tolerancePercent":0,"thresholds":{"line":95,"branch":85},"effectiveThresholds":{"line":95,"branch":85}}
            """;
        File.WriteAllText(path, json);
        var expected = File.ReadAllBytes(path);

        InstalledConsumerProducer.ValidateGateReportForTests(directory.Root, hasPatch: false);

        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void GateReportContract_RejectsOversizedCandidateBytesBeforeParsing()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Join(directory.Root, "coverage-gate.json");
        const string validGate = """
            {"passed":true,"tolerancePercent":0,"thresholds":{"line":95,"branch":85},"effectiveThresholds":{"line":95,"branch":85}}
            """;
        var exact = validGate + new string(' ', 1024 * 1024 - validGate.Length);
        File.WriteAllText(path, exact, new UTF8Encoding(false));
        InstalledConsumerProducer.ValidateGateReportForTests(directory.Root, hasPatch: false);
        File.AppendAllText(path, " ", new UTF8Encoding(false));

        var exception = Assert.Throws<InvalidDataException>(() =>
            InstalledConsumerProducer.ValidateGateReportForTests(directory.Root, hasPatch: false));
        Assert.Contains("size limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1024 * 1024 + 1, new FileInfo(path).Length);
    }

    [Fact]
    public void ReadJsonFile_RejectsInvalidLimitsAndMissingFiles()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Join(directory.Root, "missing.json");

        Assert.Throws<ArgumentOutOfRangeException>(() => ValidationProof.ReadJsonFile(path, 0, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => ValidationProof.ReadJsonFile(path, (long)int.MaxValue + 1, CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => ValidationProof.ReadJsonFile(path, 1024, CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Directory.CreateTempSubdirectory("appsurface-native-json-").FullName;
        }

        public string Root { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
