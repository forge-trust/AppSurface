using System.Globalization;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>
/// Projects symbol-confirmed async observations between retained coverage report copies.
/// </summary>
internal static class CoverageAsyncPairing
{
    internal static IReadOnlyList<(XDocument Document, string SourceRoot)> Project(
        IReadOnlyList<(XDocument Document, string SourceRoot)> inputs,
        IReadOnlyList<IReadOnlyList<CoverageAsyncMethod>> metadata)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(metadata);

        if (inputs.Count < 2 || metadata.Count != inputs.Count || metadata[0].Count == 0)
        {
            return inputs;
        }

        var primaryMethods = GetUniqueMethods(metadata[0]);
        var result = inputs.ToArray();
        var promotions = new Dictionary<XElement, LinePromotion>();
        XDocument? primaryDocument = null;
        for (var index = 1; index < inputs.Count; index++)
        {
            var nativeMethods = GetUniqueMethods(metadata[index]);
            var pairs = primaryMethods.Select(entry => (Primary: entry.Value, Native: nativeMethods.GetValueOrDefault(entry.Key)))
                .Where(pair => pair.Native is not null && pair.Primary.Fingerprint == pair.Native.Fingerprint)
                .ToArray();
            if (pairs.Length == 0) continue;

            primaryDocument ??= NormalizeCopy(inputs[0]);
            var nativeDocument = NormalizeCopy(inputs[index]);
            result[index] = (nativeDocument, inputs[index].SourceRoot);
            foreach (var pair in pairs)
                AddObservations(primaryDocument, pair.Primary, metadata[0], nativeDocument, pair.Native!, promotions);
        }
        if (primaryDocument is null) return inputs;
        result[0] = (primaryDocument, inputs[0].SourceRoot);

        foreach (var (line, promotion) in promotions)
        {
            if (promotion.Hits > 0)
                line.SetAttributeValue("hits", promotion.Hits.ToString(CultureInfo.InvariantCulture));
            if (promotion.FullBranches is { } branches)
            {
                var denominator = branches.ToString(CultureInfo.InvariantCulture);
                line.SetAttributeValue("condition-coverage", $"100% ({denominator}/{denominator})");
            }
        }

