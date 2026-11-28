using System.Collections;
using System.Collections.Concurrent;
using Microsoft.Build.Framework;

namespace ForgeTrust.AppSurface.Web.Tailwind.Tests;

internal sealed class RecordingBuildEngine : IBuildEngine
{
    public ConcurrentQueue<BuildErrorEventArgs> Errors { get; } = new();

    public ConcurrentQueue<BuildWarningEventArgs> Warnings { get; } = new();

    // The process runner drains stdout and stderr concurrently.
    public ConcurrentQueue<BuildMessageEventArgs> Messages { get; } = new();

    public bool ContinueOnError => false;

    public int LineNumberOfTaskNode => 0;

    public int ColumnNumberOfTaskNode => 0;

    public string ProjectFileOfTaskNode => string.Empty;

    public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Enqueue(e);

    public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Enqueue(e);

    public void LogMessageEvent(BuildMessageEventArgs e) => Messages.Enqueue(e);

    public void LogCustomEvent(CustomBuildEventArgs e)
    {
    }

    public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => true;
}
