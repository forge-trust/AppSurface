using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Release;

namespace ForgeTrust.AppSurface.Release.Tests;

internal sealed class FakeCommandRunner : ICommandRunner
{
    private readonly Dictionary<string, CommandResult> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<CommandResult>> _sequences = new(StringComparer.Ordinal);
    private readonly List<string> _calls = [];

    internal IReadOnlyList<string> Calls => _calls;

    internal Action<CommandInvocation>? BeforeRun { get; set; }

    internal static FakeCommandRunner WithSourceCommit(string sourceCommit)
    {
        var runner = new FakeCommandRunner();
        runner.Add("git rev-parse HEAD", new CommandResult(0, sourceCommit + "\n", ""));
        runner.Add("git for-each-ref --format=%(refname:short) refs/tags/v*", new CommandResult(0, "", ""));
        return runner;
    }

    internal void Add(string command, CommandResult result)
    {
        _results[command] = result;
    }

    internal void AddSequence(string command, params CommandResult[] results)
    {
        _sequences[command] = new Queue<CommandResult>(results);
    }

    public Task<CommandResult> RunAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        BeforeRun?.Invoke(invocation);
        var command = invocation.Executable + " " + string.Join(' ', invocation.Arguments);
        _calls.Add(command);
        if (_sequences.TryGetValue(command, out var sequence) && sequence.Count > 0)
        {
            return Task.FromResult(sequence.Count > 1 ? sequence.Dequeue() : sequence.Peek());
        }

        if (_results.TryGetValue(command, out var result))
        {
            return Task.FromResult(result);
        }

        return Task.FromResult(TryCreateCanonicalTagObject(invocation) ?? new CommandResult(1, "", "command not configured"));
    }

    private CommandResult? TryCreateCanonicalTagObject(CommandInvocation invocation)
    {
        if (!string.Equals(invocation.Executable, "git", StringComparison.Ordinal)
            || invocation.Arguments.Count != 3
            || !string.Equals(invocation.Arguments[0], "cat-file", StringComparison.Ordinal)
            || !string.Equals(invocation.Arguments[1], "-p", StringComparison.Ordinal)
            || !invocation.Arguments[2].StartsWith("refs/tags/v", StringComparison.Ordinal))
        {
            return null;
        }

        var tag = invocation.Arguments[2]["refs/tags/".Length..];
        var version = tag[1..];
        var sidecar = GetStandardOutput($"git show {tag}:releases/v{version}.md.yml");
        var manifest = GetStandardOutput($"git show {tag}:releases/v{version}.release.json");
        var evidence = GetStandardOutput($"git show {tag}:releases/v{version}.evidence.json");
        var subjectSha256 = TryReadEvidenceSubjectSha256(evidence) ?? new string('0', 64);

        var binding = new ReleaseTagBinding(
            tag,
            ReleaseEvidence.ComputeSha256Hex(sidecar),
            ReleaseEvidence.ComputeSha256Hex(manifest),
            subjectSha256);
        var tagObject = $"object abc123\ntype commit\ntag {tag}\ntagger Release Tests <release-tests@example.test> 1770000000 +0000\n\n{binding.Render()}";
        return new CommandResult(0, tagObject, string.Empty);
    }

    private static string? TryReadEvidenceSubjectSha256(string evidence)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(evidence));
            if (!reader.Read()
                || !JsonDocument.TryParseValue(ref reader, out var document)
                || document is null)
            {
                return null;
            }

            using (document)
            {
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("subject", out var subject)
                    && subject.TryGetProperty("sha256", out var sha256)
                    ? sha256.GetString()
                    : null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string GetStandardOutput(string command)
    {
        return _results.TryGetValue(command, out var result) && result.ExitCode == 0
            ? result.StandardOutput
            : string.Empty;
    }
}
