using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Console.Tests;

public class ConsoleOptionsTests
{
    [Fact]
    public void Default_ReturnsNewMutableInstance()
    {
        var first = ConsoleOptions.Default;
        var second = ConsoleOptions.Default;

        first.OutputMode = ConsoleOutputMode.CommandFirst;
        first.CustomRegistrations.Add(_ => { });

        Assert.NotSame(first, second);
        Assert.Equal(ConsoleOutputMode.Default, second.OutputMode);
        Assert.Empty(second.CustomRegistrations);
    }
}
