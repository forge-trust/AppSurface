using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace ForgeTrust.AppSurface.Web.Tests;

public sealed class PwaStaticFileShadowValidatorTests
{
    [Fact]
    public void ThrowIfInvalid_WhenWorkerStaticFileExists_RejectsStartup()
    {
        var options = new PwaOptions();
        options.Push.Enabled = true;

        var exception = Assert.Throws<InvalidOperationException>(
            () => PwaStaticFileShadowValidator.ThrowIfInvalid(options, new StubFileProvider("service-worker.js")));

        Assert.Contains("ASPWA024", exception.Message, StringComparison.Ordinal);
        Assert.Contains("service worker", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_WhenHelperStaticFileExists_RejectsStartup()
    {
        var options = new PwaOptions();
        options.Push.Enabled = true;

        var exception = Assert.Throws<InvalidOperationException>(
            () => PwaStaticFileShadowValidator.ThrowIfInvalid(options, new StubFileProvider("_appsurface/pwa/register.js")));

        Assert.Contains("ASPWA024", exception.Message, StringComparison.Ordinal);
        Assert.Contains("registration helper", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_WhenWorkerIsInactive_DoesNotInspectStaticFiles()
    {
        var options = new PwaOptions();

        var exception = Record.Exception(
            () => PwaStaticFileShadowValidator.ThrowIfInvalid(options, new StubFileProvider("service-worker.js")));

        Assert.Null(exception);
    }

    [Fact]
    public void ThrowIfInvalid_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => PwaStaticFileShadowValidator.ThrowIfInvalid(null!, new NullFileProvider()));
        Assert.Throws<ArgumentNullException>(
            () => PwaStaticFileShadowValidator.ThrowIfInvalid(new PwaOptions(), null!));
    }

    private sealed class StubFileProvider(params string[] paths) : IFileProvider
    {
        private readonly HashSet<string> _paths = new(paths, StringComparer.Ordinal);

        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

        public IFileInfo GetFileInfo(string subpath) => _paths.Contains(subpath)
            ? new StubFileInfo(subpath)
            : new NotFoundFileInfo(subpath);

        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }

    private sealed class StubFileInfo(string name) : IFileInfo
    {
        public bool Exists => true;

        public long Length => 0;

        public string? PhysicalPath => null;

        public string Name => name;

        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;

        public bool IsDirectory => false;

        public Stream CreateReadStream() => Stream.Null;
    }
}
