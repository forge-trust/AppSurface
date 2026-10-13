using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using FakeItEasy;
using ForgeTrust.AppSurface.Aspire;
using ForgeTrust.AppSurface.Testing;

public class AspireStartupContextTests
{
    [Fact]
    public void Resolve_FirstCallInvokesGenerateAndCachesBuilder()
    {
        var builder = A.Fake<IDistributedApplicationBuilder>();
        var context = new AspireStartupContext(builder, Path.GetTempPath());
        var component = A.Fake<IAspireComponent<IResource>>();
        var resourceBuilder = A.Fake<IResourceBuilder<IResource>>();

        A.CallTo(() => component.Generate(context, builder))
            .Returns(resourceBuilder);

        var first = context.Resolve(component);
        var second = context.Resolve(component);

        Assert.Same(resourceBuilder, first);
        Assert.Same(first, second);
        A.CallTo(() => component.Generate(context, builder)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void Resolve_NewComponentInstanceInvokesGenerate()
    {
        var builder = A.Fake<IDistributedApplicationBuilder>();
        var context = new AspireStartupContext(builder, Path.GetTempPath());
        var firstComponent = A.Fake<IAspireComponent<IResource>>();
        var secondComponent = A.Fake<IAspireComponent<IResource>>();
        var firstResource = A.Fake<IResourceBuilder<IResource>>();
        var secondResource = A.Fake<IResourceBuilder<IResource>>();
        A.CallTo(() => firstComponent.Generate(context, builder)).Returns(firstResource);
        A.CallTo(() => secondComponent.Generate(context, builder)).Returns(secondResource);

        Assert.Same(firstResource, context.Resolve(firstComponent));
        Assert.Same(secondResource, context.Resolve(secondComponent));
        Assert.Same(firstResource, context.Resolve(firstComponent));
        Assert.Same(secondResource, context.Resolve(secondComponent));
        A.CallTo(() => firstComponent.Generate(context, builder)).MustHaveHappenedOnceExactly();
        A.CallTo(() => secondComponent.Generate(context, builder)).MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetPathFromRoot_AppHostCheckoutResolvesBothConsumerAssets(bool gitFile)
    {
        using var tree = new RepositoryTree();
        tree.AddMarker(tree.Root, gitFile);
        var builder = tree.CreateBuilder();
        var context = new AspireStartupContext(builder);

        Assert.False(tree.AppHost.StartsWith(AppContext.BaseDirectory, StringComparison.Ordinal));
        Assert.Equal(tree.Root, context.GetPathFromRoot(string.Empty));
        foreach (var (relativePath, expected) in new[]
                 {
                     (RepositoryTree.AssetPaths[0], Path.Combine(tree.Root, "src/Skoolit.AppHost/keycloak/skoolit-realm.json")),
                     (RepositoryTree.AssetPaths[1], Path.Combine(tree.Root, "src/Skoolit.KeycloakTheme/theme/skoolit"))
                 })
        {
            var actual = context.GetPathFromRoot(relativePath);
            Assert.True(Path.IsPathFullyQualified(actual));
            Assert.Equal(expected, actual);
        }
        Assert.True(File.Exists(context.GetPathFromRoot(RepositoryTree.AssetPaths[0])));
        Assert.True(Directory.Exists(context.GetPathFromRoot(RepositoryTree.AssetPaths[1])));
    }

    [Fact]
    public void GetPathFromRoot_CombineRootWithRelativePath()
    {
        var rootPath = Path.GetTempPath();
        var builder = A.Fake<IDistributedApplicationBuilder>();
        var context = new AspireStartupContext(builder, rootPath);

        var combined = context.GetPathFromRoot("src/appsettings.json");

        Assert.Equal(Path.Combine(rootPath, "src/appsettings.json"), combined);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetPathFromRoot_NearestMarkerIncludesAppHost(bool markerAtAppHost)
    {
        using var tree = new RepositoryTree();
        tree.AddMarker(tree.Root, gitFile: false);
        var nearest = markerAtAppHost ? tree.AppHost : Path.Combine(tree.Root, "src");
        tree.AddMarker(nearest, gitFile: true);

        var context = new AspireStartupContext(tree.CreateBuilder());

        Assert.Equal(nearest, context.GetPathFromRoot(string.Empty));
        Assert.Equal(Path.Combine(nearest, "asset"), context.GetPathFromRoot("asset"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative/apphost")]
    public void Constructor_UnusableAnchorDefersFailureAndAllowsPathFreeResolution(string? anchor)
    {
        var builder = A.Fake<IDistributedApplicationBuilder>();
        A.CallTo(() => builder.AppHostDirectory).Returns(anchor!);
        var context = new AspireStartupContext(builder);
        var component = A.Fake<IAspireComponent<IResource>>();
        var resource = A.Fake<IResourceBuilder<IResource>>();
        A.CallTo(() => component.Generate(context, builder)).Returns(resource);

        Assert.Same(resource, context.Resolve(component));
        Assert.Same(resource, context.Resolve(component));
        A.CallTo(() => component.Generate(context, builder)).MustHaveHappenedOnceExactly();
        AssertMissingRoot(context);
    }

    [Fact]
    public void Constructor_MetadataFreeTreeDefersFailureUntilPathRequest()
    {
        using var tree = new RepositoryTree();
        var builder = tree.CreateBuilder();
        var context = new AspireStartupContext(builder);
        var component = A.Fake<IAspireComponent<IResource>>();
        var resource = A.Fake<IResourceBuilder<IResource>>();
        A.CallTo(() => component.Generate(context, builder)).Returns(resource);

        Assert.Same(resource, context.Resolve(component));
        Assert.Same(resource, context.Resolve(component));
        A.CallTo(() => component.Generate(context, builder)).MustHaveHappenedOnceExactly();
        AssertMissingRoot(context);
        Assert.Throws<InvalidOperationException>(() => context.GetPathFromRoot(Path.GetTempPath()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative")]
    [InlineData("./repository")]
    public void Constructor_InvalidExplicitRootRejectsBeforeReadingAnchor(string root)
    {
        var builder = A.Fake<IDistributedApplicationBuilder>();
        A.CallTo(() => builder.AppHostDirectory).Throws(new InvalidOperationException("Anchor must not be read."));

        var exception = Assert.Throws<ArgumentException>(() => new AspireStartupContext(builder, root));

        Assert.Equal("applicationRoot", exception.ParamName);
        Assert.Contains("fully qualified", exception.Message);
        A.CallTo(() => builder.AppHostDirectory).MustNotHaveHappened();
    }

    [Fact]
    public void Constructor_WindowsPartiallyQualifiedRootsAreRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var root in new[] { "C:repo", @"\repo" })
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                new AspireStartupContext(A.Fake<IDistributedApplicationBuilder>(), root));
            Assert.Equal("applicationRoot", exception.ParamName);
        }
    }

    [Fact]
    public void Constructor_ExplicitRootNormalizesWithoutExistenceOrAnchorAccess()
    {
        using var tree = new RepositoryTree();
        var root = Path.Combine(tree.Root, "unused", "..", "nonexistent");
        var builder = A.Fake<IDistributedApplicationBuilder>();
        A.CallTo(() => builder.AppHostDirectory).Throws(new InvalidOperationException("Anchor must not be read."));

        var context = new AspireStartupContext(builder, root);

        Assert.False(Directory.Exists(Path.GetFullPath(root)));
        Assert.Equal(Path.GetFullPath(root), context.GetPathFromRoot(string.Empty));
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "asset"), context.GetPathFromRoot("asset"));
        A.CallTo(() => builder.AppHostDirectory).MustNotHaveHappened();
    }

    [Fact]
    public void Constructor_ExplicitRootOverridesConflictingDiscoveredMarker()
    {
        using var tree = new RepositoryTree();
        tree.AddMarker(tree.Root, gitFile: true);
        var suppliedRoot = Path.Combine(tree.Root, "caller-layout");

        var context = new AspireStartupContext(tree.CreateBuilder(), suppliedRoot);

        Assert.Equal(suppliedRoot, context.GetPathFromRoot(string.Empty));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetPathFromRoot_NullArgumentPrecedesRootFailure(bool resolved)
    {
        var context = new AspireStartupContext(A.Fake<IDistributedApplicationBuilder>(), resolved ? Path.GetTempPath() : null);

        var exception = Assert.Throws<ArgumentNullException>(() => context.GetPathFromRoot(null!));

        Assert.Equal("relativePath", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../sibling")]
    [InlineData("missing/asset")]
    public void GetPathFromRoot_PreservesCombinationWithoutCheckingExistence(string relativePath)
    {
        using var tree = new RepositoryTree();
        var context = new AspireStartupContext(tree.CreateBuilder(), tree.Root);

        var expected = relativePath switch
        {
            "" => tree.Root,
            "../sibling" => Path.Combine(tree.Root, "../sibling"),
            "missing/asset" => Path.Combine(tree.Root, "missing/asset"),
            _ => throw new InvalidOperationException("Unexpected combination test input.")
        };
        Assert.Equal(expected, context.GetPathFromRoot(relativePath));
    }

    [Fact]
    public void GetPathFromRoot_RootedInputPreservesPathCombine()
    {
        using var tree = new RepositoryTree();
        var context = new AspireStartupContext(tree.CreateBuilder(), tree.Root);
        var rootedInput = Path.Combine(Path.GetTempPath(), "elsewhere");

        Assert.Equal(Path.Combine(tree.Root, rootedInput), context.GetPathFromRoot(rootedInput));
    }

    [Fact]
    public void Constructor_ResolvedRootSnapshotSurvivesMarkerRemoval()
    {
        using var tree = new RepositoryTree();
        tree.AddMarker(tree.Root, gitFile: true);
        var builder = tree.CreateBuilder();
        var original = new AspireStartupContext(builder);
        File.Delete(Path.Combine(tree.Root, ".git"));

        Assert.Equal(tree.Root, original.GetPathFromRoot(string.Empty));
        Assert.Equal(Path.Combine(tree.Root, "asset"), original.GetPathFromRoot("asset"));
        AssertMissingRoot(new AspireStartupContext(builder));
    }

    [Fact]
    public void Constructor_ResolvedRootSnapshotIgnoresNewNearerMarker()
    {
        using var tree = new RepositoryTree();
        tree.AddMarker(tree.Root, gitFile: false);
        var builder = tree.CreateBuilder();
        var original = new AspireStartupContext(builder);
        tree.AddMarker(tree.AppHost, gitFile: true);

        Assert.Equal(tree.Root, original.GetPathFromRoot(string.Empty));
        Assert.Equal(Path.Combine(tree.Root, "asset"), original.GetPathFromRoot("asset"));
        Assert.Equal(tree.AppHost, new AspireStartupContext(builder).GetPathFromRoot(string.Empty));
    }

    [Fact]
    public void Constructor_UnresolvedRootSnapshotIgnoresNewMarker()
    {
        using var tree = new RepositoryTree();
        var builder = tree.CreateBuilder();
        var original = new AspireStartupContext(builder);
        tree.AddMarker(tree.Root, gitFile: true);

        AssertMissingRoot(original);
        Assert.Equal(tree.Root, new AspireStartupContext(builder).GetPathFromRoot(string.Empty));
    }

    private static void AssertMissingRoot(AspireStartupContext context)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => context.GetPathFromRoot("asset"));
        Assert.Contains("AppHostDirectory", exception.Message);
        Assert.Contains("explicit absolute applicationRoot", exception.Message);
        Assert.Contains("README.md#repository-relative-assets", exception.Message);
    }

    private sealed class RepositoryTree : IDisposable
    {
        public static readonly string[] AssetPaths =
        [
            "src/Skoolit.AppHost/keycloak/skoolit-realm.json",
            "src/Skoolit.KeycloakTheme/theme/skoolit"
        ];

        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"appsurface-context-{Guid.NewGuid():N}"));
        public string AppHost => Path.Combine(Root, "src", "Skoolit.AppHost");

        public RepositoryTree()
        {
            Directory.CreateDirectory(Path.Combine(AppHost, "keycloak"));
            File.WriteAllText(TestPathUtils.PathUnder(Root, AssetPaths[0]), "{}");
            Directory.CreateDirectory(TestPathUtils.PathUnder(Root, AssetPaths[1]));
        }

        public void AddMarker(string directory, bool gitFile)
        {
            var marker = Path.Combine(directory, ".git");
            if (gitFile)
            {
                File.WriteAllText(marker, "Marker contents are intentionally not parsed.");
            }
            else
            {
                Directory.CreateDirectory(marker);
            }
        }

        public IDistributedApplicationBuilder CreateBuilder()
        {
            var builder = A.Fake<IDistributedApplicationBuilder>();
            A.CallTo(() => builder.AppHostDirectory).Returns(AppHost);
            return builder;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
