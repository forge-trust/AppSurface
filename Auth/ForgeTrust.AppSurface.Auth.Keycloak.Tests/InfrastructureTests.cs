using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

/// <summary>Protects capture, acquisition, and bounded-process trust boundaries; fails on unverified bytes, unsafe publication, or uncontrolled child-process effects; claims exact observable outcomes through internal seams.</summary>
public sealed class InfrastructureTests
{
    [Fact]
    public async Task Snapshot_is_digest_bound_disposable_and_concurrent()
    {
        using var root = new TempDirectory();
        var source = Path.Combine(root.Path, "source"); File.WriteAllText(source, "captured");
        var digest = Digest(source); var destination = Path.Combine(root.Path, "snapshot");
        using var snapshot = await SnapshotStore.CaptureAsync([new CaptureFile("input", source, digest, 8, 420)], new string('a', 64), new string('b', 64), destination, default);
        Assert.Equal(digest, snapshot.Files[0].Sha256); using var lease = snapshot.Enter(); Assert.Equal(1, snapshot.ActiveConsumers);
        var copy = Path.Combine(root.Path, "copy"); var task = SnapshotStore.CopyForAcceptedUseAsync(snapshot, lease, copy, default); snapshot.Dispose();
        Assert.Equal(copy, await task); Assert.True(File.Exists(Path.Combine(copy, "input"))); lease.Dispose();
        Assert.Equal(0, snapshot.ActiveConsumers); Assert.Throws<ObjectDisposedException>(() => snapshot.Enter());
    }

