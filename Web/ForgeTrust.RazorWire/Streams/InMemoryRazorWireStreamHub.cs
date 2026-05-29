using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ForgeTrust.RazorWire.Streams;

/// <summary>
/// Provides an in-memory implementation of <see cref="IRazorWireStreamHub"/> using <see cref="Channel{T}"/> for message distribution.
/// </summary>
/// <remarks>
/// Live subscriber state is tracked separately from replay state. Empty live channels are removed after the last
/// subscriber disconnects or after publish-time pruning finds stale writers, while replay buffers remain available until
/// the bounded replay retention policy removes them. The hub keeps up to 25 retained messages per replay channel and
/// prunes inactive replay channels when more than 256 replay channels are retained.
/// </remarks>
public class InMemoryRazorWireStreamHub : IRazorWireStreamHub
{
    private const int ReplayCapacity = 25;
    private const int MaxReplayChannels = 256;
    private const int ReplayLockStripeCount = 64;

    private readonly ConcurrentDictionary<string, LiveChannelState> _channels = new();
    private readonly ConcurrentDictionary<ChannelReader<string>, LiveSubscription> _subscriptionsByReader = new();
    private readonly ConcurrentDictionary<ChannelWriter<string>, LiveSubscription> _subscriptionsByWriter = new();
    private readonly ConcurrentDictionary<string, Queue<string>> _replayMessages = new();
    private readonly object[] _replayLocks = CreateReplayLocks();
    private readonly ConcurrentDictionary<string, ReplayTouch> _replayTouched = new();
    private long _replayTouchSequence;

    /// <summary>
    /// Gets or sets an internal test hook that runs after an empty live channel state is retired and before it is removed from the live channel map.
    /// </summary>
    /// <remarks>
    /// Production code leaves this unset. Regression tests use it to force subscribe/cleanup interleavings that would
    /// otherwise depend on timing.
    /// </remarks>
    internal Action? AfterLiveChannelRetiredForTesting { get; set; }

    /// <summary>
    /// Gets or sets an internal test hook that runs before a replay prune candidate takes its channel stripe lock.
    /// </summary>
    /// <remarks>
    /// Production code leaves this unset. Regression tests use it to force publish/prune interleavings that would
    /// otherwise depend on timing.
    /// </remarks>
    internal Action<string>? BeforeReplayPruneCandidateForTesting { get; set; }

    /// <summary>
    /// Publishes a message to all subscribers of the specified channel. Any subscribers that are closed or unable to accept the message are removed during the process.
    /// </summary>
    /// <param name="channel">The name of the channel to publish to.</param>
    /// <param name="message">The message payload to deliver to subscribers.</param>
    /// <returns>`ValueTask.CompletedTask` on success, or a faulted `ValueTask` containing the exception if publishing failed.</returns>
    public ValueTask PublishAsync(string channel, string message)
    {
        return PublishAsync(channel, message, options: null);
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(string channel, string message, RazorWireStreamPublishOptions? options)
    {
        try
        {
            if (options?.Replay == true)
            {
                var gate = GetReplayLock(channel);
                lock (gate)
                {
                    AddReplayMessageLocked(channel, message);
                    PublishLiveMessage(channel, message);
                }

                PruneReplayStateIfNeeded();
            }
            else
            {
                PublishLiveMessage(channel, message);
            }

            return ValueTask.CompletedTask;
        }
        catch (Exception exception)
        {
            return ValueTask.FromException(exception);
        }
    }

    /// <summary>
    /// Subscribes to a named channel and returns a reader that receives messages published to that channel.
    /// The subscription uses an in-memory bounded buffer with capacity 100 that drops the oldest messages when full.
    /// </summary>
    /// <param name="channel">The name of the channel to subscribe to.</param>
    /// <returns>A <see cref="ChannelReader{String}"/> that yields messages published to the specified channel until the subscription is removed or the writer is completed.</returns>
    public ChannelReader<string> Subscribe(string channel)
    {
        return Subscribe(channel, options: null);
    }

    /// <inheritdoc />
    public ChannelReader<string> Subscribe(string channel, RazorWireStreamSubscribeOptions? options)
    {
        var subscriber = Channel.CreateBounded<string>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });

        if (options?.Replay == true)
        {
            var gate = GetReplayLock(channel);
            lock (gate)
            {
                foreach (var message in GetReplayMessagesLocked(channel))
                {
                    subscriber.Writer.TryWrite(message);
                }

                AddLiveSubscriber(channel, subscriber.Reader, subscriber.Writer);
            }

            return subscriber.Reader;
        }

