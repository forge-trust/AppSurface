<!-- appsurface:unreleased-entry section="migration-watch" -->

- The protected [EvidenceHost Linux runtime](../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision/README.md)
  now uses supervisor and isolated worker roles in the same AppSurface executable. The C# owner handles
  application and producer shutdown, counted output, retained report custody, and strict account cleanup;
  the former Python runtime launcher has been retired. Python remains in finite repository test drivers.
  This implementation does not make protected execution a supported public workflow or enable Trusted
  PR/release claims: continue using [planning and structural verification](../../start-here/evidencehost.md#current-support-status)
  while the [consumer acceptance requirements](../../docs/evidence/issue779-consumer-acceptance.md) remain open.
  Protected deployments still require an independently armed OS owner and sanitized startup before .NET
  begins; launching another ordinary CLI process is insufficient. See the
  [cutover and bootstrap reference](../../docs/evidence/issue779-csharp-runtime-cutover.md#deployment-and-startup)
  for the required packaging and isolation boundaries. Production registration and accepted-proof tables
  remain closed, and production limits and numerical coverage gates are unchanged.
