using System.Diagnostics.Metrics;

namespace ForgeTrust.RazorWire.Streams;

/// <summary>
/// Controls admission for live RazorWire stream subscriptions in the current application process.
/// </summary>
/// <remarks>
/// <para>
/// The controller is the endpoint-side guardrail that sits before <see cref="IRazorWireStreamHub.Subscribe(string, RazorWireStreamSubscribeOptions)" />.
/// It validates decoded channel names, enforces the configured live-channel and live-subscription limits from
/// <see cref="RazorWireStreamOptions" />, and returns a disposable lease for accepted requests.
/// </para>
/// <para>
/// Use this controller for bounded in-process SSE fanout. It is not a distributed quota system, an IP/user fairness
/// mechanism, or a replacement for ASP.NET Core rate limiting, reverse-proxy limits, SignalR, or managed pub/sub. Each
/// accepted live SSE request consumes one subscription slot until the lease is disposed.
/// </para>
/// <para>
/// State changes are synchronized by an internal lock. Callers must dispose accepted leases exactly once the endpoint is
/// done with the matching hub subscription; the lease itself is idempotent, but skipping disposal leaves admission
/// capacity reserved for the lifetime of the process. Rejection metrics are low-cardinality diagnostics tagged only by
/// rejection reason.
/// </para>
/// </remarks>
internal sealed class RazorWireStreamAdmissionController
{
    /// <summary>
    /// Identifies the meter that publishes RazorWire admission diagnostics.
    /// </summary>
    private const string MeterName = "ForgeTrust.RazorWire";

    /// <summary>
    /// Shared process-wide meter used for admission counters and gauges.
    /// </summary>
    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Synchronizes live subscription and channel-count mutations.
    /// </summary>
    private readonly object _sync = new();

    /// <summary>
    /// Tracks live subscription counts by decoded channel name for this process.
    /// </summary>
    private readonly Dictionary<string, int> _subscriptionsByChannel = new(StringComparer.Ordinal);

    /// <summary>
    /// Counts admission rejections tagged by <c>reason</c>.
    /// </summary>
    private readonly Counter<int> _rejections;

    /// <summary>
    /// Tracks live subscription deltas for accepted and released SSE requests.
    /// </summary>
    private readonly UpDownCounter<int> _liveSubscriptionsMetric;

    /// <summary>
    /// Tracks live channel deltas when the first subscriber joins or the last subscriber leaves a channel.
    /// </summary>
    private readonly UpDownCounter<int> _liveChannelsMetric;

    /// <summary>
    /// Provides the configured channel grammar and admission limits.
    /// </summary>
    private readonly RazorWireOptions _options;

    /// <summary>
    /// Stores the current live SSE subscription count for this process.
    /// </summary>
    private int _liveSubscriptionCount;

    /// <summary>
    /// Initializes a new per-process admission controller with RazorWire options and metrics instruments.
    /// </summary>
    /// <param name="options">
    /// The options object that supplies <see cref="RazorWireStreamOptions.MaxChannelNameLength" />,
    /// <see cref="RazorWireStreamOptions.MaxLiveChannels" />,
    /// <see cref="RazorWireStreamOptions.MaxLiveSubscriptions" />, and
    /// <see cref="RazorWireStreamOptions.MaxLiveSubscriptionsPerChannel" />.
    /// </param>
    /// <remarks>
    /// Options are validated by <see cref="RazorWireOptionsValidator" /> during service registration. Constructing this
    /// type directly with invalid or mutable options is intended only for tests; callers should prefer the singleton
    /// registered by <c>AddRazorWire</c>. The constructor creates metrics instruments but does not allocate channel
    /// tracking state until the first accepted subscription.
    /// </remarks>
    public RazorWireStreamAdmissionController(RazorWireOptions options)
    {
        _options = options;
        _rejections = Meter.CreateCounter<int>("razorwire.stream.admission.rejections");
        _liveSubscriptionsMetric = Meter.CreateUpDownCounter<int>("razorwire.stream.live_subscriptions");
        _liveChannelsMetric = Meter.CreateUpDownCounter<int>("razorwire.stream.live_channels");
    }

