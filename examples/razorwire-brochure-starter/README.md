# Northstar brochure starter

Northstar is a fictional editorial studio and a small, package-first MVC starter. The application owns its controllers, views, routes, CSS, and artwork; [ForgeTrust.RazorWire](../../Web/ForgeTrust.RazorWire/README.md) supplies the host integration and runtime tag helpers.

## When to use this starter

Use this starter when you need a small, server-rendered brochure site with [RazorWire's page-navigation runtime](../../Web/ForgeTrust.RazorWire/Docs/page-navigation.md) and a documented static-export workflow. It is deliberately an application that consumes `ForgeTrust.RazorWire`; it is not a package or a package-publishing template.

Plain ASP.NET Core MVC is enough when conventional server-rendered pages are all you need and you do not need RazorWire's module/runtime features or its static-export workflow. Start with the platform's MVC template in that case, then adopt RazorWire only when those capabilities solve a real site requirement.

## Package-first setup

The application project has exactly one product package reference:

```xml
<RazorWirePackageVersion Condition="'$(RazorWirePackageVersion)' == ''">0.1.0</RazorWirePackageVersion>
<PackageReference Include="ForgeTrust.RazorWire" Version="$(RazorWirePackageVersion)" />
```

Restore from the package feed selected by the host:

```bash
dotnet restore ./examples/razorwire-brochure-starter/NorthstarBrochureStarter.csproj \
  -p:RazorWirePackageVersion=<RAZORWIRE_VERSION> \
  --source <PACKAGE_SOURCE>
```

Run locally:

```bash
dotnet run \
  --project ./examples/razorwire-brochure-starter/NorthstarBrochureStarter.csproj \
  --urls http://localhost:5233
```

Export locally with a host-selected [RazorWire CLI tool package](../../Web/ForgeTrust.RazorWire.Cli/README.md):

```bash
dotnet tool install ForgeTrust.RazorWire.Cli \
  --tool-path ./.tools/razorwire \
  --version <RAZORWIRE_CLI_VERSION> \
  --source <RAZORWIRE_CLI_PACKAGE_SOURCE>

./.tools/razorwire/razorwire export \
  --url http://localhost:5233 \
  --output ./dist/northstar
```

CDN mode is the default export mode, so no `--mode cdn` flag is needed. Replace the placeholders with package artifacts available to the host. For the repository's broader package and CLI guidance, continue with the linked [RazorWire README](../../Web/ForgeTrust.RazorWire/README.md), which also points to this starter as the smallest package-consumer baseline.

## Contact handoff

The contact page is intentionally a browser-only demo. It shows visible required fields and a GET-only confirmation preview, and both the contact page and confirmation say, “No message was sent.” Actual contact delivery remains owned by the host application, which should supply request validation, spam protection, email delivery, a protected workflow, storage, notification policy, and a success page. The starter intentionally selects no provider for any of those responsibilities.

The canonical preview link is `/thank-you`; `/thank-you.html` is the equivalent static-friendly alias. To make the form deliver a real message, adapt the host-owned workflow rather than treating this brochure as the delivery boundary.
