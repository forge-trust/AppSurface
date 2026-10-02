using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Compares protected-base policy and fixture inputs with candidate inputs without creating an Evidence claim.
/// </summary>
/// <remarks>
/// Every input is read under a fixed byte limit and deserialized with the Evidence contract serializer. A missing
/// candidate fixture file is treated as an empty candidate set, while a missing base fixture file is an error. The
/// result is written to a new JSON file and is always marked <c>claimEligible=false</c>.
/// </remarks>
[Command("evidence shadow-policy", Description = "Compare base and candidate evidence policies without making a claim.")]
internal sealed partial class EvidencePolicyShadowCommand : ICommand
{
    private const int MaximumInputBytes = 1_048_576;
    private const int MaximumOutputBytes = 1_048_576;
    private const string OutputFileName = "evidence-policy-shadow.json";

    private static readonly JsonSerializerOptions OutputJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Gets or sets the policy from the protected base checkout.</summary>
    [CommandOption("base-policy", Description = "Evidence policy loaded from the protected base checkout.")]
    public string BasePolicyPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the candidate policy under review.</summary>
    [CommandOption("candidate-policy", Description = "Candidate Evidence policy JSON path.")]
    public string CandidatePolicyPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the base-owned fixture set from the protected base checkout.</summary>
    [CommandOption("base-fixtures", Description = "Fixture array loaded from the protected base checkout.")]
    public string BaseFixturesPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the candidate fixture set; a specified but missing file means an empty candidate set.</summary>
    [CommandOption("candidate-fixtures", Description = "Candidate fixture array path; a missing file is treated as empty.")]
    public string CandidateFixturesPath { get; set; } = string.Empty;

    /// <summary>Gets or sets a new JSON file path or an output directory.</summary>
    [CommandOption("output", Description = "New JSON file path or directory for the bounded shadow result.")]
    public string OutputPath { get; set; } = string.Empty;

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        var cancellationToken = console.RegisterCancellationHandler();
        EvidencePolicyShadowResult result;
        try
        {
            result = await CompareAsync(cancellationToken);
        }
        catch (ShadowInputException exception)
        {
            result = CreateFailureResult(exception.Code, exception.FixtureSource, exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NullReferenceException)
        {
            result = CreateFailureResult(
                "ASEPSCLI004",
                EvidencePolicyShadowFixtureSource.Candidate,
                "Policy-shadow input could not be validated; no comparison was performed.");
        }

        byte[] outputBytes;
        try
        {
            outputBytes = SerializeBounded(result);
        }
        catch (ShadowResultTooLargeException)
        {
            result = CreateFailureResult(
                "ASEPSCLI005",
                EvidencePolicyShadowFixtureSource.Candidate,
                $"The shadow result exceeds the {MaximumOutputBytes}-byte output limit; the full result was omitted.");
            outputBytes = SerializeBounded(result);
        }

        try
        {
            await WriteNewOutputAsync(OutputPath, outputBytes, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or SecurityException)
        {
            throw new CommandException("ASEPSCLI006: The bounded shadow result could not be written to a new output target.");
        }

        if (!result.IsCompatible)
        {
            var codes = result.Diagnostics
                .Select(static diagnostic => diagnostic.Code)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static code => code, StringComparer.Ordinal);
            var codeList = string.Join(", ", codes);
            throw new CommandException(
                $"ASEPSCLI010: Evidence policy shadow is incompatible; claimEligible=false; diagnostics={codeList}. Inspect the bounded JSON result.");
        }

        await console.Output.WriteLineAsync(
            $"Evidence policy shadow compatible: {result.Selections.Count} selections; claimEligible=false.");
    }

    private async Task<EvidencePolicyShadowResult> CompareAsync(CancellationToken cancellationToken)
    {
        var basePolicyBytes = await ReadBoundedInputAsync(
            BasePolicyPath,
            "--base-policy",
            EvidencePolicyShadowFixtureSource.Base,
            allowMissing: false,
            cancellationToken);
        var candidatePolicyBytes = await ReadBoundedInputAsync(
            CandidatePolicyPath,
            "--candidate-policy",
            EvidencePolicyShadowFixtureSource.Candidate,
            allowMissing: false,
            cancellationToken);
        var baseFixtureBytes = await ReadBoundedInputAsync(
            BaseFixturesPath,
            "--base-fixtures",
            EvidencePolicyShadowFixtureSource.Base,
            allowMissing: false,
            cancellationToken);
        var candidateFixtureBytes = await ReadBoundedInputAsync(
            CandidateFixturesPath,
            "--candidate-fixtures",
            EvidencePolicyShadowFixtureSource.Candidate,
            allowMissing: true,
            cancellationToken);

        var basePolicy = DeserializeInput<EvidencePolicy>(
            basePolicyBytes!,
            "--base-policy",
            EvidencePolicyShadowFixtureSource.Base);
        var candidatePolicy = DeserializeInput<EvidencePolicy>(
            candidatePolicyBytes!,
            "--candidate-policy",
            EvidencePolicyShadowFixtureSource.Candidate);
        var baseFixtures = DeserializeInput<EvidencePolicyShadowFixture[]>(
            baseFixtureBytes!,
            "--base-fixtures",
            EvidencePolicyShadowFixtureSource.Base);
        EvidencePolicyShadowFixture[] candidateFixtures = candidateFixtureBytes is null
            ? []
            : DeserializeInput<EvidencePolicyShadowFixture[]>(
                candidateFixtureBytes,
                "--candidate-fixtures",
                EvidencePolicyShadowFixtureSource.Candidate);

        return EvidencePolicyShadowValidator.Validate(basePolicy, candidatePolicy, baseFixtures, candidateFixtures);
    }

