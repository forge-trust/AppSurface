<!-- appsurface:unreleased-entry section="included" -->

- **Cloud Tasks joins the coordinated public preview.** Google Cloud callers can install
  [ForgeTrust.AppSurface.Durable.GoogleCloudTasks](../../Durable/ForgeTrust.AppSurface.Durable.GoogleCloudTasks/README.md#install-and-configure)
  at the same preview version as their AppSurface packages after NuGet publication. The packed checks verify the
  public API and keep Google transport dependencies out of neutral Durable consumers. Before rollout, verify identity,
  queue permissions, routing, and delivery in the target deployment using the
  [Cloud Tasks activation reference](../../Durable/cloudtasks-activation.md).
