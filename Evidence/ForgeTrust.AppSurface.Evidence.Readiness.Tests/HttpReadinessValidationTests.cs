using System.Net;

namespace ForgeTrust.AppSurface.Evidence.Readiness.Tests;

public sealed class HttpReadinessValidationTests
{
    [Fact]
    public void Defaults_and_snapshot_match_the_documented_policy()
    {
        var options = new HttpReadinessOptions();
        var settings = HttpReadinessSettings.Snapshot(options);

        Assert.Equal(TimeSpan.FromSeconds(3), options.RequestTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.RetryDelay);
        Assert.Null(options.MaxAttempts);
        Assert.Equal(options.RequestTimeout, settings.RequestTimeout);
        Assert.Equal(options.RetryDelay, settings.RetryDelay);
        Assert.Null(settings.MaxAttempts);
        Assert.True(settings.IsSuccess(HttpStatusCode.OK));
        Assert.False(settings.IsSuccess(HttpStatusCode.Accepted));
    }

    [Theory]
    [InlineData(TimeSpan.TicksPerMillisecond * 0)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void ValidateDuration_rejects_nonpositive_and_unsupported_values(long ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadinessDeadline.ValidateDuration(TimeSpan.FromTicks(ticks), "timeout"));
    }

    [Fact]
    public void ValidateDuration_accepts_the_largest_supported_timer_value()
    {
        ReadinessDeadline.ValidateDuration(TimeSpan.FromMilliseconds(uint.MaxValue - 1d), "timeout");
    }

    [Theory]
    [InlineData("request")]
    [InlineData("retry")]
    public void Snapshot_rejects_invalid_durations(string field)
    {
        var options = field == "request"
            ? new HttpReadinessOptions { RequestTimeout = TimeSpan.Zero }
            : new HttpReadinessOptions { RetryDelay = TimeSpan.FromMilliseconds(-1) };

        Assert.Throws<ArgumentOutOfRangeException>(() => HttpReadinessSettings.Snapshot(options));
    }

    [Fact]
    public void Snapshot_rejects_null_options_and_null_predicate()
    {
        Assert.Throws<ArgumentNullException>(() => HttpReadinessSettings.Snapshot(null!));
        Assert.Throws<ArgumentNullException>(() => HttpReadinessSettings.Snapshot(new HttpReadinessOptions { IsSuccess = null! }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Snapshot_rejects_nonpositive_attempt_cap(int maximum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpReadinessSettings.Snapshot(new HttpReadinessOptions { MaxAttempts = maximum }));
    }

    [Theory]
    [InlineData("healthz", "https://readiness.example:8443/apps/service/healthz")]
    [InlineData("/healthz", "https://readiness.example:8443/healthz")]
    [InlineData("../healthz", "https://readiness.example:8443/apps/healthz")]
    [InlineData("/%2e%2e/healthz", "https://readiness.example:8443/%2e%2e/healthz")]
    [InlineData("healthz%2fadmin", "https://readiness.example:8443/apps/service/healthz%2fadmin")]
    [InlineData("healthz?ready=true", "https://readiness.example:8443/apps/service/healthz?ready=true")]
    public void ResolveProbeUri_accepts_relative_root_relative_and_encoded_paths(string path, string expected)
    {
        var resolved = HttpReadinessProbe.ResolveProbeUri(ReadinessTestData.Endpoint, path);

        Assert.Equal(new Uri(expected), resolved);
        Assert.Equal(ReadinessTestData.Endpoint.IdnHost, resolved.IdnHost);
        Assert.Equal(ReadinessTestData.Endpoint.Port, resolved.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("https://readiness.example:8443/healthz")]
    [InlineData("//readiness.example/healthz")]
    [InlineData("\\\\readiness.example\\healthz")]
    [InlineData("health\\z")]
    [InlineData("healthz#fragment")]
    public void ValidateProbePath_rejects_ambiguous_paths(string path)
    {
        Assert.Throws<ArgumentException>(() => HttpReadinessProbe.ValidateProbePath(path));
    }

    [Theory]
    [InlineData("https://foreign.example/healthz")]
    [InlineData("http://readiness.example:8443/healthz")]
    [InlineData("//foreign.example/healthz")]
    [InlineData("https://readiness.example:9443/healthz")]
    public void ResolveProbeUri_rejects_origin_changes(string path)
    {
        Assert.Throws<ArgumentException>(() => HttpReadinessProbe.ResolveProbeUri(ReadinessTestData.Endpoint, path));
    }

    [Theory]
    [InlineData("file:///tmp/healthz")]
    [InlineData("ftp://readiness.example/healthz")]
    [InlineData("/healthz")]
    public void ResolveProbeUri_rejects_non_http_or_relative_endpoints(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => HttpReadinessProbe.ResolveProbeUri(new Uri(endpoint, UriKind.RelativeOrAbsolute), "healthz"));
    }

    [Fact]
    public async Task WaitAsync_validates_arguments_before_dispatch()
    {
        var handler = new ControlledHandler((_, _) => Task.FromResult(ReadinessTestData.Response(HttpStatusCode.OK)));
        using var client = ReadinessTestData.Client(handler);

        await Assert.ThrowsAsync<ArgumentNullException>(() => HttpReadinessProbe.WaitAsync(null!, ReadinessTestData.Endpoint, "healthz", TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => HttpReadinessProbe.WaitAsync(client, null!, "healthz", TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => HttpReadinessProbe.WaitAsync(client, ReadinessTestData.Endpoint, "healthz", TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentException>(() => HttpReadinessProbe.WaitAsync(client, ReadinessTestData.Endpoint, "https://foreign.example/healthz", TimeSpan.FromSeconds(1)));
        Assert.Equal(0, handler.RequestCount);
    }
}
