<!-- appsurface:unreleased-entry section="included" -->

- [Caching](../../Caching/ForgeTrust.AppSurface.Caching/README.md#deterministic-freshness-transitions) lets a concrete `Memo` owner supply a shared `TimeProvider` and await registered background refreshes before shutdown. Existing constructors keep system time; the drain joins a snapshot without cancelling refreshes or treating failed refreshes as successful values.
- [Tailwind](../../Web/ForgeTrust.AppSurface.Web.Tailwind/README.md) recognizes styled version and completion messages as informational while retaining original stderr bytes. Unknown messages and unsupported escape sequences retain error severity.
- The [native PostgreSQL template smoke](../../tools/ForgeTrust.AppSurface.PackageIndex/README.md) uses short cluster paths on Linux and macOS so deeply nested temporary directories do not exceed PostgreSQL's Unix socket limit. Existing ownership checks and cleanup budgets still apply.
