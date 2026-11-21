using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

internal sealed class PostgreSqlFixtureTimingCsv : IAsyncDisposable
{
    private static readonly HashSet<string> SupportedPhases = new(StringComparer.Ordinal)
    {
        "database_setup",
        "database_teardown",
        "template_prepare",
        "template_teardown"
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StreamWriter _writer;
    private readonly string _displayPath;
    private bool _disposed;

    private PostgreSqlFixtureTimingCsv(StreamWriter writer, string displayPath)
    {
        _writer = writer;
        _displayPath = displayPath;
    }

    internal static PostgreSqlFixtureTimingCsv Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var displayPath = GetDisplayPath(path);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            stream = null;
            try
            {
                writer.WriteLine("phase,elapsed_ms");
                writer.Flush();
            }
            catch
            {
                writer.Dispose();
                throw;
            }

            return new PostgreSqlFixtureTimingCsv(writer, displayPath);
        }
        catch (Exception exception)
        {
            throw new PostgreSqlFixtureException(
                $"Cannot create fixture timing CSV '{displayPath}'. {PostgreSqlFixtureException.Describe(exception)}");
        }
        finally
        {
            stream?.Dispose();
        }
    }

    internal void Record(string phase, TimeSpan elapsed)
    {
        if (!SupportedPhases.Contains(phase))
        {
            throw new PostgreSqlFixtureException("The fixture timing phase is unsupported.");
        }

        if (elapsed < TimeSpan.Zero)
        {
            throw new PostgreSqlFixtureException("Fixture timing durations cannot be negative.");
        }

        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer.Write(phase);
            _writer.Write(',');
            _writer.WriteLine(elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture));
            _writer.Flush();
        }
        catch (Exception exception) when (exception is not PostgreSqlFixtureException)
        {
            throw new PostgreSqlFixtureException(
                $"Cannot write fixture timing CSV '{_displayPath}'. {PostgreSqlFixtureException.Describe(exception)}");
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void RecordElapsed(string phase, long startedTimestamp) =>
        Record(phase, Stopwatch.GetElapsedTime(startedTimestamp));

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                await _writer.DisposeAsync();
            }
            catch (Exception exception)
            {
                throw new PostgreSqlFixtureException(
                    $"Cannot finish fixture timing CSV '{_displayPath}'. {PostgreSqlFixtureException.Describe(exception)}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string GetDisplayPath(string path)
    {
        // Preserve a relative results path when possible. A malformed path may itself contain a connection string.
        try
        {
            var relative = Path.GetRelativePath(Environment.CurrentDirectory, Path.GetFullPath(path));
            var display = Path.IsPathRooted(relative) || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                ? Path.GetFileName(relative)
                : relative;
            if (display.Length is > 0 and <= 240 && display.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '/' or '\\'))
            {
                return display;
            }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            // The file operation provides the safe exception category in the diagnostic.
        }

        return "<requested timing file>";
    }
}
