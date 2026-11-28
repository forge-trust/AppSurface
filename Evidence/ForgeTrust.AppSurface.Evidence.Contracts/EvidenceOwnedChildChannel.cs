using System.Collections.ObjectModel;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Owns the environment mapping for one cooperative child channel.</summary>
/// <param name="pipeName">The current-user invocation pipe.</param>
/// <param name="executionId">The invocation's execution identifier.</param>
/// <param name="nonce">The invocation's enrollment nonce.</param>
/// <remarks>The coordinator supplies generated channel values. Clients use <see cref="Read"/> to validate
/// the environment before enrolling. Channel values are invocation-local and must be kept out of logs and keys.</remarks>
internal sealed class EvidenceOwnedChildChannel(string pipeName, string executionId, string nonce)
{
    /// <summary>Names the environment variable for the invocation pipe.</summary>
    internal const string PipeEnvironmentName = "APPSURFACE_EVIDENCE_CHILD_PIPE";
    /// <summary>Names the environment variable for the current execution.</summary>
    internal const string ExecutionEnvironmentName = "APPSURFACE_EVIDENCE_CHILD_EXECUTION_ID";
    /// <summary>Names the environment variable for the enrollment nonce.</summary>
    internal const string NonceEnvironmentName = "APPSURFACE_EVIDENCE_CHILD_NONCE";

    private static readonly string[] EnvironmentNames = [PipeEnvironmentName, ExecutionEnvironmentName, NonceEnvironmentName];

    /// <summary>Gets the current-user invocation pipe.</summary>
    internal string PipeName { get; } = pipeName;
    /// <summary>Gets the invocation's execution identifier.</summary>
    internal string ExecutionId { get; } = executionId;
    /// <summary>Gets the invocation's enrollment nonce.</summary>
    internal string Nonce { get; } = nonce;

    /// <summary>Gets the read-only channel values to pass to an owned child.</summary>
    internal IReadOnlyDictionary<string, string> Environment { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PipeEnvironmentName] = pipeName,
            [ExecutionEnvironmentName] = executionId,
            [NonceEnvironmentName] = nonce,
        });

    /// <summary>Reads only the channel variables present in the current process environment.</summary>
    internal static IReadOnlyDictionary<string, string> ReadAmbientEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in EnvironmentNames)
            if (System.Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        return environment;
    }

    /// <summary>Validates the channel values supplied to an enrolled child.</summary>
    /// <param name="environment">Invocation environment using the caller's key comparison.</param>
    /// <returns>A complete channel, or null when every channel variable is absent.</returns>
    /// <exception cref="InvalidOperationException">At least one channel key exists and the channel is partial or invalid.</exception>
    internal static EvidenceOwnedChildChannel? Read(IReadOnlyDictionary<string, string> environment)
    {
        var hasPipe = environment.TryGetValue(PipeEnvironmentName, out var pipeName);
        var hasExecution = environment.TryGetValue(ExecutionEnvironmentName, out var executionId);
        var hasNonce = environment.TryGetValue(NonceEnvironmentName, out var nonce);
        if (!hasPipe && !hasExecution && !hasNonce) return null;
        if (!hasPipe || !hasExecution || !hasNonce
            || !IsProtocolToken(pipeName, 180) || !IsValidExecutionId(executionId) || !IsProtocolToken(nonce, 128))
            throw new InvalidOperationException("Cooperative child enrollment requires a valid current-user Evidence invocation channel.");
        return new(pipeName!, executionId!, nonce!);
    }

    /// <summary>Checks the execution ID accepted by both client and coordinator.</summary>
    internal static bool IsValidExecutionId(string? value) => IsProtocolToken(value, 128);

    /// <summary>Copies the complete channel to an owned process environment.</summary>
    internal void ApplyTo(IDictionary<string, string?> environment)
    {
        foreach (var (name, value) in Environment) environment[name] = value;
    }

    /// <summary>Removes inherited channel values from a process that will run without enrollment.</summary>
    internal static void RemoveFrom(IDictionary<string, string?> environment)
    {
        foreach (var name in EnvironmentNames) environment.Remove(name);
    }

    private static bool IsProtocolToken(string? value, int maximumLength)
        => value is { Length: > 0 } && value.Length <= maximumLength
            && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
