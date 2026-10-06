using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;

namespace ForgeTrust.AppSurface.EvidenceHost.RuntimeSubject;

public sealed partial class RuntimeBoundaryTests
{
    private const string AllowedInputMarker = "EVIDENCE_RUNTIME_SUBJECT_ALLOWED_INPUT";
    private const string ToolRootMarker = "/appsurface-evidencehost-runtime-";

    [Fact]
    public void Subject_reads_its_declared_input_and_cannot_read_or_modify_protected_state()
    {
        Assert.True(OperatingSystem.IsLinux(), "The subject must run on Linux.");

        var subjectRoot = Path.GetFullPath(Environment.CurrentDirectory);
        var declaredInput = Path.Combine(subjectRoot, "tests", "evidencehost-consumer", "RuntimeSubject", "Program.cs");
        var source = File.ReadAllText(declaredInput);
        Assert.Contains(AllowedInputMarker, source, StringComparison.Ordinal);

        var resultsRoot = Environment.GetEnvironmentVariable("EVIDENCE_TEST_OUTPUT_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(resultsRoot));
        Assert.True(Directory.Exists(resultsRoot));

        AssertSanitizedEnvironment();

        var cgroup = File.ReadAllText("/proc/self/cgroup");
        var subjectUnit = Regex.Match(cgroup, @"(?m)(?:^|/)(evidencehost-([0-9a-f]{12})-s-([0-9]+)\.service)$");
        Assert.True(subjectUnit.Success, "The subject must run in its dedicated systemd service.");
        var runTag = subjectUnit.Groups[2].Value;
        var subjectUnitIndex = int.Parse(subjectUnit.Groups[3].Value, CultureInfo.InvariantCulture);
        var workerUnit = $"evidencehost-{runTag}-worker.service";
        var workerCgroup = Path.Combine("/sys/fs/cgroup/system.slice", workerUnit, "cgroup.procs");
        var workerPidText = File.ReadAllText(workerCgroup).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        Assert.True(int.TryParse(workerPidText, NumberStyles.None, CultureInfo.InvariantCulture, out var workerPid));
        Assert.True(workerPid > 1);

        var workerStatus = File.ReadAllText($"/proc/{workerPid}/status");
        var uidLine = Regex.Match(workerStatus, @"(?m)^Uid:\s+([0-9]+)");
        Assert.True(uidLine.Success);
        var workerUid = uint.Parse(uidLine.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.NotEqual(NativeMethods.GetEffectiveUserId(), workerUid);

        var protectedMounts = ReadProtectedMountPoints();
        var toolRoot = FindExactlyOne(protectedMounts, path =>
            path.Contains(ToolRootMarker, StringComparison.Ordinal) && path.EndsWith("/tool-root", StringComparison.Ordinal), "tool root");
        var outputRoot = FindExactlyOne(protectedMounts, path =>
            path.Contains(ToolRootMarker, StringComparison.Ordinal) && path.Contains("/output-parent/run-", StringComparison.Ordinal), "output root");
        var policyFile = FindExactlyOne(protectedMounts, path =>
            path.Contains(ToolRootMarker, StringComparison.Ordinal) && path.EndsWith("/evidence.policy.json", StringComparison.Ordinal), "policy file");
        var controlRoot = FindExactlyOne(protectedMounts, path =>
            path.StartsWith($"/run/evidencehost-{runTag}-", StringComparison.Ordinal)
            && !path.Contains("evidencehost-subject-", StringComparison.Ordinal), "worker control root");

        var cliPath = Path.Combine(toolRoot, "ForgeTrust.AppSurface.Cli.dll");
        AssertDeniedRead(cliPath, "protected CLI read");
        AssertDeniedWriteOpen(cliPath, "protected CLI write-open");
        AssertDeniedRead(policyFile, "protected policy read");
        AssertDeniedWriteOpen(policyFile, "protected policy write-open");

        AssertDeniedDirectoryRead(outputRoot, "protected output read");
        AssertDeniedAnonymousDirectoryWrite(outputRoot, "protected output write");

        var descriptorPath = Path.Combine(controlRoot, "worker-control.json");
        var controlSocket = Path.Combine(controlRoot, "broker", "control.sock");
        AssertDeniedRead(descriptorPath, "worker descriptor read");
        AssertDeniedWriteOpen(descriptorPath, "worker descriptor write-open");
        AssertDeniedUnixSocketConnect(controlSocket, "worker control connection");

        var workerEnvironment = $"/proc/{workerPid}/environ";
        AssertDeniedRead(workerEnvironment, "worker environment read");
        AssertDeniedWriteOpen(workerEnvironment, "worker environment write-open");
        AssertDeniedReadWriteOpen($"/proc/{workerPid}/mem", "worker memory read-write-open");

        Assert.True(subjectUnitIndex >= 0);
    }

    private static void AssertSanitizedEnvironment()
    {
        var forbiddenNames = new[]
        {
            "GITHUB_TOKEN", "GH_TOKEN", "CODECOV_TOKEN", "NUGET_AUTH_TOKEN", "SYSTEM_ACCESSTOKEN",
            "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AZURE_CLIENT_SECRET", "CODECOV_TOKEN_PRESENT",
        };

        foreach (var name in forbiddenNames)
        {
            Assert.Null(Environment.GetEnvironmentVariable(name));
        }

        Assert.Equal("/nonexistent", Environment.GetEnvironmentVariable("HOME"));
        Assert.Equal("/usr/bin:/bin", Environment.GetEnvironmentVariable("PATH"));
    }

    private static IReadOnlyList<string> ReadProtectedMountPoints()
    {
        var result = new List<string>();
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5)
            {
                continue;
            }

            var mountPoint = fields[4]
                .Replace("\\040", " ", StringComparison.Ordinal)
                .Replace("\\011", "\t", StringComparison.Ordinal)
                .Replace("\\012", "\n", StringComparison.Ordinal)
                .Replace("\\134", "\\", StringComparison.Ordinal);
            if (mountPoint.Contains(ToolRootMarker, StringComparison.Ordinal)
                || mountPoint.StartsWith("/run/evidencehost-", StringComparison.Ordinal))
            {
                result.Add(mountPoint);
            }
        }

        return result;
    }

