# Should I Use AppSurface?

Start with plain ASP.NET Core. Reach for AppSurface when a startup concern is important enough to name, test, document, and reuse.

That rule works for one service and for many services. A single `Program.cs` can still benefit when the setup is complex, safety-sensitive, or easy to get wrong. The value becomes more obvious when the same concern appears across several services.

## Decision Matrix

| Situation | Plain ASP.NET Core is enough | AppSurface helps |
| --- | --- | --- |
| One small service has a simple startup file | Keep the code in `Program.cs`. | Not needed yet. |
| One service has setup policy that is hard to read or easy to misorder | Still possible, especially with local extension methods. | Name the policy as a module and test the behavior. |
| Several services copy the same startup block | Works, but drift is likely. | Put the shared convention behind one module contract. |
| A team needs onboarding docs for startup behavior | The docs must explain local code in each app. | The docs can point to one module and its behavior contract. |
| The app needs custom low-level host behavior | Plain ASP.NET Core gives full control. | Use AppSurface only if the module hook makes the policy clearer. |

## Use Plain ASP.NET Core When

- The setup is short, local, and obvious.
- The behavior belongs to only one app.
- The app already has a clear extension method or startup convention.
- A module would make the code harder to trace.

This is not a failure. ASP.NET Core already has excellent primitives for middleware, routing, options, status pages, exception handling, and dependency injection.

## Use AppSurface When

- The setup has a name your team uses in conversation.
- The setup must behave the same way across services.
- The setup has sharp edges that deserve tests.
- The setup should live near package docs rather than inside each app's `Program.cs`.
- A team lead needs one documented startup convention that service owners can follow.

The first proof in this docs path is browser status pages and production error pages. ASP.NET Core can handle those directly. AppSurface helps when the team wants one named behavior contract for browser HTML, API-friendly responses, preview routes, and safe production `500` pages.

## Pitfalls

- Do not adopt AppSurface to avoid learning ASP.NET Core. You still need to understand the host, middleware, routing, and options model.
- Do not standardize a concern before it has a real policy. A module with no policy is just indirection.
- Do not compare this Start Here path against larger frameworks. The honest first question is whether AppSurface is clearer than the plain ASP.NET Core startup code your team would otherwise write.

Next: [First Success Path](first-success-path.md)
