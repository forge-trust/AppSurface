# Start Here for Runnable Evaluators

Use this path when you need to decide whether Runnable belongs in an ASP.NET Core service or across a set of services.

The target reader is a team lead, engineering manager, architect, or senior developer who owns startup consistency. You may be evaluating from one service first. That is fine. The question is still the same: when does a named Runnable module make startup clearer than keeping the setup directly in `Program.cs`?

## The Short Path

1. Read [Should I Use Runnable?](should-i-use-runnable.md) to decide whether plain ASP.NET Core is still enough.
2. Run [First Success Path](first-success-path.md) to prove the smallest web app starts.
3. Read [From Program.cs to a Runnable Module](../guides/from-program-cs-to-module.md) to inspect one concrete startup concern as a module.
4. Keep [Troubleshoot Startup and Modules](../troubleshooting/startup-and-modules.md) nearby if the first run or module composition does not behave as expected.
5. Use the [Runnable Glossary](../concepts/glossary.md) when a term in package docs is unfamiliar.

## What Runnable Is

Runnable is a small module composition layer over familiar .NET and ASP.NET Core primitives. It does not replace the host, middleware, routing, dependency injection, or options model.

Runnable gives you a place to name startup concerns. A module can register services, configure host behavior, configure web options, register middleware, and map endpoints through one documented surface.

Start with the [package chooser](../packages/README.md) when you need an install path. Start with this evaluator path when you need the adoption argument.

## What To Look For

Look for a setup concern that is repeated, safety-sensitive, or easy to configure differently in each service.

Good early candidates look like this:

- Each service needs the same browser-facing recovery behavior.
- Each service needs the same API-friendly exception or status behavior.
- A team wants the same startup convention without copying a block of `Program.cs`.
- A setup concern has enough policy that it deserves a name, tests, and reference docs.

Runnable is not valuable because `Program.cs` is bad. It is valuable when `Program.cs` is carrying a policy that your team should understand, reuse, and verify.

## Common Pitfalls

- Do not install every optional Runnable package first. Pick the package that matches the app.
- Do not hide ASP.NET Core. Runnable should make the underlying ASP.NET Core behavior easier to find, not harder.
- Do not turn a one-off local choice into a shared module too early. Name the concern when the policy matters.
- Do not compare Runnable against large application frameworks in this Start Here path. The first comparison is plain ASP.NET Core startup code.
