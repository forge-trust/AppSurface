# Console app example

This sample demonstrates how to build a console application with **ForgeTrust.AppSurface**.

It defines a module and a `greet` command. The command uses attributes from the [CliFx](https://github.com/Tyrrrz/CliFx)
library—see the [CliFx documentation on attributes](https://github.com/Tyrrrz/CliFx/blob/master/docs/attributes.md) for
more details. Run the sample with:

```bash
dotnet run --project examples/console-app/ConsoleAppExample.csproj -- greet World
```

This will output:

```
Hello, World!
```
