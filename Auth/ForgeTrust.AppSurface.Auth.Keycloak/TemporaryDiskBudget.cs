namespace ForgeTrust.AppSurface.Auth.Keycloak;
/// <summary>Conservatively bounds cumulative owned output writes across one public invocation.</summary>
/// <remarks>Deleted staging bytes remain charged. Nested phases share the budget; unrelated invocations do not.</remarks>
internal static class TemporaryDiskBudget
{
    internal const long MaximumBytes = 8L * 1024 * 1024 * 1024;
    private static readonly AsyncLocal<Budget?> Current = new();
    internal static IDisposable BeginInvocation(long maximumBytes = MaximumBytes)
    {
        if (maximumBytes is <= 0 or > MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (Current.Value is not null)
            return new Scope(null);
        var budget = new Budget(maximumBytes);
        Current.Value = budget;
        return new Scope(budget);
    }

    internal static void Account(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Current.Value?.Charge(bytes);
    }

    private sealed class Budget(long maximum)
    {
        private readonly object sync = new();
        private long charged;
        internal void Charge(long bytes)
        {
            lock (sync)
            {
                if (bytes > maximum - charged)
                    throw new InvalidDataException("Invocation temporary disk budget exceeded.");
                charged += bytes;
            }
        }
    }

    private sealed class Scope(Budget? owner) : IDisposable
    {
        private Budget? owner = owner;
        public void Dispose()
        {
            var previous = Interlocked.Exchange(ref owner, null);
            if (previous is not null && ReferenceEquals(Current.Value, previous))
                Current.Value = null;
        }
    }
}
