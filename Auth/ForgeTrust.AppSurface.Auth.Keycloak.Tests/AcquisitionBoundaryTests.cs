using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// protects: exact acquisition authority, finite redirects, one transport attempt and atomic output.
// fails_when: a cache/redirect or response can broaden approved sources or commit wrong bytes.
// no_claim: controlled HTTP responses do not prove a remote service's future availability.
public sealed class AcquisitionBoundaryTests
{
    [Theory]
    [InlineData("http://repo.example.test/pkg")]
    [InlineData("https://user:password@repo.example.test/pkg")]
    [InlineData("https://127.0.0.1/pkg")]
    [InlineData("https://repo.example.test/pkg?q=secret")]
    [InlineData("https://repo.example.test/pkg#fragment")]
    [InlineData("https://repo.example.test/encoded%2fpath")]
    [InlineData("https://repo.example.test./pkg")]
    [InlineData("https://repo.example.test/a/../pkg")]
    [InlineData("https://repo.example.test/a//pkg")]
    public void Unsafe_uri_authorities_are_rejected_before_transport(string value)
    {
        var failure = Assert.Throws<MaterialAcquisitionException>(() => MaterialAcquisition.CanonicalExactUri(new Uri(value)));
        Assert.False(failure.Transient); Assert.StartsWith("MATERIAL_URI_", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://REPO.example.test:443/pkg", "https://repo.example.test/pkg")]
    [InlineData("https://repo.example.test:8443/pkg", "https://repo.example.test:8443/pkg")]
    [InlineData("https://repo.example.test", "https://repo.example.test/")]
    public void Canonical_transport_identity_preserves_exact_nondefault_port(string value, string expected)
        => Assert.Equal(expected, MaterialAcquisition.CanonicalExactUri(new Uri(value)));

    [Theory]
    [InlineData("empty-uris")]
    [InlineData("duplicate-uris")]
    [InlineData("too-many-uris")]
    [InlineData("zero-length")]
    [InlineData("oversize")]
    [InlineData("bad-hash")]
    [InlineData("hash-length")]
    [InlineData("deadline")]
    [InlineData("redirect-limit")]
    public async Task Invalid_policy_never_sends_a_request(string shape)
    {
        using var root = new Root(); var approved = Material(); var limits = new InvocationLimits(Deadline: TimeSpan.FromSeconds(5)); var deadline = TimeSpan.FromSeconds(5);
        switch (shape)
        {
            case "empty-uris": approved = approved with { PermittedUris = [] }; break;
            case "duplicate-uris": approved = approved with { PermittedUris = [Uri, Uri] }; break;
            case "too-many-uris": approved = approved with { PermittedUris = [Uri, Uri] }; limits = limits with { MaxRedirects = 0 }; break;
            case "zero-length": approved = approved with { Length = 0 }; break;
            case "oversize": limits = limits with { MaxFileBytes = 1 }; break;
            case "bad-hash": approved = approved with { Sha256 = new string('g', 64) }; break;
            case "hash-length": approved = approved with { Sha256 = "aa" }; break;
            case "deadline": deadline = TimeSpan.Zero; break;
            default: limits = limits with { MaxRedirects = -1 }; break;
        }
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => { calls++; throw new InvalidOperationException(); }));
        var error = await Assert.ThrowsAsync<MaterialAcquisitionException>(() => MaterialAcquisition.DownloadCoreAsync(client, approved, Path.Combine(root.Path, "out"), deadline, default, limits));
        Assert.False(error.Transient); Assert.Equal(0, calls); Assert.False(File.Exists(Path.Combine(root.Path, "out")));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("absolute")]
    public async Task Only_explicitly_approved_redirects_commit_exact_bytes(string kind)
    {
        using var root = new Root(); var next = new Uri("https://repo.example.test/next"); var count = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            count++;
            return Task.FromResult(count == 1
                ? new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request, Headers = { Location = kind == "relative" ? new Uri("/next", UriKind.Relative) : next } }
                : Response(request, Bytes));
        }));
        var result = await MaterialAcquisition.DownloadCoreAsync(client, Material() with { PermittedUris = [Uri, next] }, Path.Combine(root.Path, "out"), TimeSpan.FromSeconds(5), default);
        Assert.Equal(2, count); Assert.Equal(1, result.Redirects); Assert.Equal(next.AbsoluteUri, result.FinalUri); Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(root.Path, "out")));
    }

    [Theory]
    [InlineData("bypass", "MATERIAL_REDIRECT_BYPASS")]
    [InlineData("missing-location", "MATERIAL_REDIRECT_LIMIT")]
    [InlineData("loop", "MATERIAL_REDIRECT_LIMIT")]
    [InlineData("encoding", "MATERIAL_LENGTH_INVALID")]
    [InlineData("truncated", "MATERIAL_DIGEST_MISMATCH")]
    [InlineData("growing", "MATERIAL_LENGTH_INVALID")]
    [InlineData("wrong-hash", "MATERIAL_DIGEST_MISMATCH")]
    [InlineData("racer", "MATERIAL_DESTINATION_CONFLICT")]
    public async Task Rejected_response_preserves_prior_destination_and_cleans_stage(string shape, string code)
    {
        using var root = new Root(); var destination = Path.Combine(root.Path, "out"); var calls = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            if (shape == "bypass") return Task.FromResult(Response(new HttpRequestMessage(HttpMethod.Get, "https://other.example.test/pkg"), Bytes));
            if (shape is "loop" or "missing-location") { var redirected = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request }; if (shape == "loop") redirected.Headers.Location = Uri; return Task.FromResult(redirected); }
            if (shape == "racer") File.WriteAllText(destination, "prior");
            var response = Response(request, shape == "truncated" ? Bytes[..^1] : shape == "growing" ? Bytes.Concat(new byte[] { 1 }).ToArray() : shape == "wrong-hash" ? Encoding.UTF8.GetBytes("rejected") : Bytes);
            response.Content.Headers.ContentLength = null;
            if (shape == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsAsync<MaterialAcquisitionException>(() => MaterialAcquisition.DownloadCoreAsync(client, Material(), destination, TimeSpan.FromSeconds(5), default, new InvocationLimits(Deadline: TimeSpan.FromSeconds(5), MaxRedirects: shape == "loop" ? 0 : 5)));
        Assert.Equal(code, error.Message); Assert.False(error.Transient); Assert.Equal(1, calls);
        if (shape == "racer") Assert.Equal("prior", File.ReadAllText(destination)); else Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(root.Path, ".acquire-*"));
    }

    [Fact]
    public async Task Network_and_forced_deadline_are_transient_but_caller_cancellation_is_not_retried()
    {
        using var root = new Root();
        using var failed = new HttpClient(new Handler((_, _) => throw new HttpRequestException("unsafe response detail")));
        var network = await Assert.ThrowsAsync<MaterialAcquisitionException>(() => MaterialAcquisition.DownloadCoreAsync(failed, Material(), Path.Combine(root.Path, "network"), TimeSpan.FromSeconds(5), default));
        Assert.True(network.Transient); Assert.Equal("MATERIAL_NETWORK_FAILED", network.Message);
        using var delayed = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }));
        var timeout = await Assert.ThrowsAsync<MaterialAcquisitionException>(() => MaterialAcquisition.DownloadCoreAsync(delayed, Material(), Path.Combine(root.Path, "timeout"), TimeSpan.FromMilliseconds(50), default));
        Assert.True(timeout.Transient); Assert.Equal("MATERIAL_TIMEOUT", timeout.Message);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => MaterialAcquisition.DownloadCoreAsync(delayed, Material(), Path.Combine(root.Path, "cancel"), TimeSpan.FromSeconds(5), cancel.Token));
        Assert.Empty(Directory.GetFiles(root.Path));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("zero-file")]
    [InlineData("redirect-over")]
    [InlineData("deadline-over")]
    public async Task InvalidAcquisitionEnvelopeStopsBeforeAnyTransport(string shape)
    {
        using var root = new Root();
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => { calls++; throw new InvalidOperationException(); }));
        var material = shape == "default" ? Material() with { PermittedUris = default } : Material();
        var limits = shape switch
        {
            "zero-file" => new InvocationLimits(MaxFileBytes: 0),
            "redirect-over" => new InvocationLimits(MaxRedirects: 33),
            _ => new InvocationLimits()
        };
        var deadline = shape == "deadline-over" ? TimeSpan.FromSeconds(301) : TimeSpan.FromSeconds(5);
        var failure = await Assert.ThrowsAsync<MaterialAcquisitionException>(() => MaterialAcquisition.DownloadCoreAsync(client, material,
            Path.Combine(root.Path, "destination"), deadline, default, limits));
        Assert.Equal("MATERIAL_POLICY_INVALID", failure.Message);
        Assert.Equal(0, calls);
        Assert.Empty(Directory.GetFileSystemEntries(root.Path));
    }

    [Fact]
    public async Task MaximumLengthCaptureCanAcceptShorterVerifiedBytesAndTightenDefaultDeadline()
    {
        using var root = new Root();
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(Response(request, Bytes))));
        var output = Path.Combine(root.Path, "destination");
        var observed = await MaterialAcquisition.DownloadCoreAsync(client, Material() with { Length = Bytes.Length + 10, ExactLength = false },
            output, TimeSpan.FromSeconds(5), default, new InvocationLimits(Deadline: TimeSpan.FromSeconds(6)));
        Assert.Equal(Bytes.Length, observed.Length);
        Assert.Equal(Bytes, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("large")]
    public async Task DeclaredExactLengthMismatchCannotPublish(string shape)
    {
        using var root = new Root();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            var response = Response(request, Bytes);
            response.Content.Headers.ContentLength = Bytes.Length + (shape == "short" ? -1 : 1);
            return Task.FromResult(response);
        }));
        var failure = await Assert.ThrowsAsync<MaterialAcquisitionException>(() => MaterialAcquisition.DownloadCoreAsync(client, Material(),
            Path.Combine(root.Path, "destination"), TimeSpan.FromSeconds(5), default));
        Assert.Equal("MATERIAL_LENGTH_INVALID", failure.Message);
        Assert.Empty(Directory.GetFileSystemEntries(root.Path));
    }

    [Fact]
    public async Task ExistingOutputAndLinkedAncestorCannotBeReplaced()
    {
        if (OperatingSystem.IsWindows()) return;
        using var root = new Root();
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => { calls++; throw new InvalidOperationException(); }));
        var existing = Path.Combine(root.Path, "existing"); File.WriteAllText(existing, "prior");
        var failure = await Assert.ThrowsAsync<MaterialAcquisitionException>(() => MaterialAcquisition.DownloadCoreAsync(client, Material(), existing,
            TimeSpan.FromSeconds(5), default));
        Assert.Equal("MATERIAL_DESTINATION_CONFLICT", failure.Message);
        var actual = Path.Combine(root.Path, "actual"); Directory.CreateDirectory(actual);
        var linked = Path.Combine(root.Path, "linked"); Directory.CreateSymbolicLink(linked, actual);
        failure = await Assert.ThrowsAsync<MaterialAcquisitionException>(() => MaterialAcquisition.DownloadCoreAsync(client, Material(), Path.Combine(linked, "new"),
            TimeSpan.FromSeconds(5), default));
        Assert.Equal("MATERIAL_OUTPUT_LINK_REJECTED", failure.Message);
        Assert.Equal("prior", File.ReadAllText(existing));
        Assert.Empty(Directory.GetFileSystemEntries(actual)); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ProductionDownloadTransportHonorsPreCancellationBeforeNetwork()
    {
        using var root = new Root();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var output = Path.Combine(root.Path, "absent");
        await Assert.ThrowsAsync<OperationCanceledException>(() => MaterialAcquisition.DownloadAsync(Material(), output,
            TimeSpan.FromSeconds(5), cancellation.Token));
        Assert.False(Path.Exists(output));
    }

    [Fact]
    public async Task MissingResponseAuthorityCannotMintAcquisition()
    {
        using var root = new Root();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = null, Content = new ByteArrayContent(Bytes) })));
        var failure = await Assert.ThrowsAsync<MaterialAcquisitionException>(() => MaterialAcquisition.DownloadCoreAsync(client, Material(),
            Path.Combine(root.Path, "absent"), TimeSpan.FromSeconds(5), default));
        Assert.Equal("MATERIAL_REDIRECT_BYPASS", failure.Message);
        Assert.Empty(Directory.GetFileSystemEntries(root.Path));
    }

    [Theory]
    [InlineData("relative", "MATERIAL_URI_INVALID")]
    [InlineData("backslash", "MATERIAL_URI_INVALID")]
    [InlineData("control", "MATERIAL_URI_INVALID")]
    public void TransportAuthorityRejectsUnescapedOrRelativeInput(string shape, string expected)
    {
        var uri = shape switch
        {
            "relative" => new Uri("relative/material", UriKind.Relative),
            "backslash" => new Uri("https://repo.example.test/a\\b"),
            _ => new Uri("https://repo.example.test/a\n")
        };
        var failure = Assert.Throws<MaterialAcquisitionException>(() => MaterialAcquisition.CanonicalExactUri(uri));
        Assert.Equal(expected, failure.Message);
    }

    private static readonly Uri Uri = new("https://repo.example.test/pkg");
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("approved");
    private static ApprovedMaterial Material() => new("fixture", Convert.ToHexStringLower(SHA256.HashData(Bytes)), Bytes.Length, [Uri], "fixture");
    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes) => new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class Root : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(KeycloakSecurity.PrivateTemporaryRoot(), "keycloak-acquisition-boundary-" + Guid.NewGuid().ToString("N"));
        internal Root()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
