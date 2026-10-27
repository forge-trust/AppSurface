using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ForgeTrust.AppSurface.Durable.CloudTasks;
using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.Tasks.V2;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProviderTask = Google.Cloud.Tasks.V2.Task;
using Task = System.Threading.Tasks.Task;

namespace ForgeTrust.AppSurface.Durable.CloudTasks.Tests;

// protects: real official-SDK bounds, explicit credentials and acceptance uncertainty.
// fails_when: SDK/HTTP retries, decoding or metadata accounting changes across package upgrades.
// why_new: CallInvoker mocks cannot prove hidden HTTP/2 behavior; seam: internal real channel.
// no_claim: local fixtures do not establish live Google IAM/OIDC or queue delivery.
public sealed class SdkHttp2Tests
{
    // protects=official SDK request count, receipt mapping, and bounded response decoding across real HTTP/2 fixtures; fails_when=SDK retry/status/framing behavior escapes the expected bounds; no_claim=live IAM or queue delivery.
    [Theory]
    [InlineData("success", "ASCT001", 1, 1)]
    [InlineData("unavailable", "ASCT302", 1, 0)]
    [InlineData("already-exists", "ASCT002", 1, 0)]
    [InlineData("refused-once", "ASCT001", 2, 1)]
    [InlineData("refused-always", "ASCT302", 4, 0)]
    [InlineData("response-lost", "ASCT305", 1, 1)]
    [InlineData("message-below-limit", "ASCT001", 1, 1)]
    [InlineData("message-at-limit", "ASCT001", 1, 1)]
    [InlineData("message-over-limit", "ASCT303", 1, 1)]
    [InlineData("gzip-below-limit", "ASCT001", 1, 1)]
    [InlineData("gzip-at-limit", "ASCT001", 1, 1)]
    [InlineData("gzip-over-limit", "ASCT304", 1, 1)]
    [InlineData("headers-over-limit", "ASCT304", 1, 1)]
    [InlineData("trailers-over-limit", "ASCT304", 1, 1)]
    [InlineData("headers-trailers-combined-over-limit", "ASCT304", 1, 1)]
    [InlineData("non-grpc-no-body", "ASCT305", 1, 0)]
    [InlineData("non-grpc-proxy-404", "ASCT305", 1, 0)]
    [InlineData("missing-name", "ASCT306", 1, 1)]
    [InlineData("mismatched-name", "ASCT306", 1, 1)]
    [InlineData("unsupported-encoding", "ASCT305", 1, 1)]
    [InlineData("definitive-trailers-only-unsupported-encoding", "ASCT201", 1, 0)]
    [InlineData("headers-budget-below", "ASCT001", 1, 1)]
    [InlineData("headers-budget-at", "ASCT001", 1, 1)]
    [InlineData("headers-budget-above", "ASCT304", 1, 1)]
    [InlineData("trailers-budget-below", "ASCT001", 1, 1)]
    [InlineData("trailers-budget-at", "ASCT001", 1, 1)]
    [InlineData("trailers-budget-above", "ASCT304", 1, 1)]
    public async Task OfficialSdkRespectsBoundedTransport(string mode, string expectedCode, int expectedRequests, int expectedAccepted)
        => await VerifyAsync(mode, expectedCode, expectedRequests, expectedAccepted);

