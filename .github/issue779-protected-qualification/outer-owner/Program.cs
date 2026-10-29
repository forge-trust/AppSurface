namespace EvidenceHost.CompletedIsolation;

/// <summary>Private Linux CI owner entry point; no product registration, admission or arbitrary command execution.</summary>
internal static class Program
{
    /// <summary>Exposes only fixed diagnostic categories; arbitrary exception text is never retained.</summary>
    internal static string SafeFailureCode(Exception error) => error switch
    {
        IsolationException issue => issue.Message switch
        {
            "proc-numeric-field" or "proc-credential-fields" or "proc-uid-format" or "proc-gid-format"
                or "proc-groups-format" or "proc-file-open" or "proc-identity-race" or "proc-starttime-format"
                or "rootfs-source-shape" or "rootfs-source-path" or "rootfs-duplicate-shape" or "rootfs-duplicate-content"
                or "trusted-binary-dependencies" or "trusted-library-missing" or "stdlib-extension-dependencies"
                or "dotnet-runtime-dependencies" or "dotnet-dlopen-library" or "dotnet-dlopen-dependencies"
                or "source-clone" or "source-checkout" or "capture-commit-unavailable" or "git-command"
                or "rootfs-directory-shape" or "tool-parent-shape" or "tool-parent-private-path"
                or "custody-file-shape" or "tracked-mode" or "source-file-binding" or "source-index-binding"
                or "source-not-clean" or "git-metadata-file" or "git-metadata-directory" or "git-config-shape"
                or "uid-range-exhausted" or "getent-failed" or "uid-marker-open" or "utility-contract"
                or "image-create" or "image-format" or "loop-attach" or "image-mount" or "ext4-quota"
                or "input-shape" or "input-path" or "repository" or "evidence-parent" or "host-parent"
                or "root-required" or "systemd-cgroupv2-required" or "linux-x64-required"
                or "launcher-environment" or "launcher-identity" or "launcher-capabilities" or "launcher-nnp"
                or "launcher-descriptor" => issue.Message,
            _ => "closed-runtime-failure"
        },
        UnauthorizedAccessException => "access-denied",
        IOException => "io-failure",
        System.ComponentModel.Win32Exception => "process-start-failure",
        EntryPointNotFoundException => "interop-entrypoint-missing",
        _ => "runtime-failure"
    };

    internal static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            if (args.Length == 1 && args[0] == "policy-controls") return PolicyControls.Run();
            if (!OperatingSystem.IsLinux()) throw new IsolationException("linux-required");
            if (args.Length == 2 && args[0] == "phase"
                && Enum.TryParse<OwnerPhase>(args[1], ignoreCase: true, out var phase)
                && Enum.IsDefined(phase)) return await PhaseLauncher.RunAsync(phase);
            if (args.Length == 4 && args[0] == "controls")
                return await OwnerCoordinator.ControlsAsync(args[1], args[2], args[3], cancellation.Token);
            if (args.Length == 6 && args[0] == "completed")
                return await OwnerCoordinator.CompletedAsync(args[1], args[2], args[3], args[4], args[5], cancellation.Token);
            Console.Error.WriteLine("ISOLATION_INPUT_REJECTED");
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("ISOLATION_CANCELLED");
            return 130;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // Child and provider text may contain secrets or target-controlled paths.
            Console.Error.WriteLine("ISOLATION_REJECTED");
            Console.Error.WriteLine("ISOLATION_FAILURE_CODE:" + SafeFailureCode(error));
            if (OperatingSystem.IsLinux()) Console.Error.WriteLine("ISOLATION_SETUP_STAGE:" + OwnerWorkspace.SetupStage);
            return 1;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }
}
