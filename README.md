# AppSurface

[![Build](https://github.com/forge-trust/AppSurface/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/forge-trust/AppSurface/actions/workflows/build.yml)
[![Package Gate](https://github.com/forge-trust/AppSurface/actions/workflows/package-gate.yml/badge.svg?branch=main)](https://github.com/forge-trust/AppSurface/actions/workflows/package-gate.yml)
[![Package Artifacts](https://github.com/forge-trust/AppSurface/actions/workflows/package-artifacts.yml/badge.svg)](https://github.com/forge-trust/AppSurface/actions/workflows/package-artifacts.yml)
[![Code Quality](https://github.com/forge-trust/AppSurface/actions/workflows/code-quality.yml/badge.svg?branch=main)](https://github.com/forge-trust/AppSurface/actions/workflows/code-quality.yml)
[![CodeQL](https://img.shields.io/badge/CodeQL-enabled-0f6fff?logo=github&logoColor=white)](https://github.com/forge-trust/AppSurface/security/code-scanning)
[![Codecov](https://codecov.io/gh/forge-trust/AppSurface/branch/main/graph/badge.svg)](https://codecov.io/gh/forge-trust/AppSurface)
[![NuGet Packages](https://img.shields.io/badge/NuGet-package%20chooser-004880?logo=nuget&logoColor=white)](./packages/README.md)
[![Dependabot](https://img.shields.io/badge/dependabot-enabled-025e8c?logo=dependabot&logoColor=white)](https://github.com/forge-trust/AppSurface/blob/main/.github/dependabot.yml)
[![Security Policy](https://img.shields.io/badge/security-private%20reporting-2ea44f?logo=github)](./SECURITY.md)
[![License](https://img.shields.io/badge/license-Polyform%20Small%20Business-blue)](./licensing.md)

AppSurface is a family of .NET 10 libraries and tools you can adopt one capability at a time.
You might use [RazorWire](./Web/ForgeTrust.RazorWire/README.md) to update part of an MVC page after a form
submission, or add the [coverage CLI](./Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run)
to your repository's tool manifest to collect one report across your test projects.

## Why we're building AppSurface

We want coding agents to be able to follow your team's conventions, and we want you to be able to check their
work. A good place for a convention is in the repository, where it survives the end of a chat. For example,
the [Durable worker starter](./start-here/durable-worker.md) gives you C# you own and a test that submits a job
and checks its result. The next person or agent adding a job has a working example to follow.

When tests miss a changed line, the [coverage gate](./Cli/ForgeTrust.AppSurface.Cli/README.md#agent-actionable-patch-targets)
can report its path and line number in both JSON and Markdown. An agent can go straight to that line to write
a test. In review, you can open the same code and judge whether the test exercises the case you care about.

## Choose a starting point

The [package chooser](./packages/README.md) lists what to install for each job, what comes with it, and which
packages are in preview. Use AppSurface for evaluation while it is under development. Before upgrading,
check the [release notes](./releases/README.md) and the [pre-1.0 upgrade policy](./releases/upgrade-policy.md).

- [Add AppSurface to a web app](./start-here/first-success-path.md#package-first-path). Start with a fresh
  ASP.NET Core project and map a route. For an existing service, the [module guide](./start-here/should-i-use-appsurface.md)
  helps you decide which setup to keep in `Program.cs`.
- [Use your existing authorization policies](./start-here/auth-adoption-ladder.md). Switch personas in the
  [Auth and RazorWire example](./examples/auth-web-razorwire-proof/README.md) to see how one ASP.NET Core
  policy determines both the API response and what the page shows.
- [Run background work with PostgreSQL](./start-here/durable-worker.md). Generate a worker, submit its first
  job, and inspect the saved result.
- [Publish docs from a repository](./Web/ForgeTrust.AppSurface.Docs/use-appsurface-docs.md). Serve Markdown
  guides alongside API reference generated from source.
- [Choose CI checks based on what changed](./start-here/evidencehost.md). EvidenceHost can explain which
  checks a change needs before you run them. AppSurface uses the
  [same policy locally and in CI](https://github.com/forge-trust/AppSurface/blob/main/.github/evidence.policy.json).

The [examples directory](./examples/README.md) also has a [console command](./examples/console-app/README.md),
an [Aspire AppHost](./examples/aspire-apphost/README.md), and a
[flow that waits for approval](./examples/flow-approval-local/README.md).

## Run the web example

An [AppSurface module](./ForgeTrust.AppSurface.Core/README.md) gives startup code a home when several apps
need the same behavior. A web API and a console worker, for example, can share the
[module that configures OpenTelemetry](./Observability/ForgeTrust.AppSurface.Observability/README.md).
A web module can also add routes of its own.

To see a module in a running app, clone this repository and install the .NET SDK version pinned in
[global.json](https://github.com/forge-trust/AppSurface/blob/main/global.json). From the repository root, run the
[web example](./examples/web-app/README.md).

```bash
dotnet run --project examples/web-app -- --port 5055
```

In another terminal, request the route the app defines in `Program.cs`, then the route that
[ExampleModule](https://github.com/forge-trust/AppSurface/blob/main/examples/web-app/ExampleModule.cs) adds.

```console
$ curl http://127.0.0.1:5055
Hello World from the root!
$ curl http://127.0.0.1:5055/module
Hello from the example module!
```

For a module you might share across services, see the [error-page example](./guides/from-program-cs-to-module.md).
Every service that opts in can show a readable 404 to someone using a browser and preserve the response
its API clients expect.

## Feedback and contributing

Use the [issue forms](https://github.com/forge-trust/AppSurface/issues/new/choose) for bugs, feature requests,
or a quickstart that left you stuck. The [contributor guide](./CONTRIBUTING.md) covers local setup.
Run `task` to list the [repository tasks](./CONTRIBUTING.md#repository-tasks) for building, testing, and
verifying a change. Report vulnerabilities through the [private security reporting process](./SECURITY.md).

<a id="coverage-efficiency-evidence-for-issue-728"></a>

If you're investigating a slow coverage run, see the [coverage timing workflow](./Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-efficiency-evidence-workflow).

## License

AppSurface uses the [PolyForm Small Business License 1.0.0](https://github.com/forge-trust/AppSurface/blob/main/LICENSE).
See [licensing](./licensing.md) for commercial terms and support.
