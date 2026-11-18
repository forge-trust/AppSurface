<!-- appsurface:unreleased-entry section="included" -->

- The [Durable worker template](../../Durable/ForgeTrust.AppSurface.Durable.Templates/README.md) checks Docker availability through its daemon-version request, avoiding unrelated client metadata plugins that can exhaust the existing five-second probe budget. Docker remains required, and the real container proof and cleanup checks are unchanged. Existing generated projects can copy the documented fixture change; updating the template does not modify application-owned source.
