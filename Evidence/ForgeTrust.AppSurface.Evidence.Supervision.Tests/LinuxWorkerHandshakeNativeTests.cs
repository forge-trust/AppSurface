using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Exercises rejected worker handshakes over a real root UNIX peer without issuing a supervisor or admission.</summary>
/// <remarks>The peer deliberately sends invalid or incomplete frames. No test constructs a READY descriptor or authentic owner.</remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxWorkerHandshakeNativeTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [RootHandshakeTheory]
    [InlineData("eof")]
    [InlineData("invalid-json")]
    [InlineData("empty-object")]
    [InlineData("preparation-zero")]
    [InlineData("preparation-extra")]
    [InlineData("admission-wrong-phase")]
    [InlineData("admission-eof")]
    [InlineData("descriptor-after-admission")]
    public async Task ActualRootPeerCannotTurnIncompleteOrMalformedFramesIntoWorkerAdmission(string fault)
    {
        var path = "/tmp/hs-" + Guid.NewGuid().ToString("N") + ".sock";
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var deadline = new CancellationTokenSource(Guard);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var peer = ServeAsync(listener, fault, deadline.Token);
        try
        {
            var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
                EvidenceLinuxWorkerSupervisor.ConnectAsync(path, deadline.Token));
            Assert.Equal("ASEVD402", error.Code);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain(path, error.Message, StringComparison.Ordinal);
            await peer.WaitAsync(Guard);
        }
        finally
        {
            deadline.Cancel();
            listener.Dispose();
            try { await peer.WaitAsync(Guard); }
            finally { File.Delete(path); }
        }
    }

    private static async Task ServeAsync(Socket listener, string fault, CancellationToken token)
    {
        using var accepted = await listener.AcceptAsync(token);
        using var stream = new NetworkStream(accepted, ownsSocket: false);
        var request = new List<byte>();
        var one = new byte[1];
        while (request.Count < 64)
        {
            Assert.Equal(1, await stream.ReadAsync(one, token));
            if (one[0] == (byte)'\n') break;
            request.Add(one[0]);
        }
        Assert.Equal("{\"op\":\"ready\"}", Encoding.UTF8.GetString(request.ToArray()));
        var preparation = "{\"ok\":true,\"phase\":\"pre-admission\",\"remaining_job_ms\":30000}\n";
        var frames = fault switch
        {
            "eof" => string.Empty,
            "invalid-json" => "not-json\n",
            "empty-object" => "{}\n",
            "preparation-zero" => "{\"ok\":true,\"phase\":\"pre-admission\",\"remaining_job_ms\":0}\n",
            "preparation-extra" => "{\"ok\":true,\"phase\":\"pre-admission\",\"remaining_job_ms\":30000,\"extra\":true}\n",
            "admission-wrong-phase" => preparation + "{\"ok\":true,\"phase\":\"unexpected\"}\n",
            "admission-eof" => preparation,
            "descriptor-after-admission" => preparation + "{\"ok\":true,\"phase\":\"admission-start\"}\n{}\n",
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
        await stream.WriteAsync(Encoding.UTF8.GetBytes(frames), token);
        accepted.Shutdown(SocketShutdown.Send);
    }

    private sealed class RootHandshakeTheoryAttribute : TheoryAttribute
    {
        public RootHandshakeTheoryAttribute()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64
                || GetEffectiveUid() != 0)
                Skip = "Requires an actual Linux x64 root peer; skipped cases grant no native coverage or authority.";
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();
}
