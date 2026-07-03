# Use Env Or Key-Per-File In CI And Containers

LocalSecrets is a local user-session feature. CI jobs, containers, service accounts, and headless Linux sessions should
prefer environment variables, key-per-file, or a remote secret provider.

Use environment variables when the host already owns secret injection:

```bash
Stripe__ApiKey=<secret> dotnet run
```

Use key-per-file when container orchestration mounts secrets as files. Keep LocalSecrets out of CI so platform prompt,
DBus, Keychain, or user-profile failures do not block non-interactive builds.

LocalSecrets remains useful before CI: it lets a developer run the same logical config key locally, then move that key to
environment variables or Google Secret Manager without changing app code.
