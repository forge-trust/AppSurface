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
