# ForgeTrust.AppSurface.Web.OpenApi.Tests

This test project verifies the public behavior of `ForgeTrust.AppSurface.Web.OpenApi`.

## Coverage

- `AppSurfaceWebOpenApiModule.ConfigureServices` registers OpenAPI document generation and endpoint API explorer services.
- `AppSurfaceWebOpenApiModule.ConfigureEndpoints` maps the conventional `/openapi/{documentName}.json` endpoint.
- Hosted integration coverage confirms generated OpenAPI documents use the `StartupContext.ApplicationName` title.
- Hosted integration coverage confirms the default document and operation transformers remove framework-owned `ForgeTrust.AppSurface.Web` tags while preserving consumer tags.

The tests exercise the module through its public `IAppSurfaceWebModule` entry points and a real `WebStartup<AppSurfaceWebOpenApiModule>` host. This keeps the contract focused on observable package behavior rather than private OpenAPI option internals.