    private static async Task<byte[]?> ReadBoundedInputAsync(
        string path,
        string optionName,
        EvidencePolicyShadowFixtureSource source,
        bool allowMissing,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ShadowInputException(
                "ASEPSCLI001",
                source,
                $"{optionName} must name an input file; no comparison was performed.");
        }

        try
        {
            await using var input = new FileStream(
                Path.GetFullPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length > MaximumInputBytes)
            {
                throw new ShadowInputException(
                    "ASEPSCLI003",
                    source,
                    $"{optionName} exceeds the {MaximumInputBytes}-byte input limit; no comparison was performed.");
            }

            using var buffer = new MemoryStream((int)Math.Min(input.Length, MaximumInputBytes));
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var count = await input.ReadAsync(chunk.AsMemory(), cancellationToken);
                if (count == 0)
                {
                    break;
                }

                if (buffer.Length + count > MaximumInputBytes)
                {
                    throw new ShadowInputException(
                        "ASEPSCLI003",
                        source,
                        $"{optionName} exceeds the {MaximumInputBytes}-byte input limit; no comparison was performed.");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
            }

            return buffer.ToArray();
        }
        catch (FileNotFoundException) when (allowMissing)
        {
            return null;
        }
        catch (DirectoryNotFoundException) when (allowMissing)
        {
            return null;
        }
        catch (ShadowInputException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or SecurityException)
        {
            throw new ShadowInputException(
                "ASEPSCLI001",
                source,
                $"{optionName} could not be read; no comparison was performed.");
        }
    }

    private static TValue DeserializeInput<TValue>(
        byte[] bytes,
        string optionName,
        EvidencePolicyShadowFixtureSource source)
    {
        try
        {
            return EvidenceCanonicalJson.Deserialize<TValue>(bytes);
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException)
        {
            throw new ShadowInputException(
                "ASEPSCLI002",
                source,
                $"{optionName} is malformed Evidence JSON; no comparison was performed.");
        }
    }

    private static EvidencePolicyShadowResult CreateFailureResult(
        string code,
        EvidencePolicyShadowFixtureSource source,
        string message) =>
        new(
            IsCompatible: false,
            Selections: [],
            Diagnostics: [new EvidencePolicyShadowDiagnostic(code, source, null, null, null, message)],
            DiagnosticsTruncated: false);

    private static byte[] SerializeBounded(EvidencePolicyShadowResult result)
    {
        var contents = JsonSerializer.SerializeToUtf8Bytes(new ShadowOutput(false, result), OutputJsonOptions);
        if (contents.Length > MaximumOutputBytes)
        {
            throw new ShadowResultTooLargeException();
        }

        return contents;
    }

    private static async Task WriteNewOutputAsync(
        string outputPath,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("The output target is required.", nameof(outputPath));
        }

        var fullPath = Path.GetFullPath(outputPath);
        var resultPath = Directory.Exists(fullPath)
            ? Path.Join(fullPath, OutputFileName)
            : string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : Path.Join(fullPath, OutputFileName);
        var outputDirectory = Path.GetDirectoryName(resultPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("The output target has no parent directory.", nameof(outputPath));
        }

        Directory.CreateDirectory(outputDirectory);
        await using var output = new FileStream(
            resultPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(contents.AsMemory(), cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private sealed record ShadowOutput(bool ClaimEligible, EvidencePolicyShadowResult Result);

    private sealed class ShadowInputException(
        string code,
        EvidencePolicyShadowFixtureSource fixtureSource,
        string message)
        : Exception(message)
    {
        public string Code { get; } = code;

        public EvidencePolicyShadowFixtureSource FixtureSource { get; } = fixtureSource;
    }

    private sealed class ShadowResultTooLargeException : Exception
    {
    }

}
