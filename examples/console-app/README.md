# Console app example

This sample demonstrates how to build a console application with **ForgeTrust.AppSurface**.

It defines a module and a `greet` command. The command uses CliFx binding attributes and is marked `partial` so CliFx 3
can generate the descriptor AppSurface registers at startup. Run the sample with:

```bash
dotnet run --project examples/console-app/ConsoleAppExample.csproj -- greet World
```

This will output:

```
Hello, World!
```

The sample also exposes the app-owned configuration diagnostics wrapper:

```bash
dotnet run --project examples/console-app/ConsoleAppExample.csproj -- config diagnostics
```

That command runs inside the sample app's own AppSurface host and prints the active environment's known configuration
audit entries. It does not accept a command-level `--environment`; pass AppSurface host environment input at startup
when the whole app should start under another environment.
