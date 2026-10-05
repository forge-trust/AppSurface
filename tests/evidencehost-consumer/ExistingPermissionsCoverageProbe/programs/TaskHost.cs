using System;
using System.Collections;
using System.IO;
using System.Text.Json;
using Coverlet.MSbuild.Tasks;
using Microsoft.Build.Framework;

internal static class Program
{
    public static int Main(string[] args)
    {
        var engine = new ProbeEngine();
        var prepare = new InstrumentationTask
        {
            BuildEngine = engine,
            Path = Path.GetFullPath(args[0]),
            Include = "[CounterFixture]*",
            IncludeTestAssembly = true,
            DisableManagedInstrumentationRestore = false,
        };
        if (!prepare.Execute())
        {
            return 65;
        }

        string state = prepare.InstrumenterState.ItemSpec;
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "prepared", state, temp = Path.GetTempPath() }));
        Console.Out.Flush();
        if (Console.ReadLine() != "collect")
        {
            return 66;
        }

        var collect = new CoverageResultTask
        {
            BuildEngine = engine,
            InstrumenterState = prepare.InstrumenterState,
            Output = Path.GetFullPath(args[1]),
            OutputFormat = "json,cobertura",
            Threshold = "0",
            ThresholdType = "line,branch,method",
            ThresholdStat = "total",
        };
        bool passed = collect.Execute();
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "collected", passed, errors = engine.Errors, warnings = engine.Warnings }));
        return passed && engine.Errors == 0 && engine.Warnings == 0 ? 0 : 67;
    }
}

internal sealed class ProbeEngine : IBuildEngine
{
    public int Errors { get; private set; }
    public int Warnings { get; private set; }
    public bool ContinueOnError => false;
    public int LineNumberOfTaskNode => 0;
    public int ColumnNumberOfTaskNode => 0;
    public string ProjectFileOfTaskNode => "ExistingPermissionsProcedureProbe";
    public void LogErrorEvent(BuildErrorEventArgs e) { Errors++; Console.Error.WriteLine(e.Message); }
    public void LogWarningEvent(BuildWarningEventArgs e) { Warnings++; Console.Error.WriteLine(e.Message); }
    public void LogMessageEvent(BuildMessageEventArgs e) { Console.Error.WriteLine(e.Message); }
    public void LogCustomEvent(CustomBuildEventArgs e) { Console.Error.WriteLine(e.Message); }
    public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => throw new NotSupportedException();
}