    [Fact]
    public async Task Snapshot_rejects_symlinked_source_and_wrong_digest()
    {
        using var root = new TempDirectory(); var target = Path.Combine(root.Path, "target"); File.WriteAllText(target, "x"); var link = Path.Combine(root.Path, "link"); Directory.CreateSymbolicLink(link, target);
        Assert.Throws<InvalidDataException>(() => SnapshotStore.CaptureAsync([new CaptureFile("x", link, Digest(target), 1, 420)], new string('a', 64), new string('b', 64), Path.Combine(root.Path, "out"), default).GetAwaiter().GetResult());
        Assert.Throws<InvalidDataException>(() => SnapshotStore.CaptureAsync([new CaptureFile("x", target, new string('0', 64), 1, 420)], new string('a', 64), new string('b', 64), Path.Combine(root.Path, "out2"), default).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Bounded_process_redacts_output_and_has_deterministic_cancel_seam()
    {
        var executable = File.Exists("/usr/local/share/dotnet/dotnet") ? "/usr/local/share/dotnet/dotnet" : "dotnet";
        var observation = await BoundedProcess.RunAsync(executable, ["--version"], null, TimeSpan.FromSeconds(5), 4096, ["10"], default);
        Assert.NotEmpty(observation.Output);
        var flood = await Assert.ThrowsAsync<BoundedProcessException>(() => BoundedProcess.RunAsync(executable, ["--version"], null, TimeSpan.FromSeconds(5), 1, [], default));
        Assert.Equal(ProcessStopReason.OutputLimit, flood.Reason);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => BoundedProcess.RunAsync(executable, ["--version"], null, TimeSpan.FromSeconds(5), 4096, [], cancelled.Token));
    }

    [Fact]
    public async Task Material_acquisition_counts_transport_calls_and_commits_only_verified_bytes()
    {
        using var root = new TempDirectory(); var uri = new Uri("https://repo.example.test/pkg"); var bytes = Encoding.UTF8.GetBytes("approved");
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri), Content = new ByteArrayContent(bytes) });
        using var client = new HttpClient(handler); var approved = new ApprovedMaterial("pkg", Digest(bytes), bytes.Length, [uri], "pkg");
        var result = await MaterialAcquisition.DownloadCoreAsync(client, approved, Path.Combine(root.Path, "pkg"), TimeSpan.FromSeconds(5), default);
        Assert.Equal(1, handler.Calls); Assert.Equal(uri.ToString(), result.FinalUri); Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root.Path, "pkg")));

        using var wrong = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri), Content = new ByteArrayContent(Encoding.UTF8.GetBytes("wrong")) });
        using var wrongClient = new HttpClient(wrong); await Assert.ThrowsAsync<MaterialAcquisitionException>(() => MaterialAcquisition.DownloadCoreAsync(wrongClient, approved, Path.Combine(root.Path, "wrong"), TimeSpan.FromSeconds(5), default));
        Assert.False(File.Exists(Path.Combine(root.Path, "wrong"))); Assert.Equal(1, wrong.Calls);
    }

    [Fact]
    public async Task Material_acquisition_cancellation_stops_before_transport()
    {
        using var root = new TempDirectory(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var handler = new ScriptedHandler(_ => throw new InvalidOperationException("transport should not be called")); using var client = new HttpClient(handler);
        var bytes = Encoding.UTF8.GetBytes("approved"); var uri = new Uri("https://repo.example.test/pkg"); var approved = new ApprovedMaterial("pkg", Digest(bytes), bytes.Length, [uri], "pkg");
        await Assert.ThrowsAsync<OperationCanceledException>(() => MaterialAcquisition.DownloadCoreAsync(client, approved, Path.Combine(root.Path, "pkg"), TimeSpan.FromSeconds(5), cancelled.Token)); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task Material_acquisition_classifies_http_failures_without_committing(HttpStatusCode status, bool transient)
    {
        using var root = new TempDirectory(); var uri = new Uri("https://repo.example.test/pkg"); var bytes = Encoding.UTF8.GetBytes("approved");
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage(status) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri) }); using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<MaterialAcquisitionException>(() => MaterialAcquisition.DownloadCoreAsync(client, Approved(uri, bytes), Path.Combine(root.Path, "pkg"), TimeSpan.FromSeconds(5), default));
        Assert.Equal(transient, error.Transient); Assert.Equal("MATERIAL_HTTP_FAILED", error.Message); Assert.False(File.Exists(Path.Combine(root.Path, "pkg")));
    }

    [Fact]
    public async Task Material_acquisition_rejects_redirect_origin_and_header_length_shapes()
    {
        using var root = new TempDirectory(); var uri = new Uri("https://repo.example.test/pkg"); var evil = new Uri("https://evil.example.test/pkg"); var bytes = Encoding.UTF8.GetBytes("approved");
        using var redirect = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri), Headers = { Location = evil } }); using var redirectClient = new HttpClient(redirect);
        var originError = await Assert.ThrowsAsync<MaterialAcquisitionException>(() => MaterialAcquisition.DownloadCoreAsync(redirectClient, Approved(uri, bytes), Path.Combine(root.Path, "redirect"), TimeSpan.FromSeconds(5), default)); Assert.Equal("MATERIAL_ORIGIN_REJECTED", originError.Message); Assert.Equal(1, redirect.Calls);
        using var tooLong = new ScriptedHandler(_ => Response(uri, bytes, bytes.Length + 1)); using var tooLongClient = new HttpClient(tooLong);
        var lengthError = await Assert.ThrowsAsync<MaterialAcquisitionException>(() => MaterialAcquisition.DownloadCoreAsync(tooLongClient, Approved(uri, bytes), Path.Combine(root.Path, "too-long"), TimeSpan.FromSeconds(5), default)); Assert.Equal("MATERIAL_LENGTH_INVALID", lengthError.Message);
        using var missing = new ScriptedHandler(_ => ResponseWithoutLength(uri, bytes)); using var missingClient = new HttpClient(missing);
        var missingResult = await MaterialAcquisition.DownloadCoreAsync(missingClient, Approved(uri, bytes) with { ExactLength = false }, Path.Combine(root.Path, "missing"), TimeSpan.FromSeconds(5), default); Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root.Path, "missing"))); Assert.Equal(uri.ToString(), missingResult.FinalUri);
    }

    [Fact]
    public async Task Material_acquisition_rejects_destination_and_cleans_failed_stage()
    {
        using var root = new TempDirectory(); var uri = new Uri("https://repo.example.test/pkg"); var bytes = Encoding.UTF8.GetBytes("approved"); var destination = Path.Combine(root.Path, "pkg"); File.WriteAllText(destination, "existing");
        using var handler = new ScriptedHandler(_ => Response(uri, bytes, bytes.Length)); using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<MaterialAcquisitionException>(() => MaterialAcquisition.DownloadCoreAsync(client, Approved(uri, bytes), destination, TimeSpan.FromSeconds(5), default)); Assert.Equal("MATERIAL_DESTINATION_CONFLICT", error.Message); Assert.Equal("existing", File.ReadAllText(destination));
        var failed = Path.Combine(root.Path, "failed"); using var wrong = new ScriptedHandler(_ => Response(uri, Encoding.UTF8.GetBytes("bad"), bytes.Length)); using var wrongClient = new HttpClient(wrong); await Assert.ThrowsAsync<MaterialAcquisitionException>(() => MaterialAcquisition.DownloadCoreAsync(wrongClient, Approved(uri, bytes), failed, TimeSpan.FromSeconds(5), default)); Assert.False(File.Exists(failed)); Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path, ".acquire-*"));
    }

    [Fact]
    public async Task Snapshot_rejects_existing_destination_tamper_and_undeclared_entries()
    {
        using var root = new TempDirectory(); var source = Path.Combine(root.Path, "source"); File.WriteAllText(source, "captured"); var destination = Path.Combine(root.Path, "snapshot"); using var snapshot = await SnapshotStore.CaptureAsync([new CaptureFile("input", source, Digest(source), 8, 420)], new string('a', 64), new string('b', 64), destination, default);
        var existing = Path.Combine(root.Path, "existing"); Directory.CreateDirectory(existing); await Assert.ThrowsAsync<IOException>(() => SnapshotStore.CaptureAsync([new CaptureFile("input", source, Digest(source), 8, 420)], new string('a', 64), new string('b', 64), existing, default));
        File.WriteAllText(Path.Combine(destination, "extra"), "tamper"); await Assert.ThrowsAsync<InvalidDataException>(() => SnapshotStore.CopyForUseAsync(snapshot, Path.Combine(root.Path, "copy-extra"), default)); File.Delete(Path.Combine(destination, "extra")); Directory.CreateDirectory(Path.Combine(destination, "extra-dir")); await Assert.ThrowsAsync<InvalidDataException>(() => SnapshotStore.CopyForUseAsync(snapshot, Path.Combine(root.Path, "copy-dir"), default));
    }

    [Fact]
    public async Task Snapshot_rejects_invalid_identity_limits_mode_and_manifest_tamper()
    {
        using var root = new TempDirectory(); var source = Path.Combine(root.Path, "source"); File.WriteAllText(source, "x"); var digest = Digest(source);
        await Assert.ThrowsAsync<InvalidDataException>(() => SnapshotStore.CaptureAsync([new CaptureFile("snapshot.json", source, digest, 1, 420)], new string('a', 64), new string('b', 64), Path.Combine(root.Path, "id"), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SnapshotStore.CaptureAsync([new CaptureFile("x", source, digest, 1, 420)], new string('a', 64), new string('b', 64), Path.Combine(root.Path, "limit"), default, new InvocationLimits(MaxFileBytes: 0)));
        await Assert.ThrowsAsync<InvalidDataException>(() => SnapshotStore.CaptureAsync([new CaptureFile("x", source, digest, 1, 384)], new string('a', 64), new string('b', 64), Path.Combine(root.Path, "mode"), default));
        var destination = Path.Combine(root.Path, "snapshot"); using var snapshot = await SnapshotStore.CaptureAsync([new CaptureFile("x", source, digest, 1, 420)], new string('a', 64), new string('b', 64), destination, default); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(destination, "snapshot.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite); File.AppendAllText(Path.Combine(destination, "snapshot.json"), " "); await Assert.ThrowsAsync<InvalidDataException>(() => SnapshotStore.CopyForUseAsync(snapshot, Path.Combine(root.Path, "tampered"), default));
    }

    [Fact]
    public async Task Snapshot_rejects_foreign_lease_and_noncanonical_digest_aliases()
    {
        using var root = new TempDirectory(); var source = Path.Combine(root.Path, "source"); File.WriteAllText(source, "x"); var digest = Digest(source); using var first = await SnapshotStore.CaptureAsync([new CaptureFile("x", source, digest, 1, 420)], new string('a', 64), new string('b', 64), Path.Combine(root.Path, "one"), default); using var second = await SnapshotStore.CaptureAsync([new CaptureFile("x", source, digest, 1, 420)], new string('a', 64), new string('b', 64), Path.Combine(root.Path, "two"), default); using var foreign = second.Enter(); await Assert.ThrowsAsync<ObjectDisposedException>(() => SnapshotStore.CopyForAcceptedUseAsync(first, foreign, Path.Combine(root.Path, "foreign"), default)); await Assert.ThrowsAsync<InvalidDataException>(() => SnapshotStore.CaptureAsync([new CaptureFile("x", source, digest.ToUpperInvariant(), 1, 420)], new string('a', 64), new string('b', 64), Path.Combine(root.Path, "upper"), default));
    }

    [Fact]
    public async Task Bounded_process_rejects_start_argument_and_deadline_limits()
    {
        var start = await Assert.ThrowsAsync<BoundedProcessException>(() => BoundedProcess.RunAsync(Path.Combine(Path.GetTempPath(), "missing-executable-" + Guid.NewGuid()), [], null, TimeSpan.FromSeconds(5), 4096, [], default)); Assert.Equal(ProcessStopReason.Start, start.Reason);
        var arguments = new InvocationLimits(Deadline: TimeSpan.FromSeconds(5), MaxProcessOutputBytes: 4096, MaxArgumentCount: 1); await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BoundedProcess.RunAsync("dotnet", ["--version", "extra"], null, TimeSpan.FromSeconds(5), 4096, [], default, arguments));
        var deadline = new InvocationLimits(Deadline: TimeSpan.FromSeconds(1), CleanupDeadline: TimeSpan.FromSeconds(5), MaxProcessOutputBytes: 4096); var timeout = await Assert.ThrowsAsync<BoundedProcessException>(() => BoundedProcess.RunAsync("/bin/sh", ["-c", "while true; do :; done"], null, TimeSpan.FromSeconds(5), 4096, [], default, deadline)); Assert.Equal(ProcessStopReason.Deadline, timeout.Reason);
    }

    private static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static ApprovedMaterial Approved(Uri uri, byte[] bytes) => new("pkg", Digest(bytes), bytes.Length, [uri], "pkg");
    private static HttpResponseMessage Response(Uri uri, byte[] bytes, long declared) { var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri), Content = new ByteArrayContent(bytes) }; response.Content.Headers.ContentLength = declared; return response; }
    private static HttpResponseMessage ResponseWithoutLength(Uri uri, byte[] bytes) => new(HttpStatusCode.OK) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri), Content = new UnknownLengthContent(bytes) };
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent { protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)); protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask(); protected override bool TryComputeLength(out long length) { length = 0; return false; } }
    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> script) : HttpMessageHandler { internal int Calls { get; private set; } protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return Task.FromResult(script(request)); } }
    private sealed class TempDirectory : IDisposable { internal string Path { get; } = System.IO.Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : System.IO.Path.GetTempPath(), "keycloak-tests-" + Guid.NewGuid().ToString("N")); internal TempDirectory() => Directory.CreateDirectory(Path); public void Dispose() { try { Directory.Delete(Path, true); } catch { } } }
}