    /// <summary>
    /// Gets the current in-process live subscription and live channel counts.
    /// </summary>
    /// <remarks>
    /// The snapshot is moment-in-time diagnostic state. It is useful for development responses, logs, and tests, but it
    /// should not be treated as a stable concurrency token because another request can change the counts immediately
    /// after the snapshot is read.
    /// </remarks>
    public RazorWireStreamAdmissionSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new RazorWireStreamAdmissionSnapshot(_liveSubscriptionCount, _subscriptionsByChannel.Count);
            }
        }
    }

    /// <summary>
    /// Attempts to reserve live stream capacity for a decoded channel name.
    /// </summary>
    /// <param name="channel">The decoded channel name from the route value or tag helper input.</param>
    /// <returns>
    /// An accepted result with a disposable lease when the channel is valid and all configured limits allow the
    /// subscription; otherwise, a rejected result that identifies the first failed validation or capacity check.
    /// </returns>
    /// <remarks>
    /// This method validates channel grammar before capacity checks. Accepted callers must subscribe to the hub only
    /// after this method succeeds and must dispose the returned lease after unsubscribing. Failed results record the
    /// rejection metric and do not reserve capacity.
    /// </remarks>
    public RazorWireStreamAdmissionResult TryAcquire(string channel)
    {
        RazorWireStreamAdmissionResult result;

        lock (_sync)
        {
            result = TryAcquireCore(channel);
        }

        RecordRejection(result);

        return result;
    }

    /// <summary>
    /// Records a pre-authorization channel validation rejection without reserving live stream capacity.
    /// </summary>
    /// <param name="channel">The decoded channel name that failed route-level validation.</param>
    /// <param name="reason">
    /// The validation rejection reason. Only <see cref="RazorWireStreamAdmissionRejectionReason.InvalidChannelName" />
    /// and <see cref="RazorWireStreamAdmissionRejectionReason.ChannelNameTooLong" /> are valid here.
    /// </param>
    /// <returns>
    /// A rejected result that includes the current live-count snapshot and records the rejection metric.
    /// </returns>
    /// <remarks>
    /// RazorWire validates malformed channels before resolving custom authorizers so bad input returns <c>400</c> without
    /// invoking host authorization code. Use this method for that non-acquiring path so diagnostics and metrics stay
    /// consistent with capacity rejections. Do not use it for authorization denials or capacity failures.
    /// </remarks>
    public RazorWireStreamAdmissionResult RejectPreAuthorizationValidation(
        string channel,
        RazorWireStreamAdmissionRejectionReason reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);

        if (reason is not RazorWireStreamAdmissionRejectionReason.InvalidChannelName
            and not RazorWireStreamAdmissionRejectionReason.ChannelNameTooLong)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "Only channel validation rejection reasons can be recorded before authorization.");
        }

        RazorWireStreamAdmissionResult result;

        lock (_sync)
        {
            result = Reject(reason, channel, current: channel.Length);
        }

        RecordRejection(result);

        return result;
    }

    private void RecordRejection(RazorWireStreamAdmissionResult result)
    {
        if (!result.Accepted && result.RejectionReason is { } reason)
        {
            _rejections.Add(1, KeyValuePair.Create<string, object?>("reason", reason.ToString()));
        }
    }

    private RazorWireStreamAdmissionResult TryAcquireCore(string channel)
    {
        var streamOptions = _options.Streams;
        var validation = RazorWireStreamChannelValidation.Validate(channel, streamOptions);
        if (!validation.IsValid)
        {
            return Reject(validation.RejectionReason!.Value, channel, current: channel.Length);
        }

        if (_liveSubscriptionCount >= streamOptions.MaxLiveSubscriptions)
        {
            return Reject(
                RazorWireStreamAdmissionRejectionReason.TooManyLiveSubscriptions,
                channel,
                current: _liveSubscriptionCount);
        }

        if (_subscriptionsByChannel.TryGetValue(channel, out var channelSubscriptions))
        {
            if (channelSubscriptions >= streamOptions.MaxLiveSubscriptionsPerChannel)
            {
                return Reject(
                    RazorWireStreamAdmissionRejectionReason.TooManyLiveSubscriptionsPerChannel,
                    channel,
                    current: channelSubscriptions);
            }

            _subscriptionsByChannel[channel] = channelSubscriptions + 1;
            _liveSubscriptionCount++;
            _liveSubscriptionsMetric.Add(1);

            return Accept(channel);
        }

        if (_subscriptionsByChannel.Count >= streamOptions.MaxLiveChannels)
        {
            return Reject(
                RazorWireStreamAdmissionRejectionReason.TooManyLiveChannels,
                channel,
                current: _subscriptionsByChannel.Count);
        }

        _subscriptionsByChannel[channel] = 1;
        _liveSubscriptionCount++;
        _liveSubscriptionsMetric.Add(1);
        _liveChannelsMetric.Add(1);

        return Accept(channel);
    }

    private RazorWireStreamAdmissionResult Accept(string channel)
    {
        return RazorWireStreamAdmissionResult.Accept(new RazorWireStreamAdmissionLease(this, channel), SnapshotCore());
    }

    private RazorWireStreamAdmissionResult Reject(
        RazorWireStreamAdmissionRejectionReason reason,
        string channel,
        int current)
    {
        return RazorWireStreamAdmissionResult.Rejected(reason, channel.Length, current, SnapshotCore());
    }

    private RazorWireStreamAdmissionSnapshot SnapshotCore()
    {
        return new RazorWireStreamAdmissionSnapshot(_liveSubscriptionCount, _subscriptionsByChannel.Count);
    }

    private void Release(string channel)
    {
        lock (_sync)
        {
            if (!_subscriptionsByChannel.TryGetValue(channel, out var channelSubscriptions))
            {
                return;
            }

            if (channelSubscriptions <= 1)
            {
                _subscriptionsByChannel.Remove(channel);
                _liveChannelsMetric.Add(-1);
            }
            else
            {
                _subscriptionsByChannel[channel] = channelSubscriptions - 1;
            }

            if (_liveSubscriptionCount > 0)
            {
                _liveSubscriptionCount--;
                _liveSubscriptionsMetric.Add(-1);
            }
        }
    }

    /// <summary>
    /// Represents one accepted live stream admission reservation.
    /// </summary>
    /// <remarks>
    /// Dispose the lease after the corresponding hub subscription is removed. Disposal is idempotent so nested cleanup
    /// paths can call it safely, but the endpoint should still keep lease lifetime aligned with the hub reader lifetime.
    /// </remarks>
    public sealed class RazorWireStreamAdmissionLease : IDisposable
    {
        private readonly RazorWireStreamAdmissionController _owner;
        private readonly string _channel;
        private int _disposed;

        /// <summary>
        /// Creates a lease bound to a controller and decoded channel name.
        /// </summary>
        /// <param name="owner">The controller that owns the reserved capacity.</param>
        /// <param name="channel">The decoded channel name whose live count should be decremented on disposal.</param>
        internal RazorWireStreamAdmissionLease(RazorWireStreamAdmissionController owner, string channel)
        {
            _owner = owner;
            _channel = channel;
        }

        /// <summary>
        /// Releases the reserved subscription slot and, when this was the final subscriber, the live channel slot.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _owner.Release(_channel);
        }
    }
}

