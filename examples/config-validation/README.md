# Config validation example

This sample shows scalar validation on a strongly typed AppSurface config wrapper.

Run it from the repository root:

```bash
dotnet run --project examples/config-validation
```

The sample intentionally exits with a non-zero status so you can see the startup failure shape:

```text
Configuration validation failed for key 'PortConfig' (PortConfig -> Int32): 1 error(s).
- <value>: The configuration value must be between 1 and 65535.
Fix the configured value or relax the scalar rule on the config wrapper.
```

The failed value is not printed. Configuration values often include secrets, so validation output names the key and rule without echoing the attempted value.
