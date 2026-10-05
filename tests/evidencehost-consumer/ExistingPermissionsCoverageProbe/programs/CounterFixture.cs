using System;
using System.IO;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private const int MaximumSessionBytes = 4096;
    private const int MaximumBasenameBytes = 255;
    // .NET 10 Unix IOException preserves raw EBUSY, not HRESULT_FROM_WIN32.
    private const int BusyErrnoHResult = 16;
    private const int ReadOnlyFileSystemErrnoHResult = 30;

    public static int Main(string[] args)
    {
        string operation = "arguments";
        try
        {
            if (!ValidateArguments(args))
            {
                return Fail("argument-rejected");
            }

            string session = args[0];
            string sibling = session + "-worker-rename-probe";
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "role-ready" }));
            Console.Out.Flush();
            operation = "continue";
            if (Console.ReadLine() != "continue")
            {
                return Fail("continue-not-received");
            }

            for (int i = 1; i < args.Length; i++)
            {
                string path = Path.Combine(session, args[i]);
                operation = "root-file-exists";
                if (!File.Exists(path))
                {
                    return Fail("selected-file-missing");
                }

                operation = "root-file-read";
                if (!Denied(() => { _ = File.ReadAllBytes(path); }))
                {
                    return Fail("required-file-denial-not-observed");
                }

                operation = "root-file-write";
                if (!Denied(() => { using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Write); }))
                {
                    return Fail("required-file-denial-not-observed");
                }

                operation = "root-file-delete";
                if (!Denied(() => File.Delete(path)))
                {
                    return Fail("required-file-denial-not-observed");
                }
            }

            operation = "session-rename";
            if (!RenameDenied(session, sibling))
            {
                return Fail("required-session-denial-not-observed");
            }

            // The controller selects this fixed assembly and independently pins its inode.
            // No reflective lookup or private Coverlet interface is used.
            operation = "assembly-write";
            string assemblyPath = Path.Combine(AppContext.BaseDirectory, "CounterFixture.dll");
            if (!File.Exists(assemblyPath)
                || !ReadOnlyAssemblyDenied(() => { using FileStream stream = File.Open(assemblyPath, FileMode.Open, FileAccess.Write); }))
            {
                return Fail("required-assembly-denial-not-observed");
            }

            operation = "calculate";
            int value = Calculate(1) + Calculate(-1);
            if (value != 3)
            {
                return Fail("calculation-mismatch");
            }

            Console.WriteLine(JsonSerializer.Serialize(new { stage = "completed", denials_passed = true, value }));
            Console.Out.Flush();
            return 0;
        }
        catch (Exception exception)
        {
            return Fail("probe-failure", operation, exception);
        }
    }

    public static int Calculate(int value)
    {
        if (value >= 0)
        {
            return value + 1;
        }

        return -value;
    }

    private static bool Denied(Action operation)
    {
        try
        {
            operation();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool RenameDenied(string session, string sibling)
    {
        if (!Directory.Exists(session) || Directory.Exists(sibling) || File.Exists(sibling))
        {
            return false;
        }

        bool denied;
        try
        {
            Directory.Move(session, sibling);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            denied = true;
        }
        catch (IOException exception) when (exception.HResult == BusyErrnoHResult)
        {
            // Exact raw Linux EBUSY from the .NET 10 Unix rename error path.
            denied = true;
        }

        // Same-path existence is checked here; actual mounted-inode identity is
        // checked separately by the root controller's retained descriptors.
        return denied && Directory.Exists(session)
            && !Directory.Exists(sibling) && !File.Exists(sibling);
    }

    private static bool ReadOnlyAssemblyDenied(Action operation)
    {
        try
        {
            operation();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException exception) when (exception.HResult == ReadOnlyFileSystemErrnoHResult)
        {
            // Only raw EROFS from .NET 10 Unix is accepted for this write probe.
            return true;
        }
    }

    private static bool ValidateArguments(string[] args)
    {
        if (args.Length != 4)
        {
            return false;
        }

        string session = args[0];
        if (session.Length == 0 || Encoding.UTF8.GetByteCount(session) > MaximumSessionBytes
            || session[0] != '/' || session.EndsWith("/", StringComparison.Ordinal)
            || session.Contains('\\') || session.Contains('\0') || session.Contains('\r') || session.Contains('\n')
            || !Path.IsPathFullyQualified(session)
            || !string.Equals(Path.GetFullPath(session), session, StringComparison.Ordinal))
        {
            return false;
        }

        string[] components = session.Split('/');
        for (int i = 1; i < components.Length; i++)
        {
            if (components[i].Length == 0 || components[i] is "." or "..")
            {
                return false;
            }
        }

        string? parent = Path.GetDirectoryName(session);
        if (string.IsNullOrEmpty(parent))
        {
            return false;
        }

        for (int i = 1; i < args.Length; i++)
        {
            string name = args[i];
            if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > MaximumBasenameBytes || name is "." or "..")
            {
                return false;
            }

            foreach (char character in name)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-')
                {
                    return false;
                }
            }

            for (int j = 1; j < i; j++)
            {
                if (string.Equals(name, args[j], StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int Fail(string category, string? operation = null, Exception? exception = null)
    {
        try
        {
            if (exception is null)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { stage = "failed", category }));
            }
            else
            {
                string exceptionClass = exception switch
                {
                    UnauthorizedAccessException => "unauthorized",
                    IOException => "io",
                    InvalidOperationException => "invalid-operation",
                    _ => "unknown",
                };
                int? nativeErrno = exception is IOException && exception.HResult is 1 or 13 or 16 or 18 or 30
                    ? exception.HResult : null;
                Console.WriteLine(JsonSerializer.Serialize(new { stage = "failed", category, operation, exception_class = exceptionClass, native_errno = nativeErrno }));
            }
            Console.Out.Flush();
        }
        catch (Exception)
        {
            // A broken output pipe cannot turn failure into success or raw error output.
        }

        return 1;
    }
}
