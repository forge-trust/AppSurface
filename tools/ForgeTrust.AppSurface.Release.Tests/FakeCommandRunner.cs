using ForgeTrust.AppSurface.Release;

namespace ForgeTrust.AppSurface.Release.Tests;

internal sealed class FakeCommandRunner : ICommandRunner
{
    private readonly Dictionary<string, CommandResult> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<CommandResult>> _sequences = new(StringComparer.Ordinal);

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
        var command = invocation.Executable + " " + string.Join(' ', invocation.Arguments);
        if (_sequences.TryGetValue(command, out var sequence) && sequence.Count > 0)
        {
            return Task.FromResult(sequence.Count > 1 ? sequence.Dequeue() : sequence.Peek());
        }

        return Task.FromResult(_results.TryGetValue(command, out var result)
            ? result
            : new CommandResult(1, "", "command not configured"));
    }
}
