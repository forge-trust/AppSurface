using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace EvidenceHost.CompletedIsolation;

/// <summary>One private, closed child-side dispatcher for a root-owned isolation phase.</summary>
/// <remarks>
/// The launcher receives one bounded release frame on standard input only after the root process
/// has verified this process in its systemd cgroup. The token is never placed in argv or unit
/// properties. It is added to a fresh environment only for the two trusted metadata commands.
/// Replay has exactly the four-variable worker environment and cannot inherit the release pipe.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class PhaseLauncher
{
    private const int MaximumReleaseBytes = 32 * 1024;
    private const int MaximumTokenBytes = 8192;
    private const string Python = "/usr/bin/python3";

    private static readonly string[] EnvironmentKeys =
    [
        "PATH", "HOME", "LANG", "LC_ALL"
    ];

    private static readonly string[] ContextKeys =
    [
        "GITHUB_ACTIONS", "GITHUB_EVENT_NAME", "GITHUB_REPOSITORY", "GITHUB_REF",
        "GITHUB_REF_PROTECTED", "GITHUB_WORKFLOW_REF", "GITHUB_REPOSITORY_ID",
        "GITHUB_SHA", "GITHUB_WORKFLOW_SHA"
    ];

    private static readonly HashSet<string> ControlNames = new(StringComparer.Ordinal)
    {
        "baseline", "readonly-writes", "credential-denial", "spawn-escape", "ignore-stop",
        "output-overflow", "quota", "mutate-source"
    };

    /// <summary>Checks the clean nonroot process and exact environment, waits for root release, then runs one fixed child.</summary>
    /// <param name="phase">Closed phase selected by the private owner command line.</param>
    /// <returns>A finite child exit code; malformed input and local failures are reduced to 2.</returns>
    internal static async Task<int> RunAsync(OwnerPhase phase)
    {
        byte[]? frameBytes = null;
        byte[]? tokenBytes = null;
        try
        {
            RequirePhase(phase);
            NativeFacts.RequireSelfEnvironment(EnvironmentKeys);
            NativeFacts.RequireSelfIdentity();
            NativeFacts.RequireSelfCapabilitiesAndDescriptors();

            Console.Out.Write("ISOLATION_READY:");
            Console.Out.WriteLine(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Console.Out.Flush();

            frameBytes = await ReadOneFrameAsync(Console.OpenStandardInput()).ConfigureAwait(false);
            // The release channel is a one-frame capability. Close the descriptor before creating
            // any target process so neither the launcher nor its child can retain it.
            if (Close(0) != 0) throw new InvalidDataException();
            var release = ParseRelease(frameBytes, phase);
            if (phase == OwnerPhase.Control)
                return await ControlProbe.RunAsync(release.Control ?? throw new InvalidDataException()).ConfigureAwait(false);
            var startInfo = BuildCommand(phase, release);
            if (release.Token is not null)
                tokenBytes = Encoding.UTF8.GetBytes(release.Token);

            using var child = new Process { StartInfo = startInfo, EnableRaisingEvents = false };
            if (!child.Start()) return Reject();

            // The Python command receives EOF on stdin, never the root-to-launcher release pipe.
            child.StandardInput.Close();
            await child.WaitForExitAsync().ConfigureAwait(false);
            return child.ExitCode;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            return Reject();
        }
        finally
        {
            if (frameBytes is not null) CryptographicOperations.ZeroMemory(frameBytes);
            if (tokenBytes is not null) CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    private static ProcessStartInfo BuildCommand(OwnerPhase phase, PhaseRelease release)
    {
        var start = new ProcessStartInfo(Python)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = phase == OwnerPhase.Replay ? OwnerPaths.Source : OwnerPaths.Validator,
            CreateNoWindow = true
        };

        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-S");
        start.ArgumentList.Add("-B");
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/bin:/bin";
        start.Environment["HOME"] = "/nonexistent";
        start.Environment["LANG"] = "C.UTF-8";
        start.Environment["LC_ALL"] = "C";

        switch (phase)
        {
            case OwnerPhase.Prepare:
                start.ArgumentList.Add(OwnerPaths.Validator + "/.github/issue779-protected-qualification/completed_entrypoint.py");
                start.ArgumentList.Add("prepare");
                start.ArgumentList.Add(OwnerPaths.ValidatorCapture);
                start.ArgumentList.Add(OwnerPaths.SourceCapture);
                start.ArgumentList.Add(OwnerPaths.Source);
                start.ArgumentList.Add(OwnerPaths.Native);
                start.ArgumentList.Add(OwnerPaths.Stage);
                start.ArgumentList.Add(OwnerPaths.Preparation);
                AddCredentialEnvironment(start, release);
                break;

            case OwnerPhase.Replay:
                start.WorkingDirectory = OwnerPaths.Validator;
                start.ArgumentList.Add(OwnerPaths.Validator + "/.github/issue779-protected-qualification/completed_replay_worker.py");
                start.ArgumentList.Add(OwnerPaths.Preparation);
                start.ArgumentList.Add(OwnerPaths.SourceCapture);
                start.ArgumentList.Add(OwnerPaths.Output);
                start.ArgumentList.Add(OwnerPaths.ReplayResult);
                break;

            case OwnerPhase.Close:
                start.ArgumentList.Add(OwnerPaths.Validator + "/.github/issue779-protected-qualification/completed_entrypoint.py");
                start.ArgumentList.Add("close");
                start.ArgumentList.Add(OwnerPaths.ValidatorCapture);
                start.ArgumentList.Add(OwnerPaths.SourceCapture);
                start.ArgumentList.Add(OwnerPaths.Preparation);
                start.ArgumentList.Add(OwnerPaths.ReplayResult);
                start.ArgumentList.Add(OwnerPaths.Report + "/observation.json");
                AddCredentialEnvironment(start, release);
                break;

            default:
                throw new InvalidDataException();
        }

        return start;
    }

    private static void AddCredentialEnvironment(ProcessStartInfo start, PhaseRelease release)
    {
        if (release.Token is null || release.Context is null) throw new InvalidDataException();
        start.Environment["GH_TOKEN"] = release.Token;
        start.Environment["GITHUB_EVENT_PATH"] = OwnerPaths.Event;
        foreach (var key in ContextKeys)
        {
            if (!release.Context.TryGetValue(key, out var value)) throw new InvalidDataException();
            start.Environment[key] = value;
        }
    }

    internal static PhaseRelease ParseRelease(ReadOnlySpan<byte> bytes, OwnerPhase phase)
    {
        if (bytes.Length is < 2 or > MaximumReleaseBytes || bytes[^1] != (byte)'\n'
            || bytes[..^1].Contains((byte)'\n') || bytes.Contains((byte)'\r')) throw new InvalidDataException();

        using var document = JsonDocument.Parse(bytes[..^1].ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!values.TryAdd(property.Name, property.Value)) throw new InvalidDataException();
        if (values.Count != 4 || !values.TryGetValue("phase", out var phaseValue)
            || !values.TryGetValue("token", out var tokenValue)
            || !values.TryGetValue("control", out var controlValue)
            || !values.TryGetValue("context", out var contextValue)
            || phaseValue.ValueKind != JsonValueKind.String || phaseValue.GetString() != PhaseName(phase))
            throw new InvalidDataException();

        var token = tokenValue.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => tokenValue.GetString(),
            _ => throw new InvalidDataException()
        };
        var control = controlValue.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => controlValue.GetString(),
            _ => throw new InvalidDataException()
        };
        Dictionary<string, string>? context = null;
        if (contextValue.ValueKind == JsonValueKind.Object)
        {
            context = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in contextValue.EnumerateObject())
            {
                if (!ContextKeys.Contains(property.Name, StringComparer.Ordinal)
                    || property.Value.ValueKind != JsonValueKind.String
                    || !context.TryAdd(property.Name, property.Value.GetString() ?? throw new InvalidDataException()))
                    throw new InvalidDataException();
            }
            if (context.Count != ContextKeys.Length || context.Values.Any(value => value.Length > 1024 || HasControl(value)))
                throw new InvalidDataException();
        }
        else if (contextValue.ValueKind != JsonValueKind.Null) throw new InvalidDataException();

        switch (phase)
        {
            case OwnerPhase.Prepare:
            case OwnerPhase.Close:
                if (token is null || token.Length == 0 || Encoding.UTF8.GetByteCount(token) > MaximumTokenBytes || !IsVisibleAscii(token)
                    || control is not null || context is null) throw new InvalidDataException();
                break;
            case OwnerPhase.Replay:
                if (token is not null || control is not null || context is not null) throw new InvalidDataException();
                break;
            case OwnerPhase.Control:
                if (token is not null || control is null || !ControlNames.Contains(control) || context is not null)
                    throw new InvalidDataException();
                break;
            default:
                throw new InvalidDataException();
        }

        return new(PhaseName(phase), token, control, context);
    }

    private static async Task<byte[]> ReadOneFrameAsync(Stream input)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        try
        {
            while (buffer.Length < MaximumReleaseBytes)
            {
                var count = await input.ReadAsync(one.AsMemory(0, 1)).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                buffer.WriteByte(one[0]);
                if (one[0] == (byte)'\n')
                {
                    if (await input.ReadAsync(one.AsMemory(0, 1)).ConfigureAwait(false) != 0)
                        throw new InvalidDataException();
                    return buffer.ToArray();
                }
            }
            throw new InvalidDataException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.GetBuffer());
            CryptographicOperations.ZeroMemory(one);
        }
    }

    private static bool IsVisibleAscii(string value) => value.Length <= MaximumTokenBytes
        && value.All(static character => character is >= '!' and <= '~');

    private static bool HasControl(string value) => value.Any(char.IsControl);

    private static string PhaseName(OwnerPhase phase) => phase switch
    {
        OwnerPhase.Prepare => "prepare",
        OwnerPhase.Replay => "replay",
        OwnerPhase.Close => "close",
        OwnerPhase.Control => "control",
        _ => throw new InvalidDataException()
    };

    private static void RequirePhase(OwnerPhase phase)
    {
        if (!Enum.IsDefined(phase)) throw new InvalidDataException();
    }

    private static int Reject()
    {
        Console.Error.WriteLine("ISOLATION_PHASE_REJECTED");
        return 2;
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
