# ForgeTrust.Runnable.Config

Strongly typed configuration primitives for Runnable applications.

## Overview

This package provides the configuration layer for Runnable modules. It combines file-based configuration, environment-aware providers, and strongly typed configuration objects so modules can consume configuration without hard-coding access patterns throughout the codebase.

## Key Types

- **`RunnableConfigModule`**: Registers the configuration services for a Runnable application.
- **`IConfigManager`**: Central access point for resolving configuration values.
- **`IConfigProvider`**: Abstraction for reading configuration from a source.
- **`FileBasedConfigProvider`**: Loads configuration from files.
- **`IConfigAuditReporter`**: Builds source-aware configuration audit reports for known config entries.
- **`ConfigAuditTextRenderer`**: Renders a safe, human-readable audit report from the structured model.
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
public sealed class MyModule : RunnableConfigModule
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
dotnet add package ForgeTrust.Runnable.Config --project <path-to-your-app.csproj>
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

### Redaction Defaults

Audit reports are redacted by default before renderers see values. Sensitive-looking paths, keys, and environment
variable names render as `[redacted]`. The built-in fragments include `password`, `secret`, `token`, `apikey`, `key`,
`connectionstring`, `credential`, and `private`.

Redaction is deliberately conservative. It does not expose string lengths, sensitive collection counts, or sensitive
nested values. Source metadata remains visible unless a provider marks the source metadata itself as sensitive. This
keeps reports safe to paste into support issues while still showing where a value came from.

### Audit Pitfalls

- Audit reports cover Runnable-known entries, not every raw process environment variable or every unused file key.
- Provider-discovered raw key enumeration is intentionally separate from the first audit surface.
- File origins include file path and config path. Line and column origins are not part of the first report.
- Validation diagnostics name keys and rules; they do not include attempted secret values.
- A direct environment object value replaces the whole object, while child environment variables produce member-level
  patch provenance.

## Environment Overrides

Environment variables override file-based providers. Runnable supports both the legacy flattened shape and the
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

When `App.Settings` is resolved as an object, Runnable first looks for a direct environment value. If none exists,
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

Runnable has two validation paths:

- object-valued config models use ordinary DataAnnotations on the model and its members
- scalar config wrappers use Runnable scalar attributes or a `ValidateValue` override on the wrapper

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

When validation fails, Runnable throws one `ConfigurationValidationException` for the config key. The exception message is designed for logs:

```text
Configuration validation failed for key 'RetryConfig' (RetryConfig -> RetryOptions): 1 error(s).
- Count: The field Count must be between 1 and 5.
```

The exception also exposes structured failures through `Failures`. Each failure includes the config key, wrapper type, value type, member names, and validation message. Attempted values are not exposed because config values often include secrets.

### Required Key Presence

Config wrappers are optional by default. A missing provider value with no default leaves `HasValue` false and skips value validation. Use `[ConfigKeyRequired]` when the key itself must resolve a value during startup:

```csharp
using ForgeTrust.Runnable.Config;

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

When a required wrapper has no provider value and no default, Runnable throws `ConfigurationValidationException` through the same startup failure family used by object and scalar validation:

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
using ForgeTrust.Runnable.Config;

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
using ForgeTrust.Runnable.Config;

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

Runnable uses the Microsoft marker attributes as the public authoring contract, but it owns the runtime traversal and `ConfigurationValidationException` output. It does not invoke the Options source generator or the `IValidateOptions<TOptions>` pipeline.

### Pitfalls

- DataAnnotations attributes such as `[Range]` on the wrapper class are not scalar validation attributes. Use Runnable `ConfigValue*` attributes on scalar wrappers, or wrap related settings in an options object and use ordinary DataAnnotations on that object.
- `[ConfigKeyRequired]` on a wrapper and `[Required]` on an options property solve different problems. The wrapper attribute requires a config key to resolve; the DataAnnotations attribute validates a member on an object value that already resolved.
- Scalar validation validates values that exist after provider/default resolution. It is not a required-presence contract for missing keys.
- Required key presence does not report which provider did or did not supply a value. It only reports that provider/default resolution ended without a value.
- Built-in scalar attributes support only the value types documented above. Use `ValidateValue` for decimal, date/time, URI, domain-specific, or cross-cutting scalar rules.
- DataAnnotations can short-circuit. For example, object-level `IValidatableObject` validation may not run when property validation already failed.
- Recursive validation is opt-in. A nested object without `[ValidateObjectMembers]` and a collection without `[ValidateEnumeratedItems]` is not traversed.
- The `Validator` constructor overloads on Microsoft Options validation attributes are not supported by Runnable Config validation.

## Example

Run the scalar validation sample from the repository root:

```bash
dotnet run --project examples/config-validation
```

It intentionally exits non-zero and prints the validation failure shape without echoing the invalid value.

## Notes

- The package is intended to make configuration access explicit and testable.
- Environment-aware providers make it easier to layer defaults, file configuration, and deployment-specific values.
