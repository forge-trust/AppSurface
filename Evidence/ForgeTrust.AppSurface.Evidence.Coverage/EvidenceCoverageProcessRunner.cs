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
    private const string ChildLifetimeProtocolIdentity = "child-lifetime:cooperative-v1";
    private static readonly IReadOnlyDictionary<string, string> FixedEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
        ["MSBUILDDISABLENODEREUSE"] = "1",
    };
    private static readonly string[] MsBuildIsolation = ["-nodeReuse:false", "-p:UseSharedCompilation=false"];
    private static readonly string[] BuildAndTestIsolation = ["--disable-build-servers", .. MsBuildIsolation];
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> FixedArguments =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["msbuild"] = MsBuildIsolation,
            ["build"] = BuildAndTestIsolation,
            ["test"] = BuildAndTestIsolation,
        };
    private readonly ICoverageRunProcessRunner _inner;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly EvidenceChildLifetimeSession? _session;

    /// <summary>Gets stable protocol and effective isolation tokens for producer key material.</summary>
    /// <remarks>Contains no execution ID, endpoint, nonce, working directory or other invocation-specific path.</remarks>
    internal static IReadOnlyList<string> EffectiveArgumentIdentity { get; } = Array.AsReadOnly(
        FixedEnvironment.Select(static variable => $"environment:{variable.Key}={variable.Value}")
            .Concat(FixedArguments.SelectMany(static operation => operation.Value.Select(argument => $"{operation.Key}:{argument}")))
            .Append(ChildLifetimeProtocolIdentity)
            .Order(StringComparer.Ordinal).ToArray());

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
        foreach (var variable in FixedEnvironment) environment[variable.Key] = variable.Value;
        var arguments = request.Arguments.ToList();
        var operation = arguments.FirstOrDefault();
        if (operation is not null && FixedArguments.TryGetValue(operation, out var isolation))
        {
            // Insert before '--' so a test application's own arguments are never rewritten.
            var boundary = arguments.IndexOf("--");
            if (boundary < 0) boundary = arguments.Count;
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
