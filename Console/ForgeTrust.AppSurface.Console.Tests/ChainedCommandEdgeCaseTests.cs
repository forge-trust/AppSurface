using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Console;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Console.Tests;

[Collection(CommandServiceStateCollection.Name)]
public class ChainedCommandEdgeCaseTests
{
    private static bool _requiredChildExecuted;
    private static bool _typeMismatchChildExecuted;
    private static int _typeMismatchChildValue;
    private static string? _typeMismatchChildMetadata;
    private static bool _nullableChildExecuted;
    private static int? _nullableChildValue;

    [Fact]
    public async Task ExecuteAsync_WhenRequiredChildOptionIsMissingOnParent_ThrowsBeforeExecution()
    {
        // Arrange
        ResetTracker();
        var (command, provider) = CreateCommand<MissingParentCompositeCommand>(
            services =>
            {
                services.AddTransient<MissingParentCompositeCommand>();
                services.AddTransient<RequiredChildCommand>();
            });
        using (provider)
        {
            // Act
            var exception = await Assert.ThrowsAsync<CommandException>(async () =>
                await command.ExecuteAsync(new FakeConsole()));

            // Assert
            Assert.Contains("RequiredChildCommand.Required", exception.Message);
            Assert.False(_requiredChildExecuted);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenPropertyTypesDoNotMatch_DoesNotWireValues()
    {
        // Arrange
        ResetTracker();
        var (command, provider) = CreateCommand<TypeMismatchCompositeCommand>(
            services =>
            {
                services.AddTransient<TypeMismatchCompositeCommand>();
                services.AddTransient<TypeMismatchChildCommand>();
            });
        using (provider)
        {
            command.Value = "42";
            command.Metadata = "parent-metadata";

            // Act
            await command.ExecuteAsync(new FakeConsole());

            // Assert
            Assert.True(_typeMismatchChildExecuted);
            Assert.Equal(0, _typeMismatchChildValue);
            Assert.Null(_typeMismatchChildMetadata);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithNullParentValue_KeepsChildDefaultValue()
    {
        // Arrange
        ResetTracker();
        var (command, provider) = CreateCommand<NullableParentCompositeCommand>(
            services =>
            {
                services.AddTransient<NullableParentCompositeCommand>();
                services.AddTransient<NullableChildCommand>();
            });
        using (provider)
        {
            // Act
            await command.ExecuteAsync(new FakeConsole());

            // Assert
            Assert.True(_nullableChildExecuted);
            Assert.Equal(7, _nullableChildValue);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithNoExecutableChildren_CompletesSuccessfully()
    {
        // Arrange
        ResetTracker();
        var (command, provider) = CreateCommand<EmptyChainCompositeCommand>(
            services =>
            {
                services.AddTransient<EmptyChainCompositeCommand>();
                services.AddTransient<RequiredChildCommand>();
            });
        using (provider)
        {
            // Act
            await command.ExecuteAsync(new FakeConsole());

            // Assert
            Assert.False(_requiredChildExecuted);
        }
    }

    private static (TCommand Command, ServiceProvider Provider) CreateCommand<TCommand>(
        Action<IServiceCollection> registerCommands)
        where TCommand : class, ICommand
    {
        var services = new ServiceCollection();
        registerCommands(services);

        var provider = services.BuildServiceProvider();
        CommandService.PrimaryServiceProvider = provider;

        return (provider.GetRequiredService<TCommand>(), provider);
    }

    private static void ResetTracker()
    {
        _requiredChildExecuted = false;
        _typeMismatchChildExecuted = false;
        _typeMismatchChildValue = 0;
        _typeMismatchChildMetadata = null;
        _nullableChildExecuted = false;
        _nullableChildValue = null;
    }

    [Command("edge-required-child")]
    public sealed class RequiredChildCommand : ICommand
    {
        [CommandOption("required", IsRequired = true)]
        public string? Required { get; set; }

        public ValueTask ExecuteAsync(IConsole console)
        {
            _requiredChildExecuted = true;
            return default;
        }
    }

    [Command("edge-missing-parent-composite")]
    public sealed class MissingParentCompositeCommand : ChainedCommand
    {
        protected override void Configure(CommandChainBuilder builder)
        {
            builder.Add<RequiredChildCommand>();
        }
    }

    [Command("edge-type-mismatch-child")]
    public sealed class TypeMismatchChildCommand : ICommand
    {
        [CommandOption("value")]
        public int Value { get; set; }

        // This property intentionally has no [CommandOption] attribute to verify
        // it is not bound by CliFx.
        public string? Metadata { get; set; }

        public ValueTask ExecuteAsync(IConsole console)
        {
            _typeMismatchChildExecuted = true;
            _typeMismatchChildValue = Value;
            _typeMismatchChildMetadata = Metadata;
            return default;
        }
    }

    [Command("edge-type-mismatch-composite")]
    public sealed class TypeMismatchCompositeCommand : ChainedCommand
    {
        [CommandOption("value")]
        public string? Value { get; set; }

        [CommandOption("metadata")]
        public string? Metadata { get; set; }

        protected override void Configure(CommandChainBuilder builder)
        {
            builder.Add<TypeMismatchChildCommand>();
        }
    }

    [Command("edge-nullable-child")]
    public sealed class NullableChildCommand : ICommand
    {
        [CommandOption("count")]
        public int? Count { get; set; } = 7;

        public ValueTask ExecuteAsync(IConsole console)
        {
            _nullableChildExecuted = true;
            _nullableChildValue = Count;
            return default;
        }
    }

    [Command("edge-nullable-parent")]
    public sealed class NullableParentCompositeCommand : ChainedCommand
    {
        [CommandOption("count")]
        public int? Count { get; set; }

        protected override void Configure(CommandChainBuilder builder)
        {
            builder.Add<NullableChildCommand>();
        }
    }

    [Command("edge-empty-chain")]
    public sealed class EmptyChainCompositeCommand : ChainedCommand
    {
        protected override void Configure(CommandChainBuilder builder)
        {
            builder.AddIf<RequiredChildCommand>(() => false);
        }
    }
}
