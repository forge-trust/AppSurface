# Runnable Glossary

This glossary defines the terms needed to read the Runnable package docs and examples. Package READMEs remain the source of truth for full API behavior.

## RunnableStartup

`RunnableStartup` is the startup pipeline that composes modules on top of the .NET Generic Host. Use the package-specific entry points, such as `WebApp<TModule>`, unless you need deeper host customization.

## Root Module

The root module is the module type passed to a Runnable entry point. It is the starting point for module composition.

Example:

```csharp
await WebApp<ExampleModule>.RunAsync(args);
```

`ExampleModule` is the root module.

## Dependent Module

A dependent module is another module registered by a module through `RegisterDependentModules`. Use dependencies when one module needs another module's services, options, middleware, endpoints, or host behavior.

## Host Module

A host module participates in .NET host setup through the core module lifecycle. `IRunnableHostModule` is the core contract for modules that can configure services and host behavior.

## StartupContext

`StartupContext` carries startup metadata such as the app label, host application identity, application discovery assembly, environment, and console output mode.

Keep display labels and host identity separate. Static web assets use host identity, not the label you want to show to users.

## Package Module

A package module is a Runnable module shipped by a package, such as the Web, Console, Aspire, or optional web modules. Install package modules only when the [package chooser](../packages/README.md) says they match the app.

## Web Module

A web module participates in ASP.NET Core startup through `IRunnableWebModule`. It can configure web options, register middleware, and map endpoints.

The base web package is [ForgeTrust.Runnable.Web](../Web/ForgeTrust.Runnable.Web/README.md).

## Console Startup

Console startup is the Runnable path for CLI commands and worker-style processes. Start with the [Console package docs](../Console/ForgeTrust.Runnable.Console/README.md) when the app is command-oriented instead of web-oriented.

## Package Chooser

The package chooser is the generated install map for the coordinated Runnable package family. Use it before adding optional modules.

Read it here: [Runnable v0.1 package chooser](../packages/README.md)
