using System.Security.Claims;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.Tests;

public sealed class AppSurfaceAspNetCorePolicyEvaluatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AuthorizeAsync_WithBlankPolicyName_ThrowsArgumentException(string? policyName)
    {
        using var scope = CreatePolicyScope(PolicyAuthorizationResult.Success());
        SetHttpContext(scope, Principal("sub", "user-1"));

        var evaluator = scope.ServiceProvider.GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => evaluator.AuthorizeAsync(policyName!));
    }

    [Fact]
    public async Task AuthorizeAsync_WithoutHttpContext_ReturnsMissingServices()
    {
        using var scope = CreatePolicyScope(PolicyAuthorizationResult.Success());

        var result = await scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
            .AuthorizeAsync("Operators");

        Assert.Equal(AppSurfaceAuthReason.MissingServices, result.Reason);
        Assert.Equal("missing_http_context", result.Metadata[AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode]);
    }

    [Fact]
    public async Task AuthorizeAsync_WithoutAuthorizationServices_ReturnsMissingServices()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceAspNetCoreAuth();
        using var scope = services.BuildServiceProvider().CreateScope();
        SetHttpContext(scope, Principal("sub", "user-1"));

        var result = await scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
            .AuthorizeAsync("Operators");

        Assert.Equal(AppSurfaceAuthReason.MissingServices, result.Reason);
        Assert.Equal(
            typeof(IAuthorizationPolicyProvider).FullName,
            result.Metadata[AppSurfaceAspNetCoreAuthMetadataKeys.MissingService]);
    }

    [Fact]
    public async Task AuthorizeAsync_WithoutPolicyEvaluator_ReturnsMissingServices()
    {
        var services = new ServiceCollection();
        services.AddAuthorization(options => options.AddPolicy("Operators", policy => policy.RequireAssertion(_ => true)));
        services.RemoveAll<IPolicyEvaluator>();
        services.AddAppSurfaceAspNetCoreAuth();
        using var scope = services.BuildServiceProvider().CreateScope();
        SetHttpContext(scope, Principal("sub", "user-1"));

        var result = await scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
            .AuthorizeAsync("Operators");

        Assert.Equal(AppSurfaceAuthReason.MissingServices, result.Reason);
        Assert.Equal(typeof(IPolicyEvaluator).FullName, result.Metadata[AppSurfaceAspNetCoreAuthMetadataKeys.MissingService]);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenPolicyEvaluatorCannotBeConstructed_ReturnsMissingServices()
    {
        var services = new ServiceCollection();
        services.AddAuthorization(options => options.AddPolicy("Operators", policy => policy.RequireAssertion(_ => true)));
        services.RemoveAll<ILogger<DefaultAuthorizationService>>();
        services.AddAppSurfaceAspNetCoreAuth();
        using var scope = services.BuildServiceProvider().CreateScope();
        SetHttpContext(scope, Principal("sub", "user-1"));

        var result = await scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
            .AuthorizeAsync("Operators");

        Assert.Equal(AppSurfaceAuthOutcome.SetupFailure, result.Outcome);
        Assert.Equal(AppSurfaceAuthReason.MissingServices, result.Reason);
        Assert.Equal(typeof(IPolicyEvaluator).FullName, result.Metadata[AppSurfaceAspNetCoreAuthMetadataKeys.MissingService]);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenPolicyEvaluatorResolutionThrowsHostException_PropagatesException()
    {
        var services = new ServiceCollection();
        services.AddAuthorization(options => options.AddPolicy("Operators", policy => policy.RequireAssertion(_ => true)));
        services.RemoveAll<IPolicyEvaluator>();
        services.AddSingleton<IPolicyEvaluator>(_ => throw new InvalidOperationException("Policy evaluator factory failed."));
        services.AddAppSurfaceAspNetCoreAuth();
        using var scope = services.BuildServiceProvider().CreateScope();
        SetHttpContext(scope, Principal("sub", "user-1"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider
                .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
                .AuthorizeAsync("Operators"));
    }

    [Fact]
    public async Task AuthorizeAsync_WithMissingPolicy_ReturnsMissingPolicy()
    {
        using var scope = CreatePolicyScope(PolicyAuthorizationResult.Success());
        SetHttpContext(scope, Principal("sub", "user-1"));

        var result = await scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
            .AuthorizeAsync("Missing");

        Assert.Equal(AppSurfaceAuthReason.MissingPolicy, result.Reason);
        Assert.Equal("Missing", result.Metadata[AppSurfaceAspNetCoreAuthMetadataKeys.PolicyName]);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenPolicyRequiresSchemeWithoutAuthenticationServices_ReturnsMissingServices()
    {
        var services = CreateServices();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                "SchemePolicy",
                policy => policy
                    .AddAuthenticationSchemes("Proof")
                    .RequireAuthenticatedUser());
        });
        using var scope = services.BuildServiceProvider().CreateScope();
        SetHttpContext(scope, Principal("sub", "user-1"));

        var result = await scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
            .AuthorizeAsync("SchemePolicy");

        Assert.Equal(AppSurfaceAuthOutcome.SetupFailure, result.Outcome);
        Assert.Equal(AppSurfaceAuthReason.MissingServices, result.Reason);
        Assert.Equal("missing_authentication_service", result.Metadata[AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode]);
        Assert.Equal(
            typeof(IAuthenticationService).FullName,
            result.Metadata[AppSurfaceAspNetCoreAuthMetadataKeys.MissingService]);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenPolicyRequiresUnregisteredScheme_ReturnsMissingServices()
    {
        var services = CreateServices();
        services.AddLogging();
        services.AddAuthentication();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                "SchemePolicy",
                policy => policy
                    .AddAuthenticationSchemes("Missing")
                    .RequireAuthenticatedUser());
        });
        using var scope = services.BuildServiceProvider().CreateScope();
        SetHttpContext(scope, Principal("sub", "user-1"));

        var result = await scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
            .AuthorizeAsync("SchemePolicy");

        Assert.Equal(AppSurfaceAuthOutcome.SetupFailure, result.Outcome);
        Assert.Equal(AppSurfaceAuthReason.MissingServices, result.Reason);
        Assert.Equal("missing_authentication_service", result.Metadata[AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode]);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenAuthenticationHandlerThrows_PropagatesException()
    {
        var services = CreateServices();
        services.AddLogging();
        services
            .AddAuthentication("Throwing")
            .AddScheme<AuthenticationSchemeOptions, ThrowingAuthenticationHandler>(
                "Throwing",
                options => { _ = options; });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                "SchemePolicy",
                policy => policy
                    .AddAuthenticationSchemes("Throwing")
                    .RequireAuthenticatedUser());
        });
        using var scope = services.BuildServiceProvider().CreateScope();
        SetHttpContext(scope, Principal("sub", "user-1"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider
                .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
                .AuthorizeAsync("SchemePolicy"));
    }

    [Fact]
    public async Task AuthorizeAsync_WhenPolicySucceeds_ReturnsAllowedWithMappedContext()
    {
        using var scope = CreatePolicyScope(PolicyAuthorizationResult.Success());
        SetHttpContext(scope, Principal("sub", "user-1"));

        var result = await scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
            .AuthorizeAsync("Operators");

        Assert.Equal(AppSurfaceAuthOutcome.Allowed, result.Outcome);
        Assert.Equal("user-1", result.Context?.User?.Id);
    }

    [Fact]
    public async Task AuthorizeAsync_WithoutExplicitResource_UsesCurrentHttpContextAsResource()
    {
        var services = CreateServices();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                "RequiresHttpContextResource",
                policy => policy.RequireAssertion(context => context.Resource is HttpContext));
        });
        using var scope = services.BuildServiceProvider().CreateScope();
        SetHttpContext(scope, Principal("sub", "user-1"));

        var result = await scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
            .AuthorizeAsync("RequiresHttpContextResource");

        Assert.Equal(AppSurfaceAuthOutcome.Allowed, result.Outcome);
    }

    [Fact]
    public async Task AuthorizeAsync_WithExplicitResource_PassesResourceToPolicyEvaluation()
    {
        var resource = new object();
        var services = CreateServices();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                "RequiresExplicitResource",
                policy => policy.RequireAssertion(context => ReferenceEquals(context.Resource, resource)));
        });
        using var scope = services.BuildServiceProvider().CreateScope();
        SetHttpContext(scope, Principal("sub", "user-1"));

        var result = await scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
            .AuthorizeAsync("RequiresExplicitResource", resource);

        Assert.Equal(AppSurfaceAuthOutcome.Allowed, result.Outcome);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenPolicyForbids_ReturnsForbiddenWithMappedContext()
    {
        using var scope = CreatePolicyScope(PolicyAuthorizationResult.Forbid());
        SetHttpContext(scope, Principal("sub", "user-1"));

        var result = await scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
            .AuthorizeAsync("Operators");

        Assert.Equal(AppSurfaceAuthOutcome.Forbid, result.Outcome);
        Assert.Equal(AppSurfaceAuthReason.Forbidden, result.Reason);
        Assert.Equal("user-1", result.Context?.User?.Id);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenPolicyChallenges_ReturnsChallenge()
    {
        using var scope = CreatePolicyScope(
            PolicyAuthorizationResult.Challenge(),
            AuthenticateResult.NoResult());
        SetHttpContext(scope, new ClaimsPrincipal(new ClaimsIdentity()));

        var result = await scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
            .AuthorizeAsync("Operators");

        Assert.Equal(AppSurfaceAuthOutcome.Challenge, result.Outcome);
        Assert.False(result.Context?.IsAuthenticated);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenAuthenticatedPrincipalHasNoSubject_ReturnsMissingSubject()
    {
        using var scope = CreatePolicyScope(PolicyAuthorizationResult.Success());
        SetHttpContext(scope, Principal("role", "operator"));

        var result = await scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
            .AuthorizeAsync("Operators");

        Assert.Equal(AppSurfaceAuthOutcome.SetupFailure, result.Outcome);
        Assert.Equal(AppSurfaceAuthReason.MissingSubject, result.Reason);
        Assert.Equal("missing_subject_claim", result.Metadata[AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode]);
    }

    [Fact]
    public async Task AuthorizeAsync_UsesPolicyAuthenticationPrincipalWhenAvailable()
    {
        var policyPrincipal = Principal("sub", "policy-user");
        using var scope = CreatePolicyScope(
            PolicyAuthorizationResult.Success(),
            AuthenticateResult.Success(new AuthenticationTicket(policyPrincipal, "PolicyScheme")));
        SetHttpContext(scope, Principal("sub", "request-user"));

        var result = await scope.ServiceProvider
            .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
            .AuthorizeAsync("Operators");

        Assert.Equal(AppSurfaceAuthOutcome.Allowed, result.Outcome);
        Assert.Equal("policy-user", result.Context?.User?.Id);
    }

    [Fact]
    public async Task AuthorizeAsync_WithPreCanceledToken_ThrowsOperationCanceledException()
    {
        using var scope = CreatePolicyScope(PolicyAuthorizationResult.Success());
        SetHttpContext(scope, Principal("sub", "user-1"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scope.ServiceProvider
                .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
                .AuthorizeAsync("Operators", cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task AuthorizeAsync_WhenPolicyLookupIsCanceled_ThrowsOperationCanceledException()
    {
        var services = CreateServices();
        services.AddSingleton<IAuthorizationPolicyProvider>(new SlowPolicyProvider());
        services.AddSingleton<IPolicyEvaluator>(new FakePolicyEvaluator(PolicyAuthorizationResult.Success()));
        using var scope = services.BuildServiceProvider().CreateScope();
        SetHttpContext(scope, Principal("sub", "user-1"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scope.ServiceProvider
                .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
                .AuthorizeAsync("Operators", cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task AuthorizeAsync_WhenPolicyProviderThrows_PropagatesException()
    {
        var services = CreateServices();
        services.AddSingleton<IAuthorizationPolicyProvider>(new ThrowingPolicyProvider());
        services.AddSingleton<IPolicyEvaluator>(new FakePolicyEvaluator(PolicyAuthorizationResult.Success()));
        using var scope = services.BuildServiceProvider().CreateScope();
        SetHttpContext(scope, Principal("sub", "user-1"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider
                .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
                .AuthorizeAsync("Operators"));
    }

    [Fact]
    public async Task AuthorizeAsync_WhenPolicyEvaluatorThrows_PropagatesException()
    {
        using var scope = CreatePolicyScope(PolicyAuthorizationResult.Success(), throwOnAuthorize: true);
        SetHttpContext(scope, Principal("sub", "user-1"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider
                .GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>()
                .AuthorizeAsync("Operators"));
    }

    private static IServiceScope CreatePolicyScope(
        PolicyAuthorizationResult authorizationResult,
        AuthenticateResult? authenticationResult = null,
        bool throwOnAuthorize = false)
    {
        var services = CreateServices();
        services.AddAuthorization(options => options.AddPolicy("Operators", policy => policy.RequireAssertion(_ => true)));
        services.RemoveAll<IPolicyEvaluator>();
        services.AddSingleton<IPolicyEvaluator>(
            new FakePolicyEvaluator(authorizationResult, authenticationResult, throwOnAuthorize));
        return services.BuildServiceProvider().CreateScope();
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddAppSurfaceAspNetCoreAuth();
        return services;
    }

    private static DefaultHttpContext SetHttpContext(IServiceScope scope, ClaimsPrincipal principal)
    {
        var httpContext = new DefaultHttpContext
        {
            User = principal,
            RequestServices = scope.ServiceProvider,
        };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;
        return httpContext;
    }

    private static ClaimsPrincipal Principal(string claimType, string value)
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(claimType, value)], "Test"));
    }

    private sealed class FakePolicyEvaluator : IPolicyEvaluator
    {
        private readonly PolicyAuthorizationResult _authorizationResult;
        private readonly AuthenticateResult? _authenticationResult;
        private readonly bool _throwOnAuthorize;

        public FakePolicyEvaluator(
            PolicyAuthorizationResult authorizationResult,
            AuthenticateResult? authenticationResult = null,
            bool throwOnAuthorize = false)
        {
            _authorizationResult = authorizationResult;
            _authenticationResult = authenticationResult;
            _throwOnAuthorize = throwOnAuthorize;
        }

        public Task<AuthenticateResult> AuthenticateAsync(AuthorizationPolicy policy, HttpContext context)
        {
            return Task.FromResult(_authenticationResult ?? AuthenticateResult.Success(
                new AuthenticationTicket(context.User, "Test")));
        }

        public Task<PolicyAuthorizationResult> AuthorizeAsync(
            AuthorizationPolicy policy,
            AuthenticateResult authenticationResult,
            HttpContext context,
            object? resource)
        {
            if (_throwOnAuthorize)
            {
                throw new InvalidOperationException("Authorization handler failed.");
            }

            return Task.FromResult(_authorizationResult);
        }
    }

    private sealed class SlowPolicyProvider : IAuthorizationPolicyProvider
    {
        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
        {
            return Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith<AuthorizationPolicy?>(_ => null);
        }

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync()
        {
            return Task.FromResult(new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build());
        }

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync()
        {
            return Task.FromResult<AuthorizationPolicy?>(null);
        }
    }

    private sealed class ThrowingPolicyProvider : IAuthorizationPolicyProvider
    {
        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
        {
            throw new InvalidOperationException("Policy provider failed.");
        }

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync()
        {
            return Task.FromResult(new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build());
        }

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync()
        {
            return Task.FromResult<AuthorizationPolicy?>(null);
        }
    }

    private sealed class ThrowingAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public ThrowingAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            throw new InvalidOperationException("Authentication handler failed.");
        }
    }
}