/// <summary>
/// Describes the outcome of a RazorWire stream admission attempt.
/// </summary>
/// <param name="Accepted">Whether live capacity was reserved.</param>
/// <param name="Lease">The lease that must be disposed for accepted results; <see langword="null" /> for rejections.</param>
/// <param name="RejectionReason">The reason admission failed, or <see langword="null" /> for accepted results.</param>
/// <param name="ChannelLength">The decoded channel-name length reported for rejected requests.</param>
/// <param name="Current">The count or length that triggered the rejection.</param>
/// <param name="Snapshot">The live-count snapshot captured when the result was created.</param>
/// <remarks>
/// Consumers should branch on <see cref="Accepted" />. Do not assume <see cref="Lease" /> is present unless admission was
/// accepted, and do not expose raw channel names in logs or diagnostics when building responses from rejected results.
/// </remarks>
internal sealed record RazorWireStreamAdmissionResult(
    bool Accepted,
    RazorWireStreamAdmissionController.RazorWireStreamAdmissionLease? Lease,
    RazorWireStreamAdmissionRejectionReason? RejectionReason,
    int ChannelLength,
    int Current,
    RazorWireStreamAdmissionSnapshot Snapshot)
{
    /// <summary>
    /// Creates an accepted result for a reserved subscription.
    /// </summary>
    /// <param name="lease">The live capacity lease owned by the accepted subscription.</param>
    /// <param name="snapshot">The live-count snapshot after accepting the subscription.</param>
    /// <returns>An accepted admission result.</returns>
    public static RazorWireStreamAdmissionResult Accept(
        RazorWireStreamAdmissionController.RazorWireStreamAdmissionLease lease,
        RazorWireStreamAdmissionSnapshot snapshot)
    {
        return new RazorWireStreamAdmissionResult(true, lease, null, 0, 0, snapshot);
    }

    /// <summary>
    /// Creates a rejected result without reserving capacity.
    /// </summary>
    /// <param name="reason">The validation or capacity reason admission failed.</param>
    /// <param name="channelLength">The decoded channel-name length.</param>
    /// <param name="current">The count or length compared against the configured limit.</param>
    /// <param name="snapshot">The live-count snapshot captured at rejection time.</param>
    /// <returns>A rejected admission result.</returns>
    public static RazorWireStreamAdmissionResult Rejected(
        RazorWireStreamAdmissionRejectionReason reason,
        int channelLength,
        int current,
        RazorWireStreamAdmissionSnapshot snapshot)
    {
        return new RazorWireStreamAdmissionResult(false, null, reason, channelLength, current, snapshot);
    }
}

