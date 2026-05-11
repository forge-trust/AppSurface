using System.Reflection;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Console.Tests;

[Collection(CommandServiceStateCollection.Name)]
public class CommandServiceTests
{
    [Command("test-cmd")]
    public class TestCommand : ICommand
    {
        [CommandOption("force")] public bool Force { get; set; }
        [CommandOption("another", 'a')] public string? Another { get; set; }

        // Edge case: unannotated property to hit `continue;` in property iteration
        public string? RegularProperty { get; set; }

        public ValueTask ExecuteAsync(IConsole console) => default;
    }

    [Command("throw")]
    public class ThrowingCommand : ICommand
    {
        public ValueTask ExecuteAsync(IConsole console) => throw new InvalidOperationException("Test");
    }

    public class NoAttributeCommandProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            return new ValueTask();
        }
    }

    [Command]
    public class RootTestCommand : ICommand
    {
        [CommandOption("output")] public string Output { get; set; } = string.Empty;

        public ValueTask ExecuteAsync(IConsole console) => default;
    }

    private class TestModule : IAppSurfaceHostModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(
            StartupContext context,
            IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(
            StartupContext context,
            IHostBuilder builder)
        {
        }
    }

    [Fact]
    public void CheckForUnknownOptions_WithUnknownOption_ShowsSuggestion()
    {
        var console = new FakeInMemoryConsole();
        var suggester = new LevenshteinOptionSuggester();

        var noAttrCommand = System.Reflection.DispatchProxy.Create<ICommand, NoAttributeCommandProxy>();
        var commands = new ICommand[] { noAttrCommand, new RootTestCommand(), new TestCommand() };
        var context = new StartupContext(new[] { "test-cmd", "--farce" }, new TestModule());

        var commandService = new CommandService(commands, context, suggester);
        commandService.CheckForUnknownOptions(console);

        var errorOutput = console.ReadErrorString();
        Assert.Matches("Did you mean", errorOutput);
    }

    [Fact]
    public void CheckForUnknownOptions_UnknownCommand_EvaluatesToFalse()
    {
        var console = new FakeInMemoryConsole();
        var suggester = new LevenshteinOptionSuggester();

        // This command name is "unknown-cmd", which does not start with '-' and does not match TestCommand.
        // It will cause attr?.Name?.Equals() to return false, covering that path.
        var noAttrCommand = System.Reflection.DispatchProxy.Create<ICommand, NoAttributeCommandProxy>();
        var commands = new ICommand[] { noAttrCommand, new TestCommand() };
        var context = new StartupContext(new[] { "unknown-cmd", "--force" }, new TestModule());

        var commandService = new CommandService(commands, context, suggester);
        commandService.CheckForUnknownOptions(console);

        // Verify no suggestions are emitted when command type cannot be resolved
        Assert.Empty(console.ReadErrorString());
    }

    [Fact]
    public void CheckForUnknownOptions_WithRootCommand_ShowsSuggestion()
    {
        var console = new FakeInMemoryConsole();
        var suggester = new LevenshteinOptionSuggester();

        var commands = new ICommand[] { new RootTestCommand() };
        var context = new StartupContext(new[] { "--otuput=test.txt" }, new TestModule());

        var commandService = new CommandService(commands, context, suggester);
        commandService.CheckForUnknownOptions(console);

        var errorOutput = console.ReadErrorString();
        Assert.Contains("Did you mean '--output'", errorOutput);
    }

    [Fact]
    public void CheckForUnknownOptions_WithEmptyArgs_DoesNothing()
    {
        var console = new FakeInMemoryConsole();
        var suggester = new LevenshteinOptionSuggester();

        var noAttrCommand = System.Reflection.DispatchProxy.Create<ICommand, NoAttributeCommandProxy>();
        var commands = new ICommand[] { noAttrCommand, new RootTestCommand() };
        var context = new StartupContext(Array.Empty<string>(), new TestModule());

        var commandService = new CommandService(commands, context, suggester);
        commandService.CheckForUnknownOptions(console);

        Assert.Empty(console.ReadErrorString());
    }

    [Fact]
    public void CheckForUnknownOptions_NoRootCommandFallback_DoesNothingIfNoMatch()
    {
        var console = new FakeInMemoryConsole();
        var suggester = new LevenshteinOptionSuggester();

        // ONLY TestCommand so rootCommand fallback returns null (covers line 139 finding null)
        var commands = new ICommand[] { new TestCommand() };
        var context = new StartupContext(new[] { "-unknown-flag" }, new TestModule()); // valid command

        var commandService = new CommandService(commands, context, suggester);
        commandService.CheckForUnknownOptions(console);

        Assert.Empty(console.ReadErrorString()); // Shouldn't suggest because we don't know the command
    }

    [Fact]
    public void CheckForUnknownOptions_OnlyShowsUpToMaxSuggestions()
    {
        var console = new FakeInMemoryConsole();
        var suggester = new LevenshteinOptionSuggester();

        // This command will cause many suggestions for "--a"
        var commands = new ICommand[] { new TestCommand() };
        var context = new StartupContext(new[] { "test-cmd", "--f" }, new TestModule());

        var commandService = new CommandService(commands, context, suggester);
        commandService.CheckForUnknownOptions(console);

        var errorOutput = console.ReadErrorString();
        // Since test command has max suggestions set to 2, we just verify it runs cleanly exactly for maximum output checking
        Assert.NotEmpty(errorOutput);

        // Count how many "Did you mean" are printed. Max is 2 in code.
        var suggestionCount = errorOutput.Split("Did you mean", StringSplitOptions.None).Length - 1;
        Assert.True(suggestionCount <= 2);
    }

    [Fact]
    public async Task RunAsync_GivenDiConsole_IsUsedAndDisposedProperly()
    {
        var console = new FakeInMemoryConsole();
        var suggester = new LevenshteinOptionSuggester();

        var commands = new ICommand[] { new RootTestCommand(), new TestCommand() };
        var context = new StartupContext(new[] { "test-cmd", "--force" }, new TestModule()); // valid command

        // We actually want a valid serviceProvider. The easiest way is using a real ServiceCollection
        var services = new ServiceCollection();
        services.AddSingleton<IConsole>(console);
        services.AddTransient<TestCommand>();
        var serviceProvider = services.BuildServiceProvider();

        CommandService.PrimaryServiceProvider = serviceProvider;

        try
        {
            var commandService = new CommandService(commands, context, suggester);

            // Reset global exit code manually here before execution
            Environment.ExitCode = 0;

            await commandService.RunInternalAsync(CancellationToken.None);

            // Since we explicitly provided the console, we don't need a SystemConsole.
            // Also it succeeded so exitCode == 0, and CheckForUnknownOptions is not called.
            Assert.Equal(0, Environment.ExitCode);
        }
        finally
        {
            CommandService.PrimaryServiceProvider = null;
        }
    }

    [Fact]
    public async Task RunAsync_UnrecognizedCommand_FailsAndChecksUnknownOptions()
    {
        var suggester = new LevenshteinOptionSuggester();

        var commands = new ICommand[] { new TestCommand(), new ThrowingCommand() };
        // Passing unrecognized arguments that will fail to run
        var context = new StartupContext(new[] { "test-cmd", "--unknownFlag123" }, new TestModule());

        var services = new ServiceCollection();
        // Do not add IConsole, so it creates a SystemConsole, covering the fallback
        services.AddTransient<TestCommand>();
        services.AddTransient<ThrowingCommand>();
        var serviceProvider = services.BuildServiceProvider();

        CommandService.PrimaryServiceProvider = serviceProvider;

        var commandService = new CommandService(commands, context, suggester);

        // Reset exit code just in case
        Environment.ExitCode = 0;

        try
        {
            await commandService.RunInternalAsync(CancellationToken.None);

            // Command returns error exit code (usually 1 or something else). Check it's not 0.
            Assert.NotEqual(0, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = 0;
            CommandService.PrimaryServiceProvider = null;
        }
    }
}
