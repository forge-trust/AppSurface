using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

if (args is ["fail"])
    return 62;

if (args is not ["hold"])
    return 64;

const string stateDirectory = "/proof/state";
const string childPidPath = stateDirectory + "/detached-child.pid";
const string receiptPath = stateDirectory + "/lifecycle-probe.json";
const string childScript = """
    exec </dev/null >/dev/null 2>&1
    trap '' TERM INT
    printf '%s\n' "$$" > /proof/state/detached-child.pid
    while :; do sleep 1; done
    """;

Directory.CreateDirectory(stateDirectory);

var startInfo = new ProcessStartInfo("/usr/bin/setsid")
{
    UseShellExecute = false,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};
startInfo.ArgumentList.Add("/bin/sh");
startInfo.ArgumentList.Add("-c");
startInfo.ArgumentList.Add(childScript);
startInfo.ArgumentList.Add("issue888-detached-child");

using var launcher = Process.Start(startInfo);
if (launcher is null)
    return 70;

launcher.StandardInput.Close();
_ = launcher.StandardOutput.ReadToEndAsync();
_ = launcher.StandardError.ReadToEndAsync();

var startup = Stopwatch.StartNew();
var childPid = 0;
var childIdentity = (ProcessGroup: 0, Session: 0);
while (startup.Elapsed < TimeSpan.FromSeconds(5))
{
    if (File.Exists(childPidPath)
        && int.TryParse(File.ReadAllText(childPidPath).Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out childPid)
        && childPid > 0)
    {
        try
        {
            childIdentity = ReadProcessIdentity(childPid);
            break;
        }
        catch (IOException)
        {
            // The detached shell may not yet be visible in procfs.
        }
    }

    await Task.Delay(10);
}

if (childPid <= 0 || childIdentity.Session == 0)
    return 70;

var parentPid = Environment.ProcessId;
var parentIdentity = ReadProcessIdentity(parentPid);
File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
{
    parentPid,
    parentSession = parentIdentity.Session,
    parentProcessGroup = parentIdentity.ProcessGroup,
    childPid,
    childSession = childIdentity.Session,
    processGroup = childIdentity.ProcessGroup
}));

using var ignoreTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal => signal.Cancel = true);
using var ignoreInterrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, signal => signal.Cancel = true);
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;

static (int ProcessGroup, int Session) ReadProcessIdentity(int processId)
{
    var stat = File.ReadAllText($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/stat");
    var closingParenthesis = stat.LastIndexOf(')');
    if (closingParenthesis < 0)
        throw new InvalidDataException("Invalid proc stat record.");

    var fields = stat[(closingParenthesis + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (fields.Length < 4)
        throw new InvalidDataException("Incomplete proc stat record.");

    var processGroup = int.Parse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture);
    var session = int.Parse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture);
    return (processGroup, session);
}
