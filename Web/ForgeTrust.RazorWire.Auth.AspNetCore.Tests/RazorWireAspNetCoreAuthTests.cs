using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.RazorWire.Auth;
using ForgeTrust.RazorWire.Auth.AspNetCore;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.Auth.AspNetCore.Tests;

public sealed class RazorWireAspNetCoreAuthTests
{
    [Fact]
    public async Task Provider_DelegatesPolicyResourceAndCancellation()
    {
        var evaluator = new CapturingEvaluator(AppSurfaceAuthResult.Allowed());
        var services = new ServiceCollection();
        services.AddSingleton<IAppSurfaceAspNetCorePolicyEvaluator>(evaluator);
        var provider = new RazorWireAspNetCoreAuthResultProvider(services.BuildServiceProvider());
        using var cancellation = new CancellationTokenSource();
        var resource = new object();

        var result = await provider.AuthorizeAsync(
            new RazorWireAuthRequest("docs.publish", resource),
            cancellation.Token);

        Assert.Same(evaluator.Result, result);
        Assert.Equal("docs.publish", evaluator.PolicyName);
        Assert.Same(resource, evaluator.Resource);
        Assert.Equal(cancellation.Token, evaluator.CancellationToken);
    }

    [Fact]
    public async Task Provider_WhenEvaluatorMissing_ReturnsSetupFailure()
    {
        var services = new ServiceCollection();
        var provider = new RazorWireAspNetCoreAuthResultProvider(services.BuildServiceProvider());

        var result = await provider.AuthorizeAsync(new RazorWireAuthRequest("docs.publish"));

        Assert.Equal(AppSurfaceAuthOutcome.SetupFailure, result.Outcome);
        Assert.Equal(AppSurfaceAuthReason.MissingServices, result.Reason);
        Assert.Equal(
            RazorWireAuthDiagnostics.MissingAspNetCorePolicyEvaluator,
            result.Metadata[RazorWireAuthDiagnostics.DiagnosticCodeMetadataKey]);
    }

    [Fact]
    public void Provider_WhenServiceProviderNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RazorWireAspNetCoreAuthResultProvider(null!));
    }

    [Fact]
    public async Task Provider_WhenRequestNull_Throws()
    {
        var provider = new RazorWireAspNetCoreAuthResultProvider(new ServiceCollection().BuildServiceProvider());

        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.AuthorizeAsync(null!));
    }

    [Fact]
    public void AddRazorWireAspNetCoreAuth_WhenServicesNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RazorWireAspNetCoreAuthServiceCollectionExtensions.AddRazorWireAspNetCoreAuth(null!));
    }

    [Fact]
    public void AddRazorWireAspNetCoreAuth_RegistersProviderWithoutReplacingExisting()
    {
        var services = new ServiceCollection();
        var custom = new StaticProvider(AppSurfaceAuthResult.Forbidden());
        services.AddSingleton<IRazorWireAuthResultProvider>(custom);

        services.AddRazorWireAspNetCoreAuth();

        using var provider = services.BuildServiceProvider();
        Assert.Same(custom, provider.GetRequiredService<IRazorWireAuthResultProvider>());
    }

    [Fact]
    public void AddRazorWireAspNetCoreAuth_RegistersAdapterWhenMissing()
    {
        var services = new ServiceCollection();

        services.AddRazorWireAspNetCoreAuth();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<RazorWireAspNetCoreAuthResultProvider>(
            provider.GetRequiredService<IRazorWireAuthResultProvider>());
    }

    [Fact]
    public void Assemblies_DoNotReferenceDevAuth()
    {
        var adapterReferences = typeof(RazorWireAspNetCoreAuthServiceCollectionExtensions)
            .Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToArray();
        var coreReferences = typeof(IRazorWireAuthResultProvider)
            .Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToArray();

        Assert.Contains("ForgeTrust.AppSurface.Auth.AspNetCore", adapterReferences);
        Assert.Contains("ForgeTrust.RazorWire", adapterReferences);
        Assert.DoesNotContain("ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth", adapterReferences);
        Assert.DoesNotContain("ForgeTrust.AppSurface.Auth.AspNetCore", coreReferences);
        Assert.DoesNotContain("ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth", coreReferences);
    }

    private sealed class CapturingEvaluator : IAppSurfaceAspNetCorePolicyEvaluator
    {
        public CapturingEvaluator(AppSurfaceAuthResult result)
        {
            Result = result;
        }

        public AppSurfaceAuthResult Result { get; }

        public string? PolicyName { get; private set; }

        public object? Resource { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<AppSurfaceAuthResult> AuthorizeAsync(
            string policyName,
            object? resource = null,
            CancellationToken cancellationToken = default)
        {
            PolicyName = policyName;
            Resource = resource;
            CancellationToken = cancellationToken;
            return Task.FromResult(Result);
        }
    }

    private sealed class StaticProvider : IRazorWireAuthResultProvider
    {
        private readonly AppSurfaceAuthResult _result;

        public StaticProvider(AppSurfaceAuthResult result)
        {
            _result = result;
        }

        public Task<AppSurfaceAuthResult> AuthorizeAsync(
            RazorWireAuthRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }
    }
}
