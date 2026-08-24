extern alias AppHost;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Aspire;
using ForgeTrust.AppSurface.Auth.Aspire.Keycloak;
using AuthAspireKeycloakCandidateFixtureComponent = AppHost::AuthAspireKeycloakAppHost.AuthAspireKeycloakCandidateFixtureComponent;
using AuthAspireKeycloakComponent = AppHost::AuthAspireKeycloakAppHost.AuthAspireKeycloakComponent;
using AuthAspireKeycloakIdentityBootstrapComponent = AppHost::AuthAspireKeycloakAppHost.AuthAspireKeycloakIdentityBootstrapComponent;
using AuthAspireKeycloakWebComponent = AppHost::AuthAspireKeycloakAppHost.AuthAspireKeycloakWebComponent;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AuthAspireKeycloakAppHostTests
{
    [Fact]
    public void WebProofComponent_UsesTheRegisteredFixedRedirectPortAfterTheTwoOrderedConsumerSeeds()
    {
        const string enableSeeds = "AUTH_ASPIRE_KEYCLOAK_ENABLE_LOCAL_SEEDS";
        var previousEnableSeeds = Environment.GetEnvironmentVariable(enableSeeds);
        Environment.SetEnvironmentVariable(enableSeeds, "true");
        try
        {
            var builder = DistributedApplication.CreateBuilder([]);
            var context = new AspireStartupContext(builder);
            var keycloak = new AuthAspireKeycloakComponent();
            var identityBootstrap = new AuthAspireKeycloakIdentityBootstrapComponent(keycloak);
            var candidateFixture = new AuthAspireKeycloakCandidateFixtureComponent(keycloak, identityBootstrap);
            var web = context.Resolve(new AuthAspireKeycloakWebComponent(keycloak, candidateFixture));
            var identity = context.Resolve(identityBootstrap);
            var candidate = context.Resolve(candidateFixture);

            var endpoint = Assert.Single(
                web.Resource.Annotations.OfType<EndpointAnnotation>(),
                annotation => string.Equals(annotation.Name, "http", StringComparison.Ordinal));

            Assert.Equal(AppSurfaceKeycloakDefaults.WebProofPort, endpoint.Port);
            Assert.Equal(AppSurfaceKeycloakDefaults.WebProofPort, endpoint.TargetPort);
            Assert.False(endpoint.IsProxied);

            var identityDependency = Assert.Single(identity.Resource.Annotations.OfType<WaitAnnotation>());
            Assert.Equal("keycloak-realm-ready", identityDependency.Resource.Name);
            Assert.Equal(WaitType.WaitForCompletion, identityDependency.WaitType);

            var candidateDependencies = candidate.Resource.Annotations.OfType<WaitAnnotation>().ToArray();
            Assert.Contains(
                candidateDependencies,
                dependency => dependency.WaitType == WaitType.WaitForCompletion
                    && string.Equals(dependency.Resource.Name, "keycloak-realm-ready", StringComparison.Ordinal));
            Assert.Contains(
                candidateDependencies,
                dependency => dependency.WaitType == WaitType.WaitForCompletion
                    && string.Equals(dependency.Resource.Name, identity.Resource.Name, StringComparison.Ordinal));

            var webDependencies = web.Resource.Annotations.OfType<WaitAnnotation>().ToArray();
            Assert.Contains(
                webDependencies,
                dependency => dependency.WaitType == WaitType.WaitForCompletion
                    && string.Equals(dependency.Resource.Name, candidate.Resource.Name, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(enableSeeds, previousEnableSeeds);
        }
    }
}
