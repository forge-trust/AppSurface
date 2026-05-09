# From Program.cs to a Runnable Module

The clearest Runnable proof is not a smaller `Program.cs`. It is a startup policy that becomes named, tested, and reusable.

This page uses browser status pages and production error pages from `ForgeTrust.Runnable.Web` as that proof.

## Plain ASP.NET Core Baseline

ASP.NET Core can handle this directly. A small app can use built-in status-code pages, exception handling, Problem Details, endpoint handlers, or custom middleware.

That baseline is correct when the policy is local and obvious.

The cost appears when several services must remember the same details:

- Which requests should get browser HTML instead of API-friendly behavior?
- Which status codes have conventional pages?
- Which route previews are safe for checking the pages?
- Which production `500` page avoids leaking exception details?
- Where does each service document the convention?

Plain ASP.NET Core gives you the primitives. Runnable gives the team one named place to express the convention.

## Runnable Module Version

Runnable Web exposes the concern through `WebOptions.Errors`.

```csharp
public void ConfigureWebOptions(StartupContext context, WebOptions options)
{
    options.Mvc = options.Mvc with
    {
        MvcSupportLevel = MvcSupport.Controllers
    };

    options.Errors.UseConventionalBrowserStatusPages();
    options.Errors.UseConventionalExceptionPage();
}
```

The browser status page call enables conventional browser-facing pages for empty `401`, `403`, and `404` responses. The production exception page call enables a generic browser-facing `500` page for unhandled exceptions.

These are separate features. Status-code pages do not catch thrown exceptions.

## Behavior Contract

Runnable Web verifies this behavior in package tests:

```bash
dotnet test Web/ForgeTrust.Runnable.Web.Tests/ForgeTrust.Runnable.Web.Tests.csproj --filter "BrowserStatusPageTests|ConventionalExceptionPageTests"
```

The source-of-truth test classes are:

- `Web/ForgeTrust.Runnable.Web.Tests/BrowserStatusPageTests.cs`
- `Web/ForgeTrust.Runnable.Web.Tests/ConventionalExceptionPageTests.cs`

Those tests cover the HTTP contract this page relies on:

- Browser requests to empty supported status responses render recovery-oriented HTML and preserve the original status.
- Direct preview routes such as `/_runnable/errors/404` render the conventional page.
- JSON and non-HTML requests do not receive browser HTML.
- Production exception pages return a generic `500` page for browser requests.
- The generic `500` page includes a request id but not exception messages, stack traces, headers, cookies, route values, or form fields.
- JSON requests to throwing endpoints do not render the conventional HTML exception page.

## What The Module Buys

The value is not that ASP.NET Core cannot do this. It can.

The value is that a team can point service owners at one module-level behavior contract instead of copying a `Program.cs` block and hoping every service keeps the same edge cases.

For a single service, this is useful when the error posture is important enough to name. Across several services, it becomes a standard.

## Pitfalls

- Do not enable browser pages for API-only behavior that should stay JSON or Problem Details.
- Do not expose exception details on production `500` pages.
- Do not assume status-code pages catch thrown exceptions. They handle empty status responses; exception handling is a separate pipeline.
- Do not duplicate the full Web package reference here. Use the [Runnable Web README](../Web/ForgeTrust.Runnable.Web/README.md) for the full API shape.

Next recovery path: [Troubleshoot Startup and Modules](../troubleshooting/startup-and-modules.md)
