# ForgeTrust.AppSurface.Config

Strongly typed configuration primitives for AppSurface applications.

## Overview

This package provides the configuration layer for AppSurface modules. It combines file-based configuration, environment-aware providers, and strongly typed configuration objects so modules can consume configuration without hard-coding access patterns throughout the codebase.

`ForgeTrust.AppSurface.Config` does not store secrets by itself. Use `ForgeTrust.AppSurface.Config.LocalSecrets` only
when a solo or hobbyist app needs local, single-machine, pre-vault secret posture with source-aware diagnostics. Use
environment variables, key-per-file, or a remote vault for CI, containers, team environments, and production. The
migration ladder is:

```text
appsettings defaults < LocalSecrets < environment variables < future remote vault provider
```

## Release Guidance

AppSurface publishes the coordinated `v0.1.0` release as one package-facing story. Before installing this package from a package feed, read the [v0.1.0 release note](../../releases/v0.1.0.md) for current release risk, migration guidance, and package readiness.

## Key Types

- **`AppSurfaceConfigModule`**: Registers the configuration services for an AppSurface application.
- **`IConfigManager`**: Central access point for resolving configuration values.
- **`IConfigProvider`**: Abstraction for reading configuration from a source.
- **`FileBasedConfigProvider`**: Loads configuration from files.
- **`IConfigAuditReporter`**: Builds source-aware configuration audit reports for known config entries.
- **`ConfigAuditTextRenderer`**: Renders a safe, human-readable audit report from the structured model.
- **`ConfigAuditCollectionTraversalAttribute`**: Enables bounded collection element traversal for a discovered config wrapper.
- **`ConfigAuditDictionaryKeyCorrelationOptions`**: Supplies opt-in scoped HMAC key material for dictionary key correlation ids.
- **`ConfigDiagnosticsCommandRunner`**: Runs the app-owned text diagnostics workflow for the active AppSurface environment.
- **`Config<T>` / `ConfigStruct<T>`**: Base types for strongly typed configuration values.
- **`ConfigKeyAttribute`**: Associates a configuration type or property with a specific key.
- **`ConfigKeyRequiredAttribute`**: Requires a config wrapper to resolve a provider or default value during startup.
- **`ConfigValueNotEmptyAttribute`**: Validates that a resolved scalar `string` or `Guid` value is not empty.
- **`ConfigValueRangeAttribute`**: Validates that a resolved scalar `int` or `double` value is within an inclusive range.
- **`ConfigValueMinLengthAttribute`**: Validates that a resolved scalar `string` value meets a minimum length.
- **`ConfigurationValidationException`**: Startup-time exception that reports object DataAnnotations and scalar validation failures for a resolved config value.
- **`ConfigurationValidationFailure`**: Structured validation failure details for logging and tests.

## Usage

Register the module and model your settings with strongly typed config objects:

```csharp
public sealed class MyModule : AppSurfaceConfigModule
{
}
```

Define configuration models:

```csharp
public sealed class DocsPathConfig : Config<string>
{
}
```

Resolve them through the configuration services used by your module or application startup flow.

Install the package from an application project with:

```bash
dotnet add package ForgeTrust.AppSurface.Config --project <path-to-your-app.csproj>
```

## Configuration Audit Reports

Use `IConfigAuditReporter` when an operator or maintainer needs to answer:

```text
What does this app believe its configuration is, and why?
```

The reporter returns a structured `ConfigAuditReport` for an environment. The report includes provider order, known
configuration entries, source records, entry states, diagnostics, and display-safe values. A known entry is either a
discovered `Config<T>` / `ConfigStruct<T>` wrapper or a key registered explicitly with `AddConfigAuditKey<T>()`.
Audit values are redacted before they enter `ConfigAuditReport`, JSON serialization, text rendering, or diagnostics
command output. The report is still an internal support artifact: key names, provider names, file paths, environment
variable names, diagnostics, and the existence of redacted values can still be operationally sensitive.

```csharp
var report = auditReporter.GetReport("Staging");
var text = textRenderer.Render(report);
```

Explicit registrations are useful for keys that are read directly through `IConfigManager` instead of a wrapper:

```csharp
services.AddConfigAuditKey<string>("Billing.Endpoint");
```

Mark domain-specific secrets explicitly when the key name does not contain a built-in sensitive fragment:

```csharp
services.AddConfigAuditKey<string>(
    "Partner.Payload",
    options => options.Sensitivity = ConfigAuditSensitivity.Sensitive);
```

### Config audit source locations in 5 minutes

File-backed source records can include an optional `Location` when AppSurface can confidently map the merged JSON value
back to a property token in the same file content used to initialize the file provider snapshot:

```csharp
var report = auditReporter.GetReport("Staging");
var source = report.Entries
    .Single(entry => entry.Key == "MyApp.Settings")
    .Sources
    .Single(source => source.Kind == ConfigAuditSourceKind.File);

var coordinate = source.Location is { } location
    ? $"{source.FilePath}:{location.LineNumber}:{location.ByteColumnNumber}"
    : source.FilePath;

Console.WriteLine(coordinate);
```

