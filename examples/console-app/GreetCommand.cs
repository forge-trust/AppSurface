using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Config;

namespace ConsoleAppExample;

[Command("greet", Description = "Prints a greeting message.")]
public class GreetCommand : ICommand
{
    private readonly FooConfig _foo;
    private readonly BarConfig _bar;

    [CommandParameter(0, Description = "The name to greet.")] public string Name { get; set; } = string.Empty;

    public GreetCommand(FooConfig foo, BarConfig bar)
    {
        _foo = foo;
        _bar = bar;
    }

    public ValueTask ExecuteAsync(IConsole console)
    {
        if (!_foo.IsDefaultValue)
        {
            console.Output.WriteLine($"Holy batman, {Name}! TIL: Foo = {_foo.Value}");
        }
        else
        {
            console.Output.WriteLine($"Hello, {Name}!");
        }

        if (_bar.Value is { } bar)
        {
            console.Output.WriteLine($"Bar says: {bar.Message} {bar.Overage}");
        }

        return default;
    }
}

public class FooConfig : Config<string>
{
    public override string? DefaultValue { get; } = "Just a string";
}

public record Bar(string Message, int Overage);

public class BarConfig : Config<Bar>
{
    public override Bar? DefaultValue { get; } = new(Message: "The universal answer is", Overage: 42);
}
