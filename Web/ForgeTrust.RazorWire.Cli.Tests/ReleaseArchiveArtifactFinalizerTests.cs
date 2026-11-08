namespace ForgeTrust.RazorWire.Cli.Tests;

public sealed class ReleaseArchiveArtifactFinalizerTests
{
    [Fact]
    public void ArchiveArtifactWritersRequireEnabledArchiveAndNonNullCallback()
    {
        var context = new ExportContext("dist", null, "http://localhost:5000");
        Func<ExportContext, CancellationToken, Task> writer = (_, _) => Task.CompletedTask;

        Assert.Empty(context.ReleaseArchiveArtifactFinalizers);
        Assert.Throws<InvalidOperationException>(() => context.RegisterReleaseArchiveArtifactFinalizer(writer));
        Assert.Empty(context.ReleaseArchiveArtifactFinalizers);

        context.EnableReleaseArchiveManifest();
        Assert.Throws<ArgumentNullException>(() => context.RegisterReleaseArchiveArtifactFinalizer(null!));
        Assert.Empty(context.ReleaseArchiveArtifactFinalizers);
        context.RegisterReleaseArchiveArtifactFinalizer(writer);
        Assert.Same(writer, Assert.Single(context.ReleaseArchiveArtifactFinalizers));
    }
}
