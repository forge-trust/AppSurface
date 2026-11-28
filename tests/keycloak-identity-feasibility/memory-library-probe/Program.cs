using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Keycloak;

return await LibraryProbe.RunAsync(args);

static class LibraryProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] != "anchor") return await ReaderAsync(args[0]);
            if (args.Length > 1) throw new InvalidDataException();
            var envelope = await ReadEnvelopeAsync();
            using var capture = KeycloakClientCredentialMemoryCapture.Create(Guid.NewGuid().ToString("N"),
                new Uri(envelope.Authority), envelope.Client, envelope.Secret);
            if (args is ["anchor"])
            {
                Console.WriteLine(JsonSerializer.Serialize(new { stage = "anchor-ready", descriptor = capture.Reference }, JsonOptions));
                await Task.Delay(TimeSpan.FromSeconds(60));
                return 124;
            }

            var reference = capture.Reference;
            var memory = reference.MemoryIdentity!;
            var checks = new Dictionary<string, bool>();
            var fd = memory.FileDescriptor;
            var seals = Native.fcntl(fd, 1034, 0);
            checks["required-seals"] = seals >= 0 && (seals & 15) == 15;
            checks["write-rejected"] = Native.write(fd, [0x41], 1) == -1;
            checks["truncate-rejected"] = Native.ftruncate(fd, 0) == -1;
            checks["seal-change-rejected"] = Native.fcntl(fd, 1033, 0) == -1;
            checks["wrong-run"] = Rejects(reference, memory, Guid.NewGuid().ToString("N"), reference.Authority, reference.ClientId, "binding or expiry");
            checks["wrong-authority"] = Rejects(reference, memory, reference.RunId, new Uri("https://identity.example/realms/other"), reference.ClientId, "binding or expiry");
            checks["wrong-client"] = Rejects(reference, memory, reference.RunId, reference.Authority, "other-client", "binding or expiry");
            checks["wrong-parent"] = Rejects(reference, memory with { ProcessStartCounter = memory.ProcessStartCounter + 1 }, reference.RunId, reference.Authority, reference.ClientId, "holder identity");
            checks["wrong-inode"] = Rejects(reference, memory with { Inode = memory.Inode + 1 }, reference.RunId, reference.Authority, reference.ClientId, "file identity");
            using var replacement = KeycloakClientCredentialMemoryCapture.Create(reference.RunId, reference.Authority, reference.ClientId, envelope.Secret);
            checks["replacement-file"] = Rejects(reference, memory with { FileDescriptor = replacement.Reference.MemoryIdentity!.FileDescriptor }, reference.RunId, reference.Authority, reference.ClientId, "file identity");
            using var expired = KeycloakClientCredentialMemoryCapture.Create(reference.RunId, reference.Authority, reference.ClientId, envelope.Secret,
                new FixedClock(DateTimeOffset.UtcNow.AddMinutes(-10)));
            checks["expiry"] = Rejects(expired.Reference, expired.Reference.MemoryIdentity!, reference.RunId, reference.Authority, reference.ClientId, "binding or expiry");
            using var owner = KeycloakClientCredentialMemoryCapture.CreateForOwnerLifetime(reference.RunId, reference.Authority, reference.ClientId, envelope.Secret);
            checks["owner-lifetime-beyond-former-expiry"] = KeycloakClientCredentialMemoryCapture.Read(owner.Reference,
                new FixedClock(DateTimeOffset.UtcNow.AddDays(1))) == envelope.Secret;
            owner.Dispose();
            try { _ = owner.Reference.Read(); checks["owner-disposed-rejected"] = false; }
            catch (IOException) { checks["owner-disposed-rejected"] = true; }
            var unsealed = Native.memfd_create("unsealed-library-control", 3);
            if (unsealed < 0) throw new InvalidOperationException();
            try
            {
                checks["unsealed"] = Rejects(reference, memory with { FileDescriptor = unsealed }, reference.RunId, reference.Authority, reference.ClientId, "capture was rejected");
            }
            finally { Native.close(unsealed); }
            var results = await Task.WhenAll(ChildAsync(reference, envelope.Secret), ChildAsync(reference, envelope.Secret));
            checks["worker"] = results[0];
            checks["web"] = results[1];
            checks["reads-extracted-pure-library"] = typeof(KeycloakClientCredentialReference).Assembly.Location.StartsWith("/proof/code/", StringComparison.Ordinal);
            checks["no-Aspire-runtime"] = !AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name!.Contains("Aspire", StringComparison.Ordinal));
            Console.Error.WriteLine(JsonSerializer.Serialize(checks));
            var passed = checks.Values.All(value => value);
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "complete", passed, childrenStopped = results.All(x => x), testCount = checks.Count, platformVersion = Environment.Version.ToString() }));
            return passed ? 0 : 1;
        }
        catch
        {
            Console.WriteLine("{\"stage\":\"library-probe-failed\",\"passed\":false,\"childrenStopped\":false,\"testCount\":0}");
            return 1;
        }
    }

    private static bool Rejects(KeycloakClientCredentialReference original, KeycloakClientCredentialMemoryIdentity memory,
        string run, Uri authority, string client, string expectedMessage)
    {
        try { _ = KeycloakClientCredentialReference.ForSealedMemory(memory, run, authority, client).Read(); return false; }
        catch (InvalidDataException error) { return error.Message.Contains(expectedMessage, StringComparison.Ordinal); }
    }

    private static async Task<bool> ChildAsync(KeycloakClientCredentialReference reference, string expected)
    {
        var start = new ProcessStartInfo("/usr/share/dotnet/dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(JsonSerializer.Serialize(reference, JsonOptions));
        using var child = Process.Start(start) ?? throw new InvalidOperationException();
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await child.StandardInput.WriteAsync(expected);
            child.StandardInput.Close();
            await child.WaitForExitAsync(deadline.Token);
            var summary = JsonSerializer.Deserialize<ReaderResult>(await output, JsonOptions);
            await error;
            return child.ExitCode == 0 && summary is { Accepted: true, FailureStage: "none" };
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                await child.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static async Task<int> ReaderAsync(string json)
    {
        var stage = "reference";
        try
        {
            if (json.Length > 8192) throw new InvalidDataException();
            var reference = JsonSerializer.Deserialize<KeycloakClientCredentialReference>(json, JsonOptions) ?? throw new InvalidDataException();
            var expected = await ReadBoundedAsync(Console.OpenStandardInput(), 4096);
            stage = "native-read";
            var actual = reference.Read();
            if (!Encoding.UTF8.GetBytes(actual).AsSpan().SequenceEqual(expected)) throw new InvalidDataException();
            Console.WriteLine(JsonSerializer.Serialize(new ReaderResult("reader-accepted", true, "none"), JsonOptions));
            return 0;
        }
        catch (InvalidDataException error)
        {
            stage = error.Message.Contains("holder identity", StringComparison.Ordinal) ? "parent-identity" : stage;
        }
        catch { }
        Console.WriteLine(JsonSerializer.Serialize(new ReaderResult("reader-rejected", false, stage), JsonOptions));
        return 20;
    }

    private static async Task<Envelope> ReadEnvelopeAsync()
    {
        var bytes = await ReadBoundedAsync(Console.OpenStandardInput(), 8192);
        return JsonSerializer.Deserialize<Envelope>(bytes, JsonOptions) ?? throw new InvalidDataException();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0) break;
            if (buffer.Length + read > maximum) throw new InvalidDataException();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private sealed record Envelope(string Secret, string Authority, string Client);
    private sealed record ReaderResult(string Stage, bool Accepted, string FailureStage);
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private static class Native
    {
        [DllImport("libc", SetLastError = true)] internal static extern int fcntl(int fd, int command, int argument);
        [DllImport("libc", SetLastError = true)] internal static extern int ftruncate(int fd, long length);
        [DllImport("libc", SetLastError = true)] internal static extern nint write(int fd, byte[] bytes, nuint count);
        [DllImport("libc", SetLastError = true)] internal static extern int memfd_create(string name, int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int close(int fd);
    }
}
