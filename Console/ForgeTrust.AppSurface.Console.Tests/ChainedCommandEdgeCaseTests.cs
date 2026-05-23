using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Console;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Console.Tests;

[Collection(CommandServiceStateCollection.Name)]
public partial class ChainedCommandEdgeCaseTests
{
    private static bool _requiredChildExecuted;
    private static bool _typeMismatchChildExecuted;
    private static int _typeMismatchChildValue;
    private static string? _typeMismatchChildMetadata;
    private static bool _nullableChildExecuted;
    private static int? _nullableChildValue;
    private static bool _requiredNonInputChildExecuted;

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

    [Fact]
    public async Task ExecuteAsync_WhenRequiredChildMemberIsNotCliInput_DoesNotValidateIt()
    {
        // Arrange
        ResetTracker();
        var (command, provider) = CreateCommand<RequiredNonInputCompositeCommand>(
            services =>
            {
                services.AddTransient<RequiredNonInputCompositeCommand>();
                services.AddTransient<RequiredNonInputChildCommand>();
            });
        using (provider)
        {
            // Act
            await command.ExecuteAsync(new FakeConsole());

            // Assert
            Assert.True(_requiredNonInputChildExecuted);
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
        _requiredNonInputChildExecuted = false;
    }

    [Command("edge-required-child")]
    public sealed partial class RequiredChildCommand : ICommand
    {
        [CommandOption("required")]
        public required string? Required { get; set; }

        public ValueTask ExecuteAsync(IConsole console)
        {
            _requiredChildExecuted = true;
            return default;
        }
    }

    [Command("edge-missing-parent-composite")]
    public sealed partial class MissingParentCompositeCommand : ChainedCommand
    {
        protected override void Configure(CommandChainBuilder builder)
        {
            builder.Add<RequiredChildCommand>();
        }
    }

    [Command("edge-type-mismatch-child")]
    public sealed partial class TypeMismatchChildCommand : ICommand
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
    public sealed partial class TypeMismatchCompositeCommand : ChainedCommand
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
    public sealed partial class NullableChildCommand : ICommand
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
    public sealed partial class NullableParentCompositeCommand : ChainedCommand
    {
        [CommandOption("count")]
        public int? Count { get; set; }

        protected override void Configure(CommandChainBuilder builder)
        {
            builder.Add<NullableChildCommand>();
        }
    }

    [Command("edge-empty-chain")]
    public sealed partial class EmptyChainCompositeCommand : ChainedCommand
    {
        protected override void Configure(CommandChainBuilder builder)
        {
            builder.AddIf<RequiredChildCommand>(() => false);
        }
    }

    [Command("edge-required-non-input-child")]
    public sealed partial class RequiredNonInputChildCommand : ICommand
    {
        public required string RuntimeValue { get; set; }

        public ValueTask ExecuteAsync(IConsole console)
        {
            _requiredNonInputChildExecuted = true;
            return default;
        }
    }

    [Command("edge-required-non-input")]
    public sealed partial class RequiredNonInputCompositeCommand : ChainedCommand
    {
        protected override void Configure(CommandChainBuilder builder)
        {
            builder.Add<RequiredNonInputChildCommand>();
        }
    }
}
