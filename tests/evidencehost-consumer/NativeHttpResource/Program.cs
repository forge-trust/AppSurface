using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;

if (args is ["--descendant", var marker])
{
    using var signal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true);
    await File.WriteAllTextAsync(marker, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

if (args.Length != 4 || args[0] != "--socket" || args[2] != "--case" ||
    args[3] is not ("normal" or "readiness-failure" or "cancel" or "stuck-descendant"))
{
    throw new ArgumentException("Expected the controller-selected socket and closed control case.");
}

var socket = Path.GetFullPath(args[1]);
var control = args[3];
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(socket));
await using var app = builder.Build();
var toolRoot = Environment.GetEnvironmentVariable("PROOF_PROTECTED_TOOLS") ?? throw new InvalidOperationException("Missing protected tools.");
var outputRoot = Environment.GetEnvironmentVariable("PROOF_PROTECTED_OUTPUT") ?? throw new InvalidOperationException("Missing protected output.");
var tool = Path.Combine(toolRoot, "protected-tool.dll");
var deniedToolRead = ToolOpenDenied(tool);
var deniedToolLoad = ToolLoadDenied(tool);
var deniedTools = deniedToolRead && deniedToolLoad;
var deniedOutput = OutputCreateDenied(Path.Combine(outputRoot, "native-resource-output-probe"));
var allowedInput = Environment.GetEnvironmentVariable("PROOF_ALLOWED_INPUT") ?? throw new InvalidOperationException("Missing declared input.");
var inputPassed = File.ReadAllText(allowedInput) == "declared-native-input\n";
app.MapGet("/health", () => control == "readiness-failure" || !deniedTools || !deniedOutput || !inputPassed
    ? Results.StatusCode(503)
    : Results.Text("native-http-ready", "text/plain"));
if (control == "stuck-descendant")
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    start.ArgumentList.Add(typeof(Program).Assembly.Location);
    start.ArgumentList.Add("--descendant");
    start.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(socket)!, "descendant-armed"));
    using var descendant = Process.Start(start) ?? throw new InvalidOperationException("Descendant did not start.");
}

await app.RunAsync();

static bool ToolOpenDenied(string path)
{
    try
    {
        using var stream = File.OpenRead(path);
        return false;
    }
    catch (UnauthorizedAccessException)
    {
        return true;
    }
}

static bool ToolLoadDenied(string path)
{
    try
    {
        Assembly.LoadFile(path);
        return false;
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or FileNotFoundException ||
        exception.HResult == unchecked((int)0x80070005) || exception.InnerException is UnauthorizedAccessException)
    {
        // The root controller confirms this real managed file exists. A masked file is inaccessible.
        return true;
    }
}

static bool OutputCreateDenied(string path)
{
    try
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        return false;
    }
    catch (UnauthorizedAccessException)
    {
        return true;
    }
}
