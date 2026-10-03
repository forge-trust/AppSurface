<!-- appsurface:unreleased-entry section="included" -->
### DevAuth persona fixture activation

- Hosts can opt into an awaited, request-scoped DevAuth handler that prepares local fixtures after a valid persona selection queues its protected cookie and before DevAuth redirects or renders the control page. The default remains unchanged without a handler; hosts own readiness, persistence, deadlines, and recovery because cookie delivery and fixture writes are not atomic. See the [activation contract](../../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md#opt-in-persona-fixture-activation) and [compiled shared-candidate example](../../examples/auth-aspnetcore-dev-auth/README.md#persona-fixture-activation).
