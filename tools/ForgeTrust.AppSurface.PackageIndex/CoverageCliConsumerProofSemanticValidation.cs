using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Validates the bounded Cobertura contract used by the packaged default-collector consumer proof.
/// </summary>
/// <remarks>
/// This validator deliberately does not reuse the CLI's private project-slug allocator. The CLI emits one adjacent
/// <c>coverage-project.json</c> manifest for every selected project; this reader binds the known consumer project to
/// its sibling raw report, preserves that exact report into the merge input, and verifies retained semantic facts in
/// the fan-in result. It is not a general Cobertura parser or an assertion about the MSBuild compatibility driver.
/// </remarks>
internal static class CoverageCliConsumerProofSemanticValidator
{
    internal const string ExpectedProjectPath = "Smoke.Tests/Smoke.Tests.csproj";
    internal const string ManifestFileName = "coverage-project.json";
    internal const string CoverageFileName = "coverage.cobertura.xml";
    private const int ManifestSchemaVersion = 1;
    private const int MaxCharactersInDocument = 1_048_576;
    private const int MaxDepth = 32;
    private const int MaxElements = 10_000;

    private static readonly Regex PositiveInteger = new("^[1-9][0-9]*$", RegexOptions.CultureInvariant);
    private static readonly Regex NonNegativeInteger = new("^(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant);
    private static readonly Regex ConditionCoverage = new("^(0|[1-9][0-9]?|100)% \\((0|[1-9][0-9]*)/(0|[1-9][0-9]*)\\)$", RegexOptions.CultureInvariant);
    private static readonly Regex Percent = new("^(0|[1-9][0-9]?|100)%$", RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string?>> AllowedChildren =
        new Dictionary<string, IReadOnlySet<string?>>
        {
            ["coverage"] = new HashSet<string?>(StringComparer.Ordinal) { "sources", "packages" },
            ["sources"] = new HashSet<string?>(StringComparer.Ordinal) { "source" },
            ["source"] = new HashSet<string?>(StringComparer.Ordinal),
            ["packages"] = new HashSet<string?>(StringComparer.Ordinal) { "package" },
            ["package"] = new HashSet<string?>(StringComparer.Ordinal) { "classes" },
            ["classes"] = new HashSet<string?>(StringComparer.Ordinal) { "class" },
            ["class"] = new HashSet<string?>(StringComparer.Ordinal) { "methods", "lines" },
            ["methods"] = new HashSet<string?>(StringComparer.Ordinal) { "method" },
            ["method"] = new HashSet<string?>(StringComparer.Ordinal) { "lines" },
            ["lines"] = new HashSet<string?>(StringComparer.Ordinal) { "line" },
            ["line"] = new HashSet<string?>(StringComparer.Ordinal) { "conditions" },
            ["conditions"] = new HashSet<string?>(StringComparer.Ordinal) { "condition" },
            ["condition"] = new HashSet<string?>(StringComparer.Ordinal),
        };

    /// <summary>
    /// Selects and validates the raw artifact belonging to the known default-collector fixture project.
    /// </summary>
    /// <param name="coverageRunDirectory">The default collector's coverage-run output directory.</param>
    /// <returns>Raw selection, parsed semantic facts, and deterministic failures.</returns>
    internal static CoverageCliConsumerProofSemanticProof ValidateRaw(string coverageRunDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coverageRunDirectory);
        var projectsDirectory = Path.Join(coverageRunDirectory, "projects");
        if (!IsRegularDirectory(projectsDirectory))
        {
            return CoverageCliConsumerProofSemanticProof.RawFailure(
                new CoverageCliConsumerProofFailure(
                    "CPV001",
                    "raw",
                    "The default collector output has no regular projects directory.",
                    "Run the packaged default collector proof again and retain its owned project artifacts.",
                    "projects"));
        }

        var manifestPaths = new List<string>();
        try
        {
            foreach (var projectDirectory in Directory.EnumerateDirectories(projectsDirectory, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                if (!IsRegularDirectory(projectDirectory))
                {
                    return CoverageCliConsumerProofSemanticProof.RawFailure(
                        new CoverageCliConsumerProofFailure(
                            "CPV001",
                            "raw",
                            "A per-project coverage artifact directory is non-regular or linked.",
                            "Regenerate the default collector artifacts in a clean owned proof directory.",
                            RelativeProjectEvidence(projectsDirectory, projectDirectory)));
                }

                var manifestPath = Path.Join(projectDirectory, ManifestFileName);
                if (File.Exists(manifestPath) || Directory.Exists(manifestPath))
                {
                    manifestPaths.Add(manifestPath);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CoverageCliConsumerProofSemanticProof.RawFailure(
                new CoverageCliConsumerProofFailure(
                    "CPV001",
                    "raw",
                    "The default collector project manifests could not be read as regular files.",
                    "Use an owned writable proof directory and rerun the packaged proof.",
                    "projects"));
        }

        if (manifestPaths.Count == 0)
        {
            return CoverageCliConsumerProofSemanticProof.RawFailure(
                new CoverageCliConsumerProofFailure(
                    "CPV001",
                    "raw",
                    "No per-project coverage manifest was produced for the default collector run.",
                    "Upgrade or repair the packaged CLI so coverage run emits coverage-project.json beside each selected project.",
                    "projects"));
        }

        var manifests = new List<CoverageProjectManifest>();
        foreach (var manifestPath in manifestPaths)
        {
            if (!IsRegularFile(manifestPath))
            {
                return CoverageCliConsumerProofSemanticProof.RawFailure(
                    new CoverageCliConsumerProofFailure(
                        "CPV001",
                        "raw",
                        "A per-project coverage manifest is absent, non-regular, or linked.",
                        "Regenerate the default collector artifacts in a clean owned proof directory.",
                        RelativeProjectEvidence(projectsDirectory, manifestPath)));
            }

            var manifest = ReadManifest(manifestPath);
            if (manifest is null)
            {
                return CoverageCliConsumerProofSemanticProof.RawFailure(
                    new CoverageCliConsumerProofFailure(
                        "CPV002",
                        "raw",
                        "A per-project coverage manifest is malformed or does not bind its directory safely.",
                        "Regenerate the coverage run output; do not edit coverage-project.json by hand.",
                        RelativeProjectEvidence(projectsDirectory, manifestPath)));
            }

            manifests.Add(manifest);
        }

        var matching = manifests
            .Where(manifest => string.Equals(manifest.ProjectPath, ExpectedProjectPath, StringComparison.Ordinal))
            .ToArray();
        if (matching.Length == 0)
        {
            return CoverageCliConsumerProofSemanticProof.RawFailure(
                new CoverageCliConsumerProofFailure(
                    "CPV001",
                    "raw",
                    "The default collector run did not produce a manifest for Smoke.Tests/Smoke.Tests.csproj.",
                    "Keep Smoke.Tests on the default collector path; do not switch drivers to satisfy this proof.",
                    "projects"));
        }

        if (matching.Length != 1)
        {
            return CoverageCliConsumerProofSemanticProof.RawFailure(
                new CoverageCliConsumerProofFailure(
                    "CPV002",
                    "raw",
                    "More than one project manifest claims Smoke.Tests/Smoke.Tests.csproj.",
                    "Clean stale project artifacts and rerun the packaged default collector proof.",
                    "projects"));
        }

        var selectedManifest = matching[0];
        var reportPath = Path.Join(Path.GetDirectoryName(selectedManifest.Path)!, CoverageFileName);
        var rawArtifact = new CoverageCliConsumerProofRawArtifact(
            selectedManifest.Path,
            reportPath,
            selectedManifest.ProjectPath,
            selectedManifest.Slug,
            Sha256: string.Empty);
        if (!IsRegularFile(reportPath))
        {
            return CoverageCliConsumerProofSemanticProof.RawFailure(
                rawArtifact,
                new CoverageCliConsumerProofFailure(
                    "CPV001",
                    "raw",
                    "The selected Smoke.Tests manifest has no regular sibling coverage.cobertura.xml report.",
                    "Repair the default collector output and rerun the packaged proof.",
                    RelativeProjectEvidence(projectsDirectory, reportPath)));
        }

        string sha256;
        try
        {
            sha256 = ComputeSha256(reportPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CoverageCliConsumerProofSemanticProof.RawFailure(
                rawArtifact,
                new CoverageCliConsumerProofFailure(
                    "CPV001",
                    "raw",
                    "The selected Smoke.Tests report could not be read as a regular raw artifact.",
                    "Regenerate the default collector artifacts in a clean owned proof directory.",
                    RelativeProjectEvidence(projectsDirectory, reportPath)));
        }

        rawArtifact = rawArtifact with { Sha256 = sha256 };
        var parsed = ParseCobertura(reportPath, "raw");
        if (parsed.Failure is not null)
        {
            return CoverageCliConsumerProofSemanticProof.RawFailure(rawArtifact, parsed.Failure);
        }

        var validation = ValidateFacts(parsed.Facts!, "raw");
        return new CoverageCliConsumerProofSemanticProof(
            rawArtifact,
            new CoverageCliConsumerProofSemanticOutcome(
                validation.Failures.Count == 0 ? "passed" : "failed",
                reportPath,
                sha256,
                validation.Invariants,
                parsed.Facts),
            CoverageCliConsumerProofSemanticOutcome.NotRun,
            validation.Failures);
    }

    /// <summary>
    /// Verifies the byte-preserved selected shard and independently validates the merged fan-in report.
    /// </summary>
    /// <param name="rawProof">The result returned from <see cref="ValidateRaw"/>.</param>
    /// <param name="copiedShardPath">The shard copied from the selected raw report into fan-in input.</param>
    /// <param name="mergedCoveragePath">The merged Cobertura report produced by coverage merge.</param>
    /// <returns>Raw and merged semantic proof with raw-first deterministic failure ordering.</returns>
    internal static CoverageCliConsumerProofSemanticProof ValidateMerged(
        CoverageCliConsumerProofSemanticProof rawProof,
        string copiedShardPath,
        string mergedCoveragePath)
    {
        ArgumentNullException.ThrowIfNull(rawProof);
        ArgumentException.ThrowIfNullOrWhiteSpace(copiedShardPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mergedCoveragePath);

        if (rawProof.RawArtifact is null || rawProof.Raw.Facts is null)
        {
            return rawProof;
        }

        var failures = rawProof.Failures.ToList();
        var copiedShardMatches = false;
        try
        {
            copiedShardMatches = IsRegularFile(copiedShardPath)
                && string.Equals(rawProof.RawArtifact.Sha256, ComputeSha256(copiedShardPath), StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            copiedShardMatches = false;
        }

        if (!copiedShardMatches)
        {
            failures.Add(new CoverageCliConsumerProofFailure(
                "CPV011",
                "raw-to-merged",
                "The selected Smoke.Tests raw report was not preserved byte-for-byte in the merge input.",
                "Regenerate the proof; the fan-in input must be copied only from the selected manifest-bound raw report.",
                "coverage-shards/Smoke.Tests/coverage.cobertura.xml"));
        }

        if (!IsRegularFile(mergedCoveragePath))
        {
            failures.Add(new CoverageCliConsumerProofFailure(
                "CPV003",
                "merged",
                "The coverage merge command did not produce a regular merged coverage.cobertura.xml report.",
                "Inspect the merge artifacts and rerun the packaged proof.",
                "coverage-fan-in/coverage.cobertura.xml"));
            return rawProof with
            {
                Merged = new CoverageCliConsumerProofSemanticOutcome("failed", mergedCoveragePath, null, [], null),
                Failures = failures,
            };
        }

        var parsed = ParseCobertura(mergedCoveragePath, "merged");
        if (parsed.Failure is not null)
        {
            failures.Add(parsed.Failure);
            return rawProof with
            {
                Merged = new CoverageCliConsumerProofSemanticOutcome("failed", mergedCoveragePath, null, [], null),
                Failures = failures,
            };
        }

        var validation = ValidateFacts(parsed.Facts!, "merged");
        failures.AddRange(validation.Failures);
        if (!Equivalent(rawProof.Raw.Facts, parsed.Facts!))
        {
            failures.Add(new CoverageCliConsumerProofFailure(
                "CPV011",
                "merged",
                "The merged report did not retain the selected raw Smoke Calculator semantic facts.",
                "Inspect the coverage merge path; it must retain the selected class, covered Sign line, and covered jump branch.",
                "coverage-fan-in/coverage.cobertura.xml"));
        }

        return rawProof with
        {
            Merged = new CoverageCliConsumerProofSemanticOutcome(
                validation.Failures.Count == 0 && Equivalent(rawProof.Raw.Facts, parsed.Facts!) ? "passed" : "failed",
                mergedCoveragePath,
                null,
                validation.Invariants,
                parsed.Facts),
            Failures = failures,
        };
    }

    private static CoverageProjectManifest? ReadManifest(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion)
                || !schemaVersion.TryGetInt32(out var version)
                || version != ManifestSchemaVersion
                || !document.RootElement.TryGetProperty("projectPath", out var projectPath)
                || projectPath.ValueKind != JsonValueKind.String
                || !document.RootElement.TryGetProperty("slug", out var slug)
                || slug.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var normalizedProjectPath = projectPath.GetString();
            var manifestSlug = slug.GetString();
            var directory = Path.GetDirectoryName(path);
            if (!IsSafeProjectPath(normalizedProjectPath)
                || !IsSafeSlug(manifestSlug)
                || string.IsNullOrWhiteSpace(directory)
                || !string.Equals(Path.GetFileName(directory), manifestSlug, StringComparison.Ordinal))
            {
                return null;
            }

            return new CoverageProjectManifest(path, normalizedProjectPath!, manifestSlug!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static CoberturaParseResult ParseCobertura(string path, string scope)
    {
        try
        {
            using var reader = XmlReader.Create(
                path,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxCharactersInDocument,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                });
            var elements = 0;
            var stack = new Stack<string>();
            var packages = new Stack<string>();
            var discoveredPackages = new List<string>();
            var classes = new Stack<CoberturaClass>();
            var discoveredClasses = new List<CoberturaClass>();
            CoberturaLine? activeLine = null;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Depth > MaxDepth || ++elements > MaxElements)
                    {
                        return CoberturaParseResult.Unsupported(scope, "The Cobertura document exceeds the supported depth or element limit.");
                    }

                    if (!string.IsNullOrEmpty(reader.NamespaceURI)
                        || !AllowedChildren.TryGetValue(reader.LocalName, out var allowedChildren)
                        || !string.Equals(reader.Name, reader.LocalName, StringComparison.Ordinal))
                    {
                        return CoberturaParseResult.Unsupported(scope, "The Cobertura document uses an unsupported element or namespace.");
                    }

                    var parent = stack.Count == 0 ? null : stack.Peek();
                    if (parent is null)
                    {
                        if (stack.Count != 0 || !string.Equals(reader.LocalName, "coverage", StringComparison.Ordinal))
                        {
                            return CoberturaParseResult.Unsupported(scope, "The Cobertura document must have one coverage root element.");
                        }
                    }
                    else if (!AllowedChildren[parent].Contains(reader.LocalName))
                    {
                        return CoberturaParseResult.Unsupported(scope, "The Cobertura document has an unsupported element shape.");
                    }

                    var attributes = ReadAttributes(reader);
                    if (!ValidateAttributes(reader.LocalName, attributes, out var attributeError))
                    {
                        return CoberturaParseResult.Unsupported(scope, attributeError!);
                    }

                    if (reader.LocalName == "package")
                    {
                        var packageName = attributes["name"];
                        packages.Push(packageName);
                        discoveredPackages.Add(packageName);
                    }
                    else if (reader.LocalName == "class")
                    {
                        if (packages.Count == 0)
                        {
                            return CoberturaParseResult.Unsupported(scope, "A Cobertura class was not contained in a package.");
                        }

                        var coverageClass = new CoberturaClass(packages.Peek(), attributes["name"], attributes["filename"]);
                        classes.Push(coverageClass);
                        discoveredClasses.Add(coverageClass);
                    }
                    else if (reader.LocalName == "line" && classes.Count > 0 && IsDirectClassLine(stack))
                    {
                        activeLine = new CoberturaLine(
                            int.Parse(attributes["number"], System.Globalization.CultureInfo.InvariantCulture),
                            int.Parse(attributes["hits"], System.Globalization.CultureInfo.InvariantCulture),
                            attributes.TryGetValue("branch", out var branch) && string.Equals(branch, "True", StringComparison.Ordinal),
                            attributes.TryGetValue("condition-coverage", out var coverage) ? coverage : null,
                            []);
                        classes.Peek().Lines.Add(activeLine);
                    }
                    else if (reader.LocalName == "condition" && activeLine is not null)
                    {
                        activeLine.Conditions.Add(new CoberturaCondition(attributes["type"], attributes["coverage"]));
                    }

                    if (!reader.IsEmptyElement)
                    {
                        stack.Push(reader.LocalName);
                    }
                    else
                    {
                        CloseElement(reader.LocalName, packages, classes, ref activeLine);
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (stack.Count == 0 || !string.Equals(stack.Pop(), reader.LocalName, StringComparison.Ordinal))
                    {
                        return CoberturaParseResult.Unsupported(scope, "The Cobertura document has an unbalanced element shape.");
                    }

                    CloseElement(reader.LocalName, packages, classes, ref activeLine);
                }
            }

            if (stack.Count != 0 || discoveredClasses.Count == 0)
            {
                return CoberturaParseResult.Unsupported(scope, "The Cobertura document is incomplete or contains no classes.");
            }

            return new CoberturaParseResult(CreateFacts(discoveredPackages, discoveredClasses), null);
        }
        catch (XmlException exception)
        {
            return exception.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("maximum", StringComparison.OrdinalIgnoreCase)
                ? CoberturaParseResult.Unsupported(scope, "The Cobertura XML exceeds the supported parser safety boundary.")
                : CoberturaParseResult.Malformed(scope, "The Cobertura XML is malformed.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or OverflowException)
        {
            return CoberturaParseResult.Unsupported(scope, "The Cobertura document could not be read as the supported bounded schema.");
        }
    }

    private static IReadOnlyDictionary<string, string> ReadAttributes(XmlReader reader)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!reader.HasAttributes)
        {
            return attributes;
        }

        while (reader.MoveToNextAttribute())
        {
            if (!string.IsNullOrEmpty(reader.NamespaceURI)
                || !attributes.TryAdd(reader.LocalName, reader.Value))
            {
                throw new InvalidDataException("Cobertura attributes must be unique and unqualified.");
            }
        }

        reader.MoveToElement();
        return attributes;
    }

    private static bool ValidateAttributes(string element, IReadOnlyDictionary<string, string> attributes, out string? error)
    {
        var allowed = element switch
        {
            "coverage" => new[] { "line-rate", "branch-rate", "lines-covered", "lines-valid", "branches-covered", "branches-valid", "complexity", "version", "timestamp" },
            "package" => new[] { "name", "line-rate", "branch-rate", "complexity" },
            "class" => new[] { "name", "filename", "line-rate", "branch-rate", "complexity" },
            "method" => new[] { "name", "signature", "line-rate", "branch-rate", "complexity" },
            "line" => new[] { "number", "hits", "branch", "condition-coverage" },
            "condition" => new[] { "number", "type", "coverage" },
            _ => [],
        };
        if (attributes.Keys.Any(attribute => !allowed.Contains(attribute, StringComparer.Ordinal)))
        {
            error = "The Cobertura document contains an unsupported attribute.";
            return false;
        }

        var required = element switch
        {
            "package" => new[] { "name" },
            "class" => new[] { "name", "filename" },
            "line" => new[] { "number", "hits" },
            "condition" => new[] { "number", "type", "coverage" },
            _ => [],
        };
        if (required.Any(attribute => !attributes.TryGetValue(attribute, out var value) || string.IsNullOrWhiteSpace(value)))
        {
            error = "The Cobertura document omits a required identity or numeric attribute.";
            return false;
        }

        if (element == "line"
            && (!PositiveInteger.IsMatch(attributes["number"])
                || !NonNegativeInteger.IsMatch(attributes["hits"])
                || (attributes.TryGetValue("branch", out var branch)
                    && !string.Equals(branch, "True", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(branch, "False", StringComparison.OrdinalIgnoreCase))
                || (attributes.TryGetValue("condition-coverage", out var conditionCoverage) && !IsValidConditionCoverage(conditionCoverage))))
        {
            error = "The Cobertura line attributes use unsupported numeric or branch grammar.";
            return false;
        }

        if (element == "line"
            && attributes.TryGetValue("branch", out var isBranch)
            && isBranch == "True"
            && !attributes.ContainsKey("condition-coverage"))
        {
            error = "A branch line must include condition-coverage.";
            return false;
        }

        if (element == "condition"
            && (!NonNegativeInteger.IsMatch(attributes["number"])
                || !Percent.IsMatch(attributes["coverage"])))
        {
            error = "The Cobertura condition attributes use unsupported numeric grammar.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsValidConditionCoverage(string value)
    {
        var match = ConditionCoverage.Match(value);
        if (!match.Success)
        {
            return false;
        }

        var covered = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var valid = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        return valid > 0 && covered <= valid;
    }

    private static void CloseElement(
        string name,
        Stack<string> packages,
        Stack<CoberturaClass> classes,
        ref CoberturaLine? activeLine)
    {
        if (name == "line")
        {
            activeLine = null;
        }
        else if (name == "class" && classes.Count > 0)
        {
            classes.Pop();
        }
        else if (name == "package" && packages.Count > 0)
        {
            packages.Pop();
        }
    }

    private static bool IsDirectClassLine(Stack<string> stack)
        => stack.Count >= 2
            && string.Equals(stack.ElementAt(0), "lines", StringComparison.Ordinal)
            && string.Equals(stack.ElementAt(1), "class", StringComparison.Ordinal);

    private static CoverageCliConsumerProofCoverageFacts CreateFacts(
        IReadOnlyList<string> packages,
        IReadOnlyList<CoberturaClass> classes)
    {
        var matchingPackages = packages.Count(item => string.Equals(item, "Smoke", StringComparison.Ordinal));
        var matchingClasses = classes
            .Where(item => string.Equals(item.PackageName, "Smoke", StringComparison.Ordinal)
                && string.Equals(item.Name, "Smoke.Calculator", StringComparison.Ordinal))
            .ToArray();
        var selected = matchingClasses.Length == 1 ? matchingClasses[0] : null;
        var signLines = selected?.Lines.Where(line => line.Number == 7).ToArray() ?? [];
        var sign = signLines.Length == 1 ? signLines[0] : null;
        return new CoverageCliConsumerProofCoverageFacts(
            matchingPackages,
            matchingClasses.Length,
            selected?.Filename,
            selected?.Lines.Count(line => line.Hits > 0) ?? 0,
            signLines.Length,
            sign?.Hits ?? 0,
            sign?.Branch == true,
            sign?.ConditionCoverage,
            sign?.Conditions.Count ?? 0,
            sign?.Conditions.Count ?? 0,
            sign?.Conditions.Count(condition => string.Equals(condition.Type, "jump", StringComparison.Ordinal)
                && string.Equals(condition.Coverage, "100%", StringComparison.Ordinal)) ?? 0);
    }

    private static CoverageFactsValidation ValidateFacts(CoverageCliConsumerProofCoverageFacts facts, string scope)
    {
        var failures = new List<CoverageCliConsumerProofFailure>();
        if (facts.SmokePackageCount == 0)
        {
            failures.Add(Failure("CPV005", scope, "The Cobertura report does not contain the expected Smoke package.", "Ensure the packaged fixture covers the Smoke library through the default collector."));
        }
        else if (facts.SmokePackageCount > 1)
        {
            failures.Add(Failure("CPV010", scope, "The Cobertura report contains duplicate Smoke package identities.", "Remove duplicate package/class identities before merging the selected coverage report."));
        }

        if (facts.SmokeCalculatorClassCount == 0)
        {
            failures.Add(Failure("CPV006", scope, "The Cobertura report does not contain the expected Smoke.Calculator class.", "Ensure the known Smoke.Tests consumer executes Calculator through the default collector."));
        }
        else if (facts.SmokeCalculatorClassCount > 1)
        {
            failures.Add(Failure("CPV010", scope, "The Cobertura report contains duplicate Smoke.Calculator identities.", "Remove duplicate class identities before merging the selected coverage report."));
        }

        if (facts.SmokeCalculatorClassCount == 1
            && !HasExpectedCalculatorFilename(facts.CalculatorFilename))
        {
            failures.Add(Failure("CPV007", scope, "The expected Smoke.Calculator class is not bound to Smoke/Calculator.cs.", "Regenerate the fixture coverage without path rewriting."));
        }

        if (facts.CoveredCalculatorLineCount == 0 || facts.SignLineCount != 1 || facts.SignHits <= 0)
        {
            failures.Add(Failure("CPV008", scope, "Calculator.Sign line 7 is not uniquely present with positive coverage hits.", "Keep both Sign test cases in Smoke.Tests and retain line coverage through the selected report."));
        }

        if (!facts.SignBranch
            || !string.Equals(facts.SignConditionCoverage, "100% (2/2)", StringComparison.Ordinal)
            || facts.SignConditionCount != 1
            || facts.SignJumpConditionCount != 1
            || facts.CoveredSignJumpConditionCount != 1)
        {
            failures.Add(Failure("CPV009", scope, "Calculator.Sign line 7 did not retain its covered two-way jump branch.", "Exercise both Sign branches and retain Coverlet branch facts through the merge."));
        }

        var invariants = failures.Count == 0
            ? new[]
            {
                "package:Smoke",
                "class:Smoke.Calculator",
                "filename:*Smoke/Calculator.cs",
                "line:Calculator.Sign@7:hits>0",
                "branch:Calculator.Sign@7:100% (2/2):jump",
            }
            : Array.Empty<string>();
        return new CoverageFactsValidation(invariants, failures);
    }

    private static CoverageCliConsumerProofFailure Failure(string code, string scope, string cause, string nextAction)
        => new(code, scope, cause, nextAction, scope == "raw" ? "coverage-merged/projects" : "coverage-fan-in/coverage.cobertura.xml");

    private static bool Equivalent(CoverageCliConsumerProofCoverageFacts first, CoverageCliConsumerProofCoverageFacts second)
        => first.SmokePackageCount == second.SmokePackageCount
            && first.SmokeCalculatorClassCount == second.SmokeCalculatorClassCount
            && HasExpectedCalculatorFilename(first.CalculatorFilename) == HasExpectedCalculatorFilename(second.CalculatorFilename)
            && first.CoveredCalculatorLineCount == second.CoveredCalculatorLineCount
            && first.SignLineCount == second.SignLineCount
            && first.SignHits == second.SignHits
            && first.SignBranch == second.SignBranch
            && string.Equals(first.SignConditionCoverage, second.SignConditionCoverage, StringComparison.Ordinal)
            && first.SignConditionCount == second.SignConditionCount
            && first.SignJumpConditionCount == second.SignJumpConditionCount
            && first.CoveredSignJumpConditionCount == second.CoveredSignJumpConditionCount;

    private static bool HasExpectedCalculatorFilename(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return false;
        }

        var normalized = filename.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized.TrimStart('/');
        }

        var components = normalized.Split('/', StringSplitOptions.None);
        return components.Length >= 2
            && components.All(component => component.Length > 0
                && component is not "." and not ".."
                && !component.Any(char.IsControl))
            && string.Equals(components[^2], "Smoke", StringComparison.OrdinalIgnoreCase)
            && string.Equals(components[^1], "Calculator.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeProjectPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
            && !Path.IsPathRooted(path)
            && !path.Contains('\\')
            && path.Split('/', StringSplitOptions.None).All(segment => segment.Length > 0 && segment is not "." and not "..");

    private static bool IsSafeSlug(string? slug)
        => !string.IsNullOrWhiteSpace(slug)
            && slug.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-');

    private static bool IsRegularFile(string path)
    {
        try
        {
            return File.Exists(path)
                && !Directory.Exists(path)
                && new FileInfo(path).LinkTarget is null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsRegularDirectory(string path)
    {
        try
        {
            return Directory.Exists(path)
                && new DirectoryInfo(path).LinkTarget is null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string RelativeProjectEvidence(string projectsDirectory, string path)
        => Path.Join("projects", Path.GetRelativePath(projectsDirectory, path).Replace('\\', '/'));

    private sealed record CoverageProjectManifest(string Path, string ProjectPath, string Slug);

    private sealed class CoberturaClass
    {
        internal CoberturaClass(string packageName, string name, string filename)
        {
            PackageName = packageName;
            Name = name;
            Filename = filename;
        }

        internal string PackageName { get; }

        internal string Name { get; }

        internal string Filename { get; }

        internal List<CoberturaLine> Lines { get; } = [];
    }

    private sealed class CoberturaLine
    {
        internal CoberturaLine(int number, int hits, bool branch, string? conditionCoverage, List<CoberturaCondition> conditions)
        {
            Number = number;
            Hits = hits;
            Branch = branch;
            ConditionCoverage = conditionCoverage;
            Conditions = conditions;
        }

        internal int Number { get; }

        internal int Hits { get; }

        internal bool Branch { get; }

        internal string? ConditionCoverage { get; }

        internal List<CoberturaCondition> Conditions { get; }
    }

    private sealed record CoberturaCondition(string Type, string Coverage);

    private sealed record CoberturaParseResult(CoverageCliConsumerProofCoverageFacts? Facts, CoverageCliConsumerProofFailure? Failure)
    {
        internal static CoberturaParseResult Malformed(string scope, string cause)
            => new(
                null,
                new CoverageCliConsumerProofFailure(
                    scope == "raw" ? "CPV002" : "CPV004",
                    scope,
                    cause,
                    scope == "raw"
                        ? "Regenerate the selected raw report with the default collector."
                        : "Regenerate the merged report with coverage merge after repairing the selected raw artifact.",
                    "coverage.cobertura.xml"));

        internal static CoberturaParseResult Unsupported(string scope, string cause)
            => new(null, new CoverageCliConsumerProofFailure("CPV004", scope, cause, "Use the documented bounded Coverlet/ReportGenerator Cobertura subset or upgrade the proof reader deliberately.", "coverage.cobertura.xml"));
    }

    private sealed record CoverageFactsValidation(IReadOnlyList<string> Invariants, IReadOnlyList<CoverageCliConsumerProofFailure> Failures);
}

/// <summary>
/// Manifest-bound raw report selected for the packaged default-collector proof.
/// </summary>
internal sealed record CoverageCliConsumerProofRawArtifact(
    string ManifestPath,
    string CoveragePath,
    string ProjectPath,
    string Slug,
    string Sha256);

/// <summary>
/// Public-safe outcome of one semantic coverage artifact validation stage.
/// </summary>
internal sealed record CoverageCliConsumerProofSemanticOutcome(
    string Outcome,
    string? ArtifactPath,
    string? Sha256,
    IReadOnlyList<string> Invariants,
    CoverageCliConsumerProofCoverageFacts? Facts)
{
    internal static CoverageCliConsumerProofSemanticOutcome NotRun { get; } = new("not-run", null, null, [], null);
}

/// <summary>
/// Bounded semantic facts retained by the package proof. These are intentionally not raw XML.
/// </summary>
internal sealed record CoverageCliConsumerProofCoverageFacts(
    int SmokePackageCount,
    int SmokeCalculatorClassCount,
    string? CalculatorFilename,
    int CoveredCalculatorLineCount,
    int SignLineCount,
    int SignHits,
    bool SignBranch,
    string? SignConditionCoverage,
    int SignConditionCount,
    int SignJumpConditionCount,
    int CoveredSignJumpConditionCount);

/// <summary>
/// One coded package coverage proof failure.
/// </summary>
internal sealed record CoverageCliConsumerProofFailure(
    string Code,
    string Scope,
    string Cause,
    string NextAction,
    string EvidenceRelativePath);

/// <summary>
/// Complete semantic default-collector proof state, including a selected raw artifact when selection succeeded.
/// </summary>
internal sealed record CoverageCliConsumerProofSemanticProof(
    CoverageCliConsumerProofRawArtifact? RawArtifact,
    CoverageCliConsumerProofSemanticOutcome Raw,
    CoverageCliConsumerProofSemanticOutcome Merged,
    IReadOnlyList<CoverageCliConsumerProofFailure> Failures)
{
    internal bool CanMerge => RawArtifact is not null
        && Raw.Facts is not null
        && Raw.Outcome == "passed"
        && Failures.Count == 0;

    internal bool Succeeded => Failures.Count == 0 && Raw.Outcome == "passed" && Merged.Outcome == "passed";

    internal static CoverageCliConsumerProofSemanticProof NotRun { get; } = new(null, CoverageCliConsumerProofSemanticOutcome.NotRun, CoverageCliConsumerProofSemanticOutcome.NotRun, []);

    internal static CoverageCliConsumerProofSemanticProof RawFailure(CoverageCliConsumerProofFailure failure)
        => RawFailure(null, failure);

    internal static CoverageCliConsumerProofSemanticProof RawFailure(
        CoverageCliConsumerProofRawArtifact? rawArtifact,
        CoverageCliConsumerProofFailure failure)
        => new(
            rawArtifact,
            new CoverageCliConsumerProofSemanticOutcome("failed", rawArtifact?.CoveragePath, rawArtifact?.Sha256, [], null),
            CoverageCliConsumerProofSemanticOutcome.NotRun,
            [failure]);
}
