using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class NativeConsole
{
    private const string Relay = "--console-relay";

    // Run a second copy of this harness as the verifier's private console owner.
    // Its stdin is the parent's control pipe; stdout/stderr carry the verifier output.
    public static Process Start(ProcessStartInfo command)
    {
        var start = TestHost.Command(TestHost.Dotnet, command.WorkingDirectory,
            [typeof(NativeConsole).Assembly.Location, Relay, command.FileName, .. command.ArgumentList]);
        start.RedirectStandardInput = true;
        start.Environment.Clear();
        foreach (var (key, value) in command.Environment)
        {
            start.Environment[key] = value;
        }
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the console relay.");
    }

    public static async Task SendCtrlCAsync(Process relay)
    {
        await relay.StandardInput.WriteLineAsync("interrupt");
        await relay.StandardInput.FlushAsync();
    }

    public static async Task<int> DispatchAsync(string[] args)
    {
        if (args.Length < 3 || args[0] != Relay)
        {
            return 2;
        }

        // Capture the redirected pipe before AllocConsole changes the Windows standard handles.
        // Pass the verifier's output through the original pipes directly.
        using var control = new StreamReader(Console.OpenStandardInput());
        if (OperatingSystem.IsWindows())
        {
            var output = GetStdHandle(-11);
            var error = GetStdHandle(-12);
            if (!FreeConsole() && Marshal.GetLastPInvokeError() != 6) // ERROR_INVALID_HANDLE: no parent console
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            if (!AllocConsole() || !SetStdHandle(-11, output) || !SetStdHandle(-12, error))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        else if (setsid() < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        // A normal handler keeps the relay alive without making the verifier inherit an ignored signal.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        var start = new ProcessStartInfo(args[1]) { UseShellExecute = false, RedirectStandardInput = true };
        foreach (var arg in args.Skip(2))
        {
            start.ArgumentList.Add(arg);
        }

        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the verifier.");
        try
        {
            child.StandardInput.Close();
            var exited = child.WaitForExitAsync();
            var request = control.ReadLineAsync();
            if (await Task.WhenAny(exited, request) == request && !child.HasExited)
            {
                if (await request != "interrupt")
                {
                    throw new IOException("The cancellation test closed its control pipe.");
                }
                // Group 0 targets this relay's private console/session and its descendants.
                var sent = OperatingSystem.IsWindows() ? GenerateConsoleCtrlEvent(0, 0) : kill(0, 2) == 0;
                if (!sent)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            await exited;
            return child.ExitCode;
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int standardHandle, nint handle);
}
