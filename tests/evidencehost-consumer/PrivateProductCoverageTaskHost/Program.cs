using System;
using System.Collections;
using System.IO;
using System.Text;
using System.Text.Json;
using Coverlet.MSbuild.Tasks;
using Microsoft.Build.Framework;

internal static class Program
{
    private const int MaximumPathBytes = 4096;
    private const int MaximumPacketBytes = 16 * 1024;
    private const string IncludeFilter =
        "[ForgeTrust.AppSurface.Evidence.Cli]*," +
        "[ForgeTrust.AppSurface.Evidence.Aspire]*," +
        "[ForgeTrust.AppSurface.Evidence.Coverage]*";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static int Main(string[] args)
    {
        try
        {
            if (!ValidateInputs(args))
            {
                return Fail("invalid-input", 64);
            }

            var engine = new PrivateTaskEngine();
            var prepare = new InstrumentationTask
            {
                BuildEngine = engine,
                Path = args[0],
                Include = IncludeFilter,
                IncludeTestAssembly = false,
                DisableManagedInstrumentationRestore = false
            };
            if (!prepare.Execute() || engine.Errors != 0 || engine.Warnings != 0 || engine.LogBoundExceeded)
            {
                return Fail("preparation-failed", 65);
            }

            string? state = prepare.InstrumenterState?.ItemSpec;
            string temporary = System.IO.Path.GetTempPath();
            if (!IsCanonicalPath(state) || !File.Exists(state) || !IsTemporaryDirectory(temporary))
            {
                return Fail("invalid-preparation-output", 65);
            }

            WritePacket(new { stage = "prepared", state, temp = temporary });
            if (!ReadCollectAcknowledgment())
            {
                return Fail("invalid-collect-acknowledgment", 66);
            }

            // The official task's actual state item remains in this living host.
            var collect = new CoverageResultTask
            {
                BuildEngine = engine,
                InstrumenterState = prepare.InstrumenterState,
                Output = args[1],
                OutputFormat = "json,cobertura",
                Threshold = "0",
                ThresholdType = "line,branch,method",
                ThresholdStat = "total"
            };
            bool passed = collect.Execute();
            WritePacket(new
            {
                stage = "collected",
                passed,
                errors = engine.Errors,
                warnings = engine.Warnings
            });

            return passed && engine.Errors == 0 && engine.Warnings == 0 && !engine.LogBoundExceeded ? 0 : 67;
        }
        catch (Exception)
        {
            return Fail("taskhost-failed", 68);
        }
    }

    private static bool ValidateInputs(string[] args)
    {
        if (args.Length != 2 || !IsCanonicalPath(args[0]) || !IsCanonicalPath(args[1]) ||
            System.IO.Path.GetFileName(args[0]) != "ForgeTrust.AppSurface.Cli.dll" ||
            System.IO.Path.GetFileName(args[1]) != "coverage" || !File.Exists(args[0]))
        {
            return false;
        }

        string? tool = System.IO.Path.GetDirectoryName(args[0]);
        string? reports = System.IO.Path.GetDirectoryName(args[1]);
        if (tool is null || reports is null || !Directory.Exists(tool) || !Directory.Exists(reports))
        {
            return false;
        }

        foreach (string library in new[] { "Cli", "Aspire", "Coverage" })
        {
            foreach (string extension in new[] { "dll", "pdb" })
            {
                string path = System.IO.Path.Combine(tool, $"ForgeTrust.AppSurface.Evidence.{library}.{extension}");
                if (!IsCanonicalPath(path) || !File.Exists(path))
                {
                    return false;
                }
            }
        }

        foreach (string suffix in new[] { "", ".json", ".cobertura.xml" })
        {
            string output = args[1] + suffix;
            if (!IsCanonicalPath(output) || File.Exists(output) || Directory.Exists(output))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCanonicalPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaximumPathBytes || path[0] != '/' ||
            path[^1] == '/' || StrictUtf8.GetByteCount(path) > MaximumPathBytes || path.Contains('\\'))
        {
            return false;
        }

        foreach (char character in path)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        string[] components = path.Split('/');
        for (int index = 1; index < components.Length; index++)
        {
            if (components[index].Length == 0 || components[index] is "." or "..")
            {
                return false;
            }
        }

        return string.Equals(System.IO.Path.GetFullPath(path), path, StringComparison.Ordinal);
    }

