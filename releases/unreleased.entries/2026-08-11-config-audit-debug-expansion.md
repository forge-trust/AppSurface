<!-- appsurface:unreleased-entry section="taking-shape" -->
### Safe configuration-audit debug expansion

- [`ForgeTrust.AppSurface.Config`](../../Config/ForgeTrust.AppSurface.Config/README.md) now offers an explicit safe
  debug report mode for already-known configuration collections. Existing `GetReport(string)`, diagnostics commands,
  text output, and default JSON remain canonical. Operators can request bounded expanded topology through
  `ConfigAuditReportRequest`, app-owned `config diagnostics --debug`, or the protected Web diagnostics selector
  `?mode=expand-known-entry-collections`; redaction, provenance, per-entry limits, and a fixed report-wide child-node
  cap remain in force. Custom reporters require no migration for canonical reports and opt into expansion only by
  implementing the request overload with equivalent safeguards. See the [Config](../../Config/ForgeTrust.AppSurface.Config/README.md)
  and [Web diagnostics](../../Web/ForgeTrust.AppSurface.Web/README.md#config-audit-http-diagnostics) guides for valid
  input, recovery, and host authorization/retention guidance.
