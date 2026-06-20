using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Configuration;

namespace ForgeTrust.AppSurface.Observability;

internal sealed record AppSurfaceObservabilityPlan(
    AppSurfaceOtlpExporterMode ExporterMode,
    Uri? Endpoint,
    string ServiceName,
    string? ServiceVersion,
    bool ShouldRegisterExporter,
    bool ShouldLogSkippedExporterDiagnostic)
{
    internal static AppSurfaceObservabilityPlan Resolve(
        StartupContext context,
        IConfiguration configuration,
        Action<AppSurfaceObservabilityOptions>? configure = null,
        IAppSurfaceEnvironmentReader? environment = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AppSurfaceObservabilityOptions();
        configuration.GetSection(AppSurfaceObservabilityOptions.SectionName).Bind(options);
        configure?.Invoke(options);

        if (!Enum.IsDefined(options.ExporterMode))
        {
            throw new InvalidOperationException(
                $"{AppSurfaceObservabilityOptions.SectionName}:ExporterMode must be one of WhenEndpointConfigured, Always, or Never.");
        }

        if (options.OtlpEndpoint is not null && !options.OtlpEndpoint.IsAbsoluteUri)
        {
            throw new InvalidOperationException(
                $"{AppSurfaceObservabilityOptions.SectionName}:OtlpEndpoint must be an absolute URI.");
        }

        environment ??= AppSurfaceEnvironmentReader.Instance;
        var endpoint = options.OtlpEndpoint
            ?? ResolveUri(configuration[AppSurfaceObservabilityOptions.OtlpEndpointEnvironmentVariable])
            ?? ResolveUri(environment.GetEnvironmentVariable(AppSurfaceObservabilityOptions.OtlpEndpointEnvironmentVariable));

        var shouldRegisterExporter = options.ExporterMode switch
        {
            AppSurfaceOtlpExporterMode.Always => true,
            AppSurfaceOtlpExporterMode.Never => false,
            AppSurfaceOtlpExporterMode.WhenEndpointConfigured => endpoint is not null,
            _ => false
        };

        var serviceName = string.IsNullOrWhiteSpace(options.ServiceName)
            ? context.ApplicationName
            : options.ServiceName.Trim();

        var serviceVersion = string.IsNullOrWhiteSpace(options.ServiceVersion)
            ? null
            : options.ServiceVersion.Trim();

        return new AppSurfaceObservabilityPlan(
            options.ExporterMode,
            endpoint,
            serviceName,
            serviceVersion,
            shouldRegisterExporter,
            options.ExporterMode == AppSurfaceOtlpExporterMode.WhenEndpointConfigured && endpoint is null);
    }

    private static Uri? ResolveUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException(
                $"{AppSurfaceObservabilityOptions.OtlpEndpointEnvironmentVariable} must be an absolute URI.");
    }
}
