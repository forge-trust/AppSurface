using ForgeTrust.AppSurface.Durable.CloudTasks;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.CloudTasks.Tests;

public sealed class CloudTasksRegistrationTests
{
    [Fact]
    public void Missing_source_is_passive_at_registration_and_fails_on_explicit_resolution()
    {
        // protects=opt-in registration performs no credential or provider I/O; fails_when=missing source fails during Add or silently resolves; why_new=distinguishes passive composition from explicit resolution failure; seam=public IServiceCollection extension and real container resolution; no_claim=host application graph is complete
        var services = new ServiceCollection();
        var returned = services.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(3)));
        Assert.Same(services, returned);
        Assert.Single(services, d => d.ServiceType == typeof(ICloudTasksActivationClient));
        Assert.Single(services, d => d.ServiceType == typeof(CloudTasksSdkTransport));
        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ICloudTasksActivationClient>());
    }

    [Fact]
    public void Source_descriptor_conflicts_and_repeated_extensions_are_rejected_atomically()
    {
        // protects=precise conflict rules without partial service registration; fails_when=bad source shape mutates collection or is accepted; why_new=registration should be deterministic and atomic; seam=public IServiceCollection descriptor inspection; no_claim=unsupported later service-provider mutation
        var noSource = new ServiceCollection();
        noSource.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(3)));
        AssertAtomicConflict(noSource, () => noSource.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(3))));
        AssertAtomicConflict(noSource, () => noSource.AddCloudRunMetadataAccessTokenSource(new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(1))));

        var duplicate = new ServiceCollection();
        duplicate.AddSingleton<ICloudTasksAccessTokenSource, FakeSource>();
        duplicate.AddSingleton<ICloudTasksAccessTokenSource, FakeSource>();
        AssertAtomicConflict(duplicate, () => duplicate.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(3))));

        foreach (var lifetime in new[] { ServiceLifetime.Scoped, ServiceLifetime.Transient })
        {
            var services = new ServiceCollection();
            ((IServiceCollection)services).Add(new ServiceDescriptor(typeof(ICloudTasksAccessTokenSource), typeof(FakeSource), lifetime));
            AssertAtomicConflict(services, () => services.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(3))));
        }

        var metadataRepeat = new ServiceCollection();
        metadataRepeat.AddCloudRunMetadataAccessTokenSource(new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(1)));
        AssertAtomicConflict(metadataRepeat, () => metadataRepeat.AddCloudRunMetadataAccessTokenSource(new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(1))));
        metadataRepeat.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(3)));
        AssertAtomicConflict(metadataRepeat, () => metadataRepeat.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(3))));

        var activatedMetadata = new ServiceCollection();
        activatedMetadata.AddCloudRunMetadataAccessTokenSource(new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(1)));
        activatedMetadata.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(3)));
        AssertAtomicConflict(activatedMetadata, () => activatedMetadata.AddCloudRunMetadataAccessTokenSource(new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(1))));
    }

    [Fact]
    public void Keyed_and_duplicate_instance_sources_are_rejected_before_activation_services_are_added()
    {
        // protects=exactly one unkeyed source requirement; fails_when=keyed or duplicated instance descriptors pass registration; why_new=keyed descriptors can otherwise hide a duplicate or missing default source; seam=Microsoft DI keyed descriptor registration; no_claim=keyed dispatch support
        var keyed = new ServiceCollection();
        keyed.AddKeyedSingleton<ICloudTasksAccessTokenSource, FakeSource>("source");
        AssertAtomicConflict(keyed, () => keyed.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(3))));

        var instance = new ServiceCollection();
        instance.AddSingleton<ICloudTasksAccessTokenSource>(new FakeSource());
        instance.AddSingleton<ICloudTasksAccessTokenSource>(new FakeSource());
        AssertAtomicConflict(instance, () => instance.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(3))));
    }

    [Fact]
    public void Later_source_registration_fails_at_resolution_before_factory_or_io()
    {
        // protects=registration guard catches unsupported post-opt-in descriptor mutation before transport creation; fails_when=I/O-capable client gets built; why_new=container is built from the mutable collection's later descriptor shape; seam=late descriptor inserted before provider build; no_claim=mutating IServiceCollection after provider construction
        var services = new ServiceCollection();
        services.AddSingleton<ICloudTasksAccessTokenSource, FakeSource>();
        services.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(3)));
        services.AddSingleton<ICloudTasksAccessTokenSource, FakeSource>();
        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ICloudTasksActivationClient>());
    }

    [Fact]
    public void First_or_replacement_source_added_after_activation_fails_before_source_factory()
    {
        // protects=source-first registration contract; fails_when=a late first or replacement source resolves; why_new=duplicate-only guards miss one late descriptor; seam=public registration and container resolution; no_claim=mutation after provider construction
        foreach (var replace in new[] { false, true })
        {
            var factories = 0;
            var services = new ServiceCollection();
            if (replace) services.AddSingleton<ICloudTasksAccessTokenSource, FakeSource>();
            services.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(3)));
            if (replace) services.Remove(services.Single(static descriptor => descriptor.ServiceType == typeof(ICloudTasksAccessTokenSource)));
            services.AddSingleton<ICloudTasksAccessTokenSource>(_ => { factories++; return new FakeSource(); });
            using var provider = services.BuildServiceProvider();
            var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ICloudTasksActivationClient>());
            Assert.Contains("ASCTV004", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, factories);
        }
    }

    [Fact]
    public void Client_is_singleton_and_container_owned_factory_source_is_disposed_once()
    {
        // protects=shared client and container-owned singleton source lifecycle; fails_when=client duplicates or factory source leaks/double-disposes; why_new=one reusable SDK channel implies explicit DI ownership; seam=container-created factory source and public client resolution; no_claim=dispatch draining host policy
        var source = new FakeSource();
        var constructions = 0;
        var services = new ServiceCollection();
        services.AddSingleton<ICloudTasksAccessTokenSource>(_ => { Interlocked.Increment(ref constructions); return source; });
        services.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(3)));
        using (var provider = services.BuildServiceProvider())
        {
            var first = provider.GetRequiredService<ICloudTasksActivationClient>();
            var second = provider.GetRequiredService<ICloudTasksActivationClient>();
            Assert.Same(first, second);
            Assert.Equal(1, constructions);
            Assert.Equal(0, source.DisposeCount);
        }
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public void Application_supplied_instance_remains_application_owned()
    {
        // protects=DI ownership of explicitly supplied source instances; fails_when=CloudTasks integration disposes app-owned source; why_new=instance registration ownership differs from factory/type registration; seam=preconstructed disposable source instance; no_claim=application lifetime beyond its own provider
        var source = new FakeSource();
        var services = new ServiceCollection();
        services.AddSingleton<ICloudTasksAccessTokenSource>(source);
        services.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(3)));
        using (services.BuildServiceProvider()) { }
        Assert.Equal(0, source.DisposeCount);
        source.Dispose();
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public void Built_in_metadata_source_and_private_transport_are_registered_singletons()
    {
        // protects=explicit built-in metadata composition and one shared SDK transport; fails_when=registration creates multiple source/transport/client descriptors; why_new=metadata is opt-in and the SDK channel is reusable singleton state; seam=descriptor lifetime inspection; no_claim=Cloud Run identity availability
        var services = new ServiceCollection();
        services.AddCloudRunMetadataAccessTokenSource(new CloudRunMetadataAccessTokenOptions(TimeSpan.FromSeconds(1)));
        services.AddCloudTasksActivation(new CloudTasksActivationClientOptions(TimeSpan.FromSeconds(3)));
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(ICloudTasksAccessTokenSource)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(ICloudTasksActivationClient)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(CloudTasksSdkTransport)).Lifetime);
        using var provider = services.BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<ICloudTasksActivationClient>(), provider.GetRequiredService<ICloudTasksActivationClient>());
        var metadata = Assert.IsType<CloudRunMetadataAccessTokenSource>(provider.GetRequiredService<ICloudTasksAccessTokenSource>());
        var transport = provider.GetRequiredService<CloudTasksSdkTransport>();
        Assert.False(transport.IsDisposed);
        provider.Dispose();
        Assert.True(transport.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => metadata.GetAccessTokenAsync());
        transport.Dispose(); // Idempotent release must not double-release its handler.
        Assert.True(transport.IsDisposed);
    }

    // protects=null options fail with stable safe validation metadata before registration; fails_when=invalid options mutate services or expose unsafe diagnostics; no_claim=all option value validation.
    [Fact]
    public void Required_options_report_safe_validation_metadata_before_registration()
    {
        var services = new ServiceCollection();
        foreach (var error in new[]
        {
            Assert.Throws<ArgumentException>(() => services.AddCloudTasksActivation(null!)),
            Assert.Throws<ArgumentException>(() => services.AddCloudRunMetadataAccessTokenSource(null!))
        })
        {
            Assert.Equal("options", error.ParamName);
            Assert.Equal("ASCTV001", error.Data["CloudTasksValidationCode"]);
        }
        Assert.Empty(services);
        Assert.Throws<ArgumentNullException>(() => CloudTasksServiceCollectionExtensions.AddCloudTasksActivation(null!, new(TimeSpan.FromSeconds(1))));
        Assert.Throws<ArgumentNullException>(() => CloudTasksServiceCollectionExtensions.AddCloudRunMetadataAccessTokenSource(null!, new(TimeSpan.FromSeconds(1))));
    }

    private static void AssertAtomicConflict(IServiceCollection services, Action action)
    {
        var before = services.ToArray();
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(before, services);
        Assert.Contains("ASCTV004", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeSource : ICloudTasksAccessTokenSource, IDisposable
    {
        public int DisposeCount { get; private set; }
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult("unused");
        public void Dispose() => DisposeCount++;
    }
}
