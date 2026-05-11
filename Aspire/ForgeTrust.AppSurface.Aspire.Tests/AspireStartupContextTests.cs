using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using FakeItEasy;
using ForgeTrust.AppSurface.Aspire;

public class AspireStartupContextTests
{
    [Fact]
    public void Resolve_FirstCallInvokesGenerateAndCachesBuilder()
    {
        var builder = A.Fake<IDistributedApplicationBuilder>();
        var context = new AspireStartupContext(builder, "/app");
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
        var context = new AspireStartupContext(builder, "/app");
        var component = A.Fake<IAspireComponent<IResource>>();
        var resourceBuilder = A.Fake<IResourceBuilder<IResource>>();

        A.CallTo(() => component.Generate(context, builder))
            .Returns(resourceBuilder);

        var result = context.Resolve(component);

        Assert.Same(resourceBuilder, result);
        A.CallTo(() => component.Generate(context, builder)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void GetPathFromRoot_CombineRootWithRelativePath()
    {
        var rootPath = "/repo";
        var builder = A.Fake<IDistributedApplicationBuilder>();
        var context = new AspireStartupContext(builder, rootPath);

        var combined = context.GetPathFromRoot("src/appsettings.json");

        Assert.Equal(Path.Combine(rootPath, "src/appsettings.json"), combined);
    }
}
