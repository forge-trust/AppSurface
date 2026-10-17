using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Applies the documented Work-only sample replacement to one generated FirstDurableWorker copy.</summary>
/// <remarks>
/// This proof is deliberately fixed to the reviewed sample and generated project name. It validates all four expected
/// Work source files before writing any of them, then changes only the definition, executor, registration, and producer.
/// The template source and the installed proof consumer remain untouched.
/// </remarks>
internal static class DurableTemplateSampleReplacement
{
    private const string GeneratedProjectName = "FirstDurableWorker";

    private static readonly string[] Files =
    [
        "SampleWork.cs",
        "SampleWorkExecutor.cs",
        "WorkRegistration.cs",
        "SampleWorkProducer.cs"
    ];

    /// <summary>Replaces the sample only after all expected generated Work files match the reviewed template shape.</summary>
    /// <param name="generatedRoot">Root directory of the separate generated consumer, named by <paramref name="projectName"/>.</param>
    /// <param name="projectName">The exact generated project name, <c>FirstDurableWorker</c>.</param>
    /// <returns>The four changed slash-separated paths, relative to <paramref name="generatedRoot"/>.</returns>
    /// <exception cref="PackageIndexException">The root, project name, links, encoding, or sample shape is invalid.</exception>
    /// <remarks>The input/result public type names and their <c>Value</c> properties remain compatible with first-Work tests.</remarks>
    internal static IReadOnlyList<string> Apply(string generatedRoot, string projectName)
    {
        if (!string.Equals(projectName, GeneratedProjectName, StringComparison.Ordinal))
        {
            throw new PackageIndexException("Sample replacement supports only the FirstDurableWorker generated project.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(generatedRoot);
        var root = Path.GetFullPath(generatedRoot);
        DurableTemplateStaging.RequireRegularPath(root);
        if (!Directory.Exists(root)
            || !Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Equals(projectName, StringComparison.Ordinal))
        {
            throw new PackageIndexException("Sample replacement requires a separate generated FirstDurableWorker root.");
        }

        var workDirectory = Path.Join(root, "src", projectName, "Work");
        DurableTemplateStaging.RequireRegularPath(workDirectory);
        if (!Directory.Exists(workDirectory))
        {
            throw new PackageIndexException("Generated Work replacement directory is missing.");
        }

        var replacement = new string[Files.Length];
        var relativePaths = new string[Files.Length];
        for (var index = 0; index < Files.Length; index++)
        {
            var fileName = Files[index];
            var relativePath = $"src/{projectName}/Work/{fileName}";
            var fullPath = Path.Join(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            DurableTemplateStaging.RequireRegularPath(fullPath);
            if (!File.Exists(fullPath))
            {
                throw new PackageIndexException("A required generated Work replacement file is missing.");
            }

            string content;
            try
            {
                content = File.ReadAllText(fullPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
            }
            catch (DecoderFallbackException exception)
            {
                throw new PackageIndexException("Generated Work replacement source is not valid UTF-8.", exception);
            }

            if (CountOccurrences(NormalizeNewlines(content), $"namespace {projectName}.Work;") != 1)
            {
                throw new PackageIndexException("Generated Work source does not use the expected project namespace.");
            }

            replacement[index] = ReplaceSample(content, fileName);
            relativePaths[index] = relativePath;
        }

        // Every source path and exact replacement anchor is checked before the first mutation.
        for (var index = 0; index < Files.Length; index++)
        {
            var fullPath = Path.Join(root, relativePaths[index].Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(fullPath, replacement[index], new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        var reportedPaths = Array.AsReadOnly(relativePaths.Order(StringComparer.Ordinal).ToArray());
        return reportedPaths;
    }

    private static string ReplaceSample(string original, string fileName)
    {
        var lineEnding = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var content = NormalizeNewlines(original);
        content = fileName switch
        {
            "SampleWork.cs" => ReplaceWorkDefinition(content),
            "SampleWorkExecutor.cs" => ReplaceWorkExecutor(content),
            "WorkRegistration.cs" => ReplaceWorkRegistration(content),
            "SampleWorkProducer.cs" => ReplaceWorkProducer(content),
            _ => throw new PackageIndexException("Sample replacement received an unapproved Work file.")
        };
        return lineEnding == "\n" ? content : content.Replace("\n", lineEnding, StringComparison.Ordinal);
    }

    private static string ReplaceWorkDefinition(string content)
    {
        content = ReplaceOnce(content,
            "/// <param name=\"Value\">A nonblank value of at most 200 characters.</param>\npublic sealed record SampleWork(string Value);",
            "/// <param name=\"Value\">A nonblank value of at most 200 characters.</param>\n/// <param name=\"Multiplier\">A value from 1 through 12; omitted values default to 2.</param>\npublic sealed record SampleWork(string Value, int Multiplier = 2);");
        content = ReplaceOnce(content,
            "/// <param name=\"Value\">The bounded processed value.</param>\npublic sealed record SampleWorkResult(string Value);",
            "/// <param name=\"Value\">The bounded processed value.</param>\n/// <param name=\"AppliedMultiplier\">The input multiplier captured by the executor.</param>\npublic sealed record SampleWorkResult(string Value, int AppliedMultiplier = 2);");
        content = ReplaceOnce(content, "Defines the immutable name, version, codecs, safety classification, and retry policy for the sample Work.",
            "Defines the immutable name, version, codecs, safety classification, and retry policy for the replacement Work.");
        content = ReplaceOnce(content,
            "public const string WorkName = \"appsurface.sample-work\";",
            "public const string WorkName = \"appsurface.replacement.multiplied-sample-work\";");
        content = ReplaceOnce(content, "public const string WorkVersion = \"v1\";", "public const string WorkVersion = \"v2\";");
        content = ReplaceOnce(content,
            "public const string Scope = \"appsurface-sample\";",
            "public const string Scope = \"appsurface-replacement-sample\";");
        content = ReplaceOnce(content, "Creates the bounded, source-generated JSON input codec used by this contract.",
            "Creates the bounded, source-generated JSON input codec used by this replacement contract.");
        content = ReplaceOnce(content, "A codec with the stable sample input identity and a 1 KiB encoded-payload limit.",
            "A codec with the replacement input identity and a 1 KiB encoded-payload limit.");
        content = ReplaceOnce(content,
            "        \"appsurface.sample-work\",\n        WorkVersion,\n        DurableDataClassification.ApprovedApplication,\n        SampleWorkJsonContext.Default.SampleWork,",
            "        \"appsurface.replacement.multiplied-sample-work-input\",\n        WorkVersion,\n        DurableDataClassification.ApprovedApplication,\n        SampleWorkJsonContext.Default.SampleWork,");
        content = ReplaceOnce(content,
            "static work => !string.IsNullOrWhiteSpace(work.Value) && work.Value.Length <= 200,",
            "static work => !string.IsNullOrWhiteSpace(work.Value) && work.Value.Length <= 200 && work.Multiplier is >= 1 and <= 12,");
        content = ReplaceOnce(content, "Creates the bounded, source-generated JSON result codec used by this contract.",
            "Creates the bounded, source-generated JSON result codec used by this replacement contract.");
        content = ReplaceOnce(content, "A codec with the stable sample result identity and a 1 KiB encoded-payload limit.",
            "A codec with the replacement result identity and a 1 KiB encoded-payload limit.");
        content = ReplaceOnce(content,
            "        \"appsurface.sample-work-result\",\n        WorkVersion,\n        DurableDataClassification.ApprovedApplication,\n        SampleWorkJsonContext.Default.SampleWorkResult,",
            "        \"appsurface.replacement.multiplied-sample-work-result\",\n        WorkVersion,\n        DurableDataClassification.ApprovedApplication,\n        SampleWorkJsonContext.Default.SampleWorkResult,");
        return ReplaceOnce(content,
            "static result => !string.IsNullOrWhiteSpace(result.Value) && result.Value.Length <= 220,",
            "static result => !string.IsNullOrWhiteSpace(result.Value) && result.Value.Length <= 220 && result.AppliedMultiplier is >= 1 and <= 12,");
    }

    private static string ReplaceWorkExecutor(string content)
    {
        content = ReplaceOnce(content, "Executes the deterministic sample without performing an external side effect.",
            "Executes the deterministic replacement and records the multiplier without performing an external side effect.");
        return ReplaceOnce(content,
            "new SampleWorkResult($\"processed:{payload.Value}\")",
            "new SampleWorkResult($\"processed:{payload.Value}\", payload.Multiplier)");
    }

    private static string ReplaceWorkRegistration(string content)
    {
        content = ReplaceOnce(content, "Registers the single sample Work contract and executor with an application service collection.",
            "Registers the replacement Work contract and its typed executor exactly once with an application service collection.");
        content = ReplaceOnce(content, "Adds the sample definition and its deterministic executor.",
            "Adds the replacement definition and its deterministic executor; repeated registration fails immediately.");
        content = ReplaceOnce(content,
            "/// <exception cref=\"ArgumentNullException\"><paramref name=\"services\"/> is null.</exception>",
            "/// <exception cref=\"ArgumentNullException\"><paramref name=\"services\"/> is null.</exception>\n    /// <exception cref=\"InvalidOperationException\">The replacement binding is already registered.</exception>");
        return ReplaceOnce(content,
            "services.AddDurableWork(SampleWorkDefinition.Definition.ExecutedBy<SampleWorkExecutor>());",
            "var binding = SampleWorkDefinition.Definition.ExecutedBy<SampleWorkExecutor>();\n        if (services.Any(descriptor => descriptor.ServiceType == typeof(DurableWorkBinding<SampleWork, SampleWorkResult, SampleWorkExecutor>)))\n        {\n            throw new InvalidOperationException(\"The replacement Work binding is already registered.\");\n        }\n\n        services.AddDurableWork(binding);");
    }

    private static string ReplaceWorkProducer(string content)
    {
        content = ReplaceOnce(content,
            "Accepts typed sample Work through the public Durable client without coupling producers to activation HTTP.",
            "Accepts typed replacement Work through the public Durable client without coupling producers to activation HTTP.");
        content = ReplaceOnce(content, "Persists one caller-identified sample request.",
            "Persists one caller-identified replacement request with a multiplier from 1 through 12.");
        content = ReplaceOnce(content, "/// <param name=\"work\">Validated typed input.</param>",
            "/// <param name=\"work\">Typed input with a nonblank value and multiplier from 1 through 12.</param>");
        content = ReplaceOnce(content,
            "The producer uses the one registered definition, its captured codecs and retry policy, and the fixed sample scope.",
            "The producer validates the multiplier, then uses the one registered definition, its captured codecs and retry policy, and the replacement scope.");
        content = ReplaceOnce(content,
            "        ArgumentNullException.ThrowIfNull(work);\n        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);",
            "        ArgumentNullException.ThrowIfNull(work);\n        if (work.Multiplier is < 1 or > 12)\n        {\n            throw new ArgumentOutOfRangeException(nameof(work), \"Multiplier must be between 1 and 12.\");\n        }\n\n        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);");
        return content;
    }

    private static string ReplaceOnce(string content, string expected, string replacement)
    {
        if (CountOccurrences(content, expected) != 1)
        {
            throw new PackageIndexException("Generated Work source differs from the reviewed replacement shape.");
        }
        return content.Replace(expected, replacement, StringComparison.Ordinal);
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>Computes a stable hash over only the approved changed Work files.</summary>
    /// <param name="generatedRoot">Root of the generated consumer.</param>
    /// <param name="relativePaths">Changed relative paths returned by <see cref="Apply"/>.</param>
    /// <returns>Lowercase SHA-256 over ordinal-sorted path/byte pairs.</returns>
    /// <exception cref="PackageIndexException">The root or path inventory is outside the approved Work shape.</exception>
    internal static string ComputeContentSha256(string generatedRoot, IReadOnlyList<string> relativePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedRoot);
        ArgumentNullException.ThrowIfNull(relativePaths);
        var root = Path.GetFullPath(generatedRoot);
        DurableTemplateStaging.RequireRegularPath(root);
        if (!Directory.Exists(root)
            || !Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Equals(GeneratedProjectName, StringComparison.Ordinal))
        {
            throw new PackageIndexException("Sample replacement hash requires a generated FirstDurableWorker root.");
        }

        var approvedPaths = Files.Select(file => $"src/{GeneratedProjectName}/Work/{file}").Order(StringComparer.Ordinal).ToArray();
        if (!relativePaths.Order(StringComparer.Ordinal).SequenceEqual(approvedPaths, StringComparer.Ordinal))
        {
            throw new PackageIndexException("Sample replacement hash requires the exact four-file Work inventory.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relativePath in relativePaths.Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath + "\0"));
            var path = Path.Join(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            DurableTemplateStaging.RequireRegularPath(path);
            if (!File.Exists(path)) throw new PackageIndexException("A sample replacement hash input is missing.");
            hash.AppendData(File.ReadAllBytes(path));
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static int CountOccurrences(string value, string candidate)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(candidate, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += candidate.Length;
        }
        return count;
    }

}
