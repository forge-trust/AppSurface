using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using Markdig;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Diagnostic envelope with a stable code and reader-actionable context.
/// </summary>
internal sealed record ReleaseDiagnostic(
    string Severity,
    string Code,
    string Problem,
    string Cause,
    string Fix,
    string Docs)
{
    /// <summary>
    /// Creates an error diagnostic.
    /// </summary>
    internal static ReleaseDiagnostic Error(string code, string problem, string cause, string fix, string docs)
    {
        return new ReleaseDiagnostic("error", code, problem, cause, fix, docs);
    }

    /// <summary>
    /// Creates a warning diagnostic.
    /// </summary>
    internal static ReleaseDiagnostic Warning(string code, string problem, string cause, string fix, string docs)
    {
        return new ReleaseDiagnostic("warning", code, problem, cause, fix, docs);
    }

    /// <summary>
    /// Renders the diagnostic envelope for CLI stderr.
    /// </summary>
    /// <returns>Human-readable diagnostic envelope.</returns>
    internal string Render()
    {
        return $"""
            Code: {Code}
            Problem: {Problem}
            Cause: {Cause}
            Fix: {Fix}
            Docs: {Docs}
            """;
    }
}

/// <summary>
/// Exception that carries a structured release diagnostic.
/// </summary>
internal sealed class ReleaseToolException : Exception
{
    /// <summary>
    /// Creates a release exception.
    /// </summary>
    /// <param name="diagnostic">Diagnostic to render to users.</param>
    internal ReleaseToolException(ReleaseDiagnostic diagnostic)
        : base(diagnostic.Problem)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>
    /// Gets the structured diagnostic.
    /// </summary>
    internal ReleaseDiagnostic Diagnostic { get; }
}