The structured model keeps the coordinate separate from the path:

```json
{
  "Kind": "File",
  "FilePath": "/app/appsettings.Staging.json",
  "ConfigPath": "MyApp.Settings.Database.Host",
  "Location": {
    "LineNumber": 9,
    "ByteColumnNumber": 9
  }
}
```

Default `System.Text.Json` options serialize enum values such as `Kind` numerically. Enable a string-enum converter if
you want the enum names shown above in exported support data.

The text renderer includes the coordinate when it is present and preserves the previous shape when it is absent:

```text
Source: FileBasedConfigProvider appsettings.Staging.json:9:9 :: MyApp.Settings.Database.Host
Source: FileBasedConfigProvider appsettings.Staging.json :: Legacy.Unlocated
```

`ByteColumnNumber` is one-based and counts UTF-8 bytes from the start of the physical line, not Unicode characters or
editor display cells. A property after `é`, emoji, or other non-ASCII text can have a byte column larger than the
column shown by an editor.

`Location` can be `null` even for a file source. AppSurface omits coordinates when it cannot prove that the coordinate
would point at the same value the existing JSON parse and merge produced. Common causes include ambiguous
case-insensitive path collisions, unsupported dotted property paths, parser mismatch, collection element descendants,
or source metadata from a provider that is not file-backed. No location is better than a misleading location.

File paths and line/byte coordinates are operational metadata. Treat rendered audit reports as support-bundle material:
review them before sharing outside the operational trust boundary, especially when deployment paths reveal tenant names,
repository layout, or host filesystem conventions.

### Discovered file keys in 5 minutes

The report also includes `DiscoveredKeys` for effective merged configuration visible to enumerable providers. The
built-in v1 surface discovers file-backed keys from `FileBasedConfigProvider`, so the text renderer labels that section
`Discovered file keys:` when all discovered sources are files. It does not enumerate environment variables, secret
providers, shadowed lower-priority file values, or every raw key a custom provider may know about. Classifications are
relative to the AppSurface audit registry: `Unknown` means unknown to `Config<T>` wrappers and
`AddConfigAuditKey<T>()`, not globally unused.

Discovered scalar values display only when the discovered key exactly matches an audit entry or when redaction replaces
the value with `[redacted]`. Unknown keys and descendants under a broad registered root are inventory by default: the
report shows the key and provenance, but omits the raw value. Register each display-safe scalar leaf explicitly with
`AddConfigAuditKey<T>("Exact.Path")` after reviewing whether the value belongs in support output.

For example, this file-backed configuration:

```json
{
  "Billing": {
    "Endpoint": "https://billing.example",
    "Password": "super-secret"
  },
  "BillingEndpiont": "https://typo.example"
}
```

with this registration:

```csharp
services.AddConfigAuditKey<BillingOptions>("Billing");
services.AddConfigAuditKey<string>("Billing.Endpoint");
```

renders discovered file keys like this:

```text
Discovered file keys:
  Billing.Endpoint [Known] = https://billing.example
    Source: FileBasedConfigProvider appsettings.Staging.json :: Billing.Endpoint
  Billing.Password [Under known entry] = [redacted]
    Redacted: true
    Source: FileBasedConfigProvider appsettings.Staging.json :: Billing.Password
  BillingEndpiont [Unknown to AppSurface audit registry] (value omitted: inventory key is not an exact audit entry; register this exact key with AddConfigAuditKey<T>() after reviewing sensitivity)
    Source: FileBasedConfigProvider appsettings.Staging.json :: BillingEndpiont
```

The structured shape carries the same decision in `ValueDisplayState`:

```json
{
  "Key": "BillingEndpiont",
  "Classification": 2,
  "DisplayValue": null,
  "IsRedacted": false,
  "ValueDisplayState": 4
}
```

Default `System.Text.Json` options serialize enum values numerically. Enable a string-enum converter if you want names
such as `OmittedInventory` in exported support data. Do not treat `DisplayValue == null` as meaning only "complex
parent": use `ValueDisplayState` to distinguish omitted inventory from omitted object or array parents. Value omission
does not make a report public-safe. Key names, provider names, file names, config paths, environment variable names, and
the existence of redacted values can still be operationally sensitive.

### Audit Hello World

Opt in a wrapper, produce the structured report, and render it when humans need a pasteable view:

```csharp
[ConfigKey("Services", root: true)]
[ConfigAuditCollectionTraversal]
public sealed class ServicesConfig : Config<List<ServiceEndpoint>>
{
}

var report = auditReporter.GetReport("Production");
Console.WriteLine(textRenderer.Render(report));
```

Given this configuration:

```json
{
  "Services": [
    {
      "Name": "billing",
      "Url": "https://billing.example",
      "Password": "do-not-print"
    }
  ]
}
```

the rendered output keeps collection provenance visible while redacting sensitive members:

```text
Services
  State: Resolved
  Source: FileBasedConfigProvider appsettings.Production.json :: Services
  Children:
    Services[0]
      State: Resolved
      Source: FileBasedConfigProvider appsettings.Production.json :: Services.0
      Children:
        Services[0].Name = billing
        Services[0].Password = [redacted]
        Services[0].Url = https://billing.example
```

