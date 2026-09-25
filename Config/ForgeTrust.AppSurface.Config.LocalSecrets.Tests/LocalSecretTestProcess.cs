using System.Diagnostics;

namespace ForgeTrust.AppSurface.Config.LocalSecrets.Tests;

/// <summary>Bounded child-process fixture; touches only paths explicitly supplied by isolated tests.</summary>
internal static class LocalSecretTestProcess
{
    public static int Main(string[] args)
    {
        if (args.Length != 3) return 2;
        File.WriteAllText(args[2], "started");
        if (args[0].StartsWith("crash:", StringComparison.Ordinal))
        {
            var parts = args[0].Split(':');
            return LocalSecretFileMigrationPersistenceTests.CrashAtBoundary(args[1], int.Parse(parts[1]), bool.Parse(parts[2]));
        }
        if (args[0] == "write")
        {
            var identity = new AppSurfaceLocalSecretIdentityNormalizer().Normalize("App", "Development", null, "Independent:Writer").Identity!;
            return new FileAppSurfaceLocalSecretStore(args[1]).Set(identity, "writer-marker").Status == LocalSecretResultStatus.Found ? 0 : 3;
        }
        if (args[0] == "lease")
        {
            using var lease = PlatformLocalSecretMaintenanceLease.Acquire(args[1], "App", "Development", null, TimeSpan.FromSeconds(10), CancellationToken.None);
            return 0;
        }
        return 2;
    }

    internal static Process Start(string operation, string path, string signal)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "ForgeTrust.AppSurface.Config.LocalSecrets.Tests" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(operation);
        info.ArgumentList.Add(path);
        info.ArgumentList.Add(signal);
        // A different temp environment must not alter the package lease identity.
        info.Environment["TMPDIR"] = Path.GetDirectoryName(path)!;
        return Process.Start(info) ?? throw new IOException("The isolated test writer could not start.");
    }
}
