using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;

/// <summary>Checks one publish phase for the repository's Linux MVC pilot.</summary>
/// <remarks>The opt-in MSBuild import owns this task; it is not a package API.</remarks>
public sealed class AppSurfacePublishPreflightTask : Microsoft.Build.Utilities.Task
{
    private readonly string _findExecutable;

    /// <summary>Creates the task with the GNU find executable on the current path.</summary>
    public AppSurfacePublishPreflightTask() : this("find")
    {
    }

    internal AppSurfacePublishPreflightTask(string findExecutable)
    {
        _findExecutable = findExecutable;
    }

    /// <summary>The required phase: paths before copying, or permissions after publish.</summary>
    [Required]
    public string Phase { get; set; } = "";

    /// <summary>Root used to display safe repository-relative source names.</summary>
    public string RepositoryDirectory { get; set; } = "";
    /// <summary>Fallback root for source names in a separate fixture.</summary>
    public string ProjectDirectory { get; set; } = "";
    /// <summary>Fresh SDK publish output root for the permissions phase.</summary>
    public string PublishDirectory { get; set; } = "";
    /// <summary>Prepared ordinary copy items carrying RelativePath metadata.</summary>
    public ITaskItem[] OrdinaryFiles { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>Prepared static asset copy items carrying TargetPath metadata.</summary>
    public ITaskItem[] StaticFiles { get; set; } = Array.Empty<ITaskItem>();

    /// <inheritdoc />
    public override bool Execute()
    {
        switch (Phase)
        {
            case "paths": return CheckPaths();
            case "permissions": return CheckPermissions();
            default: return Incomplete("Unknown preflight phase.");
        }
    }

    private bool CheckPaths()
    {
        if (OrdinaryFiles.Length == 0)
            return Incomplete("Path check: the SDK ordinary copy list is empty for the publish target.");

        var paths = new Dictionary<string, Destination>(StringComparer.OrdinalIgnoreCase);
        return AddItems(OrdinaryFiles, "RelativePath", paths) &&
            AddItems(StaticFiles, "TargetPath", paths);
    }

    private bool AddItems(ITaskItem[] items, string metadata, Dictionary<string, Destination> paths)
    {
        foreach (var item in items)
        {
            var destination = item.GetMetadata(metadata);
            var source = item.GetMetadata("FullPath");
            if (string.IsNullOrEmpty(source) || !TrySplit(destination, out var segments))
                return Incomplete("Path check: an SDK copy item has a missing source or invalid relative destination.");

            var prefix = "";
            for (var index = 0; index < segments.Length; index++)
            {
                prefix = index == 0 ? segments[index] : prefix + "/" + segments[index];
                var isFile = index == segments.Length - 1;
                if (!paths.TryGetValue(prefix, out var prior))
                {
                    paths.Add(prefix, new Destination(prefix, isFile, destination, source));
                    continue;
                }

                if (prior.IsFile == isFile && prior.Path == prefix &&
                    (!isFile || prior.Source == source))
                    continue;

                Error("ASPUB001", "Path check: conflicting publish destinations '" + Display(prior.Original) +
                    "' (" + DisplaySource(prior.Source) + ") and '" + Display(destination) +
                    "' (" + DisplaySource(source) + "). Choose distinct portable destination paths.");
                return false;
            }
        }

        return true;
    }

    private static bool TrySplit(string path, out string[] segments)
    {
        var normalized = path.Replace('\\', '/');
        segments = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.EndsWith("/", StringComparison.Ordinal) ||
            (normalized.Length >= 2 && normalized[1] == ':'))
            return false;

        foreach (var segment in segments)
        {
            if (segment == "." || segment == ".." || segment.IndexOf('\0') >= 0)
                return false;
        }

        return true;
    }