### App-Owned Diagnostics Command

Use `ConfigDiagnosticsCommandRunner` when an AppSurface console app should expose an operator command that prints the
same audit report from inside the app's own DI graph. The runner is registered by `AppSurfaceConfigModule` and stays
console-agnostic; your app owns the small CliFx wrapper:

```csharp
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Config;

namespace MyApp;

/// <summary>
/// Renders the active AppSurface configuration audit report to the console.
/// </summary>
/// <param name="runner">The console-agnostic diagnostics runner registered by the config module.</param>
/// <remarks>
/// This app-local CliFx command keeps command discovery, console output, and command-framework failure mapping in the
/// consuming app while reusing Config's redacted audit renderer. The class is partial so CliFx 3 can generate the
/// descriptor AppSurface Console discovers at startup.
/// </remarks>
[Command("config diagnostics", Description = "Prints the active AppSurface configuration audit report.")]
public sealed partial class ConfigDiagnosticsCommand(ConfigDiagnosticsCommandRunner runner) : ICommand
{
    /// <summary>
    /// Executes the diagnostics command against the already-selected AppSurface host environment.
    /// </summary>
    /// <param name="console">The CliFx console whose output writer receives the rendered audit report.</param>
    /// <returns>A completed value task when the report renders successfully.</returns>
    /// <exception cref="CommandException">
    /// Thrown with sanitized diagnostics text when the runner cannot produce a report.
    /// </exception>
    public ValueTask ExecuteAsync(IConsole console)
    {
        var result = runner.Run(console.Output);
        if (!result.Succeeded)
        {
            throw new CommandException(result.Failure?.ToDisplayString() ?? "Configuration diagnostics failed.");
        }

        return default;
    }
}
```

Run the command from the app project:

```bash
dotnet run --project src/MyApp -- config diagnostics
```

The command audits the active AppSurface environment selected during host startup. It does not define a command-level
`--environment` option; AppSurface already treats `--environment` as host startup input. A typical report starts like:

```text
Environment: Staging
Providers:
  0. EnvironmentConfigProvider (override)
  1. FileBasedConfigProvider (priority 0)

Entries:
  Billing.Endpoint = https://billing.internal
    State: Resolved
    Source: FileBasedConfigProvider appsettings.Staging.json :: Billing.Endpoint
  Billing.ApiKey = [redacted]
    State: Resolved
    Source: Environment variable BILLING__APIKEY
  Retry.Count = 10
    State: Invalid
    Diagnostic: The configuration value must be between 1 and 5.
```

Known AppSurface audit entries include discovered `Config<T>` / `ConfigStruct<T>` wrappers and entries registered with
`AddConfigAuditKey<T>()`. `DiscoveredKeys` separately exposes effective merged file-backed keys visible to enumerable
providers. The report still does not enumerate every raw environment variable or every raw key a custom provider may
know about.

Place the wrapper where AppSurface Console command discovery can see it. By default, `ConsoleStartup<TModule>` scans
`StartupContext.EntryPointAssembly`, which is normally the root module assembly. If your command lives elsewhere, set
`StartupContext.OverrideEntryPointAssembly` from a custom startup path.

Diagnostics run after the app host and command service can start. They are not a diagnostic startup mode for apps that
fail during host construction or eager validation before commands execute. AppSurface console commands also run inside
the normal Generic Host lifecycle, so unrelated hosted services can start unless your app owns a separate command-only
startup path.

Audit entry states are intentionally small:

| State | Meaning |
| --- | --- |
| `Resolved` | A provider supplied the value without member-level mixed provenance. |
| `PartiallyResolved` | An object value has a base source plus one or more member-level environment patches. |
| `Defaulted` | No provider supplied a value, but the config wrapper default supplied one. |
| `Missing` | No provider value and no default resolved for the known entry. |
| `Invalid` | A provider value, patch, or wrapper validation result was invalid. |

### Provenance Behavior

Environment variables are checked before lower-priority providers, matching normal `DefaultConfigManager` resolution.
File values report `FileBasedConfigProvider`, the file path, and the config path. Direct environment values report the
concrete environment variable name. Object-valued entries can include child entries when the final value combines a file
base with environment child patches:

```text
MyApp.Settings
  Source: FileBasedConfigProvider appsettings.Staging.json :: MyApp.Settings
  Children:
    MyApp.Settings.Database.Port = 6543
      Source: Environment variable MYAPP__SETTINGS__DATABASE__PORT
```

The report is observational. Environment patch diagnostics trace patches against a cloned value so asking for an audit
report does not mutate the provider object being inspected.

Collection element provenance is opt-in per entry. Arrays and lists use zero-based numeric element paths such as
`Services.0`. Dictionary items use display labels such as `Routes["primary"]` when the key is non-sensitive and safe to
display. Dotted, quoted, bracketed, hidden, or sensitive dictionary keys use inherited parent provenance instead of
exposing a raw key path; for example, keys such as `user.name`, `items[0]`, or `"first name"` inherit the parent source
because they cannot form an unambiguous raw config path.

