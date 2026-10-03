using System.Diagnostics;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Durable.Provider.Tests;

public sealed class ExternalActivationRegistrationTests
{
    [Fact]
    public void Registration_is_repeat_safe_passive_and_returns_the_same_collection()
    {
        var services = new ServiceCollection();
        var first = services.AddDurableExternalActivation();
        var second = services.AddDurableExternalActivation();

        Assert.Same(services, first);
        Assert.Same(services, second);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IDurableExternalActivationService));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(TimeProvider));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ILoggerFactory));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IDurableRuntimeHealth));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IDurableRuntimePumpAdmission));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IDurableRuntimePump));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ActivitySource));

        var serviceDescriptor = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IDurableExternalActivationService));
        var clockDescriptor = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(TimeProvider));
        Assert.Equal(ServiceLifetime.Singleton, serviceDescriptor.Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, clockDescriptor.Lifetime);
        Assert.Same(TimeProvider.System, clockDescriptor.ImplementationInstance);
    }

    [Fact]
    public void Existing_host_clock_and_activation_service_registrations_win()
    {
        var clock = new ExternalActivationClock();
        var service = new FixedActivationService();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDurableExternalActivationService>(service);

        services.AddDurableExternalActivation();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
        Assert.Same(service, provider.GetRequiredService<IDurableExternalActivationService>());
    }

    [Fact]
    public void Health_and_admission_can_be_registered_on_either_side_of_the_extension()
    {
        foreach (var registerDependenciesFirst in new[] { true, false })
        {
            var services = new ServiceCollection();
            var health = new ExternalActivationHealth(_ => ValueTask.FromResult(
                ExternalActivationTestSupport.Health()));
            var admission = new ExternalActivationAdmission((_, _) => ValueTask.FromResult(
                new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null)));

            if (registerDependenciesFirst)
            {
                services.AddSingleton<IDurableRuntimeHealth>(health);
                services.AddSingleton<IDurableRuntimePumpAdmission>(admission);
            }

            services.AddDurableExternalActivation();

            if (!registerDependenciesFirst)
            {
                services.AddSingleton<IDurableRuntimeHealth>(health);
                services.AddSingleton<IDurableRuntimePumpAdmission>(admission);
            }

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
            var resolved = provider.GetRequiredService<IDurableExternalActivationService>();

            Assert.Same(resolved, provider.GetRequiredService<IDurableExternalActivationService>());
            Assert.Equal(
                ServiceLifetime.Singleton,
                Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IDurableExternalActivationService)).Lifetime);
        }
    }

    [Fact]
    public void Validate_on_build_reports_each_missing_constructor_dependency()
    {
        var health = new ExternalActivationHealth(_ => ValueTask.FromResult(
            ExternalActivationTestSupport.Health()));
        var admission = new ExternalActivationAdmission((_, _) => ValueTask.FromResult(
            new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null)));

        foreach (var includeHealth in new[] { false, true })
        {
            foreach (var includeAdmission in new[] { false, true })
            {
                if (includeHealth && includeAdmission)
                {
                    continue;
                }

                var services = new ServiceCollection();
                if (includeHealth)
                {
                    services.AddSingleton<IDurableRuntimeHealth>(health);
                }

                if (includeAdmission)
                {
                    services.AddSingleton<IDurableRuntimePumpAdmission>(admission);
                }

                services.AddDurableExternalActivation();

                Assert.Throws<AggregateException>(() => services.BuildServiceProvider(new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true,
                }));
            }
        }
    }

    [Fact]
    public void Opaque_dependency_factory_is_not_run_by_build_validation_and_fails_at_resolution()
    {
        var factoryError = new InvalidOperationException("intentional test health factory failure");
        var services = new ServiceCollection();
        services.AddSingleton<IDurableRuntimeHealth>(_ => throw factoryError);
        services.AddSingleton<IDurableRuntimePumpAdmission>(new ExternalActivationAdmission((_, _) => ValueTask.FromResult(
            new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null))));
        services.AddDurableExternalActivation();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var observed = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IDurableExternalActivationService>());
        Assert.Same(factoryError, observed);
    }

    private sealed class FixedActivationService : IDurableExternalActivationService
    {
        public ValueTask<DurableExternalActivationResult> ActivateAsync(
            DurableExternalActivationRequest request,
            CancellationToken callerCancellation = default) =>
            ValueTask.FromResult(new DurableExternalActivationResult(
                DurableExternalActivationOutcomeKind.ActivationFailed,
                null,
                DurableProblemCodes.ExternalActivationFailed,
                null));
    }
}
