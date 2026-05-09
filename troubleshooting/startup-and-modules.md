# Troubleshoot Startup and Modules

Use this page when the first run or first module does not behave as expected.

Each section gives the symptom, likely cause, check, and fix. Start with the visible symptom. Startup failures often look like docs, route, or package issues at first, but the root cause is usually module registration, dependency order, configuration, or package choice.

## Module Did Not Run

### Symptom

The app starts, but the service, middleware, endpoint, or option your module owns is missing.

### Likely Cause

The root module did not register the module, or the app is running a different root module than the one you edited.

### Check

Find the root module passed to `WebApp<TModule>`, `ConsoleApp<TModule>`, or `RunnableStartup<TModule>`. Then check `RegisterDependentModules`.

### Fix

Register the missing module through `ModuleDependencyBuilder`, or move the behavior into the root module if it is truly app-local.

## Dependency Module Did Not Register

### Symptom

A module runs, but a service or option from another module is missing.

### Likely Cause

The module assumes another Runnable module is present but does not declare that dependency.

### Check

Search for the service or option owner. If it lives in another module, the consuming module should register that dependency in `RegisterDependentModules`.

### Fix

Add the dependency module explicitly. Do not duplicate the dependency module's service registrations in the consuming module.

## Configuration Failed At Startup

### Symptom

Startup exits before the app begins listening, often with a configuration validation message.

### Likely Cause

A strongly typed configuration value is missing or outside its allowed range.

### Check

Run the config validation example to see the expected failure shape:

```bash
dotnet run --project examples/config-validation
```

The error should name the key and rule without printing the attempted value.

### Fix

Set the missing value, correct the value, or relax the validation rule if the module contract was too strict.

Do not print configuration values while debugging. They may contain secrets.

## Web Route, Static Asset, Or Docs Surface Is Missing

### Symptom

A web route returns `404`, a static asset is missing, or the docs surface does not appear where expected.

### Likely Cause

The wrong module is registered, the app's host identity is not the assembly that owns static web assets, or the docs route root is not the one you are opening.

### Check

- Confirm the expected web module is registered.
- Confirm the startup log shows the URL you are opening.
- For RazorDocs, check whether the app is mounted at `/docs` or a configured docs root.
- For static web assets in custom hosts, check `StartupContext.HostApplicationName` and `StartupContext.OverrideEntryPointAssembly`.

### Fix

Use the app's logged URL and configured docs root. When a custom host needs a different assembly identity, set `StartupContext.OverrideEntryPointAssembly` instead of overloading the display name.

## Wrong Package Installed First

### Symptom

The app has extra packages, but the first example still does not map to the kind of app you are building.

### Likely Cause

An optional package was installed before the base package or the package chooser was skipped.

### Check

Read the [Runnable v0.1 package chooser](../packages/README.md). Start with the package that matches the app type.

### Fix

For a normal ASP.NET Core app, start with `ForgeTrust.Runnable.Web`. Add optional packages such as OpenAPI, Scalar, RazorWire, Tailwind, or RazorDocs only when the app needs them.