When environment variables create or replace indexed collection elements, traversed child entries keep the exact
environment source and add proof-limited diagnostics. `config-audit-environment-created-element` means the audit could
prove there was no lower-priority element for that index. `config-audit-environment-element-base-unknown` means the
environment supplied the final element, but lower-priority provider evidence was pathless, invalid, generic, or otherwise
unable to prove whether that index already existed.

For example, with traversal enabled for `MyApp.Settings`, `MYAPP__SETTINGS__ENDPOINTS__0=https://one.example` can
render like this when no lower-priority provider supplied `Endpoints[0]`:

```text
MyApp.Settings.Endpoints[0] = https://one.example
  Source: Environment variable MYAPP__SETTINGS__ENDPOINTS__0
  Diagnostic: [Info] config-audit-environment-created-element: Environment variable created this collection element; audit provenance found no prior element.
```

The report still follows runtime probing. If indexed environment variables skip `ENDPOINTS__0` and only define
`ENDPOINTS__1`, the later variable is not reported as created because AppSurface does not bind it as part of the
runtime collection.

### Collection Traversal API

`ConfigAuditEntryOptions` is an immutable snapshot that controls collection expansion for one known entry:

| Option | Default | Behavior |
| --- | --- | --- |
| `TraverseCollectionElements` | `false` | Keeps existing reports opaque unless explicitly enabled. |
| `MaxCollectionDepth` | `4` | Stops nested collection traversal after the configured depth. |
| `MaxCollectionElements` | `128` | Stops after this many elements from any one collection. |
| `MaxReportNodes` | `4096` | Bounds total child nodes created for the entry. |
| `DisplayDictionaryKeys` | `true` | Allows non-sensitive dictionary keys to appear as labels. Sensitive keys are still redacted. |
| `Sensitivity` | `Unknown` | Classifies the entry for redaction. `Sensitive` redacts root values, traversed child values, and value-derived dictionary labels. `NonSensitive` documents intent but never disables conservative redaction from fragments or sources. |
| `DictionaryKeyCorrelationMode` | `None` | Adds no durable dictionary key metadata unless explicitly set to `ScopedHmac`. |

For discovered wrappers, use `ConfigAuditCollectionTraversalAttribute`. Attribute presence enables traversal and its
named properties mirror the bounded options:

```csharp
[ConfigAuditCollectionTraversal(
    MaxCollectionElements = 32,
    DisplayDictionaryKeys = false,
    DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac)]
public sealed class ServicesByNameConfig : Config<Dictionary<string, ServiceEndpoint>>
{
}
```

Use the `AddConfigAuditKey<T>()` overload when registering provider-only keys. The callback receives a mutable
`ConfigAuditEntryOptionsBuilder`; AppSurface snapshots the builder into immutable options during registration:

```csharp
services.AddConfigAuditKey<Dictionary<string, ServiceEndpoint>>(
    "ServicesByName",
    options =>
    {
        options.TraverseCollectionElements = true;
        options.MaxCollectionElements = 32;
        options.Sensitivity = ConfigAuditSensitivity.Sensitive;
        options.DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac;
    });
```

`ConfigAuditKnownEntry` also has an options overload for wrapper-discovered or manually constructed entries.
Use an object initializer to create the immutable options snapshot:

```csharp
services.AddSingleton(
    new ConfigAuditKnownEntry(
        "Services",
        configType: null,
        typeof(List<ServiceEndpoint>),
        new ConfigAuditEntryOptions { TraverseCollectionElements = true }));
```

Each collection child can include `ConfigAuditEntry.Element`. Array and list entries set `Element.Kind` to
`ArrayItem` or `ListItem` and set `Element.Index`. Dictionary entries set `Element.Kind` to `DictionaryItem` and set
`Element.KeyLabel` to a display-safe label. Redacted dictionary labels are report-local, for example
`[redacted-key-1]`; they are not stable identifiers across reports. When dictionary key correlation is explicitly
enabled and configured, dictionary entries also set `Element.KeyCorrelationId` to a separate opaque identifier. The
correlation id is metadata; it is never part of `ConfigAuditEntry.Key` or `Element.KeyLabel`.

When wrapper discovery and manual registration use the same key, the wrapper remains the source of wrapper metadata
and validation behavior. Explicit manual option assignments override wrapper attribute options per property, including
assignments back to the default value. For example, a wrapper can hide dictionary keys by default while one manual
registration sets `DisplayDictionaryKeys = true` for a provider-only report composition.

The same duplicate-registration rule is the preferred way to classify a wrapper-discovered key as sensitive without
losing wrapper validation or traversal metadata:

```csharp
[ConfigKey("Audit.Services", root: true)]
[ConfigAuditCollectionTraversal]
public sealed class AuditServicesConfig : Config<List<ServiceEndpoint>>
{
}

services.AddConfigAuditKey<List<ServiceEndpoint>>(
    "Audit.Services",
    options => options.Sensitivity = ConfigAuditSensitivity.Sensitive);
```

### Dictionary Key Correlation

