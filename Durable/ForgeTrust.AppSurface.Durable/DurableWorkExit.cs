using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Describes the fact an exit-aware durable executor can prove after it is invoked.
/// </summary>
/// <remarks>
/// An exit is not a durable Work state transition. The provider remains authoritative for permits, claims,
/// cancellation, retry timing, and the resulting persisted state.
/// </remarks>
public enum DurableWorkExitKind
{
    /// <summary>The executor produced a terminal result.</summary>
    Succeeded = 0,

    /// <summary>The executor proved that it did not begin a provider mutation.</summary>
    RetryBeforeEffect = 1,

    /// <summary>The executor reached a terminal local outcome.</summary>
    FailedTerminal = 2,

    /// <summary>The executor cannot establish whether the external provider effect occurred.</summary>
    AmbiguousExternalOutcome = 3,
}

/// <summary>
/// Represents the fact returned by an <see cref="IDurableWorkExitExecutor{TWork,TResult}"/>.
/// </summary>
/// <typeparam name="TResult">The successful executor result type.</typeparam>
/// <remarks>
/// Only <see cref="Succeeded(TResult)"/> carries a result. The other factories require a bounded,
/// identifier-safe application code and do not provide a result. An executor must return
/// <see cref="RetryBeforeEffect(string)"/> only when it can prove the failure happened before a provider mutation.
/// Read-only provider I/O is allowed; this boundary is about the provider effect, not the Durable effect permit.
/// </remarks>
public sealed class DurableWorkExit<TResult>
{
    private DurableWorkExit(DurableWorkExitKind kind, string? code, TResult? result)
    {
        Kind = kind;
        Code = code;
        Result = result;
    }

    /// <summary>Gets the fact that the executor returned.</summary>
    public DurableWorkExitKind Kind { get; }

    /// <summary>
    /// Gets the safe application code for a non-success exit, or <see langword="null"/> for success.
    /// </summary>
    public string? Code { get; }

    /// <summary>
    /// Gets the successful result, or <see langword="null"/> for a non-success exit.
    /// </summary>
    public TResult? Result { get; }

    /// <summary>Creates a successful exit with a terminal result.</summary>
    /// <param name="result">The non-null terminal result.</param>
    /// <returns>A successful exit.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result"/> is null.</exception>
    public static DurableWorkExit<TResult> Succeeded(TResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new DurableWorkExit<TResult>(DurableWorkExitKind.Succeeded, null, result);
    }

    /// <summary>
    /// Creates an exit that proves the executor did not begin a provider mutation.
    /// </summary>
    /// <param name="code">A bounded, identifier-safe application diagnostic code.</param>
    /// <returns>A no-effect retry fact for the provider to evaluate.</returns>
    /// <remarks>
    /// This does not mean a Durable effect permit was absent. It asserts only that the executor did not begin an
    /// external provider mutation; read-only provider I/O is allowed. The provider still decides whether the fact is
    /// current and retryable.
    /// </remarks>
    public static DurableWorkExit<TResult> RetryBeforeEffect(string code) =>
        new(DurableWorkExitKind.RetryBeforeEffect, ValidateCode(code), default);

    /// <summary>Creates an exit for a terminal local executor outcome.</summary>
    /// <param name="code">A bounded, identifier-safe application diagnostic code.</param>
    /// <returns>A terminal-failure fact for the provider to evaluate.</returns>
    public static DurableWorkExit<TResult> FailedTerminal(string code) =>
        new(DurableWorkExitKind.FailedTerminal, ValidateCode(code), default);

    /// <summary>Creates an exit for an outcome whose external provider effect may be unknown.</summary>
    /// <param name="code">A bounded, identifier-safe application diagnostic code.</param>
    /// <returns>An ambiguous-outcome fact for the provider to evaluate.</returns>
    public static DurableWorkExit<TResult> AmbiguousExternalOutcome(string code) =>
        new(DurableWorkExitKind.AmbiguousExternalOutcome, ValidateCode(code), default);

