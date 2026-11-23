using System.Collections.ObjectModel;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Decorates the injected coverage runner with one cooperative-v1 invocation's environment and SDK isolation.</summary>
/// <remarks>
/// The coordinator owns the session, enrollment closure and settlement wait. This wrapper neither closes enrollment
/// after a command nor infers descendant settlement from an SDK process exit. It does not shut down shared servers.
/// Only dotnet processes receive the invocation channel; all overrides are request-local snapshots.
/// </remarks>
internal sealed class EvidenceCoverageProcessRunner : ICoverageRunProcessRunner
{
    private readonly ICoverageRunProcessRunner _inner;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly EvidenceChildLifetimeSession? _session;

    /// <summary>Gets stable protocol and effective isolation tokens for producer key material.</summary>
    /// <remarks>Contains no execution ID, endpoint, nonce, working directory or other invocation-specific path.</remarks>
    internal static IReadOnlyList<string> EffectiveArgumentIdentity { get; } = Array.AsReadOnly(new[]
    {
        "child-lifetime:cooperative-v1",
        "environment:DOTNET_CLI_USE_MSBUILD_SERVER=0",
        "environment:MSBUILDDISABLENODEREUSE=1",
        "msbuild:-nodeReuse:false",
        "msbuild:-p:UseSharedCompilation=false",
        "build:--disable-build-servers",
        "build:-nodeReuse:false",
        "build:-p:UseSharedCompilation=false",
        "test:--disable-build-servers",
        "test:-nodeReuse:false",
        "test:-p:UseSharedCompilation=false",
    }.Order(StringComparer.Ordinal).ToArray());

    /// <summary>Wraps the existing injected runner for a single coordinator-owned lifetime session.</summary>
    internal EvidenceCoverageProcessRunner(ICoverageRunProcessRunner inner, EvidenceChildLifetimeSession session)
        : this(inner, (session ?? throw new ArgumentNullException(nameof(session))).Environment, session) { }

    /// <summary>Snapshots channel variables for request-level verification without creating or owning a session.</summary>
    /// <remarks>The production adapter uses the session constructor; this seam permits deterministic runner tests.</remarks>
    internal EvidenceCoverageProcessRunner(ICoverageRunProcessRunner inner, IReadOnlyDictionary<string, string> environment)
        : this(inner, environment, null) { }

    private EvidenceCoverageProcessRunner(ICoverageRunProcessRunner inner, IReadOnlyDictionary<string, string> environment,
        EvidenceChildLifetimeSession? session)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(environment);
        _environment = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(environment, EnvironmentComparer));
        _session = session;
    }

    /// <inheritdoc />
    public Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsDotnet(request.FileName)) return _inner.RunAsync(request, cancellationToken);
        if (_session is { CanAcceptChildren: false })
            throw new IOException("The cooperative child channel is no longer available for SDK process enrollment.");
        var environment = request.EnvironmentOverrides is { } original
            ? new Dictionary<string, string?>(original, EnvironmentComparer)
            : new Dictionary<string, string?>(EnvironmentComparer);
        foreach (var variable in _environment) environment[variable.Key] = variable.Value;
        environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        environment["MSBUILDDISABLENODEREUSE"] = "1";
        var arguments = request.Arguments.ToList();
        var operation = arguments.FirstOrDefault();
        if (operation is "msbuild" or "build" or "test")
        {
            // Insert before '--' so a test application's own arguments are never rewritten.
            var boundary = arguments.IndexOf("--");
            if (boundary < 0) boundary = arguments.Count;
            var isolation = operation == "msbuild"
                ? new[] { "-nodeReuse:false", "-p:UseSharedCompilation=false" }
                : new[] { "--disable-build-servers", "-nodeReuse:false", "-p:UseSharedCompilation=false" };
            arguments.InsertRange(boundary, isolation);
        }
        return _inner.RunAsync(request with
        {
            Arguments = arguments.AsReadOnly(),
            EnvironmentOverrides = new ReadOnlyDictionary<string, string?>(environment)
        }, cancellationToken);
    }

    private static StringComparer EnvironmentComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool IsDotnet(string fileName) => Path.GetFileName(fileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(fileName).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
}
