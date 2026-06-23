using System.Text.Json;
using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.Tests;

public sealed class AppSurfaceEndpointPolicyExtensionsTests
{
    [Fact]
    public void AddAppSurfacePolicy_WithNullOptions_ThrowsArgumentNullException()
    {
        AuthorizationOptions options = null!;

        Assert.Throws<ArgumentNullException>(
            () => options.AddAppSurfacePolicy("docs.publish", policy => policy.RequireAuthenticatedUser()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAppSurfacePolicy_WithBlankPolicyName_ThrowsArgumentException(string? policyName)
    {
        var options = new AuthorizationOptions();

        Assert.ThrowsAny<ArgumentException>(
            () => options.AddAppSurfacePolicy(policyName!, policy => policy.RequireAuthenticatedUser()));
    }

    [Fact]
    public void AddAppSurfacePolicy_WithNullConfigurePolicy_ThrowsArgumentNullException()
    {
        var options = new AuthorizationOptions();

        Assert.Throws<ArgumentNullException>(() => options.AddAppSurfacePolicy("docs.publish", null!));
    }

    [Fact]
    public async Task AddAppSurfacePolicy_RegistersNormalAspNetCorePolicy()
    {
        var services = new ServiceCollection();
        services.AddAuthorization(options =>
            options.AddAppSurfacePolicy("docs.publish", policy => policy.RequireAuthenticatedUser()));
        await using var provider = services.BuildServiceProvider();
        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await policyProvider.GetPolicyAsync("docs.publish");

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void RequireSurfacePolicy_WithNullBuilder_ThrowsArgumentNullException()
    {
        TestEndpointConventionBuilder builder = null!;

        Assert.Throws<ArgumentNullException>(() => builder.RequireSurfacePolicy("docs.publish"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RequireSurfacePolicy_WithBlankPolicyName_ThrowsArgumentException(string? policyName)
    {
        var builder = new TestEndpointConventionBuilder();

        Assert.ThrowsAny<ArgumentException>(() => builder.RequireSurfacePolicy(policyName!));
    }

    [Fact]
    public void RequireSurfacePolicy_ReturnsSameBuilderAndAddsMetadata()
    {
        var builder = new TestEndpointConventionBuilder();

        var returned = builder.RequireSurfacePolicy("docs.publish");

        Assert.Same(builder, returned);

        var endpointBuilder = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse("/"), 0);
        builder.Apply(endpointBuilder);

        var metadata = Assert.IsType<AppSurfacePolicyEndpointMetadata>(
            Assert.Single(endpointBuilder.Metadata.OfType<AppSurfacePolicyEndpointMetadata>()));
        Assert.Equal("docs.publish", metadata.PolicyName);
        Assert.Single(endpointBuilder.Metadata.OfType<IAllowAnonymous>());
    }

    [Fact]
    public async Task RequireSurfacePolicy_FilterFactoryEvaluatesPolicy()
    {
        var builder = new TestEndpointConventionBuilder();
        builder.RequireSurfacePolicy("docs.publish");
        var endpointBuilder = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse("/"), 0);
        builder.Apply(endpointBuilder);
        await using var contextScope = CreateHttpContext(AppSurfaceAuthResult.Allowed());
        var httpContext = contextScope.HttpContext;
        var endpointFilter = Assert.Single(endpointBuilder.FilterFactories)(
            null!,
            _ => ValueTask.FromResult<object?>("handler-result"));

        var result = await endpointFilter(new TestEndpointFilterInvocationContext(httpContext));

        Assert.Equal("handler-result", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AppSurfacePolicyEndpointMetadata_WithBlankPolicyName_ThrowsArgumentException(string? policyName)
    {
        Assert.ThrowsAny<ArgumentException>(() => new AppSurfacePolicyEndpointMetadata(policyName!));
    }

    [Fact]
    public async Task Filter_WhenAllowed_CallsNext()
    {
        await using var contextScope = CreateHttpContext(AppSurfaceAuthResult.Allowed());
        var httpContext = contextScope.HttpContext;
        var filter = new AppSurfacePolicyEndpointFilter("docs.publish");
        var nextCalled = false;

        var result = await filter.InvokeAsync(
            new TestEndpointFilterInvocationContext(httpContext),
            _ =>
            {
                nextCalled = true;
                return ValueTask.FromResult<object?>("handler-result");
            });

        Assert.True(nextCalled);
        Assert.Equal("handler-result", result);
    }

    [Theory]
    [MemberData(nameof(FailureResults))]
    public async Task Filter_WhenPolicyFails_ReturnsProblemDetails(
        AppSurfaceAuthResult authResult,
        int expectedStatus,
        string expectedTitle)
    {
        await using var contextScope = CreateHttpContext(authResult);
        var httpContext = contextScope.HttpContext;
        var filter = new AppSurfacePolicyEndpointFilter("docs.publish");
        var nextCalled = false;

        var result = await filter.InvokeAsync(
            new TestEndpointFilterInvocationContext(httpContext),
            _ =>
            {
                nextCalled = true;
                return ValueTask.FromResult<object?>("handler-result");
            });

        Assert.False(nextCalled);
        var problem = await ExecuteProblemAsync(Assert.IsAssignableFrom<IResult>(result));

        Assert.Equal(expectedStatus, problem.Status);
        Assert.Equal(expectedTitle, problem.Title);
        Assert.Equal(authResult.Outcome.ToString(), AssertExtension(problem, "appsurfaceAuthOutcome"));
        Assert.Equal(authResult.Reason.ToString(), AssertExtension(problem, "appsurfaceAuthReason"));
        Assert.Equal("docs.publish", AssertExtension(problem, "appsurfacePolicyName"));
    }

    [Theory]
    [MemberData(nameof(SessionFailureResults))]
    public async Task Filter_WhenSessionPolicyFails_ReturnsProblemDetails(
        AppSurfaceAuthResult authResult,
        int expectedStatus,
        string expectedTitle)
    {
        await using var contextScope = CreateHttpContext(authResult);
        var httpContext = contextScope.HttpContext;
        var filter = new AppSurfacePolicyEndpointFilter("docs.publish");

        var result = await filter.InvokeAsync(
            new TestEndpointFilterInvocationContext(httpContext),
            _ => ValueTask.FromResult<object?>("handler-result"));

        var problem = await ExecuteProblemAsync(Assert.IsAssignableFrom<IResult>(result));

        Assert.Equal(expectedStatus, problem.Status);
        Assert.Equal(expectedTitle, problem.Title);
        Assert.Equal(authResult.Outcome.ToString(), AssertExtension(problem, "appsurfaceAuthOutcome"));
        Assert.Equal(authResult.Reason.ToString(), AssertExtension(problem, "appsurfaceAuthReason"));
    }

    [Fact]
    public async Task Filter_WhenEvaluatorIsMissing_ReturnsSetupFailureProblemDetails()
    {
        var services = new ServiceCollection();
        await using var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var filter = new AppSurfacePolicyEndpointFilter("docs.publish");

        var result = await filter.InvokeAsync(
            new TestEndpointFilterInvocationContext(httpContext),
            _ => ValueTask.FromResult<object?>("handler-result"));

        var problem = await ExecuteProblemAsync(Assert.IsAssignableFrom<IResult>(result));

        Assert.Equal(StatusCodes.Status500InternalServerError, problem.Status);
        Assert.Equal("AppSurface auth setup failure", problem.Title);
        Assert.Equal("SetupFailure", AssertExtension(problem, "appsurfaceAuthOutcome"));
        Assert.Equal("MissingServices", AssertExtension(problem, "appsurfaceAuthReason"));
        Assert.Equal(
            typeof(IAppSurfaceAspNetCorePolicyEvaluator).FullName,
            AssertExtension(problem, AppSurfaceAspNetCoreAuthMetadataKeys.MissingService));
    }

    [Fact]
    public async Task Filter_PassesRequestAbortedCancellationToEvaluator()
    {
        var evaluator = new CapturingPolicyEvaluator(AppSurfaceAuthResult.Allowed());
        var services = new ServiceCollection();
        services.AddSingleton<IAppSurfaceAspNetCorePolicyEvaluator>(evaluator);
        await using var provider = services.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
            RequestAborted = cancellation.Token,
        };
        var filter = new AppSurfacePolicyEndpointFilter("docs.publish");

        _ = await filter.InvokeAsync(
            new TestEndpointFilterInvocationContext(httpContext),
            _ => ValueTask.FromResult<object?>("handler-result"));

        Assert.Equal("docs.publish", evaluator.PolicyName);
        Assert.Equal(cancellation.Token, evaluator.CancellationToken);
    }

    [Fact]
    public void ProblemDetailsMapper_WithNullResult_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => AppSurfacePolicyProblemDetailsMapper.ToResult(null!, "docs.publish"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ProblemDetailsMapper_WithBlankPolicyName_ThrowsArgumentException(string? policyName)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => AppSurfacePolicyProblemDetailsMapper.ToResult(AppSurfaceAuthResult.Challenge(), policyName!));
    }

    [Fact]
    public void ProblemDetailsMapper_WithAllowedResult_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => AppSurfacePolicyProblemDetailsMapper.ToResult(AppSurfaceAuthResult.Allowed(), "docs.publish"));
    }

    [Fact]
    public async Task ProblemDetailsMapper_CopiesOnlySafeMetadata()
    {
        var result = AppSurfaceAuthResult.Challenge(
            message: "Sign in before publishing.",
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode] = "challenge",
                [AppSurfaceAspNetCoreAuthMetadataKeys.PolicyName] = "docs.publish",
                [AppSurfaceAspNetCoreAuthMetadataKeys.MissingService] = "Microsoft.AspNetCore.Authorization.IAuthorizationService",
                [AppSurfaceAspNetCoreAuthMetadataKeys.SubjectClaimTypes] = "sub,nameidentifier",
                ["email"] = "operator@example.test",
            });

        var problem = await ExecuteProblemAsync(AppSurfacePolicyProblemDetailsMapper.ToResult(result, "docs.publish"));

        Assert.Equal("Sign in before publishing.", problem.Detail);
        Assert.Equal("challenge", AssertExtension(problem, AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode));
        Assert.Equal("docs.publish", AssertExtension(problem, AppSurfaceAspNetCoreAuthMetadataKeys.PolicyName));
        Assert.Equal(
            "Microsoft.AspNetCore.Authorization.IAuthorizationService",
            AssertExtension(problem, AppSurfaceAspNetCoreAuthMetadataKeys.MissingService));
        Assert.Equal("sub,nameidentifier", AssertExtension(problem, AppSurfaceAspNetCoreAuthMetadataKeys.SubjectClaimTypes));
        Assert.False(problem.Extensions.ContainsKey("email"));
    }

    public static TheoryData<AppSurfaceAuthResult, int, string> FailureResults()
    {
        return new TheoryData<AppSurfaceAuthResult, int, string>
        {
            {
                AppSurfaceAuthResult.Challenge(
                    AppSurfaceAuthContext.Anonymous,
                    metadata: AppSurfaceAspNetCoreAuthDiagnostics.Policy("challenge", "docs.publish")),
                StatusCodes.Status401Unauthorized,
                "Authentication required"
            },
            {
                AppSurfaceAuthResult.Forbid(
                    AppSurfaceAuthContext.Anonymous,
                    metadata: AppSurfaceAspNetCoreAuthDiagnostics.Policy("forbidden", "docs.publish")),
                StatusCodes.Status403Forbidden,
                "Authorization failed"
            },
            {
                AppSurfaceAuthResult.MissingPolicy(
                    AppSurfaceAuthContext.Anonymous,
                    metadata: AppSurfaceAspNetCoreAuthDiagnostics.Policy("missing_policy", "docs.publish")),
                StatusCodes.Status500InternalServerError,
                "AppSurface auth setup failure"
            },
            {
                AppSurfaceAuthResult.MissingSubject(
                    AppSurfaceAuthContext.Anonymous,
                    metadata: AppSurfaceAspNetCoreAuthDiagnostics.Policy("missing_subject_claim", "docs.publish")),
                StatusCodes.Status500InternalServerError,
                "AppSurface auth setup failure"
            },
        };
    }

    public static TheoryData<AppSurfaceAuthResult, int, string> SessionFailureResults()
    {
        return new TheoryData<AppSurfaceAuthResult, int, string>
        {
            {
                AppSurfaceAuthResult.UnsafeReturnUrl(),
                StatusCodes.Status400BadRequest,
                "Unsafe auth navigation"
            },
            {
                AppSurfaceAuthResult.StaleOrUnknownSession(),
                StatusCodes.Status401Unauthorized,
                "Stale or unknown auth session"
            },
        };
    }

    private static TestHttpContextScope CreateHttpContext(AppSurfaceAuthResult authResult)
    {
        return new TestHttpContextScope(authResult);
    }

    private static async Task<ProblemDetails> ExecuteProblemAsync(IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        await using var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = provider;
        httpContext.Response.Body = new MemoryStream();

        await result.ExecuteAsync(httpContext);

        httpContext.Response.Body.Position = 0;
        var problem = await JsonSerializer.DeserializeAsync<ProblemDetails>(
            httpContext.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        return Assert.IsType<ProblemDetails>(problem);
    }

    private static string? AssertExtension(ProblemDetails problem, string key)
    {
        Assert.True(problem.Extensions.TryGetValue(key, out var value), $"ProblemDetails extension '{key}' was missing.");
        return value switch
        {
            JsonElement element => element.GetString(),
            string text => text,
            _ => value?.ToString(),
        };
    }

    private sealed class TestEndpointConventionBuilder : IEndpointConventionBuilder
    {
        private readonly List<Action<EndpointBuilder>> _conventions = [];

        public void Add(Action<EndpointBuilder> convention)
        {
            _conventions.Add(convention);
        }

        public void Apply(EndpointBuilder endpointBuilder)
        {
            foreach (var convention in _conventions)
            {
                convention(endpointBuilder);
            }
        }
    }

    private sealed class TestEndpointFilterInvocationContext : EndpointFilterInvocationContext
    {
        public TestEndpointFilterInvocationContext(HttpContext httpContext)
        {
            HttpContext = httpContext;
        }

        public override HttpContext HttpContext { get; }

        public override IList<object?> Arguments { get; } = [];

        public override T GetArgument<T>(int index)
        {
            return (T)Arguments[index]!;
        }
    }

    private sealed class TestHttpContextScope : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public TestHttpContextScope(AppSurfaceAuthResult authResult)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAppSurfaceAspNetCorePolicyEvaluator>(new CapturingPolicyEvaluator(authResult));
            _provider = services.BuildServiceProvider();
            HttpContext = new DefaultHttpContext { RequestServices = _provider };
        }

        public DefaultHttpContext HttpContext { get; }

        public ValueTask DisposeAsync()
        {
            return _provider.DisposeAsync();
        }
    }

    private sealed class CapturingPolicyEvaluator : IAppSurfaceAspNetCorePolicyEvaluator
    {
        private readonly AppSurfaceAuthResult _result;

        public CapturingPolicyEvaluator(AppSurfaceAuthResult result)
        {
            _result = result;
        }

        public string? PolicyName { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<AppSurfaceAuthResult> AuthorizeAsync(
            string policyName,
            object? resource = null,
            CancellationToken cancellationToken = default)
        {
            PolicyName = policyName;
            CancellationToken = cancellationToken;
            return Task.FromResult(_result);
        }
    }
}
