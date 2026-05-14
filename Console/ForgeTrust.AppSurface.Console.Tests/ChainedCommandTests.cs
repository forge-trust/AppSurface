using CliFx.Exceptions;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Console.Tests;

[Collection(CommandServiceStateCollection.Name)]
public class ChainedCommandTests
{
    private static IServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();

        // This is kinda gross, we can probably make this easier.
        var context = new StartupContext([], new DummyModule());
        new SharedTestCommandsModule().ConfigureServices(context, services);

        CommandService.PrimaryServiceProvider = services.BuildServiceProvider();

        return CommandService.PrimaryServiceProvider;
    }

    [Fact]
    public async Task ExecutesAllChildCommands_WithWiredParameters()
    {
        var provider = CreateProvider();
        var tracker = provider.GetRequiredService<ExecutionTracker>();
        var cmd = provider.GetRequiredService<CompositeCommand>();

        cmd.Foo = "foo";
        cmd.Bar = "bar";

        await cmd.ExecuteAsync(new FakeConsole());

        Assert.True(tracker.FirstExecuted);
        Assert.Equal("foo", tracker.FirstFoo);
        Assert.True(tracker.SecondExecuted);
        Assert.Equal("bar", tracker.SecondBar);
    }

    [Fact]
    public async Task MissingRequiredParameter_ThrowsBeforeExecution()
    {
        var provider = CreateProvider();
        var tracker = provider.GetRequiredService<ExecutionTracker>();
        var cmd = provider.GetRequiredService<CompositeCommand>();

        await Assert.ThrowsAsync<CommandException>(async () => await cmd.ExecuteAsync(new FakeConsole()));
        Assert.False(tracker.FirstExecuted);
        Assert.False(tracker.SecondExecuted);
    }

    [Fact]
    public async Task MissingRequiredCommandParameter_ThrowsBeforeExecution()
    {
        var provider = CreateProvider();
        var tracker = provider.GetRequiredService<ExecutionTracker>();
        var cmd = provider.GetRequiredService<CompositeWithParamCommand>();

        await Assert.ThrowsAsync<CommandException>(async () => await cmd.ExecuteAsync(new FakeConsole()));
        Assert.False(tracker.ThirdExecuted);
    }

    [Fact]
    public async Task ExecutesAllChildCommands_WithWiredCommandParameters()
    {
        var provider = CreateProvider();
        var tracker = provider.GetRequiredService<ExecutionTracker>();
        var cmd = provider.GetRequiredService<CompositeWithParamCommand>();

        cmd.Baz = "baz";

        await cmd.ExecuteAsync(new FakeConsole());

        Assert.True(tracker.ThirdExecuted);
        Assert.Equal("baz", tracker.ThirdBaz);
    }

    [Fact]
    public async Task ConditionalCommands_SkipWhenPredicateFalse()
    {
        var provider = CreateProvider();
        var tracker = provider.GetRequiredService<ExecutionTracker>();
        var cmd = provider.GetRequiredService<ConditionalCompositeCommand>();

        cmd.Bar = "bar";
        cmd.RunFirst = false;

        await cmd.ExecuteAsync(new FakeConsole());

        Assert.False(tracker.FirstExecuted);
        Assert.True(tracker.SecondExecuted);
        Assert.Equal("bar", tracker.SecondBar);
    }

    [Fact]
    public async Task ConditionalCommands_ExecuteWhenPredicateTrue()
    {
        var provider = CreateProvider();
        var tracker = provider.GetRequiredService<ExecutionTracker>();
        var cmd = provider.GetRequiredService<ConditionalCompositeCommand>();

        cmd.RunFirst = true;
        cmd.Foo = "foo";
        cmd.Bar = "bar";

        await cmd.ExecuteAsync(new FakeConsole());

        Assert.True(tracker.FirstExecuted);
        Assert.Equal("foo", tracker.FirstFoo);
        Assert.True(tracker.SecondExecuted);
        Assert.Equal("bar", tracker.SecondBar);
    }

    private class DummyModule : IAppSurfaceHostModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {

        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {

        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {

        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {

        }
    }
}