    private static string ValidateCode(string code) => DurableWorkExitCode.RequireApplicationCode(code);
}

/// <summary>
/// Carries the encoded form of a <see cref="DurableWorkExit{TResult}"/> across the provider boundary.
/// </summary>
/// <remarks>
/// This type exposes no public constructor so providers cannot invent an arbitrary result/code combination. Its
/// result is present only for <see cref="DurableWorkExitKind.Succeeded"/>, and its code is present only for a
/// non-success exit.
/// </remarks>
public sealed class DurableEncodedWorkExit
{
    private DurableEncodedWorkExit(DurableWorkExitKind kind, string? code, DurableEncodedPayload? result)
    {
        Kind = kind;
        Code = code;
        Result = result;
    }

    /// <summary>Gets the fact returned by the executor.</summary>
    public DurableWorkExitKind Kind { get; }

    /// <summary>Gets the safe application code for a non-success exit, or <see langword="null"/> for success.</summary>
    public string? Code { get; }

    /// <summary>Gets the encoded successful result, or <see langword="null"/> for a non-success exit.</summary>
    public DurableEncodedPayload? Result { get; }

    internal static DurableEncodedWorkExit Succeeded(DurableEncodedPayload result) =>
        new(DurableWorkExitKind.Succeeded, null, result ?? throw new ArgumentNullException(nameof(result)));

    internal static DurableEncodedWorkExit RetryBeforeEffect(string code) =>
        new(DurableWorkExitKind.RetryBeforeEffect, DurableWorkExitCode.RequireApplicationCode(code), null);

    internal static DurableEncodedWorkExit FailedTerminal(string code) =>
        new(DurableWorkExitKind.FailedTerminal, DurableWorkExitCode.RequireApplicationCode(code), null);

    internal static DurableEncodedWorkExit AmbiguousExternalOutcome(string code) =>
        new(DurableWorkExitKind.AmbiguousExternalOutcome, DurableWorkExitCode.RequireApplicationCode(code), null);
}

internal static class DurableWorkExitCode
{
    internal static string RequireApplicationCode(string code)
    {
        var validated = DurableIdentifier.Require(code, nameof(code), 120);
        if (validated.StartsWith("ASDUR", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Application exit codes must not use the reserved ASDUR diagnostic prefix.", nameof(code));
        }

        return validated;
    }
}

/// <summary>
/// Executes one opt-in durable Work and returns an explicit provider-effect fact.
/// </summary>
/// <typeparam name="TWork">The decoded Work payload type.</typeparam>
/// <typeparam name="TResult">The successful result type.</typeparam>
/// <remarks>
/// Use this contract only when the executor can honestly return one of the four defined facts. Use
/// <see cref="IDurableWorkerExecutor{TWork,TResult}"/> for the established success-or-exception contract.
/// </remarks>
public interface IDurableWorkExitExecutor<TWork, TResult>
{
    /// <summary>Executes the Work and returns its explicit exit fact.</summary>
    /// <param name="work">The provider-validated Work envelope.</param>
    /// <param name="cancellationToken">Token that cancels executor invocation.</param>
    /// <returns>The executor fact for the provider to translate.</returns>
    ValueTask<DurableWorkExit<TResult>> ExecuteAsync(
        DurableWorkerEnvelope<TWork> work,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Indicates that exit-aware Work was invoked through a legacy success-only provider boundary.
/// </summary>
/// <remarks>
/// The exception intentionally includes no result, application code, provider response, or nested exception. Its
/// provider-facing handling remains the ordinary ambiguous-outcome path after a committed effect permit.
/// </remarks>
public sealed class DurableWorkExitCompatibilityException : InvalidOperationException
{
    internal DurableWorkExitCompatibilityException()
        : base("Exit-aware Work returned a non-success exit, but the provider invoked the legacy success-only boundary. Upgrade the provider to InvokeExitAsync.")
    {
    }
}
