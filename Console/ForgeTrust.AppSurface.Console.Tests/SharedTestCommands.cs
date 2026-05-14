using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Console.Tests;


// All commands on an assembly are currently shared across all test classes
[Command("first")]
public class FirstCommand : ICommand
{
    private readonly ExecutionTracker _tracker;

    public FirstCommand(ExecutionTracker tracker)
    {
        _tracker = tracker;
    }

    [CommandOption("foo", IsRequired = true)]
    public string? Foo { get; set; }

    public ValueTask ExecuteAsync(IConsole console)
    {
        _tracker.FirstExecuted = true;
        _tracker.FirstFoo = Foo;

        return default;
    }
}

[Command("second")]
public class SecondCommand : ICommand
{
    private readonly ExecutionTracker _tracker;

    public SecondCommand(ExecutionTracker tracker)
    {
        _tracker = tracker;
    }

    [CommandOption("bar")]
    public string? Bar { get; set; }

    public ValueTask ExecuteAsync(IConsole console)
    {
        _tracker.SecondExecuted = true;
        _tracker.SecondBar = Bar;

        return default;
    }
}

[Command("composite")]
public class CompositeCommand : ChainedCommand
{
    [CommandOption("foo", IsRequired = true)]
    public string? Foo { get; set; }

    [CommandOption("bar")]
    public string? Bar { get; set; }

    protected override void Configure(CommandChainBuilder builder) =>
        builder
            .Add<FirstCommand>()
            .Add<SecondCommand>();
}

[Command("conditional")]
public class ConditionalCompositeCommand : ChainedCommand
{
    [CommandOption("foo")]
    public string? Foo { get; set; }

    [CommandOption("bar")]
    public string? Bar { get; set; }

    // This flag is meant to be set programmatically in tests only; not exposed as a CLI option.
    public bool RunFirst { get; set; }

    protected override void Configure(CommandChainBuilder builder) =>
        builder
            .AddIf<FirstCommand>(() => RunFirst)
            .Add<SecondCommand>();
}

[Command("third")]
public class ThirdCommand : ICommand
{
    private readonly ExecutionTracker _tracker;

    public ThirdCommand(ExecutionTracker tracker)
    {
        _tracker = tracker;
    }

    [CommandParameter(0, IsRequired = true)]
    public string? Baz { get; set; }

    public ValueTask ExecuteAsync(IConsole console)
    {
        _tracker.ThirdExecuted = true;
        _tracker.ThirdBaz = Baz;

        return default;
    }
}

[Command("composite-with-param")]
public class CompositeWithParamCommand : ChainedCommand
{
    [CommandParameter(0, IsRequired = true)]
    public string? Baz { get; set; }

    protected override void Configure(CommandChainBuilder builder) =>
        builder.Add<ThirdCommand>();
}

// These are specifically to provide coverage for `ConsoleStartup.GetCommandTypes()`.
// `t.IsAbstract` == true is covered by AbstractCommand.
// `t.IsClass` == false is covered by ITestInterfaceCommand and TestStructCommand.
public abstract class AbstractCommand : ICommand
{
    public abstract ValueTask ExecuteAsync(IConsole console);
}

public interface ITestInterfaceCommand : ICommand
{
}

public struct TestStructCommand : ICommand
{
    public ValueTask ExecuteAsync(IConsole console) => default;
}


public class SharedTestCommandsModule : IAppSurfaceHostModule
{
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton<ExecutionTracker>();
        // Registering these allows direct execution of the commands for testing.
        services.AddTransient<FirstCommand>();
        services.AddTransient<SecondCommand>();
        services.AddTransient<ThirdCommand>();
        services.AddTransient<CompositeCommand>();
        services.AddTransient<ConditionalCompositeCommand>();
        services.AddTransient<CompositeWithParamCommand>();
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

public class ExecutionTracker
{
    public bool FirstExecuted { get; set; }
    public string? FirstFoo { get; set; }

    public bool SecondExecuted { get; set; }
    public string? SecondBar { get; set; }

    public bool ThirdExecuted { get; set; }
    public string? ThirdBaz { get; set; }
}
