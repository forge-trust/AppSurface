// Provisional consumer proof: no admission authority or production API is implemented here.
using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.Length != 2)
{
    return 64;
}

var scenario = args[0];
var output = args[1];
using var stopped = new CancellationTokenSource();
using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stopped.Cancel();
});

void Mark(string name) => File.WriteAllText(Path.Join(output, name), "observed\n");

Mark("entered");
// The launcher checks PID 1's actual identity/deadline/cgroup binding before releasing callbacks.
while (!File.Exists(Path.Join(output, "activate")))
{
    Thread.Sleep(10);
}
Mark("callback-entered");
try
{
    switch (scenario)
    {
        case "success":
            Mark("disposed");
            Mark("manifest");
            return 0;
        case "cooperative":
            try
            {
                await Task.Delay(Timeout.Infinite, stopped.Token);
            }
            catch (OperationCanceledException)
            {
                // Only this settled cancellation can precede disposal/publication.
                Mark("joined");
                Mark("disposed");
                Mark("failure-manifest");
                return 2;
            }
            return 65;
        case "fatal":
            Environment.FailFast("ASEVD-FIXTURE: owned callback did not stop.");
            return 65;
        case "dispose-stall":
            Mark("joined");
            Mark("dispose-entered");
            Thread.Sleep(Timeout.Infinite);
            return 65;
        case "descendant":
            using (var child = Process.Start(new ProcessStartInfo("/usr/bin/python3")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "-c",
                    "import os,signal,time; os.setsid(); signal.signal(signal.SIGTERM, signal.SIG_IGN); " +
                    "print(os.getpid(), flush=True); time.sleep(3600)"
                }
            }) ?? throw new InvalidOperationException("Fixture child did not start."))
            {
                // A new session must still remain owned by the systemd control group.
                var pid = await child.StandardOutput.ReadLineAsync();
                File.WriteAllText(Path.Join(output, "child-pid"), pid);
                var stdoutPump = child.StandardOutput.ReadToEndAsync();
                var stderrPump = child.StandardError.ReadToEndAsync();
                await child.WaitForExitAsync();
                await Task.WhenAll(stdoutPump, stderrPump);
                Mark("pumps-joined");
                return 65;
            }
        case "configure-stall":
        case "verifier-stall":
        case "factory-stall":
        case "producer-stall":
            // Synchronous work has not returned a Task. The worker cannot enforce its own deadline.
            Thread.Sleep(Timeout.Infinite);
            return 65;
        default:
            return 64;
    }
}
finally
{
    // FailFast and an independently enforced SIGKILL must not unwind this callback marker.
    Mark("unwound");
}
