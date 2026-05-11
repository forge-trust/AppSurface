using System.Collections.Concurrent;
using ForgeTrust.RazorWire;

namespace RazorWireWebExample.Services;

/// <summary>
/// An in-memory implementation of <see cref="IUserPresenceService"/> using a thread-safe dictionary to track user activity.
/// </summary>
public class InMemoryUserPresenceService : IUserPresenceService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _userActivity = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the time window within which a user is considered active since their last recorded activity. Must be a positive duration.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is less than or equal to <see cref="TimeSpan.Zero"/>.</exception>
    public TimeSpan ActiveWindow
    {
        get;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ActiveWindow),
                    "Active window must be a positive duration.");
            }

            field = value;
        }
    } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Records the current UTC time as the specified user's last activity in the in-memory store.
    /// </summary>
    /// <param name="username">The username to record; cannot be null, empty, or whitespace.</param>
    /// <returns>The number of users whose last activity is within the current ActiveWindow.</returns>
    /// <exception cref="System.ArgumentException">Thrown when <paramref name="username"/> is null, empty, or consists only of whitespace.</exception>
    public int RecordActivity(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username cannot be null or whitespace.", nameof(username));
        }

        var now = DateTimeOffset.UtcNow;
        _userActivity[username] = now;
        var cutoff = now - ActiveWindow;

        return _userActivity.Values.Count(v => v >= cutoff);
    }

    /// <summary>
    /// Lists users whose last recorded activity falls within the current ActiveWindow.
    /// </summary>
    /// <returns>A collection of UserPresenceInfo for users with last activity at or after (now - ActiveWindow), ordered by Username.</returns>
    public IReadOnlyList<UserPresenceInfo> GetActiveUsers()
    {
        var cutoff = DateTimeOffset.UtcNow - ActiveWindow;

        return _userActivity
            .Where(kvp => kvp.Value >= cutoff)
            .Select(kvp => new UserPresenceInfo(
                kvp.Key,
                StringUtils.ToSafeId(kvp.Key, appendHash: true),
                kvp.Value))
            .OrderBy(u => u.Username)
            .ToList();
    }

    /// <summary>
    /// Removes users whose last activity is older than the sliding ActiveWindow and returns the removed entries along with the current count of active users.
    /// </summary>
    /// <returns>`Removed`: a read-only list of user presence entries that were removed because their last activity preceded the ActiveWindow; `ActiveCount`: the number of users whose last activity is within the ActiveWindow.</returns>
    public (IReadOnlyList<UserPresenceInfo> Removed, int ActiveCount) Pulse()
    {
        var cutoff = DateTimeOffset.UtcNow - ActiveWindow;
        var removed = new List<UserPresenceInfo>();

        var collection = (ICollection<KeyValuePair<string, DateTimeOffset>>)_userActivity;

        foreach (var kvp in _userActivity)
        {
            // Atomically remove only if the value matches our snapshot.
            // This prevents removing a user who updated their activity *after* we retrieved the value but *before* we tried to remove it.
            if (kvp.Value < cutoff)
            {
                if (collection.Remove(kvp))
                {
                    removed.Add(
                        new UserPresenceInfo(
                            kvp.Key,
                            StringUtils.ToSafeId(kvp.Key, appendHash: true),
                            kvp.Value));
                }
            }
        }

        var activeCount = _userActivity.Values.Count(v => v >= cutoff);

        return (removed, activeCount);
    }
}
