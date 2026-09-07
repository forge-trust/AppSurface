namespace ForgeTrust.AppSurface.Durable.AdoptionMetrics;

internal static class Program
{
    private static readonly string Usage = """
        ForgeTrust.AppSurface.Durable.AdoptionMetrics

        Verifies a pinned consumer checkout and measures the executable-contract
        adoption regions declared by an AppSurface evidence specification.

        Usage:
          dotnet run --project tools/ForgeTrust.AppSurface.Durable.AdoptionMetrics -- \
            --spec <path> --consumer-root <path> --output <path>

        Options:
          --spec <path>           Measurement specification. Required.
          --consumer-root <path>  Consumer repository checkout. Required.
          --output <path>         Deterministic JSON result. Required.
          -h, --help              Show this help.
        """;

    internal static async Task<int> Main(string[] args)
    {
        return await RunAsync(
            args,
            Console.Out,
            Console.Error,
            Directory.GetCurrentDirectory(),
            CancellationToken.None);
    }

    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter standardOut,
        TextWriter standardError,
        string currentDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOut);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        if (args.Length == 0)
        {
            await standardError.WriteLineAsync(Usage);
            return 1;
        }

        if (args.Any(IsHelp))
        {
            await standardOut.WriteLineAsync(Usage);
            return 0;
        }

        try
        {
            var options = AdoptionMetricsCommandOptions.Parse(args, currentDirectory);
            var result = await AdoptionMeasurementEngine.MeasureAsync(
                options.SpecPath,
                options.ConsumerRoot,
                options.RepositoryRoot,
                GitConsumerRevisionVerifier.Instance,
                cancellationToken);

            await AdoptionMeasurementWriter.WriteAsync(options.OutputPath, result, cancellationToken);
            await standardOut.WriteLineAsync(
                $"Measured {result.Regions.Count} regions at consumer commit {result.BaselineCommit}; overallPassed={result.OverallPassed.ToString().ToLowerInvariant()}.");
            return result.OverallPassed ? 0 : 1;
        }
        catch (AdoptionMeasurementException exception)
        {
            await standardError.WriteLineAsync($"Adoption measurement failed: {exception.Message}");
            return 1;
        }
    }

    private static bool IsHelp(string argument)
    {
        return string.Equals(argument, "--help", StringComparison.Ordinal)
            || string.Equals(argument, "-h", StringComparison.Ordinal);
    }
}

internal sealed record AdoptionMetricsCommandOptions(
    string SpecPath,
    string ConsumerRoot,
    string OutputPath,
    string RepositoryRoot)
{
    internal static AdoptionMetricsCommandOptions Parse(string[] args, string currentDirectory)
    {
        string? specPath = null;
        string? consumerRoot = null;
        string? outputPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--spec":
                    specPath = ReadRequiredValue(args, ref index, argument);
                    break;
                case "--consumer-root":
                    consumerRoot = ReadRequiredValue(args, ref index, argument);
                    break;
                case "--output":
                    outputPath = ReadRequiredValue(args, ref index, argument);
                    break;
                default:
                    throw new AdoptionMeasurementException($"Unknown option '{argument}'.");
            }
        }

        return new(
            ResolveRequiredPath(specPath, "--spec", currentDirectory),
            ResolveRequiredPath(consumerRoot, "--consumer-root", currentDirectory),
            ResolveRequiredPath(outputPath, "--output", currentDirectory),
            Path.GetFullPath(currentDirectory));
    }

    private static string ReadRequiredValue(string[] args, ref int index, string argument)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
        {
            throw new AdoptionMeasurementException($"Option '{argument}' requires a value.");
        }

        index++;
        return args[index];
    }

    private static string ResolveRequiredPath(string? path, string option, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new AdoptionMeasurementException($"Option '{option}' is required.");
        }

        return Path.GetFullPath(path, currentDirectory);
    }
}

internal sealed class AdoptionMeasurementException : Exception
{
    internal AdoptionMeasurementException(string message)
        : base(message)
    {
    }

    internal AdoptionMeasurementException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
