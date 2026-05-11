# First Success Path

Run the smallest web example first. Do not install optional packages yet.

From the repository root:

```bash
dotnet run --project examples/web-app -- --port 5055
```

Then open <http://127.0.0.1:5055> or run:

```bash
curl http://127.0.0.1:5055
```

Expected response:

```text
Hello World from the root!
```

That proves the base `ForgeTrust.AppSurface.Web` path: a root module, the AppSurface startup pipeline, and one mapped endpoint.

## What This Example Shows

The example uses `WebApp<ExampleModule>.RunAsync(...)` and maps the root endpoint from web options. The module also contributes its own endpoint at `/module`.

Try it:

```bash
curl http://127.0.0.1:5055/module
```

Expected response:

```text
Hello from the example module!
```

## What This Example Does Not Prove

This first run does not prove the status-page behavior used later in the evaluator path. It only proves the base web host starts and the module contributes behavior.

For the status-page proof, read [From Program.cs to a AppSurface Module](../guides/from-program-cs-to-module.md). That page points at the Web package behavior and the tests that verify browser status pages and production exception pages.

## Pitfalls

- Pass `--port 5055` when following docs. Without it, AppSurface may choose a deterministic development port for your worktree.
- Start with `ForgeTrust.AppSurface.Web` for a normal web app. Add optional modules only when the [package chooser](../packages/README.md) points to them.
- Treat the startup log as the source of truth if you choose a different port.

Next: [From Program.cs to a AppSurface Module](../guides/from-program-cs-to-module.md)
