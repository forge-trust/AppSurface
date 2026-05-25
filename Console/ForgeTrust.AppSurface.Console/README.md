# ForgeTrust.AppSurface.Console

Modular bootstrapping for .NET Console applications using [CliFx](https://github.com/Tyrrrz/CliFx).

## Overview

`ForgeTrust.AppSurface.Console` provides a structured way to build command-line tools. It automatically discovers CliFx commands from modules, registers their source-generated CliFx descriptors, runs them inside the .NET Generic Host, and exposes a startup options seam for console-specific behavior such as command-first output.

## Release Guidance

AppSurface is preparing the first coordinated `v0.1.0` release. Before installing this package from a prerelease feed, read the [v0.1 release preview](../../releases/v0.1-preview.md) for current release risk, provisional migration guidance, and the finalization path to the tagged release note.

## Usage

Create a startup class that inherits from `ConsoleStartup<TModule>`:

```csharp
public class MyConsoleStartup : ConsoleStartup<MyRootModule> { }
```

In your `Program.cs`:

```csharp
await ConsoleApp<MyRootModule>.RunAsync(args);
```

You can also customize console startup behavior at the entry point:

```csharp
using ForgeTrust.AppSurface.Core;

await ConsoleApp<MyRootModule>.RunAsync(
    args,
    options =>
    {
        options.OutputMode = ConsoleOutputMode.CommandFirst;
    });
```

If you are using a custom startup type directly, the same configuration can be applied fluently:

```csharp
await new MyConsoleStartup()
    .WithOptions(options => options.OutputMode = ConsoleOutputMode.CommandFirst)
    .RunAsync(args);
```

## Features

- **Command Discovery**: Automatically finds classes implementing `ICommand` from the entry point assembly and dependent modules, then registers their CliFx 3 generated command descriptors.
- **Hosted Runner**: Integrates with the .NET Generic Host to manage service lifecycles during command execution.
- **Console Options**: `ConsoleOptions` lets entry points configure shared console behavior before the host is built.

## Command authoring with CliFx 3

AppSurface uses CliFx 3 source-generated command descriptors. Command classes keep the normal CliFx attribute model, but the compiler now generates the descriptor AppSurface registers at runtime.

```csharp
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;

[Command("greet", Description = "Prints a greeting.")]
public sealed partial class GreetCommand : ICommand
{
    [CommandParameter(0, Description = "The name to greet.")]
    public required string Name { get; set; }

    public ValueTask ExecuteAsync(IConsole console)
    {
        console.Output.WriteLine($"Hello, {Name}!");
        return ValueTask.CompletedTask;
    }
}
```

- Mark every command class as `partial` so CliFx can attach the generated `Descriptor` property.
- If a command is nested, mark each containing type as `partial` too.
- Use `set` on command-bound properties. CliFx 3 generated binders assign parsed values after construction.
- Use C# `required` for required options and parameters. The older CliFx `IsRequired` attribute property is not part of CliFx 3.
- Keep constructors DI-friendly. AppSurface still resolves command instances from the application service provider.
- For an app-owned `config diagnostics` command that renders the active AppSurface configuration audit report, use the
  copy-paste wrapper in [ForgeTrust.AppSurface.Config](../../Config/ForgeTrust.AppSurface.Config/README.md#app-owned-diagnostics-command).

## ConsoleOptions

`ConsoleOptions` is the public startup configuration surface for AppSurface console apps.

- **`OutputMode`** defaults to `ConsoleOutputMode.Default`.
- **`ConsoleOutputMode.Default`** preserves the standard Generic Host experience, including lifecycle output that may appear alongside command output.
- **`ConsoleOutputMode.CommandFirst`** suppresses ambient host and command-runner lifecycle information so help, validation, and command-owned progress remain the primary console experience.
- **`CustomRegistrations`** runs after AppSurface's built-in console registrations so advanced hosts and tests can override services such as `CliFx.Infrastructure.IConsole` or add extra logging providers.

Use `CommandFirst` for public CLIs where first-touch output is part of the product surface. Leave the default in place for internal tools or apps where host lifecycle logs are useful operational context.

## Pitfalls

- `CommandFirst` suppresses ambient lifecycle information, not command-owned progress. Your command still needs to log or write the progress messages users should see.
- Console options are applied before host creation. Configure them at the entry point or through `WithOptions(...)`, not inside command handlers.
- `CustomRegistrations` overrides services late in the startup pipeline. Use it intentionally for host-level customization, not as a replacement for normal module service registration.
- If you override console startup behavior in a derived startup class, keep the shared options path intact so entry-point configuration still reaches the host.
- A command without generated CliFx metadata fails during startup with guidance to make the command and any enclosing types `partial`.

---
[📂 Back to Console List](../README.md) | [🏠 Back to Root](../../README.md)
