using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

if (args is ["--descendant", var armedMarker, var signalMarker])
{
    using var signal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        try { File.WriteAllText(signalMarker, "SIGTERM handler ran\n"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    });
    await File.WriteAllTextAsync(armedMarker,
        Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

if (args.Length != 4 || args[0] != "--socket" || args[2] != "--case" ||
    args[3] != "normal")
{
    throw new ArgumentException("Expected the closed AppHost-selected socket and resource case.");
}

var socket = Path.GetFullPath(args[1]);
#if C3Readiness
var control = "readiness-failure";
#elif C3StuckDescendant
var control = "stuck-descendant";
#else
var control = "normal";
#endif
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(socket));
await using var app = builder.Build();

var toolRoot = Environment.GetEnvironmentVariable("PROOF_PROTECTED_TOOLS")
    ?? throw new InvalidOperationException("Missing root-selected protected tools.");
var outputRoot = Environment.GetEnvironmentVariable("PROOF_PROTECTED_OUTPUT")
    ?? throw new InvalidOperationException("Missing root-selected protected output.");
var allowedInput = Environment.GetEnvironmentVariable("PROOF_ALLOWED_INPUT")
    ?? throw new InvalidOperationException("Missing root-selected declared input.");

// Each operation is attempted separately against the root-staged managed CLI PE and private output root.
var deniedToolOpen = ToolOpenDenied(Path.Combine(toolRoot, "protected-tool.dll"));
var deniedToolLoad = ToolLoadDenied(Path.Combine(toolRoot, "protected-tool.dll"), deniedToolOpen);
var deniedCreateNew = OutputCreateDenied(Path.Combine(outputRoot,
    "native-resource-output-probe"));
var declaredInputMatches = File.ReadAllBytes(allowedInput)
    .AsSpan().SequenceEqual("declared-native-input\n"u8);

app.MapGet("/health", async (HttpContext context) =>
{
    if (control == "stuck-descendant")
    {
        // This private control must remain pending until the independent root observer has
        // authenticated the installed signal handler's descendant PID. A transient 503 would
        // race the real owner's deliberately strict one-attempt readiness validation.
        var verified = Path.Combine(Path.GetDirectoryName(socket)!, "descendant-verified");
        while (!File.Exists(verified))
            await Task.Delay(10, context.RequestAborted);
    }
    return control == "readiness-failure" || !deniedToolOpen || !deniedToolLoad
        || !deniedCreateNew || !declaredInputMatches
        ? Results.StatusCode(503)
        : Results.Text("native-http-ready", "text/plain");
});

if (control == "stuck-descendant")
{
    var scratch = Path.GetDirectoryName(socket)!;
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    start.ArgumentList.Add(typeof(Program).Assembly.Location);
    start.ArgumentList.Add("--descendant");
    start.ArgumentList.Add(Path.Combine(scratch, "descendant-armed"));
    start.ArgumentList.Add(Path.Combine(scratch, "descendant-term"));
    using var descendant = Process.Start(start)
        ?? throw new InvalidOperationException("The stuck-descendant control did not start its child.");
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

static bool ToolLoadDenied(string path, bool independentlyDeniedOpen)
{
    try
    {
        _ = Assembly.LoadFile(path);
        return false;
    }
    catch (UnauthorizedAccessException)
    {
        return true;
    }
    catch (SecurityException)
    {
        return true;
    }
    catch (FileNotFoundException) when (independentlyDeniedOpen)
    {
        // Assembly.LoadFile hides an inaccessible mount as missing. The C# root independently
        // requires this exact real managed PE before start/readiness/join, and File.OpenRead
        // must already have returned access denial. A genuinely missing fixture never passes root.
        return true;
    }
    catch (FileLoadException exception) when (exception.HResult == unchecked((int)0x80070005)
        || exception.InnerException is UnauthorizedAccessException or SecurityException)
    {
        return true;
    }
}

static bool OutputCreateDenied(string path)
{
    try
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return false;
    }
    catch (UnauthorizedAccessException)
    {
        return true;
    }
}