/// <summary>
/// Captures live stream admission counts at one point in time.
/// </summary>
/// <param name="LiveSubscriptions">The number of live SSE subscriptions currently admitted in this process.</param>
/// <param name="LiveChannels">The number of decoded channel names with at least one live admitted subscription.</param>
/// <remarks>
/// Snapshots are diagnostic values for logs, development responses, and tests. They are not durable counters and should
/// not be used for distributed fairness or cross-node capacity planning.
/// </remarks>
internal readonly record struct RazorWireStreamAdmissionSnapshot(int LiveSubscriptions, int LiveChannels);

/// <summary>
/// Identifies why a RazorWire stream admission request was rejected.
/// </summary>
/// <remarks>
/// Validation reasons map to HTTP <c>400</c>. Capacity reasons map to HTTP <c>429</c>. Authorization denials are handled
/// separately and continue to map to HTTP <c>403</c>.
/// </remarks>
internal enum RazorWireStreamAdmissionRejectionReason
{
    /// <summary>
    /// The decoded channel name was empty or contained a character outside RazorWire's public stream grammar.
    /// </summary>
    InvalidChannelName = 0,

    /// <summary>
    /// The decoded channel name exceeded <see cref="RazorWireStreamOptions.MaxChannelNameLength" />.
    /// </summary>
    ChannelNameTooLong = 1,

    /// <summary>
    /// The process already has <see cref="RazorWireStreamOptions.MaxLiveSubscriptions" /> live stream subscriptions.
    /// </summary>
    TooManyLiveSubscriptions = 2,

    /// <summary>
    /// The channel already has <see cref="RazorWireStreamOptions.MaxLiveSubscriptionsPerChannel" /> live subscriptions.
    /// </summary>
    TooManyLiveSubscriptionsPerChannel = 3,

    /// <summary>
    /// The process already has <see cref="RazorWireStreamOptions.MaxLiveChannels" /> live channel names.
    /// </summary>
    TooManyLiveChannels = 4
}
