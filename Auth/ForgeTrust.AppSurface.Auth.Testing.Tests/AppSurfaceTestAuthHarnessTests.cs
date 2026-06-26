using System.Net;
using System.Security.Claims;
using System.Text.Json;
using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.AppSurface.Auth.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Auth.Testing.Tests;

public sealed class AppSurfaceTestAuthHarnessTests
{
    private const string PolicyName = "OperatorsOnly";
    private const string SubjectClaimType = "sub";

    [Theory]
    [InlineData("operator", HttpStatusCode.OK, AppSurfaceAuthOutcome.Allowed, AppSurfaceAuthReason.None, "operator-1")]
    [InlineData("viewer", HttpStatusCode.Forbidden, AppSurfaceAuthOutcome.Forbid, AppSurfaceAuthReason.Forbidden, "viewer-1")]
    public async Task DefaultScheme_EvaluatesRealAspNetCorePolicyWithPersona(
        string persona,
        HttpStatusCode expectedStatus,
        AppSurfaceAuthOutcome expectedOutcome,
        AppSurfaceAuthReason expectedReason,
        string expectedSubject)
    {
        using var host = await CreateHostAsync(options =>
        {
            options.SubjectClaimType = SubjectClaimType;
            options.AddPersona("operator", "operator-1", [new Claim("role", "operator")]);
            options.AddPersona("viewer", "viewer-1", [new Claim("role", "viewer")]);
        });
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/result")
            .WithAppSurfaceTestPersona(persona);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedOutcome.ToString(), ReadString(json.RootElement, "outcome"));
        Assert.Equal(expectedReason.ToString(), ReadString(json.RootElement, "reason"));
        Assert.Equal(expectedSubject, ReadNullableString(json.RootElement, "subject"));
    }

    [Fact]
    public async Task DefaultScheme_WithNoPersonaSelection_IsAnonymousChallenge()
    {
        using var host = await CreateHostAsync(options =>
        {
            options.AddPersona("operator", "operator-1", [new Claim("role", "operator")]);
        });
        using var client = host.GetTestClient();

        using var response = await client.GetAsync("/result");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(AppSurfaceAuthOutcome.Challenge.ToString(), ReadString(json.RootElement, "outcome"));
        Assert.Equal(AppSurfaceAuthReason.Unauthenticated.ToString(), ReadString(json.RootElement, "reason"));
        Assert.Null(ReadNullableString(json.RootElement, "subject"));
    }

    [Fact]
    public async Task DefaultScheme_WithUnknownRawRequestPersona_ReturnsSetupFailure()
    {
        using var host = await CreateHostAsync(options =>
        {
            options.AddPersona("operator", "operator-1", [new Claim("role", "operator")]);
        });
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/result")
            .WithAppSurfaceTestPersona("missing");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(AppSurfaceAuthOutcome.SetupFailure.ToString(), ReadString(json.RootElement, "outcome"));
        Assert.Equal(AppSurfaceAuthReason.MissingSubject.ToString(), ReadString(json.RootElement, "reason"));
        Assert.Equal(AppSurfaceTestAuthDiagnosticCodes.UnknownPersona, ReadString(json.RootElement, "diagnostic"));
    }

    [Fact]
    public async Task AddAppSurfaceTestAuth_DecoratesExistingPolicyEvaluator()
    {
        var calls = 0;
        using var host = await CreateHostAsync(
            options => options.AddPersona("operator", "operator-1", [new Claim("role", "operator")]),
            configureServicesBeforeTestAuth: services =>
                services.AddScoped<IAppSurfaceAspNetCorePolicyEvaluator>(_ => new CapturingPolicyEvaluator(() =>
                {
                    Interlocked.Increment(ref calls);
                    return AppSurfaceAuthResult.Forbid(
                        new AppSurfaceAuthContext(new AppSurfaceUser("custom-user")));
                })));
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/result")
            .WithAppSurfaceTestPersona("operator");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(AppSurfaceAuthOutcome.Forbid.ToString(), ReadString(json.RootElement, "outcome"));
        Assert.Equal("custom-user", ReadNullableString(json.RootElement, "subject"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AddAppSurfaceTestAuth_DecoratesExistingPolicyEvaluatorInstance()
    {
        var evaluator = new CapturingPolicyEvaluator(() =>
            AppSurfaceAuthResult.Forbid(new AppSurfaceAuthContext(new AppSurfaceUser("instance-user"))));
        using var host = await CreateHostAsync(
            options => options.AddPersona("operator", "operator-1", [new Claim("role", "operator")]),
            configureServicesBeforeTestAuth: services =>
                services.AddSingleton<IAppSurfaceAspNetCorePolicyEvaluator>(evaluator));
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/result")
            .WithAppSurfaceTestPersona("operator");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("instance-user", ReadNullableString(json.RootElement, "subject"));
    }

    [Fact]
    public async Task AddAppSurfaceTestAuth_WithCustomEvaluatorStillRejectsUnknownRequestPersona()
    {
        using var host = await CreateHostAsync(
            options => options.AddPersona("operator", "operator-1", [new Claim("role", "operator")]),
            configureServicesBeforeTestAuth: services =>
                services.AddScoped<IAppSurfaceAspNetCorePolicyEvaluator>(_ => new CapturingPolicyEvaluator(() =>
                    AppSurfaceAuthResult.Allowed(new AppSurfaceAuthContext(new AppSurfaceUser("custom-user"))))));
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/result")
            .WithAppSurfaceTestPersona("missing");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(AppSurfaceAuthOutcome.SetupFailure.ToString(), ReadString(json.RootElement, "outcome"));
        Assert.Equal(AppSurfaceAuthReason.MissingSubject.ToString(), ReadString(json.RootElement, "reason"));
        Assert.Equal(AppSurfaceTestAuthDiagnosticCodes.UnknownPersona, ReadString(json.RootElement, "diagnostic"));
    }

    [Fact]
    public async Task AppSurfaceTestPolicyEvaluator_WithUnknownPersonaStillValidatesPolicyName()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[AppSurfaceTestAuthTransport.PersonaHeaderName] = "missing";
        var accessor = new HttpContextAccessor { HttpContext = context };
        var options = new AppSurfaceTestAuthOptions();
        options.AddPersona("operator", "operator-1");
        var evaluator = new AppSurfaceTestAspNetCorePolicyEvaluator(
            new AppSurfaceTestInnerPolicyEvaluator(new CapturingPolicyEvaluator(() => AppSurfaceAuthResult.Allowed())),
            accessor,
            AppSurfaceTestPersonaRegistry.Create(options));

        await Assert.ThrowsAsync<ArgumentException>(() => evaluator.AuthorizeAsync(" "));
    }

    [Fact]
    public async Task AppSurfaceTestPolicyEvaluator_WithUnknownPersonaStillObservesCancellation()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[AppSurfaceTestAuthTransport.PersonaHeaderName] = "missing";
        var accessor = new HttpContextAccessor { HttpContext = context };
        var options = new AppSurfaceTestAuthOptions();
        options.AddPersona("operator", "operator-1");
        var evaluator = new AppSurfaceTestAspNetCorePolicyEvaluator(
            new AppSurfaceTestInnerPolicyEvaluator(new CapturingPolicyEvaluator(() => AppSurfaceAuthResult.Allowed())),
            accessor,
            AppSurfaceTestPersonaRegistry.Create(options));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            evaluator.AuthorizeAsync(PolicyName, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task AppSurfaceTestPolicyEvaluator_WithNoHttpContext_ReturnsInnerResult()
    {
        var evaluator = new AppSurfaceTestAspNetCorePolicyEvaluator(
            new AppSurfaceTestInnerPolicyEvaluator(new CapturingPolicyEvaluator(() => AppSurfaceAuthResult.Allowed())),
            new HttpContextAccessor(),
            AppSurfaceTestPersonaRegistry.Create(new AppSurfaceTestAuthOptions()));

        var result = await evaluator.AuthorizeAsync(PolicyName);

        Assert.Equal(AppSurfaceAuthOutcome.Allowed, result.Outcome);
    }

    [Fact]
    public async Task AppSurfaceTestPolicyEvaluator_WithUnknownPersonaItemAfterInnerEvaluation_ReturnsSetupFailure()
    {
        var context = new DefaultHttpContext();
        var accessor = new HttpContextAccessor { HttpContext = context };
        var evaluator = new AppSurfaceTestAspNetCorePolicyEvaluator(
            new AppSurfaceTestInnerPolicyEvaluator(new CapturingPolicyEvaluator(() =>
            {
                context.Items[AppSurfaceTestAuthTransport.UnknownPersonaItemKey] = "late-missing";
                return AppSurfaceAuthResult.Allowed();
            })),
            accessor,
            AppSurfaceTestPersonaRegistry.Create(new AppSurfaceTestAuthOptions()));

        var result = await evaluator.AuthorizeAsync(PolicyName);

        Assert.Equal(AppSurfaceAuthOutcome.SetupFailure, result.Outcome);
        Assert.Equal(AppSurfaceAuthReason.MissingSubject, result.Reason);
        Assert.Equal(
            AppSurfaceTestAuthDiagnosticCodes.UnknownPersona,
            result.Metadata[AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode]);
    }

    [Fact]
    public async Task AddAppSurfaceTestAuth_PreservesExistingPolicyEvaluatorLifetime()
    {
        var instances = 0;
        using var host = await CreateHostAsync(
            options => options.AddPersona("operator", "operator-1", [new Claim("role", "operator")]),
            configureServicesBeforeTestAuth: services =>
                services.AddTransient<IAppSurfaceAspNetCorePolicyEvaluator>(_ =>
                {
                    Interlocked.Increment(ref instances);
                    return new CapturingPolicyEvaluator(() => AppSurfaceAuthResult.Challenge());
                }));

        using var scope = host.Services.CreateScope();
        var first = scope.ServiceProvider.GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>();
        var second = scope.ServiceProvider.GetRequiredService<IAppSurfaceAspNetCorePolicyEvaluator>();

        _ = await first.AuthorizeAsync(PolicyName);
        _ = await second.AuthorizeAsync(PolicyName);

        Assert.NotSame(first, second);
        Assert.Equal(2, instances);
    }

    [Fact]
    public async Task RequireSurfacePolicy_ReturnsCanonicalProblemDetailsForForbiddenPersona()
    {
        using var host = await CreateHostAsync(options =>
        {
            options.SubjectClaimType = SubjectClaimType;
            options.AddPersona("viewer", "viewer-1", [new Claim("role", "viewer")]);
        });
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/protected")
            .WithAppSurfaceTestPersona("viewer");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AppSurfaceAuthTestAssert.HasProblemDetails(
            json.RootElement,
            AppSurfaceAuthOutcome.Forbid,
            AppSurfaceAuthReason.Forbidden,
            StatusCodes.Status403Forbidden,
            PolicyName);
    }

    [Fact]
    public async Task NamedScheme_DoesNotTakeOverDefaultAuthenticationScheme()
    {
        using var host = await CreateHostAsync(
            options =>
            {
                options.SchemeMode = AppSurfaceTestAuthSchemeMode.NamedScheme;
                options.AddPersona("operator", "operator-1", [new Claim("role", "operator")]);
            },
            configurePolicy: policy => policy
                .AddAuthenticationSchemes(AppSurfaceTestAuthDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireClaim("role", "operator"));
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/result")
            .WithAppSurfaceTestPersona("operator");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NoDefault_DoesNotTakeOverPoliciesThatDoNotOptInToTheTestScheme()
    {
        using var host = await CreateHostAsync(options =>
        {
            options.SchemeMode = AppSurfaceTestAuthSchemeMode.NoDefault;
            options.AddPersona("operator", "operator-1", [new Claim("role", "operator")]);
        });
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/result")
            .WithAppSurfaceTestPersona("operator");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(AppSurfaceAuthOutcome.Challenge.ToString(), ReadString(json.RootElement, "outcome"));
        Assert.Equal(AppSurfaceAuthReason.Unauthenticated.ToString(), ReadString(json.RootElement, "reason"));
        Assert.Null(ReadNullableString(json.RootElement, "subject"));
    }

    [Fact]
    public async Task SubjectClaimType_WhenNotOverridden_PreservesDefaultHostMapping()
    {
        using var host = await CreateHostAsync(options =>
        {
            options.AddPersona("operator", "operator-1", [new Claim("role", "operator")]);
        });
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/result")
            .WithAppSurfaceTestPersona("operator");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("operator-1", ReadNullableString(json.RootElement, "subject"));
    }

    [Fact]
    public async Task SubjectClaimType_WhenOverridden_MapsCustomSubjectClaim()
    {
        using var host = await CreateHostAsync(options =>
        {
            options.SubjectClaimType = SubjectClaimType;
            options.AddPersona("operator", "operator-1", [new Claim("role", "operator")]);
        });
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/result")
            .WithAppSurfaceTestPersona("operator");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("operator-1", ReadNullableString(json.RootElement, "subject"));
    }

    [Fact]
    public void DuplicatePersonas_FailDuringRegistration()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddAppSurfaceTestAuth(options =>
        {
            options.AddPersona("operator", "operator-1");
            options.AddPersona("operator", "operator-2");
        }));

        Assert.Contains(AppSurfaceTestAuthDiagnosticCodes.DuplicatePersona, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankSchemeName_FailsDuringRegistration()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddAppSurfaceTestAuth(options =>
        {
            options.SchemeName = " ";
        }));

        Assert.Contains(AppSurfaceTestAuthDiagnosticCodes.BlankSchemeName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankPersonaName_FailsWithDiagnosticCode()
    {
        var options = new AppSurfaceTestAuthOptions();

        var error = Assert.Throws<InvalidOperationException>(() => options.AddPersona(" ", "operator-1"));

        Assert.Contains("persona name is blank", error.Message, StringComparison.Ordinal);
        Assert.Contains(AppSurfaceTestAuthDiagnosticCodes.BlankPersonaName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankSubjectClaimType_FailsDuringRegistration()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddAppSurfaceTestAuth(options =>
        {
            options.SubjectClaimType = " ";
        }));

        Assert.Contains("subject claim type is blank", error.Message, StringComparison.Ordinal);
        Assert.Contains(AppSurfaceTestAuthDiagnosticCodes.BlankSubjectClaimType, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionEnvironment_IsBlockedUnlessExplicitlyAllowed()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateHostAsync(
                options => options.AddPersona("operator", "operator-1"),
                environmentName: "Production"));

        Assert.Contains(AppSurfaceTestAuthDiagnosticCodes.ProductionEnvironmentBlocked, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    public async Task NonProductionEnvironments_AreAllowedByDefault(string environmentName)
    {
        using var host = await CreateHostAsync(
            options => options.AddPersona("operator", "operator-1", [new Claim("role", "operator")]),
            environmentName: environmentName);
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/result")
            .WithAppSurfaceTestPersona("operator");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ProductionEnvironment_CanBeExplicitlyAllowedForIsolatedHosts()
    {
        using var host = await CreateHostAsync(
            options =>
            {
                options.AllowProductionEnvironmentForTestHost = true;
                options.AddPersona("operator", "operator-1", [new Claim("role", "operator")]);
            },
            environmentName: "Production");
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/result")
            .WithAppSurfaceTestPersona("operator");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void AssertionHelpers_ReportOutcomeMismatchWithoutTestFrameworkDependency()
    {
        var error = Assert.Throws<AppSurfaceTestAuthAssertionException>(() =>
            AppSurfaceAuthTestAssert.HasOutcome(
                AppSurfaceAuthResult.Challenge(),
                AppSurfaceAuthOutcome.Allowed));

        Assert.Contains(AppSurfaceTestAuthDiagnosticCodes.AssertionFailed, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertionHelpers_ReportReasonMismatchWithoutTestFrameworkDependency()
    {
        var error = Assert.Throws<AppSurfaceTestAuthAssertionException>(() =>
            AppSurfaceAuthTestAssert.HasOutcome(
                AppSurfaceAuthResult.Challenge(),
                AppSurfaceAuthOutcome.Challenge,
                AppSurfaceAuthReason.Forbidden));

        Assert.Contains("Expected AppSurface auth reason", error.Message, StringComparison.Ordinal);
        Assert.Contains(AppSurfaceTestAuthDiagnosticCodes.AssertionFailed, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssertionHelpers_VerifyProblemDetailsWithoutPolicyExpectation()
    {
        using var host = await CreateHostAsync(options =>
        {
            options.AddPersona("viewer", "viewer-1", [new Claim("role", "viewer")]);
        });
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/protected")
            .WithAppSurfaceTestPersona("viewer");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        var returned = AppSurfaceAuthTestAssert.HasProblemDetails(
            json.RootElement,
            AppSurfaceAuthOutcome.Forbid,
            AppSurfaceAuthReason.Forbidden,
            StatusCodes.Status403Forbidden);

        Assert.Equal(JsonValueKind.Object, returned.ValueKind);
    }

    [Fact]
    public void AssertionHelpers_ReportProblemDetailsMismatch()
    {
        using var json = JsonDocument.Parse(
            """
            {
              "status": 401,
              "appsurfaceAuthOutcome": "Challenge",
              "appsurfaceAuthReason": "Unauthenticated"
            }
            """);

        var error = Assert.Throws<AppSurfaceTestAuthAssertionException>(() =>
            AppSurfaceAuthTestAssert.HasProblemDetails(
                json.RootElement,
                AppSurfaceAuthOutcome.Forbid,
                AppSurfaceAuthReason.Forbidden,
                StatusCodes.Status403Forbidden));

        Assert.Contains(AppSurfaceTestAuthDiagnosticCodes.AssertionFailed, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """
        { "status": 403, "appsurfaceAuthOutcome": "Challenge", "appsurfaceAuthReason": "Forbidden" }
        """,
        "Expected ProblemDetails AppSurface outcome")]
    [InlineData(
        """
        { "status": 403, "appsurfaceAuthOutcome": "Forbid", "appsurfaceAuthReason": "Unauthenticated" }
        """,
        "Expected ProblemDetails AppSurface reason")]
    [InlineData(
        """
        { "status": 403, "appsurfaceAuthOutcome": "Forbid", "appsurfaceAuthReason": "Forbidden", "appsurfacePolicyName": "OtherPolicy" }
        """,
        "Expected ProblemDetails AppSurface policy")]
    [InlineData(
        """
        { "status": 403, "appsurfaceAuthReason": "Forbidden" }
        """,
        "Expected ProblemDetails property 'appsurfaceAuthOutcome' to be a string")]
    [InlineData(
        """
        { "status": "403", "appsurfaceAuthOutcome": "Forbid", "appsurfaceAuthReason": "Forbidden" }
        """,
        "Expected ProblemDetails property 'status' to be a number")]
    public void AssertionHelpers_ReportSpecificProblemDetailsMismatches(string problemJson, string expectedMessage)
    {
        using var json = JsonDocument.Parse(problemJson);

        var error = Assert.Throws<AppSurfaceTestAuthAssertionException>(() =>
            AppSurfaceAuthTestAssert.HasProblemDetails(
                json.RootElement,
                AppSurfaceAuthOutcome.Forbid,
                AppSurfaceAuthReason.Forbidden,
                StatusCodes.Status403Forbidden,
                PolicyName));

        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
        Assert.Contains(AppSurfaceTestAuthDiagnosticCodes.AssertionFailed, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestPersonaHelper_WithBlankPersona_ThrowsArgumentException()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");

        Assert.ThrowsAny<ArgumentException>(() => request.WithAppSurfaceTestPersona(" "));
    }

    [Fact]
    public void WebApplicationFactoryHelpers_ValidateArguments()
    {
        WebApplicationFactory<object>? factory = null;

        Assert.Throws<ArgumentNullException>(() => factory!.WithAppSurfaceTestAuth());

        using var realFactory = new WebApplicationFactory<object>();
        Assert.ThrowsAny<ArgumentException>(() => realFactory.CreateAppSurfaceClient(" "));
    }

    [Fact]
    public void PersonaRegistry_RequireReturnsRegisteredPersona()
    {
        var options = new AppSurfaceTestAuthOptions();
        options.AddPersona("operator", "operator-1");
        var registry = AppSurfaceTestPersonaRegistry.Create(options);

        var persona = registry.Require("operator");

        Assert.Equal("operator-1", persona.SubjectId);
    }

    [Fact]
    public void PersonaRegistry_RequireUnknownPersonaThrowsDiagnostic()
    {
        var registry = AppSurfaceTestPersonaRegistry.Create(new AppSurfaceTestAuthOptions());

        var error = Assert.Throws<InvalidOperationException>(() => registry.Require("missing"));

        Assert.Contains(AppSurfaceTestAuthDiagnosticCodes.UnknownPersona, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InnerPolicyEvaluator_WithNullPolicyEvaluator_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceTestInnerPolicyEvaluator(null!));
    }

    [Fact]
    public void Persona_ExposesImmutableAdditionalClaimsSnapshot()
    {
        Claim[] claims = [new Claim("role", "operator")];
        var persona = new AppSurfaceTestPersona(" operator ", " operator-1 ", claims);
        claims[0] = new Claim("role", "viewer");

        Assert.Equal("operator", persona.Name);
        Assert.Equal("operator-1", persona.SubjectId);
        var claim = Assert.Single(persona.Claims);
        Assert.Equal("operator", claim.Value);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<Claim>)persona.Claims)[0] = new Claim("role", "viewer"));
        Assert.Equal(
            "operator",
            persona.CreateClaims(SubjectClaimType).Single(claim => claim.Type == "role").Value);
    }

    [Fact]
    public void StaleSession_ResultCanBeAssertedWithoutSimulatingSessionFreshness()
    {
        var result = AppSurfaceAuthResult.StaleOrUnknownSession();

        var returned = AppSurfaceAuthTestAssert.HasOutcome(
            result,
            AppSurfaceAuthOutcome.StaleOrUnknownSession,
            AppSurfaceAuthReason.StaleOrUnknownSession);

        Assert.Same(result, returned);
    }

    [Fact]
    public async Task PersonaSubjectClaim_WinsOverDuplicateAdditionalSubjectClaim()
    {
        using var host = await CreateHostAsync(options =>
        {
            options.SubjectClaimType = SubjectClaimType;
            options.AddPersona(
                "operator",
                "operator-1",
                [
                    new Claim(SubjectClaimType, "wrong-subject"),
                    new Claim("role", "operator"),
                ]);
        });
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/result")
            .WithAppSurfaceTestPersona("operator");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("operator-1", ReadNullableString(json.RootElement, "subject"));
    }

    [Fact]
    public async Task PersonaSelection_IsPerRequestAndDoesNotContaminateOtherRequests()
    {
        using var host = await CreateHostAsync(options =>
        {
            options.SubjectClaimType = SubjectClaimType;
            options.AddPersona("operator", "operator-1", [new Claim("role", "operator")]);
            options.AddPersona("viewer", "viewer-1", [new Claim("role", "viewer")]);
        });
        using var client = host.GetTestClient();

        var operatorTask = SendForSubjectAsync(client, "operator");
        var viewerTask = SendForSubjectAsync(client, "viewer");
        var anonymousTask = SendForSubjectAsync(client, null);

        var subjects = await Task.WhenAll(operatorTask, viewerTask, anonymousTask);

        Assert.Collection(
            subjects,
            subject => Assert.Equal("operator-1", subject),
            subject => Assert.Equal("viewer-1", subject),
            Assert.Null);
    }

    private static async Task<IHost> CreateHostAsync(
        Action<AppSurfaceTestAuthOptions> configureTestAuth,
        string environmentName = "Testing",
        Action<Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder>? configurePolicy = null,
        Action<IServiceCollection>? configureServicesBeforeTestAuth = null)
    {
        var host = CreateHost(
            configureTestAuth,
            environmentName,
            configurePolicy,
            configureServicesBeforeTestAuth);
        await host.StartAsync();
        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/ready");
        response.EnsureSuccessStatusCode();
        return host;
    }

    private static IHost CreateHost(
        Action<AppSurfaceTestAuthOptions> configureTestAuth,
        string environmentName = "Testing",
        Action<Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder>? configurePolicy = null,
        Action<IServiceCollection>? configureServicesBeforeTestAuth = null)
    {
        return new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.UseEnvironment(environmentName);
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorization(options =>
                    {
                        options.AddAppSurfacePolicy(
                            PolicyName,
                            configurePolicy ?? (policy => policy
                                .RequireAuthenticatedUser()
                                .RequireClaim("role", "operator")));
                    });
                    configureServicesBeforeTestAuth?.Invoke(services);
                    services.AddAppSurfaceTestAuth(configureTestAuth);
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/ready", () => Results.Ok());
                        endpoints.MapGet("/result", async (IAppSurfaceAspNetCorePolicyEvaluator evaluator) =>
                        {
                            var result = await evaluator.AuthorizeAsync(PolicyName);
                            return Results.Json(
                                new
                                {
                                    outcome = result.Outcome.ToString(),
                                    reason = result.Reason.ToString(),
                                    subject = result.Context?.User?.Id,
                                    diagnostic = result.Metadata.GetValueOrDefault(AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode),
                                },
                                statusCode: StatusCodeFor(result));
                        });
                        endpoints.MapGet("/protected", () => Results.Ok(new { ok = true }))
                            .RequireSurfacePolicy(PolicyName);
                    });
                });
            })
            .Build();
    }

    private static int StatusCodeFor(AppSurfaceAuthResult result)
    {
        return result.Outcome switch
        {
            AppSurfaceAuthOutcome.Allowed => StatusCodes.Status200OK,
            AppSurfaceAuthOutcome.Challenge => StatusCodes.Status401Unauthorized,
            AppSurfaceAuthOutcome.Forbid => StatusCodes.Status403Forbidden,
            AppSurfaceAuthOutcome.SetupFailure => StatusCodes.Status500InternalServerError,
            AppSurfaceAuthOutcome.UnsafeNavigation => StatusCodes.Status400BadRequest,
            AppSurfaceAuthOutcome.StaleOrUnknownSession => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status500InternalServerError,
        };
    }

    private static async Task<string?> SendForSubjectAsync(HttpClient client, string? personaName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/result");
        if (personaName is not null)
        {
            request.WithAppSurfaceTestPersona(personaName);
        }

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        return ReadNullableString(json.RootElement, "subject");
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.GetProperty(propertyName).GetString()
            ?? throw new InvalidOperationException($"JSON property '{propertyName}' was null.");
    }

    private static string? ReadNullableString(JsonElement element, string propertyName)
    {
        var property = element.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.Null ? null : property.GetString();
    }

    private sealed class CapturingPolicyEvaluator(Func<AppSurfaceAuthResult> authorize)
        : IAppSurfaceAspNetCorePolicyEvaluator
    {
        public Task<AppSurfaceAuthResult> AuthorizeAsync(
            string policyName,
            object? resource = null,
            CancellationToken cancellationToken = default)
        {
            _ = policyName;
            _ = resource;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(authorize());
        }
    }
}
