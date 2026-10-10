using ConfigPackageConsumer.Domain;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

internal static class ExplicitRegistrationExample
{
    internal const string SafeEndpoint = "https://payments.example.test";

    public static void Register(IServiceCollection services, bool candidate)
    {
        services.AddSingleton<IPaymentsEndpointFixtureDependency, FixturePaymentsEndpointDependency>();
        if (candidate)
        {
            services.AddAppSurfaceConfig<PaymentsEndpointConfig>();
        }
    }

    public static void VerifyRuntime(IServiceProvider services, bool candidate)
    {
        var config = candidate
            ? services.GetRequiredService<PaymentsEndpointConfig>()
            : new PaymentsEndpointConfig(services.GetRequiredService<IPaymentsEndpointFixtureDependency>());
        if (!candidate)
        {
            ((IConfig)config).Init(
                services.GetRequiredService<IConfigManager>(),
                services.GetRequiredService<IEnvironmentProvider>(),
                ConfigKeyAttribute.GetLogicalKey(typeof(PaymentsEndpointConfig)));
        }

        if (config.Value?.Endpoint != SafeEndpoint || config.DependencyMarker != "fixture-dependency-ready")
        {
            throw new InvalidOperationException("explicit-domain-runtime-contract-failed");
        }
    }

    public static void VerifyAudit(ConfigAuditReport report, bool candidate)
    {
        var entries = report.Entries.Where(entry => entry.Key == "Payments:Endpoint").ToArray();
        if (candidate)
        {
            if (entries.Length != 1
                || entries[0].State != ConfigAuditEntryState.Resolved
                || entries[0].DeclaredType != typeof(PaymentsEndpointValue).FullName)
            {
                throw new InvalidOperationException("explicit-domain-audit-contract-failed");
            }

            Console.WriteLine("Explicit Domain registration: PASS");
            return;
        }

        if (entries.Length != 0)
        {
            throw new InvalidOperationException("baseline-domain-declaration-was-present");
        }

        Console.WriteLine("Baseline manual Domain selection: Missing declaration");
    }

    private sealed class FixturePaymentsEndpointDependency : IPaymentsEndpointFixtureDependency
    {
        public string Marker => "fixture-dependency-ready";
    }
}
