using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ForgeTrust.AppSurface.Durable.CloudTasks;

namespace ForgeTrust.AppSurface.Durable.CloudTasks.Tests;

// protects: owner-approved protocol-declared unprocessed replay boundary.
// fails_when: GOAWAY bypasses the finite .NET retry bound or becomes application retry.
// why_new: Kestrel graceful shutdown cannot deterministically declare stream 1 unprocessed.
// seam: loopback TLS HTTP/2 peer; no_claim: Google acceptance, IAM, or real delivery.
public sealed class GoAwayHttp2Tests
{
    // protects=protocol-declared unprocessed streams replay only within the finite SDK invocation bound; fails_when=GOAWAY causes unbounded retry or application-level replay; no_claim=Google acceptance or real delivery.
    [Fact]
    public async Task Goaway_declaring_stream_unprocessed_is_bounded_below_one_sdk_invocation()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int completeRequests = 0;
        var peer = ServeAsync();
        using var handler = CloudTasksSdkTransport.CreateHandler();
        handler.SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, peerCertificate, _, _) => peerCertificate is not null && peerCertificate.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData)
        };
        using var transport = new CloudTasksSdkTransport(new Uri($"https://127.0.0.1:{port}"), handler);
        var target = new CloudTasksActivationTarget("proof-project", "us-central1", "proof-queue", new Uri("https://worker.example/activate"), "worker@proof-project.iam.gserviceaccount.com", "worker", TimeSpan.FromSeconds(30));
        var client = new CloudTasksActivationClient(new FixtureTokenSource(), new(TimeSpan.FromSeconds(5)), transport, TimeProvider.System);
        var receipt = await client.DispatchAsync(new CloudTasksActivationRequest(target, "goaway-task"));
        await peer;
        Assert.Equal(4, completeRequests);
        Assert.Equal(CloudTasksDispatchDisposition.Indeterminate, receipt.Disposition);
        Assert.Equal("ASCT305", receipt.DiagnosticCode);

        async Task ServeAsync()
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
                await using var tls = new SslStream(socket.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = [SslApplicationProtocol.Http2]
                }, deadline.Token);
                var preface = new byte[24];
                await tls.ReadExactlyAsync(preface, deadline.Token);
                Assert.Equal("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n", System.Text.Encoding.ASCII.GetString(preface));
                await Frame(tls, 4, 0, 0, []); // Server SETTINGS.
                while (true)
                {
                    var header = new byte[9];
                    await tls.ReadExactlyAsync(header, deadline.Token);
                    var length = header[0] << 16 | header[1] << 8 | header[2];
                    Assert.InRange(length, 0, 16 * 1024);
                    var payload = new byte[length];
                    await tls.ReadExactlyAsync(payload, deadline.Token);
                    var streamId = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5)) & int.MaxValue;
                    if (header[3] == 4 && (header[4] & 1) == 0) await Frame(tls, 4, 1, 0, []);
                    if (streamId != 0 && header[3] == 0 && (header[4] & 1) != 0)
                    {
                        Interlocked.Increment(ref completeRequests);
                        // Last-Stream-ID=0 and NO_ERROR: every received request stream is declared unprocessed.
                        await Frame(tls, 7, 0, 0, new byte[8]);
                        break;
                    }
                }
            }
        }

        async Task Frame(Stream stream, byte type, byte flags, int streamId, byte[] payload)
        {
            var bytes = new byte[payload.Length + 9];
            bytes[0] = (byte)(payload.Length >> 16); bytes[1] = (byte)(payload.Length >> 8); bytes[2] = (byte)payload.Length;
            bytes[3] = type; bytes[4] = flags;
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(5), streamId);
            payload.CopyTo(bytes, 9);
            await stream.WriteAsync(bytes, deadline.Token);
            await stream.FlushAsync(deadline.Token);
        }
    }

    private sealed class FixtureTokenSource : ICloudTasksAccessTokenSource
    {
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult("fixture-only-token");
    }
}
