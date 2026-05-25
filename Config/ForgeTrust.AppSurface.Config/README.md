# ForgeTrust.AppSurface.Config

Strongly typed configuration primitives for AppSurface applications.

## Overview

This package provides the configuration layer for AppSurface modules. It combines file-based configuration, environment-aware providers, and strongly typed configuration objects so modules can consume configuration without hard-coding access patterns throughout the codebase.

## Release Guidance

AppSurface is preparing the first coordinated `v0.1.0` release. Before installing this package from a prerelease feed, read the [v0.1 release preview](../../releases/v0.1-preview.md) for current release risk, provisional migration guidance, and the finalization path to the tagged release note.

## Key Types

- **`AppSurfaceConfigModule`**: Registers the configuration services for an AppSurface application.
- **`IConfigManager`**: Central access point for resolving configuration values.
- **`IConfigProvider`**: Abstraction for reading configuration from a source.
- **`FileBasedConfigProvider`**: Loads configuration from files.
- **`IConfigAuditReporter`**: Builds source-aware configuration audit reports for known config entries.
- **`ConfigAuditTextRenderer`**: Renders a safe, human-readable audit report from the structured model.
- **`ConfigAuditCollectionTraversalAttribute`**: Enables bounded collection element traversal for a discovered config wrapper.
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

```csharp
var report = auditReporter.GetReport("Staging");
var text = textRenderer.Render(report);
```

Explicit registrations are useful for keys that are read directly through `IConfigManager` instead of a wrapper:

```csharp
services.AddConfigAuditKey<string>("Billing.Endpoint");
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

Console.WriteLine($"{source.FilePath}:{source.Location?.LineNumber}:{source.Location?.ByteColumnNumber}");
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

Known AppSurface audit entries only: the report includes discovered `Config<T>` / `ConfigStruct<T>` wrappers and
entries registered with `AddConfigAuditKey<T>()`. It does not enumerate every raw environment variable, unused JSON key,
or typo in a provider.

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

### Collection Traversal API

`ConfigAuditEntryOptions` is an immutable snapshot that controls collection expansion for one known entry:

| Option | Default | Behavior |
| --- | --- | --- |
| `TraverseCollectionElements` | `false` | Keeps existing reports opaque unless explicitly enabled. |
| `MaxCollectionDepth` | `4` | Stops nested collection traversal after the configured depth. |
| `MaxCollectionElements` | `128` | Stops after this many elements from any one collection. |
| `MaxReportNodes` | `4096` | Bounds total child nodes created for the entry. |
| `DisplayDictionaryKeys` | `true` | Allows non-sensitive dictionary keys to appear as labels. Sensitive keys are still redacted. |

For discovered wrappers, use `ConfigAuditCollectionTraversalAttribute`. Attribute presence enables traversal and its
named properties mirror the bounded options:

```csharp
[ConfigAuditCollectionTraversal(MaxCollectionElements = 32, DisplayDictionaryKeys = false)]
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
`[redacted-key-1]`; they are not stable identifiers across reports.

When wrapper discovery and manual registration use the same key, the wrapper remains the source of wrapper metadata
and validation behavior. Explicit manual option assignments override wrapper attribute options per property, including
assignments back to the default value. For example, a wrapper can hide dictionary keys by default while one manual
registration sets `DisplayDictionaryKeys = true` for a provider-only report composition.

### Collection Examples

| Shape | Traversal result |
| --- | --- |
| `string[]` or `List<T>` | Children render as `Key[0]`, `Key[1]`, preserving numeric order in text output. |
| `Dictionary<string,T>` | Non-sensitive keys render as `Key["name"]`; sensitive keys render as `Key[[redacted-key-1]]`. |
| null element | Emits an element child with a `null` display value and no grandchildren. |
| object element | Emits the element and then ordinary public property/field children below it. |
| non-string dictionary key | Uses the invariant string label when it is non-sensitive and display is enabled. |
| dotted or bracketed dictionary key | Uses a display label but inherits parent provenance because no safe exact source path is available. |
| unsupported enumerable | Emits a traversal diagnostic instead of enumerating. |
| multidimensional array | Emits a traversal diagnostic; only one-dimensional arrays are expanded. |

### Redaction Defaults

Audit reports are redacted by default before renderers see values. Sensitive-looking paths, keys, and environment
variable names render as `[redacted]`. The built-in fragments include `password`, `secret`, `token`, `apikey`, `key`,
`connectionstring`, `credential`, and `private`.

Redaction is deliberately conservative. It does not expose string lengths, sensitive collection counts, or sensitive
nested values. Source metadata remains visible unless a provider marks the source metadata itself as sensitive. This
keeps values redacted while still showing where a value came from. Treat rendered reports as internal support data:
provider names, file names, environment variable names, key names, and the existence of redacted values can still be
operationally sensitive.

Collection parent values are omitted instead of serialized when the collection key and source metadata are not
sensitive. This avoids leaking nested element fields such as `Password`, `Token`, `Secret`, or `ApiKey` through a raw
JSON dump. When element traversal is enabled, dictionary key labels and source selection are redacted before public
report objects are created. Raw sensitive dictionary keys do not appear in `Key`, `Element`, `Sources`, diagnostics, or
the text renderer.

### Audit Diagnostics

| Code | Severity | Meaning |
| --- | --- | --- |
| `config-audit-collection-kind-unsupported` | Warning | The value is an unsupported enumerable shape or a multidimensional array. |
| `config-audit-collection-depth-limit` | Warning | Traversal stopped at `MaxCollectionDepth`. |
| `config-audit-collection-element-limit` | Warning | Traversal stopped at `MaxCollectionElements` for one collection. |
| `config-audit-report-node-limit` | Warning | Traversal stopped at `MaxReportNodes` for the entry. |
| `config-audit-source-inherited` | Info | An element inherited parent provenance because an exact safe source path was unavailable. |
| `config-audit-source-unavailable` | Info | No provider source record was available for an element. |
| `config-audit-options-invalid` | Error | Entry options were invalid; safe defaults were used for bounded traversal values. |

### Migration Note

Existing reports keep the same collection shape unless an entry opts into collection traversal through
`ConfigAuditCollectionTraversalAttribute` or manual `TraverseCollectionElements`. Object child entries, redacted scalar
display values, provider precedence, and wrapper diagnostics continue to work as before. If a wrapper-discovered key
and a manual key registration use the same config key, wrapper metadata still wins for type and validation, while
explicit manual audit option assignments are merged into the selected entry.

### Audit Pitfalls

- Audit reports cover AppSurface-known entries, not every raw process environment variable or every unused file key.
- Collection display values are intentionally omitted rather than summarized or serialized; opt into element traversal
  for keys where element-level visibility is safe and useful.
- `ConfigAuditCollectionTraversalAttribute` is inherited. Put a new attribute on a derived wrapper when inherited
  traversal limits are too broad or too narrow for that derived key.
- Attribute values are compile-time metadata. Use manual `AddConfigAuditKey<T>()` options when traversal settings must
  be chosen dynamically at registration time.
- Sensitive dictionary key labels are report-local. Do not use `[redacted-key-1]` as a durable correlation identifier.
- Element traversal reports existing provider values. Environment-created missing collection elements, global debug
  expansion, diffing, raw-key drift analysis, and support-bundle export are separate product surfaces.
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