    // protects=captured SDK task reaches the actual authenticated host route and persisted work advances; fails_when=wire projection, empty body, OIDC fields, or host acceptance differ; no_claim=Google identity or production delivery.
    [Fact]
    [Trait("Category", "HostIntegration")]
    public async Task Captured_official_sdk_wake_simulates_delivery_to_real_host_and_persisted_Work()
    {
        // Simulated delivery consumes the actual SDK-captured request; the fixture uses existing host test auth.
        await using var worker = await ForgeTrust.AppSurface.Examples.DurableExternalActivation.Tests.PostgreSqlActivationFixture.StartAsync();
        var work = await worker.AcceptAsync("cloudtasks-sdk");
        Assert.Equal(ForgeTrust.AppSurface.Durable.DurableWorkState.Ready, (await worker.InspectAsync(work)).State);
        await VerifyAsync("success", "ASCT001", 1, 1, async captured =>
        {
            Assert.Equal(ForgeTrust.AppSurface.Durable.DurableWorkState.Ready, (await worker.InspectAsync(work)).State);
            Assert.True(captured.Task.HttpRequest.Body.IsEmpty);
            Assert.Equal("projects/proof-project/locations/us-central1/queues/proof-queue", captured.Parent);
            Assert.Equal("projects/proof-project/locations/us-central1/queues/proof-queue/tasks/proof-task", captured.Task.Name);
            Assert.Equal("https://worker.example/private/durable/activate", captured.Task.HttpRequest.Url);
            Assert.Equal("worker@proof-project.iam.gserviceaccount.com", captured.Task.HttpRequest.OidcToken.ServiceAccountEmail);
            Assert.Equal("https://worker.example", captured.Task.HttpRequest.OidcToken.Audience);
            using var body = new ByteArrayContent(captured.Task.HttpRequest.Body.ToByteArray());
            using var response = await worker.Client.PostAsync(new Uri(captured.Task.HttpRequest.Url).AbsolutePath, body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });
        Assert.Equal(ForgeTrust.AppSurface.Durable.DurableWorkState.Succeeded, (await worker.InspectAsync(work)).State);
    }

    private static async Task VerifyAsync(string mode, string expectedCode, int expectedRequests, int expectedAccepted, Func<CreateTaskRequest, Task>? onCreated = null)
    {
        const string expectedName = "projects/proof-project/locations/us-central1/queues/proof-queue/tasks/proof-task";
        const string token = "fixture-only-token";
        using var key = RSA.Create(2048);
        var certRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        certRequest.CertificateExtensions.Add(san.Build());
        using var certificate = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseHttps(certificate);
            });
        });
        await using var server = builder.Build();
        int wireRequests = 0, accepted = 0, validRequests = 0, authHeaders = 0;
        CreateTaskRequest? capturedRequest = null;
        server.MapPost("/google.cloud.tasks.v2.CloudTasks/CreateTask", async context =>
        {
            var attempt = Interlocked.Increment(ref wireRequests);
            var input = new MemoryStream();
            await context.Request.Body.CopyToAsync(input, context.RequestAborted);
            var bytes = input.ToArray();
            if (bytes.Length < 5 || bytes[0] != 0 || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(1, 4)) != bytes.Length - 5)
                throw new InvalidOperationException("Invalid gRPC frame.");
            var request = CreateTaskRequest.Parser.ParseFrom(bytes, 5, bytes.Length - 5);
            capturedRequest = request;
            Assert.Null(request.Task.ScheduleTime);
            Assert.Empty(request.Task.HttpRequest.Headers);
            Assert.Null(request.Task.HttpRequest.OauthToken);
            Assert.Equal(ProviderTask.MessageTypeOneofCase.HttpRequest, request.Task.MessageTypeCase);
            if (request.Task.Name == expectedName && request.Task.HttpRequest.Body.IsEmpty && request.Task.HttpRequest.HttpMethod == Google.Cloud.Tasks.V2.HttpMethod.Post && request.Task.HttpRequest.Url == "https://worker.example/private/durable/activate" && request.Task.HttpRequest.OidcToken.ServiceAccountEmail == "worker@proof-project.iam.gserviceaccount.com" && request.Task.HttpRequest.OidcToken.Audience == "https://worker.example" && request.Task.DispatchDeadline.ToTimeSpan() == TimeSpan.FromSeconds(30) && request.Parent == "projects/proof-project/locations/us-central1/queues/proof-queue" && request.ResponseView == ProviderTask.Types.View.Basic)
                Interlocked.Increment(ref validRequests);
            if (context.Request.Headers.Authorization.Count == 1 && context.Request.Headers.Authorization[0] == "Bearer " + token)
                Interlocked.Increment(ref authHeaders);
            if (mode == "refused-always" || mode == "refused-once" && attempt == 1)
            {
                context.Features.Get<IHttpResetFeature>()!.Reset(0x7); // HTTP/2 REFUSED_STREAM: no application acceptance.
                return;
            }
            if (mode == "response-lost")
            {
                Interlocked.Increment(ref accepted);
                context.Abort();
                return;
            }
            if (mode is "non-grpc-no-body" or "non-grpc-proxy-404")
            {
                if (mode == "non-grpc-proxy-404") context.Response.StatusCode = 404;
                context.Response.ContentType = "text/html";
                context.Response.ContentLength = 1024 * 1024 * 1024;
                await context.Response.StartAsync();
                await context.Response.Body.WriteAsync(new byte[] { 60 });
                await context.Response.Body.FlushAsync();
                try { await Task.Delay(TimeSpan.FromSeconds(15), context.RequestAborted); }
                catch (OperationCanceledException) { }
                return;
            }
            context.Response.ContentType = "application/grpc";
            context.Response.Headers.Date = "Tue, 27 Oct 2026 00:00:00 GMT";
            if (mode == "headers-over-limit") context.Response.Headers["fixture-padding"] = new string('x', 16 * 1024);
            if (mode == "headers-trailers-combined-over-limit") context.Response.Headers["fixture-padding"] = new string('x', 9 * 1024);
            if (mode == "definitive-trailers-only-unsupported-encoding")
            {
                context.Response.Headers["grpc-status"] = "3";
                context.Response.Headers["grpc-encoding"] = "br";
                return;
            }
            if (mode is "unavailable" or "already-exists")
            {
                context.Response.Headers["grpc-status"] = mode == "already-exists" ? "6" : "14";
                return;
            }
            Interlocked.Increment(ref accepted);
            context.Response.DeclareTrailer("grpc-status");
            var payload = new ProviderTask { Name = mode == "missing-name" ? "" : mode == "mismatched-name" ? request.Task.Name + "-other" : request.Task.Name }.ToByteArray();
            string? boundedTrailer = null;
            if (mode.Contains("-budget-", StringComparison.Ordinal))
            {
                const string paddingHeader = "fixture-budget-padding";
                var target = 16 * 1024 + (mode.EndsWith("below", StringComparison.Ordinal) ? -1 : mode.EndsWith("above", StringComparison.Ordinal) ? 1 : 0);
                // Pinned .NET HTTP/2 accounting: :status name+3 digits, every decoded name/value, then grpc-status trailer.
                var existing = 10 + context.Response.Headers.Sum(header => header.Key.Length + header.Value.ToString().Length) + "grpc-status".Length + 1 + paddingHeader.Length;
                var padding = new string('x', target - existing);
                if (mode.StartsWith("headers-", StringComparison.Ordinal)) context.Response.Headers[paddingHeader] = padding;
                else boundedTrailer = padding;
            }
            if ((mode.StartsWith("message", StringComparison.Ordinal) || mode.StartsWith("gzip", StringComparison.Ordinal)) && mode.Contains("limit", StringComparison.Ordinal))
            {
                var desired = 64 * 1024 + (mode.Contains("over-limit", StringComparison.Ordinal) ? 1 : mode.Contains("below-limit", StringComparison.Ordinal) ? -1 : 0);
                var padding = desired - payload.Length - 10;
                while (true)
                {
                    payload = new ProviderTask { Name = request.Task.Name, HttpRequest = new Google.Cloud.Tasks.V2.HttpRequest { Body = ByteString.CopyFrom(new byte[padding]) } }.ToByteArray();
                    if (payload.Length == desired) break;
                    padding += desired - payload.Length;
                }
            }
            var compressed = mode.StartsWith("gzip", StringComparison.Ordinal) || mode == "unsupported-encoding";
            if (compressed)
            {
                using var output = new MemoryStream();
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(payload);
                payload = output.ToArray();
                context.Response.Headers["grpc-encoding"] = mode == "unsupported-encoding" ? "br" : "gzip";
            }
            var framed = new byte[payload.Length + 5];
            framed[0] = compressed ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(1, 4), payload.Length);
            payload.CopyTo(framed, 5);
            await context.Response.Body.WriteAsync(framed);
            if (mode == "trailers-over-limit") context.Response.AppendTrailer("fixture-padding", new string('x', 16 * 1024));
            if (mode == "headers-trailers-combined-over-limit") context.Response.AppendTrailer("fixture-trailer-padding", new string('y', 9 * 1024));
            if (boundedTrailer is not null) context.Response.AppendTrailer("fixture-budget-padding", boundedTrailer);
            context.Response.AppendTrailer("grpc-status", "0");
        });
        await server.StartAsync();
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var transport = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 16,
            MaxResponseDrainSize = 0,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, peer, _, _) => peer is not null && peer.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData)
            }
        };
        using var observer = new InvocationObserver(transport);
        using var sdk = new CloudTasksSdkTransport(new Uri(address), observer);
        int tokenCalls = 0, sdkCalls = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var client = new CloudTasksActivationClient(new FixtureTokenSource(() => { tokenCalls++; return token; }), new(TimeSpan.FromSeconds(5)), sdk, TimeProvider.System);
        var target = new CloudTasksActivationTarget("proof-project", "us-central1", "proof-queue", new Uri("https://worker.example/private/durable/activate"), "worker@proof-project.iam.gserviceaccount.com", "https://worker.example", TimeSpan.FromSeconds(30));
        sdkCalls++;
        var receipt = await client.DispatchAsync(new CloudTasksActivationRequest(target, "proof-task"));
        Assert.Equal(expectedName, receipt.TaskName);
        Assert.Equal(expectedCode, receipt.DiagnosticCode);
        Assert.Equal(expectedRequests, wireRequests);
        Assert.Equal(expectedAccepted, accepted);
        Assert.Equal(wireRequests, validRequests);
        Assert.Equal(wireRequests, authHeaders);
        Assert.Equal(1, tokenCalls);
        Assert.Equal(1, sdkCalls);
        Assert.Equal(1, observer.Invocations);
        if (onCreated is not null) await onCreated(capturedRequest!);
        await server.StopAsync();
    }
    sealed class InvocationObserver(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Invocations { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Invocations++;
            return base.SendAsync(request, cancellationToken);
        }
    }


    private sealed class FixtureTokenSource(Func<string> token) : ICloudTasksAccessTokenSource
    {
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(token());
    }
}
