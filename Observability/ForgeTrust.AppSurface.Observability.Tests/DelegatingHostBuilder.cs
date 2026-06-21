using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Observability.Tests;

internal sealed class DelegatingHostBuilder(
    IServiceCollection services,
    IReadOnlyDictionary<string, string?>? configurationValues = null) : IHostBuilder
{
    public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

    public IHost Build()
    {
        throw new NotSupportedException();
    }

    public IHostBuilder ConfigureAppConfiguration(Action<HostBuilderContext, IConfigurationBuilder> configureDelegate)
    {
        return this;
    }

    public IHostBuilder ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> configureDelegate)
    {
        return this;
    }

    public IHostBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate)
    {
        return this;
    }

    public IHostBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                configurationValues
                ?? new Dictionary<string, string?>
                {
                    ["AppSurfaceObservability:ExporterMode"] = "Never"
                })
            .Build();
        configureDelegate(new HostBuilderContext(Properties) { Configuration = configuration }, services);
        return this;
    }

    public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
        where TContainerBuilder : notnull
    {
        return this;
    }

    public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(
        Func<HostBuilderContext, IServiceProviderFactory<TContainerBuilder>> factory)
        where TContainerBuilder : notnull
    {
        return this;
    }
}