        AddLiveSubscriber(channel, subscriber.Reader, subscriber.Writer);
        return subscriber.Reader;
    }

    /// <summary>
    /// Unregisters a subscriber from the specified channel and completes its associated writer.
    /// </summary>
    /// <param name="channel">
    /// The channel name supplied by the caller. The in-memory hub removes the reader from its recorded channel so endpoint
    /// cleanup remains correct even if this value is stale, but application code should still pass the same channel used
    /// for subscription for clarity and compatibility with other implementations.
    /// </param>
    /// <param name="reader">The subscriber's <see cref="ChannelReader{String}"/>; its paired writer will be completed and removed from channel tracking.</param>
    public void Unsubscribe(string channel, ChannelReader<string> reader)
    {
        if (_subscriptionsByReader.TryRemove(reader, out var subscription))
        {
            _subscriptionsByWriter.TryRemove(subscription.Writer, out _);
            subscription.Writer.TryComplete();
            RemoveSubscriberFromLiveState(subscription);
        }
    }

    private void PublishLiveMessage(string channel, string message)
    {
        if (!_channels.TryGetValue(channel, out var state))
        {
            return;
        }

        foreach (var subscriber in state.SnapshotWriters())
        {
            if (!subscriber.TryWrite(message))
            {
                RemoveClosedSubscriber(channel, state, subscriber);
            }
        }
    }

    private void AddLiveSubscriber(string channel, ChannelReader<string> reader, ChannelWriter<string> writer)
    {
        while (true)
        {
            var state = _channels.GetOrAdd(channel, _ => new LiveChannelState());
            if (!state.TryAdd(writer))
            {
                TryRemoveLiveChannel(channel, state);
                continue;
            }

            var subscription = new LiveSubscription(channel, reader, writer, state);
            _subscriptionsByReader[reader] = subscription;
            _subscriptionsByWriter[writer] = subscription;
            return;
        }
    }

    private void RemoveClosedSubscriber(string fallbackChannel, LiveChannelState fallbackState, ChannelWriter<string> writer)
    {
        writer.TryComplete();
        if (_subscriptionsByWriter.TryRemove(writer, out var subscription))
        {
            _subscriptionsByReader.TryRemove(subscription.Reader, out _);
            RemoveSubscriberFromLiveState(subscription);
            return;
        }

        fallbackState.Remove(writer);
        RemoveLiveChannelIfEmpty(fallbackChannel, fallbackState);
    }

    private void RemoveSubscriberFromLiveState(LiveSubscription subscription)
    {
        subscription.State.Remove(subscription.Writer);
        RemoveLiveChannelIfEmpty(subscription.ChannelName, subscription.State);
    }

    private void RemoveLiveChannelIfEmpty(string channel, LiveChannelState state)
    {
        if (state.TryRetireIfEmpty())
        {
            AfterLiveChannelRetiredForTesting?.Invoke();
            TryRemoveLiveChannel(channel, state);
        }
    }

    private bool TryRemoveLiveChannel(string channel, LiveChannelState state)
    {
        return ((ICollection<KeyValuePair<string, LiveChannelState>>)_channels)
            .Remove(new KeyValuePair<string, LiveChannelState>(channel, state));
    }

    private void AddReplayMessageLocked(string channel, string message)
    {
        var messages = _replayMessages.GetOrAdd(channel, _ => new Queue<string>());
        messages.Enqueue(message);
        while (messages.Count > ReplayCapacity)
        {
            messages.Dequeue();
        }

        _replayTouched[channel] = new ReplayTouch(
            DateTimeOffset.UtcNow,
            Interlocked.Increment(ref _replayTouchSequence));
    }

    private IReadOnlyList<string> GetReplayMessagesLocked(string channel)
    {
        if (!_replayMessages.TryGetValue(channel, out var messages))
        {
            return [];
        }

        return messages.ToArray();
    }

    private void PruneReplayStateIfNeeded()
    {
        if (_replayMessages.Count <= MaxReplayChannels)
        {
            return;
        }

        foreach (var candidate in _replayTouched
            .OrderBy(item => item.Value.TouchedUtc)
            .ThenBy(item => item.Value.Sequence)
            .ToArray())
        {
            if (_replayMessages.Count <= MaxReplayChannels)
            {
                return;
            }

            var channel = candidate.Key;
            BeforeReplayPruneCandidateForTesting?.Invoke(channel);

            var gate = GetReplayLock(channel);
            lock (gate)
            {
                if (_replayMessages.Count <= MaxReplayChannels)
                {
                    return;
                }

                if (!_replayTouched.TryGetValue(channel, out var currentTouch) || currentTouch != candidate.Value)
                {
                    continue;
                }

                if (!_replayMessages.ContainsKey(channel) || HasLiveSubscribers(channel))
                {
                    continue;
                }

                _replayMessages.TryRemove(channel, out _);
                _replayTouched.TryRemove(channel, out _);
            }
        }
    }

    private bool HasLiveSubscribers(string channel)
    {
        return _channels.TryGetValue(channel, out var state) && state.HasSubscribers;
    }

    private object GetReplayLock(string channel)
    {
        var index = unchecked((uint)StringComparer.Ordinal.GetHashCode(channel) % (uint)_replayLocks.Length);
        return _replayLocks[index];
    }

    private static object[] CreateReplayLocks()
    {
        var locks = new object[ReplayLockStripeCount];
        for (var index = 0; index < locks.Length; index++)
        {
            locks[index] = new object();
        }

        return locks;
    }

    /// <summary>
    /// Gets internal stream state counts used by regression tests to verify cleanup behavior.
    /// </summary>
    /// <returns>A snapshot of live and replay tracking counts.</returns>
    internal InMemoryRazorWireStreamHubDiagnostics GetDiagnostics()
    {
        var liveSubscriberCount = 0;
        foreach (var state in _channels.Values)
        {
            liveSubscriberCount += state.Count;
        }

        return new InMemoryRazorWireStreamHubDiagnostics(
            _channels.Count,
            liveSubscriberCount,
            _subscriptionsByReader.Count,
            _replayMessages.Count);
    }

    /// <summary>
    /// Completes the writer for an active subscription without removing the subscription maps.
    /// </summary>
    /// <param name="reader">The reader whose paired writer should be completed.</param>
    /// <returns><see langword="true"/> when the reader belongs to an active subscription; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// This internal test seam simulates a writer closed by channel infrastructure so publish-time pruning can be verified
    /// through the same cleanup path that handles stale live subscribers.
    /// </remarks>
    internal bool TryCompleteSubscriberWriterForTesting(ChannelReader<string> reader)
    {
        if (!_subscriptionsByReader.TryGetValue(reader, out var subscription))
        {
            return false;
        }

        return subscription.Writer.TryComplete();
    }

    /// <summary>
    /// Represents a snapshot of internal stream tracking counts used by the RazorWire test suite.
    /// </summary>
    /// <param name="LiveChannelCount">The number of live channel state entries currently retained.</param>
    /// <param name="LiveSubscriberCount">The number of active live subscriber writers currently retained.</param>
    /// <param name="SubscriptionCount">The number of reader-owned subscription records currently retained.</param>
    /// <param name="ReplayChannelCount">The number of channels with retained replay messages.</param>
    internal readonly record struct InMemoryRazorWireStreamHubDiagnostics(
        int LiveChannelCount,
        int LiveSubscriberCount,
        int SubscriptionCount,
        int ReplayChannelCount);

    private sealed class LiveChannelState
    {
        private readonly object _gate = new();
        private readonly HashSet<ChannelWriter<string>> _writers = [];
        private bool _retired;

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _writers.Count;
                }
            }
        }

        public bool HasSubscribers
        {
            get
            {
                lock (_gate)
                {
                    return _writers.Count > 0;
                }
            }
        }

        public bool TryAdd(ChannelWriter<string> writer)
        {
            lock (_gate)
            {
                if (_retired)
                {
                    return false;
                }

                return _writers.Add(writer);
            }
        }

        public bool Remove(ChannelWriter<string> writer)
        {
            lock (_gate)
            {
                return _writers.Remove(writer);
            }
        }

        public IReadOnlyList<ChannelWriter<string>> SnapshotWriters()
        {
            lock (_gate)
            {
                return [.. _writers];
            }
        }

        public bool TryRetireIfEmpty()
        {
            lock (_gate)
            {
                if (_writers.Count > 0)
                {
                    return false;
                }

                _retired = true;
                return true;
            }
        }
    }

    private readonly record struct LiveSubscription(
        string ChannelName,
        ChannelReader<string> Reader,
        ChannelWriter<string> Writer,
        LiveChannelState State);

    private readonly record struct ReplayTouch(DateTimeOffset TouchedUtc, long Sequence);
}