Dictionary key correlation is an opt-in support workflow for comparing hidden dictionary keys across reports. It does
not reveal raw keys and does not make `[redacted-key-1]` durable. Enable it per audit entry with
`DictionaryKeyCorrelationMode = ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac`, then configure global key material
with `ConfigAuditDictionaryKeyCorrelationOptions`:

```csharp
services.Configure<ConfigAuditDictionaryKeyCorrelationOptions>(options =>
{
    options.SecretKey = configuration["ConfigAudit:CorrelationSecret"];
    options.KeyId = "config-audit-2026-05";
    options.ApplicationScope = "billing-service";
});
```

The secret key is interpreted as UTF-8 and must contain at least 32 bytes when UTF-8 encoded. AppSurface derives ids with
HMAC-SHA256 over the algorithm version, application scope, report environment, root audit key, and raw dictionary key,
then truncates the result to 96 bits. Rendered ids look like `v1:{keyId}:{24-hex-chars}`. The key id is trimmed and can
contain only ASCII
letters, digits, `.`, `_`, and `-` so rendered reports remain line-safe. Changing the secret key, key id, application
scope, environment, or root audit key intentionally changes the id.

If correlation is enabled for an entry but global key material is missing or invalid, the report keeps current
report-local labels, omits `KeyCorrelationId`, and emits `config-audit-key-correlation-unavailable`. Support bundles may
include correlation ids, key id, and application scope, but must never include the secret key or a raw-key mapping.

Treat correlation ids as sensitive support metadata. They reveal equality, absence, recurrence, and churn across
reports. If an attacker can choose dictionary keys and obtain reports, they can still test chosen inputs against
correlation output even though HMAC prevents offline guessing without the secret. Keep correlation disabled unless a
support or operator workflow needs it.

### Collection Examples

| Shape | Traversal result |
| --- | --- |
| `string[]` or `List<T>` | Children render as `Key[0]`, `Key[1]`, preserving numeric order in text output. |
| `Dictionary<string,T>` | Non-sensitive keys render as `Key["name"]`; sensitive keys render as `Key[[redacted-key-1]]`. |
| dictionary correlation enabled | Dictionary elements include `Element.KeyCorrelationId`; text output renders it as entry metadata. |
| null element | Emits an element child with a `null` display value and no grandchildren. |
| object element | Emits the element and then ordinary public property/field children below it. |
| non-string dictionary key | Uses the invariant string label for convertible keys when non-sensitive and display is enabled; arbitrary object keys are hidden. |
| dotted or bracketed dictionary key | Uses a display label but inherits parent provenance because no safe exact source path is available. |
| unsupported enumerable | Emits a traversal diagnostic instead of enumerating. |
| multidimensional array | Emits a traversal diagnostic; only one-dimensional arrays are expanded. |

### Redaction Defaults

Audit reports are redacted by default before renderers see values. Sensitive-looking paths, keys, and environment
variable names render as `[redacted]`. The built-in fragments include `password`, `secret`, `token`, `apikey`, `key`,
`connectionstring`, `credential`, `private`, `passphrase`, `clientsecret`, `client_secret`, `sharedsecret`,
`shared_secret`, `privatekey`, `private_key`, `certificate`, `cert`, `dsn`, `sas`, `sharedaccesssignature`,
`assertion`, `cookie`, `sessionid`, `session_id`, `sessioncookie`, `session_cookie`, `bearer`, `jwt`,
`refresh_token`, `access_token`, `clientassertion`, and `client_assertion`.

Redaction is deliberately conservative and can produce false positives. Short fragments such as `key`, `cert`, `dsn`,
and `sas` are accepted because audit reports are operator diagnostics, not a public display surface. Use
`ConfigAuditSensitivity.NonSensitive` only as documentation for readers and future tooling; it is not an opt-out and
never downgrades redaction from `Sensitive`, built-in fragments, provider source sensitivity, or sensitive-looking
source paths.

Redaction does not expose string lengths, sensitive collection counts, or sensitive nested values. Display values are
redacted before entering `ConfigAuditReport`. Source metadata remains visible unless a provider marks the source
metadata itself as sensitive, which keeps values redacted while still showing where a value came from. Treat rendered
reports as internal support data: provider names, file names, environment variable names, key names, config paths, and
the existence of redacted values can still be operationally sensitive.

Collection parent values are omitted instead of serialized when the collection key and source metadata are not
sensitive. This avoids leaking nested element fields such as `Password`, `Token`, `Secret`, or `ApiKey` through a raw
JSON dump. When element traversal is enabled, dictionary key labels are redacted or hidden before public report objects
are created. Sensitive dictionary labels do not appear in `Key`, `Element.KeyLabel`, diagnostics, or the text renderer.
Source metadata remains governed by the source-metadata safety contract above.

When an entry is effectively sensitive, dictionary labels become report-local placeholders:

```text
Partner.Payloads[[redacted-key-1]] = [redacted]
```

When `DisplayDictionaryKeys = false`, labels are intentionally hidden as `[[key]]`. Hidden labels inherit parent
provenance for display, but redaction still evaluates parent and child source candidates so a source-sensitive child
value does not leak.

### Audit Diagnostics

