# ForgeTrust.AppSurface.Console

Modular bootstrapping for .NET Console applications using [CliFx](https://github.com/Tyrrrz/CliFx).

## Overview

`ForgeTrust.AppSurface.Console` provides a structured way to build command-line tools. It automatically discovers and registers CliFx commands from modules, runs them inside the .NET Generic Host, and now exposes a startup options seam for console-specific behavior such as command-first output.

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

- **Command Discovery**: Automatically registers classes implementing `ICommand` from the entry point assembly and dependent modules.
- **Hosted Runner**: Integrates with the .NET Generic Host to manage service lifecycles during command execution.
- **Console Options**: `ConsoleOptions` lets entry points configure shared console behavior before the host is built.

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

---
[📂 Back to Console List](../README.md) | [🏠 Back to Root](../../README.md)
