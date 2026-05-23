using CliFx.Binding;

namespace ForgeTrust.AppSurface.Console;

internal static class CommandDescriptorResolver
{
    private const string DescriptorPropertyName = "Descriptor";

    internal static CommandDescriptor GetRequiredDescriptor(Type commandType)
    {
        return TryGetDescriptor(commandType) ??
               throw new InvalidOperationException(
                   $"The command type '{commandType.FullName}' does not expose CliFx 3 generated metadata. " +
                   "Make the command class partial, make any enclosing command classes partial, and ensure it has a CliFx [Command] attribute.");
    }

    internal static CommandDescriptor? TryGetDescriptor(Type commandType)
    {
        var descriptorProperty = commandType.GetProperty(
            DescriptorPropertyName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        if (descriptorProperty == null ||
            !typeof(CommandDescriptor).IsAssignableFrom(descriptorProperty.PropertyType))
        {
            return null;
        }

        return descriptorProperty.GetValue(null) as CommandDescriptor;
    }
}