| Code | Severity | Meaning |
| --- | --- | --- |
| `config-audit-collection-kind-unsupported` | Warning | The value is an unsupported enumerable shape or a multidimensional array. |
| `config-audit-collection-depth-limit` | Warning | Traversal stopped at `MaxCollectionDepth`. |
| `config-audit-collection-element-limit` | Warning | Traversal stopped at `MaxCollectionElements` for one collection. |
| `config-audit-report-node-limit` | Warning | Traversal stopped at `MaxReportNodes` for the entry. |
| `config-audit-source-inherited` | Info | An element inherited parent provenance because an exact safe source path was unavailable. |
| `config-audit-source-unavailable` | Info | No provider source record was available for an element. |
| `config-audit-environment-created-element` | Info | An environment variable supplied a collection element and audit provenance proved no lower-priority element existed for that index. |
| `config-audit-environment-element-base-unknown` | Info | An environment variable supplied a collection element, but lower-priority provider evidence could not prove whether that index existed. |
| `config-audit-options-invalid` | Error | Entry options were invalid. Traversal limits use safe bounded defaults; invalid `Sensitivity` values fail closed as `Sensitive`, and invalid or unavailable correlation mode falls back to `None`. |
| `config-audit-key-correlation-unavailable` | Warning | Dictionary key correlation was requested, but global key material was missing or invalid. |

### Migration Note

Existing reports keep the same collection shape unless an entry opts into collection traversal through
`ConfigAuditCollectionTraversalAttribute` or manual `TraverseCollectionElements`. Object child entries, redacted scalar
display values, provider precedence, and wrapper diagnostics continue to work as before. If a wrapper-discovered key
and a manual key registration use the same config key, wrapper metadata still wins for type and validation, while
explicit manual audit option assignments are merged into the selected entry. Opted-in traversed collection entries may
now include additional info diagnostics for environment-created or base-unknown elements. The public report object shape
is unchanged, but text output now includes diagnostic severity and code so operators can search rendered reports.

Adding `Sensitivity = ConfigAuditSensitivity.Sensitive` is additive API surface, but redaction behavior is intentionally
broader. New built-in fragments can redact values that previously rendered, and `NonSensitive` is not an opt-out. Review
release notes for fragment changes when audit output shape matters to operators.

Discovered-key scalar display is intentionally narrower: unknown keys and non-redacted descendants under broad
registrations now omit raw values. Text parsers should not assume every scalar-looking discovered key includes
` = value`. JSON consumers should branch on `ConfigAuditDiscoveredKey.ValueDisplayState`; `DisplayValue == null` can
mean omitted inventory as well as an omitted object or array parent. Add exact leaf registrations for display-safe
provider-only values that should continue appearing in reports.

### Audit Pitfalls

- Known audit entries cover AppSurface-registered keys. `DiscoveredKeys` adds the effective merged file-backed keys
  visible to enumerable providers, but it is not a complete raw file inventory and does not include shadowed
  lower-priority file keys in v1.
- `Unknown to AppSurface audit registry` means the key is outside known `Config<T>` wrappers and
  `AddConfigAuditKey<T>()` registrations. It can be a typo, stale setting, or a value consumed outside AppSurface; it
  is not proof that the key is globally unused.
- `Under known entry` uses dotted-path segment matching. A broad root registration helps classify the key and inherit
  sensitivity, but it does not make descendant scalar values display-safe. Register the exact leaf key when the value
  should appear.
- Environment variables, secret providers, source-metadata redaction modes, shadowed raw file inventory, and strict
  drift gates are outside the first discovered-key surface.
- Collection display values are intentionally omitted rather than summarized or serialized; opt into element traversal
  for keys where element-level visibility is safe and useful.
- `ConfigAuditCollectionTraversalAttribute` is inherited. Put a new attribute on a derived wrapper when inherited
  traversal limits are too broad or too narrow for that derived key.
- Attribute values are compile-time metadata. Use manual `AddConfigAuditKey<T>()` options when traversal settings must
  be chosen dynamically at registration time.
- Sensitive dictionary key labels are report-local. Do not use `[redacted-key-1]` as a durable correlation identifier.
- `ConfigAuditSensitivity.NonSensitive` is not a redaction bypass; it cannot override sensitive fragments, source
  sensitivity, provider sensitivity, or another registration that marks the entry sensitive.
- Invalid `ConfigAuditSensitivity` enum values emit `config-audit-options-invalid` and redact as sensitive.
- Dictionary key correlation ids are separate from labels and still sensitive. They are useful for support comparison,
  not public logging, analytics, or user-facing output.
- Key rotation is explicit: changing the correlation secret or key id breaks historical correlation. V1 does not
  dual-read old keys during a migration window.
- Element traversal can report environment-created collection elements only when runtime-bound environment variables and
  audit provenance prove the element exists because of the environment override. It does not enumerate orphaned
  environment variables, sparse indices skipped by runtime probing, global debug expansion, raw-key drift analysis, or
  support-bundle export.
- Provider-discovered raw key enumeration is intentionally separate from the first audit surface.
- File origins include file path and config path. File source locations are optional and only appear when AppSurface can
  map a source record to an exact property token without ambiguity.
