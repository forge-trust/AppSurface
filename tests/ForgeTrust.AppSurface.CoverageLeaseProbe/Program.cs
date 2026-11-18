using System.Text;
using ForgeTrust.AppSurface.Evidence.Coverage;

if (args.Length > 0 && args[0] == "process")
{
    return await RunProcessProbeAsync(args[1..]);
}

if (args.Length is not (3 or 4) || args[0] is not ("hold" or "try") || args[2] is not ("shared" or "exclusive"))
{
    Console.Error.WriteLine("Usage: <hold|try> <existing-output-directory> <shared|exclusive> [private-candidate-lock-domain]");
    return 64;
}

var exclusive = args[2] == "exclusive";
var candidateDomain = args.Length == 4 ? args[3] : null;
if (args[0] == "try")
{
    try
    {
        using var lease = CoverageArtifactOperationLease.Acquire(args[1], exclusive, windowsLockRoot: candidateDomain);
        Console.WriteLine("acquired");
        return 0;
    }
    catch (CoverageExecutionException)
    {
        Console.WriteLine("busy");
        return 2;
    }
}

using (CoverageArtifactOperationLease.Acquire(args[1], exclusive, windowsLockRoot: candidateDomain))
{
    Console.WriteLine("acquired");
    Console.Out.Flush();
    _ = await Console.In.ReadLineAsync();
}

return 0;

static async Task<int> RunProcessProbeAsync(string[] processArgs)
{
    const int maximumOutputBytes = 16 * 1024 * 1024;
    if (processArgs.Length == 0)
    {
        Console.Error.WriteLine("A process probe mode is required.");
        return 64;
    }

    switch (processArgs[0])
    {
        case "echo-env" when processArgs.Length == 3:
            {
                var prefix = string.Concat(
                    Environment.GetEnvironmentVariable(processArgs[1]) ?? "unset",
                    "|",
                    Environment.GetEnvironmentVariable(processArgs[2]) ?? "unset",
                    "|");
                var output = Console.OpenStandardOutput();
                await output.WriteAsync(Encoding.UTF8.GetBytes(prefix));
                await Console.OpenStandardInput().CopyToAsync(output);
                await output.FlushAsync();
                return 0;
            }
        case "streams" when processArgs.Length == 2 && TryReadOutputCount(processArgs[1], maximumOutputBytes, out var streamBytes):
            await Task.WhenAll(
                WritePatternAsync(Console.OpenStandardOutput(), (byte)'o', streamBytes),
                WritePatternAsync(Console.OpenStandardError(), (byte)'e', streamBytes));
            return 0;
        case "stdout" when processArgs.Length == 2 && TryReadOutputCount(processArgs[1], maximumOutputBytes, out var standardOutputBytes):
            await WritePatternAsync(Console.OpenStandardOutput(), (byte)'x', standardOutputBytes);
            return 0;
        case "flood" when processArgs.Length == 1:
            await WritePatternAsync(Console.OpenStandardOutput(), (byte)'f', maximumOutputBytes);
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        case "invalid-utf8" when processArgs.Length == 1:
            await Console.OpenStandardOutput().WriteAsync(new byte[] { 0xFF });
            return 0;
        case "replacement" when processArgs.Length == 1:
            await Console.OpenStandardOutput().WriteAsync(new byte[] { 0xEF, 0xBF, 0xBD });
            return 0;
        case "hold" when processArgs.Length == 2:
            await File.WriteAllTextAsync(processArgs[1], "ready");
            await Console.OpenStandardOutput().WriteAsync("ready"u8.ToArray());
            await Console.OpenStandardOutput().FlushAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        default:
            Console.Error.WriteLine("Unsupported process probe mode or arguments.");
            return 64;
    }
}

static bool TryReadOutputCount(string value, int maximum, out int count)
    => int.TryParse(value, out count) && count >= 0 && count <= maximum;

static async Task WritePatternAsync(Stream stream, byte value, int byteCount)
{
    var buffer = new byte[Math.Min(8192, Math.Max(1, byteCount))];
    Array.Fill(buffer, value);
    var remaining = byteCount;
    while (remaining > 0)
    {
        var writeCount = Math.Min(buffer.Length, remaining);
        await stream.WriteAsync(buffer.AsMemory(0, writeCount));
        remaining -= writeCount;
    }

    await stream.FlushAsync();
}
