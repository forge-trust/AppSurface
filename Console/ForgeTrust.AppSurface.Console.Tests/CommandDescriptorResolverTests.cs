using CliFx.Binding;

namespace ForgeTrust.AppSurface.Console.Tests;

public class CommandDescriptorResolverTests
{
    [Fact]
    public void GetRequiredDescriptor_WithGeneratedCommand_ReturnsDescriptor()
    {
        var descriptor = CommandDescriptorResolver.GetRequiredDescriptor(typeof(FirstCommand));

        var option = Assert.IsAssignableFrom<CommandOptionDescriptor>(
            Assert.Single(descriptor.Inputs.OfType<CommandOptionDescriptor>(), input => input.Name == "foo"));

        Assert.Equal(typeof(FirstCommand), descriptor.Type);
        Assert.Equal("first", descriptor.Name);
        Assert.Equal("foo", option.Name);
        Assert.True(option.IsRequired);
    }

    [Fact]
    public void GetRequiredDescriptor_WithoutGeneratedDescriptor_ThrowsActionableMessage()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CommandDescriptorResolver.GetRequiredDescriptor(typeof(MissingDescriptorCommand)));

        Assert.Contains(nameof(MissingDescriptorCommand), exception.Message);
        Assert.Contains("partial", exception.Message);
        Assert.Contains("[Command]", exception.Message);
    }

    [Fact]
    public void TryGetDescriptor_WithoutGeneratedDescriptor_ReturnsNull()
    {
        Assert.Null(CommandDescriptorResolver.TryGetDescriptor(typeof(MissingDescriptorCommand)));
    }

    private sealed class MissingDescriptorCommand
    {
    }
}
