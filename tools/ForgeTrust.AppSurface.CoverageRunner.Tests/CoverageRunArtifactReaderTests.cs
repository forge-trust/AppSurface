using ForgeTrust.AppSurface.CoverageArtifacts;

namespace ForgeTrust.AppSurface.CoverageRunner.Tests;

public sealed class CoverageRunArtifactReaderTests
{
    [Fact]
    public void OpenRegularFile_ShouldReadNestedCollectorArtifact()
    {
        using var workspace = TestRepo.Create();
        var projectOutput = Path.Join(workspace.Root, "project");
        var rawResults = Path.Join(projectOutput, "collector-results", "invocation");
        var attachment = Path.Join(rawResults, "attachment");
        Directory.CreateDirectory(attachment);
        var candidate = Path.Join(attachment, "coverage.cobertura.xml");
        File.WriteAllText(candidate, "<coverage />");

        using var stream = CoverageRunArtifactReader.OpenRegularFile(
            projectOutput,
            rawResults,
            candidate);
        using var reader = new StreamReader(stream);

        Assert.Equal("<coverage />", reader.ReadToEnd());
    }

    [Fact]
    public void OpenRegularFile_ShouldRejectCandidateOutsideInvocationDirectory()
    {
        using var workspace = TestRepo.Create();
        var projectOutput = Path.Join(workspace.Root, "project");
        var rawResults = Path.Join(projectOutput, "collector-results", "invocation");
        Directory.CreateDirectory(rawResults);
        var candidate = Path.Join(projectOutput, "coverage.cobertura.xml");
        File.WriteAllText(candidate, "<coverage />");

        var exception = Assert.Throws<IOException>(() =>
            CoverageRunArtifactReader.OpenRegularFile(projectOutput, rawResults, candidate));

        Assert.Contains("escaped its invocation directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenRegularFile_ShouldRejectSymbolicLinkArtifact()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TestRepo.Create();
        var projectOutput = Path.Join(workspace.Root, "project");
        var rawResults = Path.Join(projectOutput, "collector-results", "invocation");
        Directory.CreateDirectory(rawResults);
        var external = Path.Join(workspace.Root, "external.xml");
        File.WriteAllText(external, "<coverage />");
        var candidate = Path.Join(rawResults, "coverage.cobertura.xml");
        File.CreateSymbolicLink(candidate, external);

        var exception = Assert.Throws<IOException>(() =>
            CoverageRunArtifactReader.OpenRegularFile(projectOutput, rawResults, candidate));

        Assert.Contains("securely open", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
