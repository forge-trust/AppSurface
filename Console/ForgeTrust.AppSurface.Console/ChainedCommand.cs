using System.Reflection;
using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Console;

/// <summary>
///     A command that can execute a sequence of other commands.
///     Parameters and options defined on the parent command are
///     automatically forwarded to the child commands when they share
///     the same property name. Required parameters of child commands
///     are validated before any command in the chain is executed.
/// </summary>
public abstract class ChainedCommand : ICommand
{
    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IConsole console)
    {
        var builder = new CommandChainBuilder();
        Configure(builder);
        var commandInfos = builder.Build();
        var executable = commandInfos.Where(c => c.ShouldExecute()).ToList();
        var serviceProvider = CommandService.PrimaryServiceProvider;
        ArgumentNullException.ThrowIfNull(serviceProvider);

        ValidateRequiredParameters(executable);

        foreach (var info in executable)
        {
            var command = (ICommand)serviceProvider.GetRequiredService(info.CommandType);
            WireParameters(command);
            await command.ExecuteAsync(console);
        }
    }

    /// <summary>
    ///     Configures the sequence of commands to execute.
    /// </summary>
    /// <param name="builder">Fluent builder to define the chain.</param>
    protected abstract void Configure(CommandChainBuilder builder);

    private void ValidateRequiredParameters(IEnumerable<CommandInfo> commandInfos)
    {
        var missing = new List<string>();

        foreach (var info in commandInfos)
        {
            var type = info.CommandType;
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var option = prop.GetCustomAttribute<CommandOptionAttribute>();
                var parameter = prop.GetCustomAttribute<CommandParameterAttribute>();

                var isRequired = option?.IsRequired ?? parameter?.IsRequired ?? false;
                if (!isRequired)
                {
                    continue;
                }

                var parentProp = GetType().GetProperty(prop.Name);
                if (parentProp == null || parentProp.GetValue(this) == null)
                {
                    missing.Add($"{type.Name}.{prop.Name}");
                }
            }
        }

        if (missing.Count > 0)
        {
            throw new CommandException($"Missing required parameters/options: {string.Join(", ", missing)}");
        }
    }

    private void WireParameters(ICommand command)
    {
        var parentProps = GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);

        foreach (var childProp in command.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var option = childProp.GetCustomAttribute<CommandOptionAttribute>();
            var parameter = childProp.GetCustomAttribute<CommandParameterAttribute>();

            if (option == null && parameter == null)
            {
                continue;
            }

            var parentProp =
                parentProps.FirstOrDefault(p => p.Name == childProp.Name
                                                && p.PropertyType == childProp.PropertyType);

            if (parentProp == null)
            {
                continue;
            }

            var value = parentProp.GetValue(this);
            if (value == null)
            {
                continue;
            }

            childProp.SetValue(command, value);
        }
    }

    /// <summary>
    ///     Fluent builder used to configure the chain of commands.
    /// </summary>
    protected sealed class CommandChainBuilder
    {
        private readonly List<CommandInfo> _commandTypes = new();

        /// <summary>
        ///     Adds a command to the execution chain.
        /// </summary>
        public CommandChainBuilder Add<TCommand>() where TCommand : ICommand => AddIf<TCommand>(() => true);

        /// <summary>
        ///     Adds a command to the chain that will execute only when <paramref name="condition" /> returns true.
        /// </summary>
        public CommandChainBuilder AddIf<TCommand>(Func<bool> condition) where TCommand : ICommand
        {
            _commandTypes.Add(new CommandInfo(typeof(TCommand), condition));

            return this;
        }

        internal IReadOnlyList<CommandInfo> Build() => _commandTypes;
    }

    internal sealed record CommandInfo(Type CommandType, Func<bool> ShouldExecute);
}
