using System.Collections.Concurrent;
using ForgeTrust.AppSurface.Config;

namespace ConfigPackageConsumer.Domain;

public static class DomainFixtureCounters
{
    private static readonly ConcurrentDictionary<string, int> Activations = new(StringComparer.Ordinal);

    public static int Total => Activations.Values.Sum();

    public static int For(string typeName) => Activations.TryGetValue(typeName, out var count) ? count : 0;

    public static void Reset() => Activations.Clear();

    internal static void Activated(string typeName) => Activations.AddOrUpdate(typeName, 1, static (_, count) => count + 1);
}

public abstract class TrackedDomainConfig<T> : Config<T>
    where T : class
{
    protected TrackedDomainConfig(string typeName) => DomainFixtureCounters.Activated(typeName);
}

[ConfigKey("Skoolit.Features", root: true)]
public sealed class ProductFeatureCatalogConfig : TrackedDomainConfig<string>
{
    public ProductFeatureCatalogConfig() : base(nameof(ProductFeatureCatalogConfig)) { }
}

[ConfigKey("Skoolit.FeatureWorkerParity", root: true)]
public sealed class ProductFeatureWorkerParityReceiptConfig : TrackedDomainConfig<string>
{
    public ProductFeatureWorkerParityReceiptConfig() : base(nameof(ProductFeatureWorkerParityReceiptConfig)) { }
}

public sealed record PaymentsEndpointValue(string Endpoint);

public interface IPaymentsEndpointFixtureDependency
{
    string Marker { get; }
}

[ConfigKey("Payments:Endpoint", root: true)]
public sealed class PaymentsEndpointConfig : TrackedDomainConfig<PaymentsEndpointValue>
{
    public PaymentsEndpointConfig(IPaymentsEndpointFixtureDependency dependency)
        : base(nameof(PaymentsEndpointConfig)) => DependencyMarker = dependency.Marker;

    public string DependencyMarker { get; }
}

[ConfigKey("Payments:ApiKey", root: true)]
public sealed class ConsumerPaymentConfig : Config<string>;

[ConfigKey("Consumer:SecretTripwire", root: true)]
public sealed class UnselectedSecretTripwireConfig : TrackedDomainConfig<Secret<string>>
{
    public UnselectedSecretTripwireConfig() : base(nameof(UnselectedSecretTripwireConfig)) { }
}
