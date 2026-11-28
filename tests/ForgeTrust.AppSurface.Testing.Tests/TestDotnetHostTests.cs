using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Testing.Tests;

public sealed class TestDotnetHostTests : IDisposable
{
    private readonly string _root = TestPathUtils.PathUnder(Path.GetTempPath(), "appsurface-test-dotnet-host", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Resolve_PrefersFullyQualifiedHostPathWithoutRequiringAnExistingFile()
    {
        var missingHost = TestPathUtils.PathUnder(_root, "selected", "dotnet-host");
        var rootHost = CreateHost("root", "dotnet");
        var pathHost = CreateHost("path", "dotnet");

        var resolved = TestDotnetHost.Resolve(
            missingHost,
            Path.GetDirectoryName(rootHost),
            Path.GetDirectoryName(pathHost),
            _root,
            isWindows: false);

        Assert.Equal(missingHost, resolved);
    }

    [Theory]
    [InlineData(false, "dotnet")]
    [InlineData(true, "dotnet.exe")]
    public void Resolve_UsesPlatformHostNameBeneathDotnetRoot(bool isWindows, string executable)
    {
        var expected = CreateHost("sdk", executable);
        var pathHost = CreateHost("path-sdk", "dotnet");

        var resolved = TestDotnetHost.Resolve(
            null, Path.GetDirectoryName(expected), Path.GetDirectoryName(pathHost), _root, isWindows);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void Resolve_ReturnsDotnetRootCandidateBeforeCheckingPathWhenItDoesNotExist()
    {
        var missingRootHost = TestPathUtils.PathUnder(_root, "missing-sdk", "dotnet");
        var pathHost = CreateHost("path-sdk", "dotnet");

        var resolved = TestDotnetHost.Resolve(
            null, Path.GetDirectoryName(missingRootHost), Path.GetDirectoryName(pathHost), _root, isWindows: false);

        Assert.Equal(missingRootHost, resolved);
    }

    [Fact]
    public void Resolve_SearchesPathInOrderAndResolvesRelativeEntriesFromSuppliedDirectory()
    {
        var firstSdk = CreateHost("first-sdk", "dotnet");
        var secondSdk = CreateHost("second-sdk", "dotnet");
        var relativeFirstSdk = Path.GetRelativePath(_root, Path.GetDirectoryName(firstSdk)!);
        var relativeSecondSdk = Path.GetRelativePath(_root, Path.GetDirectoryName(secondSdk)!);

        var resolved = TestDotnetHost.Resolve(null, null,
            string.Join(Path.PathSeparator, relativeFirstSdk, relativeSecondSdk), _root, isWindows: false);

        Assert.Equal(firstSdk, resolved);
    }

    [Fact]
    public void Resolve_SkipsEmptyPathEntriesUntilALaterSdkDirectory()
    {
        var laterSdk = CreateHost("later-sdk", "dotnet");
        var path = string.Join(Path.PathSeparator, "missing-sdk", string.Empty, "also-missing", Path.GetDirectoryName(laterSdk));

        var resolved = TestDotnetHost.Resolve(null, null, path, _root, isWindows: false);

        Assert.Equal(laterSdk, resolved);
    }

    [Fact]
    public void Resolve_UsesCurrentDirectoryForLeadingTrailingAndInteriorEmptyPathEntries()
    {
        var currentHost = CreateHost("dotnet");
        var pathValues = new[]
        {
            Path.PathSeparator + "missing-sdk",
            "missing-sdk" + Path.PathSeparator,
            "missing-sdk" + Path.PathSeparator + Path.PathSeparator + "also-missing",
        };

        foreach (var path in pathValues)
        {
            var resolved = TestDotnetHost.Resolve(null, null, path, _root, isWindows: false);
            Assert.Equal(currentHost, resolved);
        }
    }

    [Fact]
    public void Resolve_UsesCurrentDirectoryWhenPathIsUnset()
    {
        var currentHost = CreateHost("dotnet");

        var resolved = TestDotnetHost.Resolve(null, null, null, _root, isWindows: false);

        Assert.Equal(currentHost, resolved);
    }

    [Fact]
    public void ResolveExisting_SkipsMissingCandidatesAndUsesExistingCandidatesInOrder()
    {
        var hintedHost = CreateHost("hinted-host");
        var rootHost = CreateHost("root-sdk", "dotnet");
        var firstPathHost = CreateHost("first-sdk", "dotnet");
        var secondPathHost = CreateHost("second-sdk", "dotnet");
        var path = string.Join(Path.PathSeparator,
            Path.GetRelativePath(_root, Path.GetDirectoryName(firstPathHost)!),
            Path.GetRelativePath(_root, Path.GetDirectoryName(secondPathHost)!));
        var relativeHintedHost = Path.GetRelativePath(_root, hintedHost);
        var missingHost = TestPathUtils.PathUnder(_root, "missing-host");
        var missingRoot = Path.GetRelativePath(_root, TestPathUtils.PathUnder(_root, "missing-sdk"));

        var resolved = TestDotnetHost.ResolveExisting(
            relativeHintedHost, Path.GetDirectoryName(rootHost), path, _root, isWindows: false);
        Assert.Equal(hintedHost, resolved);

        var relativeRoot = Path.GetRelativePath(_root, Path.GetDirectoryName(rootHost)!);
        resolved = TestDotnetHost.ResolveExisting(
            missingHost, relativeRoot, path, _root, isWindows: false);
        Assert.Equal(rootHost, resolved);

        resolved = TestDotnetHost.ResolveExisting(
            missingHost, missingRoot, path, _root, isWindows: false);
        Assert.Equal(firstPathHost, resolved);
    }

    [Theory]
    [InlineData(false, "dotnet")]
    [InlineData(true, "dotnet.exe")]
    public void ResolveExisting_UsesPlatformHostNameBeneathDotnetRoot(bool isWindows, string executable)
    {
        var expected = CreateHost("sdk", executable);

        var resolved = TestDotnetHost.ResolveExisting(
            null, Path.GetDirectoryName(expected), null, _root, isWindows);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void Resolve_ThrowsWhenNoPathCandidateExists()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TestDotnetHost.Resolve("relative-host", null, "missing-sdk", _root, isWindows: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resolve_ThrowsWhenUnsetOrEmptyPathHasNoCurrentDirectoryHost(string? path)
    {
        Assert.Throws<InvalidOperationException>(() =>
            TestDotnetHost.Resolve(null, null, path, _root, isWindows: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative-directory")]
    public void Resolve_RejectsAnInvalidCurrentDirectory(string? currentDirectory)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            TestDotnetHost.Resolve(null, null, null, currentDirectory!, isWindows: false));
    }

    private string CreateHost(params string[] segments)
    {
        var hostPath = TestPathUtils.PathUnder(_root, segments);
        Directory.CreateDirectory(Path.GetDirectoryName(hostPath)!);
        File.WriteAllText(hostPath, "test host placeholder");
        return hostPath;
    }
}