- Validation diagnostics name keys and rules; they do not include attempted secret values.
- A direct environment object value replaces the whole object, while child environment variables produce member-level
  patch provenance.

## Environment Overrides

Environment variables override file-based providers. AppSurface supports both the legacy flattened shape and the
hierarchical double-underscore shape commonly used by .NET configuration:

```text
PRODUCTION_APP_SETTINGS
APP_SETTINGS
PRODUCTION__APP__SETTINGS
APP__SETTINGS
```

Use a direct value when one environment variable should replace the whole requested config value. For object-valued
config, the direct value is parsed as JSON:

```text
APP__SETTINGS={"Database":{"Host":"db.example","Port":5432}}
```

Use child variables when deployment needs to override one member without replacing the rest of an options object loaded
from JSON files, or when the target type can be built from child variables alone:

```text
APP__SETTINGS__DATABASE__PORT=6543
```

When `App.Settings` is resolved as an object, AppSurface first looks for a direct environment value. If none exists,
`DefaultConfigManager` asks `EnvironmentConfigProvider.TryPatch` to apply matching child variables. If a lower-priority
provider supplied an object, child variables patch that object and preserve existing members. If no provider produced a
value, `EnvironmentConfigProvider` can construct an instantiable target type from child variables alone. That means
`APP__SETTINGS__DATABASE__PORT` can update only `Database.Port` while preserving `Database.Host` from `appsettings.json`,
or create `App.Settings` from child variables such as `APP__SETTINGS__MODE=environment` when no provider value exists.

Indexed collection variables are supported for top-level values and object members:

```text
APP__SETTINGS__ENDPOINTS__0=https://one.example
APP__SETTINGS__ENDPOINTS__1=https://two.example
```

### Override Pitfalls

- A direct object environment variable replaces the whole object; use child variables for partial overrides.
- Child patching targets public settable properties, initialized getter-only mutable collections or nested objects, and
  public writable fields. Types that cannot be instantiated need a lower-priority provider value or an already-initialized
  nested member to patch.
- Invalid child values are ignored instead of wiping out the lower-priority provider value. For example, a non-numeric
  `APP__SETTINGS__DATABASE__PORT` leaves the existing `Port` unchanged.
- Child environment variables patch provider-supplied values or construct an instantiable missing value; they do not patch
  `Config<T>.DefaultValue`. Put deploy-time defaults in a normal provider when they need member-level environment
  overrides.

## Validation

`Config<T>` and `ConfigStruct<T>` validate the resolved value during initialization when the value is present. Validation runs after provider/default resolution, so defaults are held to the same rules as provider-supplied values. Optional `ConfigStruct<T>` values resolve through nullable `T?` provider lookups so a missing struct value is not confused with a configured zero-initialized value.

AppSurface has two validation paths:

- object-valued config models use ordinary DataAnnotations on the model and its members
- scalar config wrappers use AppSurface scalar attributes or a `ValidateValue` override on the wrapper

Use ordinary DataAnnotations on object-valued config models:

```csharp
using System.ComponentModel.DataAnnotations;

public sealed class RetryOptions
{
    [Range(1, 5)]
    public int Count { get; init; }
}

public sealed class RetryConfig : Config<RetryOptions>
{
}
```

When validation fails, AppSurface throws one `ConfigurationValidationException` for the config key. The exception message is designed for logs:

```text
Configuration validation failed for key 'RetryConfig' (RetryConfig -> RetryOptions): 1 error(s).
- Count: The field Count must be between 1 and 5.
```

The exception also exposes structured failures through `Failures`. Each failure includes the config key, wrapper type, value type, member names, and validation message. Attempted values are not exposed because config values often include secrets.

### Required Key Presence

Config wrappers are optional by default. A missing provider value with no default leaves `HasValue` false and skips value validation. Use `[ConfigKeyRequired]` when the key itself must resolve a value during startup:

```csharp
using ForgeTrust.AppSurface.Config;

[ConfigKeyRequired]
public sealed class ApiKeyConfig : Config<string>
{
}
```

Required presence runs after provider/default resolution. A provider value satisfies the requirement, and a `DefaultValue` also satisfies it because the requirement is resolved presence, not provider-source auditing:

```csharp
[ConfigKeyRequired]
public sealed class RegionConfig : Config<string>
{
    public override string? DefaultValue => "us-east-1";
}
```

When a required wrapper has no provider value and no default, AppSurface throws `ConfigurationValidationException` through the same startup failure family used by object and scalar validation:

```text
Configuration validation failed for key 'ApiKeyConfig' (ApiKeyConfig -> String): 1 error(s).
- <value>: A value is required for this configuration key.
```

Combine presence and value validation when both contracts matter:

```csharp
[ConfigKeyRequired]
[ConfigValueNotEmpty]
public sealed class ApiKeyConfig : Config<string>
{
}
```

In that example, a missing key reports the required-presence failure. A supplied empty string reports the `ConfigValueNotEmpty` value failure. The two attributes are intentionally separate so operators can distinguish "nothing resolved" from "a resolved value was invalid."

### Scalar Value Validation

