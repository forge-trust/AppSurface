namespace EvidenceHost.CompletedIsolation;

/// <summary>Private Linux CI owner entry point; no product registration, admission or arbitrary command execution.</summary>
internal static class Program
{
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
            if (OperatingSystem.IsLinux()) Console.Error.WriteLine("ISOLATION_SETUP_STAGE:" + OwnerWorkspace.SetupStage);
            return 1;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }
}
