using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Readiness;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

/// <summary>Checks owned Aspire construction and registration disposal without starting DCP or proving readiness.</summary>
/// <remarks>These tests assert API shape and lease lifetime only; they do not exercise health, endpoint-value allocation, or native readiness.</remarks>
public sealed class EvidenceAspireApplicationUnstartedOwnershipTests
{
    [Fact]
    public async Task Registration_AddOwnedApplicationBindsMultipleHealthResourcesToOneUnstartedOwner()
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan);
        var admission = await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Observation, plan,
            run.Context, run.Supervisor, null, CancellationToken.None);
        admission.Activate("test-output");
        var registration = new EvidenceHostRegistration();
        registration.AddAspireHealthResource(new EvidenceResourceDeclaration("api-health", "aspire_health", 30, []), "web");
        registration.AddAspireHealthResource(new EvidenceResourceDeclaration("worker-health", "aspire_health", 30, []), "web");
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        using var cancellation = new CancellationTokenSource();
        EvidenceAspireApplication? owned = null;
        IAsyncDisposable? apiHealth = null;
        IAsyncDisposable? workerHealth = null;
        IAsyncDisposable? extraHealth = null;
        IAsyncDisposable? laterHealth = null;

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await EvidenceAspireApplication.StartAdmittedAsync(
                    admission,
                    plan,
                    () => CreateBuilder(repoRoot),
                    application =>
                    {
                        owned = application;
                        registration.AddOwnedApplication(application);
                        cancellation.Cancel();
                    },
                    cancellation.Token));

            Assert.NotNull(owned);
            Assert.Same(owned, Assert.Single(registration.AdditionalOwned));
            Assert.Equal(["api-health", "worker-health"], registration.Resources.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("api-health", registration.Resources["api-health"].Id);
            Assert.Equal("worker-health", registration.Resources["worker-health"].Id);
            apiHealth = Assert.IsAssignableFrom<IAsyncDisposable>(registration.Resources["api-health"]);
            workerHealth = Assert.IsAssignableFrom<IAsyncDisposable>(registration.Resources["worker-health"]);

            await apiHealth.DisposeAsync();
            extraHealth = Assert.IsAssignableFrom<IAsyncDisposable>(owned.CreateHealthReadiness("extra-health", "web"));
            await extraHealth.DisposeAsync();
            extraHealth = null;
            laterHealth = Assert.IsAssignableFrom<IAsyncDisposable>(owned.CreateHealthReadiness("still-owned", "web"));
            await laterHealth.DisposeAsync();
            laterHealth = null;
            await workerHealth.DisposeAsync();
            workerHealth = null;
            Assert.Throws<ObjectDisposedException>(() => owned.CreateHealthReadiness("after-final-release", "web"));
        }
        finally
        {
            if (extraHealth is not null) await extraHealth.DisposeAsync();
            if (laterHealth is not null) await laterHealth.DisposeAsync();
            if (apiHealth is not null) await apiHealth.DisposeAsync();
            if (workerHealth is not null) await workerHealth.DisposeAsync();
            if (owned is not null) await owned.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAdmitted_CancellationAfterBuildRetainsUnstartedApplicationUntilOwnedCleanup()
    {
        var plan = EvidenceHostAdmissionTestRun.CreateObservationPlan();
        using var run = EvidenceHostAdmissionTestRun.Create(EvidenceExecutionMode.Observation, plan);
        var admission = await EvidenceAdmission.AdmitAsync(EvidenceExecutionMode.Observation, plan,
            run.Context, run.Supervisor, null, CancellationToken.None);
        admission.Activate("test-output");
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        using var cancellation = new CancellationTokenSource();
        var ownershipCalls = 0;
        EvidenceAspireApplication? owned = null;
        var registrations = new List<IAsyncDisposable>();

        try
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await EvidenceAspireApplication.StartAdmittedAsync(
                    admission,
                    plan,
                    () => CreateBuilder(repoRoot),
                    application =>
                    {
                        ownershipCalls++;
                        owned = application;
                        cancellation.Cancel();
                    },
                    cancellation.Token));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal(1, ownershipCalls);
            Assert.NotNull(owned);

            Assert.ThrowsAny<ArgumentException>(() => owned.CreateHealthReadiness(" ", "web"));
            Assert.ThrowsAny<ArgumentException>(() => owned.CreateHttpReadiness("http", "web", "http", "//other/health", TimeSpan.FromSeconds(5)));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                owned.CreateHttpReadiness("http", "web", "http", "/healthz", TimeSpan.Zero));
            Assert.Throws<ArgumentNullException>(() => owned.CreateHttpReadiness("http", "web", "http", "/healthz",
                TimeSpan.FromSeconds(5), new EvidenceAspireHttpReadinessOptions { Http = null! }));
            Assert.Equal(ReadinessFailureCategory.EndpointUnavailable,
                Assert.Throws<ReadinessException>(() =>
                    owned.CreateHttpReadiness("missing", "missing", "http", "/healthz", TimeSpan.FromSeconds(5))).Category);
            Assert.Equal(ReadinessFailureCategory.EndpointUnavailable,
                Assert.Throws<ReadinessException>(() =>
                    owned.CreateHttpReadiness("missing-endpoint", "web", "missing", "/healthz", TimeSpan.FromSeconds(5))).Category);

            var health = owned.CreateHealthReadiness("health", "web");
            var http = owned.CreateHttpReadiness("http", "web", "http", "/healthz", TimeSpan.FromSeconds(5));
            registrations.Add((IAsyncDisposable)health);
            registrations.Add((IAsyncDisposable)http);
            await registrations[0].DisposeAsync();
            var laterHealth = owned.CreateHealthReadiness("later", "web");
            registrations.Add((IAsyncDisposable)laterHealth);
            await registrations[2].DisposeAsync();
            await registrations[1].DisposeAsync();

            Assert.Throws<ObjectDisposedException>(() => owned.CreateHealthReadiness("resource", "web"));
            await owned.DisposeAsync();
            await owned.DisposeAsync();
            Assert.Equal(1, ownershipCalls);
        }
        finally
        {
            foreach (var registration in registrations)
                await registration.DisposeAsync();
            if (owned is not null) await owned.DisposeAsync();
        }
    }

    private static IDistributedApplicationBuilder CreateBuilder(string repoRoot)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            AssemblyName = typeof(EvidenceAspireApplicationUnstartedOwnershipTests).Assembly.GetName().Name,
            ProjectDirectory = repoRoot,
            DisableDashboard = true,
        });
        builder.AddResource(new ExecutableResource("web", "dotnet", "."))
            .WithAnnotation(new EndpointAnnotation(System.Net.Sockets.ProtocolType.Tcp, uriScheme: "http", name: "http"));
        return builder;
    }
}
