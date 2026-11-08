<!-- appsurface:unreleased-entry section="included" -->

- **Payload-free Cloud Tasks activation:** the optional
  [ForgeTrust.AppSurface.Durable.CloudTasks](../../Durable/ForgeTrust.AppSurface.Durable.CloudTasks/README.md)
  package submits a caller-named empty wake to an existing Durable external-activation host using an explicit creator
  token source and worker OIDC target. Closed receipts distinguish acceptance, name conflict, definitive rejection,
  and uncertain outcomes; an uncertain result keeps recovery application-owned. The official SDK transport is bounded
  and has no application/GAX retries. Its local TLS fixtures observe two streams/one acceptance for one
  `REFUSED_STREAM` followed by success, four streams/zero acceptances for repeated refusal, four streams/zero
  acceptances for GOAWAY last-stream-id zero, and one accepted request with a lost response classified
  `Indeterminate/ASCT305`. These fixture results were observed with .NET SDK 10.0.402 and .NET runtime 10.0.13, and separately
  with .NET runtime 10.0.2. See the [canonical activation reference](../../Durable/cloudtasks-activation.md) and
  [local package proof](../../Durable/ForgeTrust.AppSurface.Durable.CloudTasks/README.md#local-proof). Packed GCP and
  neutral-consumer isolation remain release gates; local fixtures do not prove Google-issued identity, live IAM, or
  real task delivery.
