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
                or "sdk-host-path" or "sdk-host-directory" or "sdk-host-binary" or "sdk-version"
                or "sdk-identity" or "sdk-path-identity" or "sdk-stage"
                or "custody-file-shape" or "tracked-mode" or "source-file-binding" or "source-index-binding"
                or "handoff-unexpected-node" or "stage-count" or "stage-changed" or "input-immutable"
                or "host-canary-changed" or "tracked-file-changed" or "tracked-git-dirty"
                or "git-metadata-changed" or "root-name-changed"
                or "immutable-replace-source-shape" or "immutable-replace-source-size"
                or "immutable-replace-unexpected-success" or "immutable-replace-unexpected-error"
                or "immutable-replace-source-changed" or "immutable-replace-destination-exists"
                or "immutable-replace-destination-stat"
                or "source-not-clean" or "git-metadata-file" or "git-metadata-directory" or "git-config-shape"
                or "uid-range-exhausted" or "getent-failed" or "uid-marker-open" or "utility-contract"
                or "image-create" or "image-format" or "loop-attach" or "image-mount" or "ext4-quota"
                or "input-shape" or "input-path" or "repository" or "evidence-parent" or "host-parent"
                or "root-required" or "systemd-cgroupv2-required" or "linux-x64-required"
                or "launcher-environment" or "launcher-identity" or "launcher-capabilities" or "launcher-nnp"
                or "launcher-descriptor"
                or "phase-unsettled" or "ready-pid" or "unit-cgroup" or "unit-process-count" or "unit-process-facts" or "unit-launcher-ambiguous"
                or "unit-launcher-facts" or "unit-launcher-security" or "unit-bootstrap-security" or "unit-bootstrap-argv"
                or "unit-root-identity" or "unit-namespace-identity" or "unit-mount-namespace"
                or "unit-readonly-mount" or "unit-readwrite-mount" or "unit-git-mount" or "unit-preparation-mount"
                or "unit-sdk-mount" or "unit-cgroup-open" or "unit-cgroup-type" or "unit-cgroup-files"
                or "unit-cgroup-initial" or "launcher-pidfd" or "launcher-pidfd-poll"
                or "proc-link" or "proc-fields" or "proc-fields-format" or "mountinfo-format"
                or "statx-handle" or "statx-path" or "native-command-start" or "native-input-size"
                or "cgroup-depth" or "cgroup-path" or "cgroup-process-limit" or "cgroup-proc-duplicate"
                or "cgroup-procs" or "cgroup-events-format" or "cgroup-read" or "cgroup-seek" or "cgroup-size"
                => issue.Message,
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
            if (OperatingSystem.IsLinux() && args.Length == 4 && args[0] == "controls")
                Console.Error.WriteLine("ISOLATION_CONTROL_CHECKPOINT:" + OwnerCoordinator.ControlStep);
            if (OperatingSystem.IsLinux()) Console.Error.WriteLine("ISOLATION_SETUP_STAGE:" + OwnerWorkspace.SetupStage);
            return 1;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }
}
