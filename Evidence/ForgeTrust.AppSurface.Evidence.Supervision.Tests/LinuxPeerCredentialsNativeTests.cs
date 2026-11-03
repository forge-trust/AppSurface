using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Tests only the real kernel peer-credential utility; these do not construct an owner or authenticated connection.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPeerCredentialsNativeTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [RootPeerFact]
    public async Task ActualSetprivPeerMatchesItsKernelPidUidAndGid()
    {
        await using var fixture = await PeerFixture.CreateAsync();
        LinuxPeerCredentials.Require(fixture.Accepted.SafeHandle, fixture.Pid, fixture.Uid, fixture.Gid);
    }

    [RootPeerTheory]
    [InlineData("pid")]
    [InlineData("uid")]
    [InlineData("gid")]
    public async Task ActualSetprivPeerRejectsEachWrongExpectedCredential(string field)
    {
        await using var fixture = await PeerFixture.CreateAsync();
        var expectedPid = field == "pid" ? fixture.Pid + 1 : fixture.Pid;
        var expectedUid = field == "uid" ? fixture.Uid - 1 : fixture.Uid;
        var expectedGid = field == "gid" ? fixture.Gid - 1 : fixture.Gid;

        var error = Assert.Throws<EvidenceAdmissionException>(() =>
            LinuxPeerCredentials.Require(fixture.Accepted.SafeHandle, expectedPid, expectedUid, expectedGid));

        Assert.Equal("ASEVD402", error.Code);
        Assert.StartsWith("ASEVD402: The protected control peer could not be authenticated. ", error.Message);
        Assert.Null(error.InnerException);
    }

    private sealed class PeerFixture : IAsyncDisposable
    {
        private readonly string _path;
        private readonly Socket _listener;
        private readonly Process _peer;

        internal Socket Accepted { get; }
        internal int Pid => _peer.Id;
        internal uint Uid => 65534;
        internal uint Gid => 65534;

        private PeerFixture(string path, Socket listener, Socket accepted, Process peer)
        {
            _path = path;
            _listener = listener;
            Accepted = accepted;
            _peer = peer;
        }

        internal static async Task<PeerFixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), "peer-" + Guid.NewGuid().ToString("N") + ".sock");
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            Process? peer = null;
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(1);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);

                var start = new ProcessStartInfo("/usr/bin/setpriv") { UseShellExecute = false };
                start.ArgumentList.Add("--reuid=65534");
                start.ArgumentList.Add("--regid=65534");
                start.ArgumentList.Add("--clear-groups");
                start.ArgumentList.Add("--");
                start.ArgumentList.Add("/usr/bin/python3");
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add("import socket,sys,time; s=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM); s.connect(sys.argv[1]); time.sleep(30)");
                start.ArgumentList.Add(path);
                peer = Process.Start(start) ?? throw new InvalidOperationException("Could not start the credential fixture peer.");
                using var timeout = new CancellationTokenSource(Guard);
                var accepted = await listener.AcceptAsync(timeout.Token).AsTask().WaitAsync(Guard);
                return new PeerFixture(path, listener, accepted, peer);
            }
            catch
            {
                if (peer is not null)
                {
                    if (!peer.HasExited) peer.Kill(entireProcessTree: true);
                    await peer.WaitForExitAsync().WaitAsync(Guard);
                    peer.Dispose();
                }
                listener.Dispose();
                if (File.Exists(path)) File.Delete(path);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Accepted.Dispose();
            _listener.Dispose();
            if (!_peer.HasExited) _peer.Kill(entireProcessTree: true);
            await _peer.WaitForExitAsync().WaitAsync(Guard);
            _peer.Dispose();
            File.Delete(_path);
        }
    }

    private sealed class RootPeerFactAttribute : FactAttribute
    {
        public RootPeerFactAttribute() { if (!Available()) Skip = "Requires Linux x64 root and /usr/bin/setpriv plus /usr/bin/python3."; }
    }

    private sealed class RootPeerTheoryAttribute : TheoryAttribute
    {
        public RootPeerTheoryAttribute() { if (!Available()) Skip = "Requires Linux x64 root and /usr/bin/setpriv plus /usr/bin/python3."; }
    }

    private static bool Available() => OperatingSystem.IsLinux()
        && RuntimeInformation.ProcessArchitecture == Architecture.X64
        && GetUid() == 0 && GetEffectiveUid() == 0
        && File.Exists("/usr/bin/setpriv") && File.Exists("/usr/bin/python3");

    [DllImport("libc", EntryPoint = "getuid")] private static extern uint GetUid();
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUid();
}
