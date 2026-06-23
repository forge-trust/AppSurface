using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Configuration;

namespace ForgeTrust.AppSurface.Observability.Tests;

public sealed class AppSurfaceObservabilityPlanTests
{
    [Fact]
    public void Resolve_WhenEndpointConfiguredWithoutEndpoint_SkipsExporterAndLogsDiagnostic()
    {
        var plan = AppSurfaceObservabilityPlan.Resolve(
            CreateContext("Catalog API"),
            CreateConfiguration(),
            environment: new TestEnvironmentReader());

        Assert.Equal(AppSurfaceOtlpExporterMode.WhenEndpointConfigured, plan.ExporterMode);
        Assert.Null(plan.Endpoint);
        Assert.False(plan.ShouldRegisterExporter);
        Assert.True(plan.ShouldLogSkippedExporterDiagnostic);
        Assert.Equal("Catalog API", plan.ServiceName);
    }

    [Fact]
    public void Resolve_WithConfiguredEndpoint_RegistersExporter()
    {
        var plan = AppSurfaceObservabilityPlan.Resolve(
            CreateContext("Catalog API"),
            CreateConfiguration(("AppSurfaceObservability:OtlpEndpoint", "http://collector:4317")),
            environment: new TestEnvironmentReader());

        Assert.True(plan.ShouldRegisterExporter);
        Assert.False(plan.ShouldLogSkippedExporterDiagnostic);
        Assert.Equal(new Uri("http://collector:4317"), plan.Endpoint);
    }

    [Fact]
    public void Resolve_ConfiguredEndpointWinsOverOtelEnvironment()
    {
        var plan = AppSurfaceObservabilityPlan.Resolve(
            CreateContext("Catalog API"),
            CreateConfiguration(("AppSurfaceObservability:OtlpEndpoint", "http://appsurface:4317")),
            environment: new TestEnvironmentReader(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://otel:4317")));

        Assert.Equal(new Uri("http://appsurface:4317"), plan.Endpoint);
    }

    [Fact]
    public void Resolve_OtelEnvironmentEndpointEnablesWhenEndpointConfigured()
    {
        var plan = AppSurfaceObservabilityPlan.Resolve(
            CreateContext("Catalog API"),
            CreateConfiguration(),
            environment: new TestEnvironmentReader(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://otel:4317")));

        Assert.True(plan.ShouldRegisterExporter);
        Assert.Equal(new Uri("http://otel:4317"), plan.Endpoint);
    }

    [Fact]
    public void Resolve_AlwaysRegistersExporterWithoutEndpoint()
    {
        var plan = AppSurfaceObservabilityPlan.Resolve(
            CreateContext("Catalog API"),
            CreateConfiguration(("AppSurfaceObservability:ExporterMode", "Always")),
            environment: new TestEnvironmentReader());

        Assert.True(plan.ShouldRegisterExporter);
        Assert.False(plan.ShouldLogSkippedExporterDiagnostic);
        Assert.Null(plan.Endpoint);
    }

    [Fact]
    public void Resolve_NeverSkipsExporterEvenWithEndpoint()
    {
        var plan = AppSurfaceObservabilityPlan.Resolve(
            CreateContext("Catalog API"),
            CreateConfiguration(
                ("AppSurfaceObservability:ExporterMode", "Never"),
                ("AppSurfaceObservability:OtlpEndpoint", "http://collector:4317")),
            environment: new TestEnvironmentReader());

        Assert.False(plan.ShouldRegisterExporter);
        Assert.False(plan.ShouldLogSkippedExporterDiagnostic);
        Assert.Equal(new Uri("http://collector:4317"), plan.Endpoint);
    }

    [Fact]
    public void Resolve_OtelConfigurationEndpointEnablesWhenEndpointConfigured()
    {
        var plan = AppSurfaceObservabilityPlan.Resolve(
            CreateContext("Catalog API"),
            CreateConfiguration(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://configured-otel:4317")),
            environment: new TestEnvironmentReader());

        Assert.True(plan.ShouldRegisterExporter);
        Assert.Equal(new Uri("http://configured-otel:4317"), plan.Endpoint);
    }

    [Fact]
    public void Resolve_AppSurfaceEnvironmentEndpointWinsOverOtelConfigurationEndpoint()
    {
        var plan = AppSurfaceObservabilityPlan.Resolve(
            CreateContext("Catalog API"),
            CreateConfiguration(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://configured-otel:4317")),
            environment: new TestEnvironmentReader(("AppSurfaceObservability__OtlpEndpoint", "http://appsurface-env:4317")));

        Assert.True(plan.ShouldRegisterExporter);
        Assert.Equal(new Uri("http://appsurface-env:4317"), plan.Endpoint);
    }

    [Fact]
    public void Resolve_ServiceNameOverrideWinsOverStartupContextApplicationName()
    {
        var plan = AppSurfaceObservabilityPlan.Resolve(
            CreateContext("Catalog API"),
            CreateConfiguration(
                ("AppSurfaceObservability:ServiceName", "orders-api"),
                ("AppSurfaceObservability:ServiceVersion", "2026.6.19")),
            environment: new TestEnvironmentReader());

        Assert.Equal("orders-api", plan.ServiceName);
        Assert.Equal("2026.6.19", plan.ServiceVersion);
    }

    [Fact]
    public void Resolve_BlankServiceVersionIsNotEmitted()
    {
        var plan = AppSurfaceObservabilityPlan.Resolve(
            CreateContext("Catalog API"),
            CreateConfiguration(("AppSurfaceObservability:ServiceVersion", "   ")),
            environment: new TestEnvironmentReader());

        Assert.Null(plan.ServiceVersion);
    }

    [Fact]
    public void Resolve_ServiceMetadataValuesAreTrimmed()
    {
        var plan = AppSurfaceObservabilityPlan.Resolve(
            CreateContext("Catalog API"),
            CreateConfiguration(
                ("AppSurfaceObservability:ServiceName", "  orders-api  "),
                ("AppSurfaceObservability:ServiceVersion", "  2026.6.19  ")),
            environment: new TestEnvironmentReader());

        Assert.Equal("orders-api", plan.ServiceName);
        Assert.Equal("2026.6.19", plan.ServiceVersion);
    }

    [Fact]
    public void Resolve_InvalidExporterModeThrowsClearMessage()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AppSurfaceObservabilityPlan.Resolve(
                CreateContext("Catalog API"),
                CreateConfiguration(),
                options => options.ExporterMode = (AppSurfaceOtlpExporterMode)999,
                new TestEnvironmentReader()));

        Assert.Contains(
            "AppSurfaceObservability:ExporterMode must be one of WhenEndpointConfigured, Always, or Never",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RelativeConfiguredEndpointThrowsClearMessage()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AppSurfaceObservabilityPlan.Resolve(
                CreateContext("Catalog API"),
                CreateConfiguration(),
                options => options.OtlpEndpoint = new Uri("/relative", UriKind.Relative),
                new TestEnvironmentReader()));

        Assert.Contains("AppSurfaceObservability:OtlpEndpoint must be an absolute URI", exception.Message);
    }

    [Fact]
    public void Resolve_InvalidEnvironmentEndpointThrowsClearMessage()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AppSurfaceObservabilityPlan.Resolve(
                CreateContext("Catalog API"),
                CreateConfiguration(),
                environment: new TestEnvironmentReader(("OTEL_EXPORTER_OTLP_ENDPOINT", "not a uri"))));

        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT must be an absolute URI", exception.Message);
    }

    [Fact]
    public void Resolve_InvalidAppSurfaceEnvironmentEndpointThrowsClearMessage()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AppSurfaceObservabilityPlan.Resolve(
                CreateContext("Catalog API"),
                CreateConfiguration(),
                environment: new TestEnvironmentReader(("AppSurfaceObservability__OtlpEndpoint", "not a uri"))));

        Assert.Contains("AppSurfaceObservability__OtlpEndpoint must be an absolute URI", exception.Message);
    }