Scalar wrappers such as `Config<string>` and `ConfigStruct<int>` validate the resolved value from attributes placed on the wrapper class. This keeps simple configuration as a primitive while still giving startup-time validation:

```csharp
using ForgeTrust.AppSurface.Config;

[ConfigValueRange(1, 65535)]
public sealed class PortConfig : ConfigStruct<int>
{
}

[ConfigValueNotEmpty]
public sealed class ApiKeyConfig : Config<string>
{
}
```

Built-in scalar attributes are intentionally small:

| Attribute | Supported values | Behavior |
| --- | --- | --- |
| `ConfigValueNotEmptyAttribute` | `string`, `Guid` | Rejects empty or whitespace-only strings and `Guid.Empty`. |
| `ConfigValueRangeAttribute(int minimum, int maximum)` | `int`, `double` | Rejects values outside the inclusive range. Integer bounds are widened for double values, so `[ConfigValueRange(1, 5)]` works on `ConfigStruct<double>`. |
| `ConfigValueRangeAttribute(double minimum, double maximum)` | `int`, `double` | Rejects values outside the inclusive range using double comparisons. |
| `ConfigValueMinLengthAttribute(int length)` | `string` | Rejects strings shorter than the configured length. |

An unsupported built-in attribute and value-type pairing fails as a structured `ConfigurationValidationException`. For example, `[ConfigValueMinLength(3)]` on `ConfigStruct<int>` reports that the attribute supports `String` values instead of silently passing or throwing a cast exception.

Use `ValidateValue` when the scalar rule is specific to your domain:

```csharp
using System.ComponentModel.DataAnnotations;
using ForgeTrust.AppSurface.Config;

public sealed class TenantSlugConfig : Config<string>
{
    protected override IEnumerable<ValidationResult>? ValidateValue(
        string value,
        ValidationContext validationContext)
    {
        if (value.Contains("..", StringComparison.Ordinal))
        {
            return [new ValidationResult("Tenant slugs cannot contain '..'.")];
        }

        return null;
    }
}
```

Scalar validation runs only for non-null resolved scalar values. `[ConfigValueNotEmpty]` rejects an empty value that was supplied by a provider or default, but it does not make a missing config value required. Use `[ConfigKeyRequired]` when the key itself must exist.

When both wrapper attributes and `ValidateValue` fail, attribute failures are reported first. Hook exceptions are not wrapped; they bubble to the caller so programming errors remain visible.

### Recursive Validation

Top-level validation follows the normal DataAnnotations runtime behavior. Nested objects and collection items are validated only when explicitly marked with Microsoft Options validation attributes:

```csharp
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

public sealed class DatabaseOptions
{
    [Required]
    public string? Host { get; init; }
}

public sealed class EndpointOptions
{
    [Required]
    public string? Url { get; init; }
}

public sealed class AppOptions
{
    [ValidateObjectMembers]
    public DatabaseOptions? Database { get; init; }

    [ValidateEnumeratedItems]
    public List<EndpointOptions> Endpoints { get; init; } = [];
}

public sealed class AppConfig : Config<AppOptions>
{
}
```

Nested member paths are reported with dot and index notation, such as `Database.Host` and `Endpoints[0].Url`. Recursive validation tracks the active traversal path so cycles do not loop forever while repeated references are still reported at each distinct reachable path. Null nested objects and null collection items are skipped unless the containing property has `[Required]`.

AppSurface uses the Microsoft marker attributes as the public authoring contract, but it owns the runtime traversal and `ConfigurationValidationException` output. It does not invoke the Options source generator or the `IValidateOptions<TOptions>` pipeline.

### Pitfalls

- DataAnnotations attributes such as `[Range]` on the wrapper class are not scalar validation attributes. Use AppSurface `ConfigValue*` attributes on scalar wrappers, or wrap related settings in an options object and use ordinary DataAnnotations on that object.
- `[ConfigKeyRequired]` on a wrapper and `[Required]` on an options property solve different problems. The wrapper attribute requires a config key to resolve; the DataAnnotations attribute validates a member on an object value that already resolved.
- Scalar validation validates values that exist after provider/default resolution. It is not a required-presence contract for missing keys.
- Required key presence does not report which provider did or did not supply a value. It only reports that provider/default resolution ended without a value.
- Built-in scalar attributes support only the value types documented above. Use `ValidateValue` for decimal, date/time, URI, domain-specific, or cross-cutting scalar rules.
- DataAnnotations can short-circuit. For example, object-level `IValidatableObject` validation may not run when property validation already failed.
- Recursive validation is opt-in. A nested object without `[ValidateObjectMembers]` and a collection without `[ValidateEnumeratedItems]` is not traversed.
- The `Validator` constructor overloads on Microsoft Options validation attributes are not supported by AppSurface Config validation.

## Example

Run the scalar validation sample from the repository root:

```bash
dotnet run --project examples/config-validation
```

It intentionally exits non-zero and prints the validation failure shape without echoing the invalid value.

## Notes

- The package is intended to make configuration access explicit and testable.
- Environment-aware providers make it easier to layer defaults, file configuration, and deployment-specific values.
