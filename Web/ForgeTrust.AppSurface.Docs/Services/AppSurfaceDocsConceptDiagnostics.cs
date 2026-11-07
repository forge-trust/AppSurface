using System.Diagnostics;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>Maps concept-domain failures to safe, caller-owned operator diagnostics.</summary>
/// <remarks>Reader HTML never contains these diagnostics, local paths, exception messages, or request query strings.</remarks>
internal static class AppSurfaceDocsConceptDiagnostics
{
    internal const string Reference = "https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Docs/README.md#concept-switch-diagnostics";

    /// <summary>Reports an expected optional-data or presentation rejection without dumping exception text.</summary>
    /// <param name="logger">Protected operator log.</param>
    /// <param name="code">Stable diagnostic code.</param>
    /// <param name="operation">Package-owned operation name.</param>
    /// <param name="artifact">Package-owned artifact type, excluding filesystem or caller URLs.</param>
    /// <param name="limit">Effective byte budget.</param>
    /// <param name="observed">Observed byte count, or null when rejection preceded a read.</param>
    /// <param name="detail">Safe package-owned validation detail, excluding exception messages and reader query values.</param>
    internal static void Rejected(ILogger logger, string code, string operation, string artifact, long limit, long? observed = null, string? detail = null)
    {
        logger.LogWarning(
            "{Code}: Concept switching rejected {Operation} {Artifact}; {Detail}; configured limit {ConfiguredBytes}, observed {ObservedBytes}. Correct the source metadata or catalog mapping, re-export and verify any changed archive, update its matching pins, then restart/redeploy. For overflow, reduce the complete artifact or deliberately adjust AppSurfaceDocs:Versioning:MaxRewrittenFileSizeBytes within 1..33554432; this also raises the HTML/search-input budget. Never patch an immutable archive or broaden CSP. Docs: {Docs}",
            code, operation, artifact, detail, limit, observed, Reference);
    }

    /// <summary>Reports sanitized unexpected processing context without copying nested exception messages.</summary>
    /// <param name="logger">Protected operator log.</param>
    /// <param name="context">Request used only for the existing correlation identifier.</param>
    /// <param name="exception">Failure whose type and bounded method identities aid investigation.</param>
    internal static void Unexpected(ILogger logger, HttpContext context, Exception exception)
    {
        var methods = new StackTrace(exception, fNeedFileInfo: false).GetFrames()
            .Take(8)
            .Select(frame => frame.GetMethod())
            .Where(method => method is not null)
            .Select(method => $"{method!.DeclaringType?.FullName}.{method.Name}");
        logger.LogError(
            "docs-switch-processing-failed: Unexpected concept presentation failure {ExceptionType}; correlation {CorrelationId}; methods {Methods}. Preserve the failing deployment and contact its maintainer with this correlation. Correct the cause before retrying once; do not modify archive bytes or CSP. Docs: {Docs}",
            exception.GetType().FullName,
            Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
            string.Join(";", methods), Reference);
    }
}
