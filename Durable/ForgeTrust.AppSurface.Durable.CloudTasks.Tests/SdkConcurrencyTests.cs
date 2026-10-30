using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ForgeTrust.AppSurface.Durable.CloudTasks;
using Google.Cloud.Tasks.V2;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProviderTask = Google.Cloud.Tasks.V2.Task;
using Task = System.Threading.Tasks.Task;

namespace ForgeTrust.AppSurface.Durable.CloudTasks.Tests;

// protects: invocation-local credentials on overlapping real SDK HTTP/2 streams.
// fails_when: shared headers/tokens bleed between requests or token acquisition repeats.
// why_new: fake transport cannot observe the authorization metadata on the wire.
// seam: internal real SDK channel and a loopback TLS peer; no_claim: live Google credential validity.
public sealed class SdkConcurrencyTests
{
    // protects=overlapping SDK requests carry their own invocation token on the HTTP/2 wire; fails_when=authorization metadata bleeds or token acquisition repeats; no_claim=validity of live Google credentials.
    [Fact]
    public async Task Concurrent_sdk_requests_each_carry_exactly_their_own_explicit_token()
    {
        using var key = RSA.Create(2048);
        var certRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.UseHttps(certificate);
        }));
        await using var server = builder.Build();
        var observed = new ConcurrentDictionary<string, string>();
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MapPost("/google.cloud.tasks.v2.CloudTasks/CreateTask", async context =>
        {
            using var content = new MemoryStream();
            await context.Request.Body.CopyToAsync(content);
            var bytes = content.ToArray();
            var task = CreateTaskRequest.Parser.ParseFrom(bytes, 5, bytes.Length - 5).Task;
            Assert.Single(context.Request.Headers.Authorization);
            Assert.True(observed.TryAdd(task.Name, context.Request.Headers.Authorization.ToString()));
            if (observed.Count == 2) bothArrived.TrySetResult();
            await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            context.Response.ContentType = "application/grpc";
            context.Response.DeclareTrailer("grpc-status");
            var response = new ProviderTask { Name = task.Name }.ToByteArray();
            var frame = new byte[response.Length + 5];
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), response.Length);
            response.CopyTo(frame, 5);
            await context.Response.Body.WriteAsync(frame);
            context.Response.AppendTrailer("grpc-status", "0");
        });
        await server.StartAsync();
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var handler = CloudTasksSdkTransport.CreateHandler();
        handler.SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, peer, _, _) => peer is not null && peer.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData)
        };
        using var transport = new CloudTasksSdkTransport(new Uri(address), handler);
        var tokens = new SequenceSource();
        var client = new CloudTasksActivationClient(tokens, new(TimeSpan.FromSeconds(5)), transport, TimeProvider.System);
        var target = new CloudTasksActivationTarget("project", "us-central1", "queue", new Uri("https://worker.example/activate"), "worker@example.com", "worker", TimeSpan.FromSeconds(15));
        var first = new CloudTasksActivationRequest(target, "first");
        var second = new CloudTasksActivationRequest(target, "second");
        var receipts = await Task.WhenAll(client.DispatchAsync(first), client.DispatchAsync(second));
        Assert.All(receipts, receipt => Assert.Equal("ASCT001", receipt.DiagnosticCode));
        Assert.Equal(2, tokens.Calls);
        Assert.Equal(2, observed.Count);
        Assert.Equal("Bearer token-1", observed[first.TaskName]);
        Assert.Equal("Bearer token-2", observed[second.TaskName]);
        await server.StopAsync();
    }

    private sealed class SequenceSource : ICloudTasksAccessTokenSource
    {
        private int _calls;
        internal int Calls => _calls;
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult("token-" + Interlocked.Increment(ref _calls));
    }
}