    [Fact]
    public void Resolve_NullContextThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            AppSurfaceObservabilityPlan.Resolve(
                null!,
                CreateConfiguration(),
                environment: new TestEnvironmentReader()));

        Assert.Equal("context", exception.ParamName);
    }

    [Fact]
    public void Resolve_NullConfigurationThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            AppSurfaceObservabilityPlan.Resolve(
                CreateContext("Catalog API"),
                null!,
                environment: new TestEnvironmentReader()));

        Assert.Equal("configuration", exception.ParamName);
    }

    [Fact]
    public void Resolve_NullEnvironmentUsesDefaultReader()
    {
        var plan = AppSurfaceObservabilityPlan.Resolve(
            CreateContext("Catalog API"),
            CreateConfiguration(("AppSurfaceObservability:OtlpEndpoint", "http://collector:4317")),
            environment: null);

        Assert.Equal(new Uri("http://collector:4317"), plan.Endpoint);
    }

    [Theory]
    [InlineData(AppSurfaceOtlpExporterMode.WhenEndpointConfigured, 0)]
    [InlineData(AppSurfaceOtlpExporterMode.Always, 1)]
    [InlineData(AppSurfaceOtlpExporterMode.Never, 2)]
    public void AppSurfaceOtlpExporterMode_NumericValuesAreStable(
        AppSurfaceOtlpExporterMode value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    private static StartupContext CreateContext(string applicationName)
    {
        return new StartupContext([], new TestHostModule(), applicationName);
    }

    private static IConfiguration CreateConfiguration(params (string Key, string Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }
}