    private static string FindExactlyOne(IEnumerable<string> paths, Func<string, bool> predicate, string label)
    {
        var matches = paths.Where(predicate).Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(matches.Length == 1, $"Expected one protected {label} mount; observed {matches.Length}.");
        return matches[0];
    }

    private static void AssertDeniedRead(string path, string label)
        => AssertPermissionDenied(() => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read), label, allowReadOnlyFileSystem: false);

    private static void AssertDeniedWriteOpen(string path, string label)
        => AssertPermissionDenied(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read), label, allowReadOnlyFileSystem: true);

    private static void AssertDeniedReadWriteOpen(string path, string label)
        => AssertPermissionDenied(() => File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read), label, allowReadOnlyFileSystem: true);

    private static void AssertPermissionDenied(Func<Stream> open, string label, bool allowReadOnlyFileSystem)
    {
        try
        {
            using var stream = open();
            Assert.Fail($"Expected access denial for {label}.");
        }
        catch (Exception exception) when (IsAccessDenied(exception, allowReadOnlyFileSystem))
        {
            // The only acceptable result is a kernel permission or read-only-filesystem denial.
        }
    }

    private static void AssertDeniedDirectoryRead(string path, string label)
    {
        try
        {
            _ = Directory.GetFileSystemEntries(path);
            Assert.Fail($"Expected access denial for {label}.");
        }
        catch (Exception exception) when (IsAccessDenied(exception, allowReadOnlyFileSystem: false))
        {
            // A visible-but-inaccessible mount is the expected result.
        }
    }

    private static void AssertDeniedAnonymousDirectoryWrite(string path, string label)
    {
        const int readWrite = 2;
        const int closeOnExec = 0x80000;
        const int temporaryFile = 0x410000;
        var descriptor = NativeMethods.Open(path, readWrite | closeOnExec | temporaryFile, Convert.ToUInt32("600", 8));
        if (descriptor >= 0)
        {
            _ = NativeMethods.Close(descriptor);
            Assert.Fail($"Expected access denial for {label}.");
        }

        var error = Marshal.GetLastPInvokeError();
        Assert.True(error is 1 or 13 or 30, $"Expected EACCES, EPERM, or EROFS for {label}; observed errno {error}.");
    }

    private static void AssertDeniedUnixSocketConnect(string path, string label)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            Assert.Fail($"Expected access denial for {label}.");
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AccessDenied)
        {
            // The subject cannot reach the launcher's protected control channel.
        }
    }

    private static bool IsAccessDenied(Exception exception, bool allowReadOnlyFileSystem)
    {
        if (exception is UnauthorizedAccessException)
        {
            return true;
        }

        if (exception is IOException)
        {
            var error = exception.HResult & 0xffff;
            return error is 1 or 13 || (allowReadOnlyFileSystem && error == 30);
        }

        return false;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc", EntryPoint = "geteuid")]
        internal static partial uint GetEffectiveUserId();

        [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Open(string path, int flags, uint mode);

        [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
        internal static partial int Close(int descriptor);
    }
}