        return result;
    }

    private static XDocument NormalizeCopy((XDocument Document, string SourceRoot) input)
        => CoverageFanInComposer.NormalizePaths(new XDocument(input.Document), input.SourceRoot);

    private static Dictionary<(string Assembly, string File, string SourceType, string SourceMethod, string StateMachineType), CoverageAsyncMethod> GetUniqueMethods(
        IReadOnlyList<CoverageAsyncMethod> methods)
    {
        return methods
            .GroupBy(method => (method.Assembly, method.File, method.SourceType, method.SourceMethod, method.StateMachineType))
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
    }

    private static void AddObservations(
        XDocument sourceDocument,
        CoverageAsyncMethod sourceMetadata,
        IReadOnlyList<CoverageAsyncMethod> sourceSymbols,
        XDocument generatedDocument,
        CoverageAsyncMethod generatedMetadata,
        Dictionary<XElement, LinePromotion> promotions)
    {
        var sourceClass = FindSourceClass(sourceDocument, sourceMetadata, out var projectedClassName);
        var generatedClass = FindUniqueClass(generatedDocument, generatedMetadata.Assembly,
            generatedMetadata.StateMachineType, generatedMetadata.File);
        if (sourceClass is null || generatedClass is null)
        {
            return;
        }

        var sourceMethod = FindUniqueMethod(sourceClass, sourceMetadata.SourceMethod);
        var moveNext = FindUniqueMethod(generatedClass, "MoveNext");
        if (sourceMethod is null || moveNext is null)
        {
            return;
        }

        var generatedLines = new HashSet<int>(generatedMetadata.Lines);
        foreach (var lineNumber in sourceMetadata.Lines.Distinct())
        {
            if (!generatedLines.Contains(lineNumber)
                || (projectedClassName is not null && !IsUniqueProjectedSourceLine(sourceSymbols, sourceMetadata, projectedClassName, lineNumber))
                || !TryFindUniqueLine(sourceMethod, lineNumber, out var sourceLine)
                || !TryFindUniqueLine(moveNext, lineNumber, out var generatedLine))
            {
                continue;
            }

            var sourceObservation = ReadObservation(sourceLine);
            var generatedObservation = ReadObservation(generatedLine);
            if (sourceObservation is null || generatedObservation is null
                || !HaveSameBranchDenominator(sourceObservation, generatedObservation))
            {
                continue;
            }

            AddPromotion(sourceObservation, generatedLine, generatedObservation, promotions);
            AddPromotion(generatedObservation, sourceLine, sourceObservation, promotions);

            AddClassPromotion(sourceClass, sourceObservation, generatedClass, generatedObservation, lineNumber, promotions);
        }
    }

    private static XElement? FindSourceClass(
        XDocument document,
        CoverageAsyncMethod metadata,
        out string? projectedClassName)
    {
        projectedClassName = null;
        var exactMatches = FindClasses(document, metadata.Assembly, metadata.SourceType, metadata.File);
        if (exactMatches.Length == 1)
        {
            return exactMatches[0];
        }

        if (exactMatches.Length != 0 || !TryGetOuterClassName(metadata.SourceType, out var outerClassName))
        {
            return null;
        }

        var outerMatches = FindClasses(document, metadata.Assembly, outerClassName, metadata.File);
        if (outerMatches.Length != 1)
        {
            return null;
        }

        projectedClassName = outerClassName;
        return outerMatches[0];
    }

    private static XElement[] FindClasses(XDocument document, string assembly, string typeName, string file)
    {
        return document.Root?.Elements("packages")
            .SelectMany(packages => packages.Elements("package"))
            .Where(package => string.Equals(package.Attribute("name")?.Value, assembly, StringComparison.Ordinal))
            .SelectMany(package => package.Elements("classes"))
            .SelectMany(classes => classes.Elements("class"))
            .Where(sourceClass => string.Equals(sourceClass.Attribute("name")?.Value, typeName, StringComparison.Ordinal)
                && string.Equals(sourceClass.Attribute("filename")?.Value, file, StringComparison.Ordinal))
            .ToArray() ?? [];
    }

    private static XElement? FindUniqueClass(XDocument document, string assembly, string typeName, string file)
    {
        var classes = FindClasses(document, assembly, typeName, file);
        return classes.Length == 1 ? classes[0] : null;
    }

    private static bool TryGetOuterClassName(string sourceType, out string outerClassName)
    {
        var nestingSeparator = sourceType.IndexOf('/');
        if (nestingSeparator <= 0 || nestingSeparator == sourceType.Length - 1)
        {
            outerClassName = string.Empty;
            return false;
        }

        outerClassName = sourceType[..nestingSeparator];
        return true;
    }

    private static bool IsUniqueProjectedSourceLine(
        IReadOnlyList<CoverageAsyncMethod> sourceSymbols,
        CoverageAsyncMethod sourceMetadata,
        string outerClassName,
        int lineNumber)
    {
        var matchingRecords = sourceSymbols.Count(symbol =>
            string.Equals(symbol.Assembly, sourceMetadata.Assembly, StringComparison.Ordinal)
            && string.Equals(GetProjectedClassName(symbol.SourceType), outerClassName, StringComparison.Ordinal)
            && string.Equals(symbol.File, sourceMetadata.File, StringComparison.Ordinal)
            && string.Equals(symbol.SourceMethod, sourceMetadata.SourceMethod, StringComparison.Ordinal)
            && symbol.Lines.Contains(lineNumber));
        return matchingRecords == 1;
    }

    private static string GetProjectedClassName(string sourceType)
    {
        return TryGetOuterClassName(sourceType, out var outerClassName) ? outerClassName : sourceType;
    }

    private static XElement? FindUniqueMethod(XElement sourceClass, string methodName)
    {
        var methods = sourceClass.Elements("methods")
            .SelectMany(container => container.Elements("method"))
            .Where(method => string.Equals(method.Attribute("name")?.Value, methodName, StringComparison.Ordinal))
            .ToArray();
        return methods.Length == 1 ? methods[0] : null;
    }

    private static bool TryFindUniqueLine(XElement owner, int lineNumber, out XElement line)
    {
        var containers = owner.Elements("lines").ToArray();
        if (containers.Length != 1)
        {
            line = null!;
            return false;
        }

        var matches = containers[0].Elements("line")
            .Where(candidate => int.TryParse(candidate.Attribute("number")?.Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number == lineNumber)
            .ToArray();
        line = matches.Length == 1 ? matches[0] : null!;
        return matches.Length == 1;
    }

    private static void AddClassPromotion(
        XElement sourceClass,
        LineObservation sourceMethodObservation,
        XElement generatedClass,
        LineObservation generatedMethodObservation,
        int lineNumber,
        Dictionary<XElement, LinePromotion> promotions)
    {
        if (!HasUniqueMethodLine(sourceClass, lineNumber)
            || !HasUniqueMethodLine(generatedClass, lineNumber)
            || !TryFindUniqueLine(sourceClass, lineNumber, out var sourceClassLine)
            || !TryFindUniqueLine(generatedClass, lineNumber, out var generatedClassLine))
        {
            return;
        }

        var sourceClassObservation = ReadObservation(sourceClassLine);
        var generatedClassObservation = ReadObservation(generatedClassLine);
        if (sourceClassObservation is null || generatedClassObservation is null
            || sourceClassObservation != sourceMethodObservation
            || generatedClassObservation != generatedMethodObservation)
        {
            return;
        }

        AddPromotion(sourceClassObservation, generatedClassLine, generatedClassObservation, promotions);
        AddPromotion(generatedClassObservation, sourceClassLine, sourceClassObservation, promotions);
    }

    private static bool HasUniqueMethodLine(XElement sourceClass, int lineNumber)
    {
        var matches = sourceClass.Elements("methods")
            .SelectMany(container => container.Elements("method"))
            .SelectMany(method => method.Elements("lines"))
            .SelectMany(lines => lines.Elements("line"))
            .Count(line => int.TryParse(line.Attribute("number")?.Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number == lineNumber);
        return matches == 1;
    }

    private static void AddPromotion(
        LineObservation sourceObservation,
        XElement targetLine,
        LineObservation targetObservation,
        Dictionary<XElement, LinePromotion> promotions)
    {
        var hit = targetObservation.Hits == 0 ? sourceObservation.Hits : 0;
        var fullBranches = sourceObservation.Hits > 0 && sourceObservation.HasBranches
            && sourceObservation.CoveredBranches == sourceObservation.ValidBranches
            && targetObservation.CoveredBranches < targetObservation.ValidBranches
            ? sourceObservation.ValidBranches : (long?)null;
        if (hit == 0 && fullBranches is null) return;
        var existing = promotions.GetValueOrDefault(targetLine);
        promotions[targetLine] = new LinePromotion(Math.Max(existing.Hits, hit), existing.FullBranches ?? fullBranches);
    }

    private static bool HaveSameBranchDenominator(LineObservation left, LineObservation right)
    {
        return left.HasBranches == right.HasBranches && left.ValidBranches == right.ValidBranches;
    }

    private static LineObservation? ReadObservation(XElement line)
    {
        if (!long.TryParse(line.Attribute("hits")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hits)
            || hits < 0)
            return null;
        var flag = line.Attribute("branch")?.Value;
        var hasBranches = string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
        if (hasBranches != (line.Attribute("condition-coverage") is not null)
            || (!hasBranches && !string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase)))
            return null;

        var (covered, valid) = CoverageFanInComposer.ReadBranchCounts(line);
        return new LineObservation(hits, hasBranches, covered, valid);
    }

    private readonly record struct LinePromotion(long Hits, long? FullBranches);

    private sealed record LineObservation(long Hits, bool HasBranches, long CoveredBranches, long ValidBranches);
}