    private static bool IsTemporaryDirectory(string path)
    {
        if (path.Length == 0 || path.Length > MaximumPathBytes || StrictUtf8.GetByteCount(path) > MaximumPathBytes)
        {
            return false;
        }

        // GetTempPath normally includes one trailing separator on Unix.
        string directory = path.EndsWith('/') ? path[..^1] : path;
        return IsCanonicalPath(directory) && Directory.Exists(directory);
    }

    private static bool ReadCollectAcknowledgment()
    {
        // No unbounded ReadLine and no state or callback supplied by stdin.
        foreach (char expected in "collect\n")
        {
            if (Console.In.Read() != expected)
            {
                return false;
            }
        }

        return true;
    }

    private static void WritePacket<T>(T packet)
    {
        string json = JsonSerializer.Serialize(packet);
        if (StrictUtf8.GetByteCount(json) + 1 > MaximumPacketBytes)
        {
            throw new InvalidOperationException();
        }

        Console.Out.Write(json);
        Console.Out.Write('\n');
        Console.Out.Flush();
    }

    private static int Fail(string category, int exitCode)
    {
        WritePacket(new { stage = "failed", category });
        return exitCode;
    }
}

internal sealed class PrivateTaskEngine : IBuildEngine
{
    private const int MaximumMessageBytes = 4096;
    private const int MaximumLogBytes = 128 * 1024;
    private const int MaximumEventCount = 1_000_000;
    private readonly object _gate = new();
    private int _errors;
    private int _warnings;
    private int _logBytes;
    private bool _logBoundExceeded;

    public int Errors { get { lock (_gate) { return _errors; } } }
    public int Warnings { get { lock (_gate) { return _warnings; } } }
    public bool LogBoundExceeded { get { lock (_gate) { return _logBoundExceeded; } } }
    public bool ContinueOnError => false;
    public int LineNumberOfTaskNode => 0;
    public int ColumnNumberOfTaskNode => 0;
    public string ProjectFileOfTaskNode => "PrivateProductCoverageTaskHost";

    public void LogErrorEvent(BuildErrorEventArgs e)
    {
        lock (_gate)
        {
            Count(ref _errors);
            WriteMessage(e.Message);
        }
    }

    public void LogWarningEvent(BuildWarningEventArgs e)
    {
        lock (_gate)
        {
            Count(ref _warnings);
            WriteMessage(e.Message);
        }
    }

    public void LogMessageEvent(BuildMessageEventArgs e)
    {
        lock (_gate) { WriteMessage(e.Message); }
    }

    public void LogCustomEvent(CustomBuildEventArgs e)
    {
        lock (_gate) { WriteMessage(e.Message); }
    }

    public bool BuildProjectFile(string projectFileName, string[] targetNames,
        IDictionary globalProperties, IDictionary targetOutputs) => throw new NotSupportedException();

    private void Count(ref int counter)
    {
        if (counter == MaximumEventCount)
        {
            _logBoundExceeded = true;
        }
        else
        {
            counter++;
        }
    }

    private void WriteMessage(string? message)
    {
        message ??= string.Empty;
        int bytes = Encoding.UTF8.GetByteCount(message);
        if (bytes >= MaximumMessageBytes || bytes + 1 > MaximumLogBytes - _logBytes)
        {
            _logBoundExceeded = true;
            return;
        }

        _logBytes += bytes + 1;
        Console.Error.Write(message);
        Console.Error.Write('\n');
        Console.Error.Flush();
    }
}
