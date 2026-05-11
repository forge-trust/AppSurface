# Web app example

This example shows how to build a minimal ASP.NET Core application using **ForgeTrust.AppSurface.Web**.

Run the application:

```bash
dotnet run --project examples/web-app -- --port 5055
```

Then open <http://127.0.0.1:5055> or run:

```bash
curl http://127.0.0.1:5055
```

Expected output:

```text
Hello World from the root!
```

The explicit `--port` keeps the quickstart copy-paste stable. Without it, AppSurface may choose a deterministic development port for your current worktree; use the startup log as the source of truth.