    private bool CheckPermissions()
    {
        if (string.IsNullOrEmpty(PublishDirectory))
            return Incomplete("Permissions check: the SDK did not provide an output directory.");

        try
        {
            // GNU find supplies lstat-based type and mode together. -P is explicit:
            // a symlink (including a dangling link) is never traversed.
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(PublishDirectory));
            var attributes = File.GetAttributes(directory);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                return Incomplete("Permissions check: the scratch output root must be a real directory.");

            var start = new ProcessStartInfo(_findExecutable)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-P");
            start.ArgumentList.Add(".");
            start.ArgumentList.Add("-printf");
            start.ArgumentList.Add(@"%y\0%m\0%P\0");
            using (var find = Process.Start(start))
            {
                if (find == null)
                    return Incomplete("Permissions check: could not start GNU find.");

                var stderr = find.StandardError.ReadToEndAsync();
                var rootSeen = false;
                var fileSeen = false;
                var complete = true;
                while (ReadField(find.StandardOutput) is string type)
                {
                    var mode = ReadField(find.StandardOutput);
                    var path = ReadField(find.StandardOutput);
                    if (mode == null || path == null)
                    {
                        complete = false;
                        break;
                    }

                    if (!rootSeen)
                    {
                        rootSeen = type == "d" && path.Length == 0;
                        complete &= rootSeen;
                    }

                    if (type == "d")
                        continue;
                    if (type != "f")
                    {
                        Incomplete("Permissions check: unexpected file type at '" + Display(path) + "'. Inspect the scratch output.");
                        continue;
                    }

                    fileSeen = true;
                    if (!TryParseMode(mode, out var bits))
                    {
                        complete = false;
                        continue;
                    }

                    const UnixFileMode unsafeModes = UnixFileMode.SetUser | UnixFileMode.SetGroup |
                        UnixFileMode.StickyBit | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
                    if (((UnixFileMode)bits & unsafeModes) != 0)
                        Error("ASPUB002", "Permissions check: '" + Display(path) + "' has mode " +
                            mode.PadLeft(4, '0') + ". Fix the source or build permissions and publish again.");
                }

                find.WaitForExit();
                if (find.ExitCode != 0 || stderr.GetAwaiter().GetResult().Length != 0 || !rootSeen || !fileSeen || !complete)
                    return Incomplete("Permissions check: the publish output could not be inspected completely.");
            }
        }
        catch (Exception exception) when (exception is IOException ||
            exception is UnauthorizedAccessException || exception is System.ComponentModel.Win32Exception)
        {
            return Incomplete("Permissions check: could not inspect the publish output with GNU find.");
        }

        return !Log.HasLoggedErrors;
    }

    private static string? ReadField(TextReader input)
    {
        var value = new StringBuilder();
        int character;
        while ((character = input.Read()) >= 0 && character != 0)
            value.Append((char)character);
        return character == 0 ? value.ToString() : null;
    }

    private static bool TryParseMode(string mode, out int bits)
    {
        bits = 0;
        if (mode.Length == 0 || mode.Length > 4)
            return false;
        foreach (var character in mode)
        {
            if (character < '0' || character > '7')
                return false;
            bits = (bits << 3) | (character - '0');
        }
        return true;
    }

    private string DisplaySource(string source)
    {
        foreach (var root in new[] { RepositoryDirectory, ProjectDirectory })
        {
            var relative = Path.GetRelativePath(root, source).Replace('\\', '/');
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal))
                return "'" + Display(relative) + "'";
        }
        return "SDK input outside the project";
    }

    private static string Display(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
            result.Append(char.IsControl(character) ? '?' : character);
        return result.ToString();
    }

    private bool Incomplete(string message)
    {
        Error("ASPUB003", message);
        return false;
    }

    private void Error(string code, string message) =>
        Log.LogError(null, code, null, null, 0, 0, 0, 0, message);

    private sealed class Destination
    {
        public Destination(string path, bool isFile, string original, string source)
        {
            Path = path;
            IsFile = isFile;
            Original = original;
            Source = source;
        }

        public string Path { get; }
        public bool IsFile { get; }
        public string Original { get; }
        public string Source { get; }
    }
}
