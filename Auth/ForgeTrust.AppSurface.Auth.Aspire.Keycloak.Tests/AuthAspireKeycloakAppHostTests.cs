extern alias AppHost;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Aspire;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;
using AuthAspireKeycloakComponent = AppHost::AuthAspireKeycloakAppHost.AuthAspireKeycloakComponent;
using AuthAspireKeycloakLifecycleWorkerComponent = AppHost::AuthAspireKeycloakAppHost.AuthAspireKeycloakLifecycleWorkerComponent;
using AuthAspireKeycloakReadinessGateComponent = AppHost::AuthAspireKeycloakAppHost.AuthAspireKeycloakReadinessGateComponent;
using AuthAspireKeycloakWebComponent = AppHost::AuthAspireKeycloakAppHost.AuthAspireKeycloakWebComponent;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AuthAspireKeycloakAppHostTests
{
    [Fact]
    public void WebProofComponent_UsesTheRegisteredFixedRedirectPortForItsPublicEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var context = new AspireStartupContext(builder);
        var keycloak = new AuthAspireKeycloakComponent();
        var readinessGate = new AuthAspireKeycloakReadinessGateComponent(keycloak);
        var lifecycleWorker = new AuthAspireKeycloakLifecycleWorkerComponent(readinessGate);
        var web = context.Resolve(new AuthAspireKeycloakWebComponent(keycloak, lifecycleWorker));
        var worker = context.Resolve(lifecycleWorker);

        var endpoint = Assert.Single(
            web.Resource.Annotations.OfType<EndpointAnnotation>(),
            annotation => string.Equals(annotation.Name, "http", StringComparison.Ordinal));

        Assert.Equal(AppSurfaceKeycloakDefaults.WebProofPort, endpoint.Port);
        Assert.Equal(AppSurfaceKeycloakDefaults.WebProofPort, endpoint.TargetPort);
        Assert.False(endpoint.IsProxied);

        var workerDependency = Assert.Single(worker.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Equal(AuthAspireKeycloakReadinessGateComponent.ResourceName, workerDependency.Resource.Name);
        Assert.Equal(WaitType.WaitForCompletion, workerDependency.WaitType);

        var webDependencies = web.Resource.Annotations.OfType<WaitAnnotation>().ToArray();
        Assert.Contains(
            webDependencies,
            dependency => dependency.WaitType == WaitType.WaitForCompletion
                && string.Equals(dependency.Resource.Name, AuthAspireKeycloakLifecycleWorkerComponent.ResourceName, StringComparison.Ordinal));
    }
}
