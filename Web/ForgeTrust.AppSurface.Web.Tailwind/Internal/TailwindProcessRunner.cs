using System.ComponentModel;
using System.Text;
using CliWrap;
using CliWrap.Exceptions;

namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Executes Tailwind CLI processes with bounded output capture.
/// </summary>
internal static class TailwindProcessRunner
{
    public static async Task<TailwindCommandResult> ExecuteAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        Action<string>? stdoutLine,
        Action<string, TailwindOutputLevel>? stderrLine,
        int captureLimit,
        CancellationToken cancellationToken)
    {
        var stdout = new BoundedOutputBuffer(captureLimit);
        var stderr = new BoundedOutputBuffer(captureLimit);

        var command = Cli.Wrap(fileName)
            .WithArguments(args)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.Create(async (stream, targetCancellationToken) =>
            {
                await DrainAsync(
                    stream,
                    stdout,
                    line => stdoutLine?.Invoke(line),
                    targetCancellationToken);
            }))
            .WithStandardErrorPipe(PipeTarget.Create(async (stream, targetCancellationToken) =>
            {
                await DrainAsync(
                    stream,
                    stderr,
                    line => stderrLine?.Invoke(line, TailwindStderrClassifier.Classify(line)),
                    targetCancellationToken);
            }));

        try
        {
            var result = await command.ExecuteAsync(cancellationToken);
            return new TailwindCommandResult(result.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is CliWrapException or Win32Exception)
        {
            throw new TailwindProcessStartException(fileName, ex);
        }
    }

    private static async Task DrainAsync(
        Stream stream,
        BoundedOutputBuffer capture,
        Action<string> lineHandler,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Console.OutputEncoding,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);

        var currentLine = new StringBuilder();
        var buffer = new char[1024];
        var previousWasCarriageReturn = false;

        while (true)
        {
            var charsRead = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (charsRead == 0)
            {
                break;
            }

            for (var i = 0; i < charsRead; i++)
            {
                var current = buffer[i];
                capture.Append(current);

                if (current == '\r')
                {
                    lineHandler(currentLine.ToString());
                    currentLine.Clear();
                    previousWasCarriageReturn = true;
                    continue;
                }

                if (current == '\n')
                {
                    if (previousWasCarriageReturn)
                    {
                        previousWasCarriageReturn = false;
                        continue;
                    }

                    lineHandler(currentLine.ToString());
                    currentLine.Clear();
                    continue;
                }

                previousWasCarriageReturn = false;
                currentLine.Append(current);
            }
        }

        if (!previousWasCarriageReturn && currentLine.Length > 0)
        {
            lineHandler(currentLine.ToString());
        }
    }

    private sealed class BoundedOutputBuffer
    {
        private readonly int _limit;
        private readonly Queue<char> _characters = new();

        public BoundedOutputBuffer(int limit)
        {
            _limit = Math.Max(0, limit);
        }

        public void Append(char value)
        {
            if (_limit == 0)
            {
                return;
            }

            _characters.Enqueue(value);
            while (_characters.Count > _limit)
            {
                _characters.Dequeue();
            }
        }

        public override string ToString()
        {
            return _characters.Count == 0 ? string.Empty : new string(_characters.ToArray());
        }
    }
}
