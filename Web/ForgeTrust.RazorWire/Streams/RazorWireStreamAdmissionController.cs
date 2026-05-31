using System.Diagnostics.Metrics;

namespace ForgeTrust.RazorWire.Streams;

internal sealed class RazorWireStreamAdmissionController
{
    private const string MeterName = "ForgeTrust.RazorWire";
    private static readonly Meter Meter = new(MeterName);

    private readonly object _sync = new();
    private readonly Dictionary<string, int> _subscriptionsByChannel = new(StringComparer.Ordinal);
    private readonly Counter<int> _rejections;
    private readonly UpDownCounter<int> _liveSubscriptionsMetric;
    private readonly UpDownCounter<int> _liveChannelsMetric;
    private readonly RazorWireOptions _options;
    private int _liveSubscriptionCount;

    public RazorWireStreamAdmissionController(RazorWireOptions options)
    {
        _options = options;
        _rejections = Meter.CreateCounter<int>("razorwire.stream.admission.rejections");
        _liveSubscriptionsMetric = Meter.CreateUpDownCounter<int>("razorwire.stream.live_subscriptions");
        _liveChannelsMetric = Meter.CreateUpDownCounter<int>("razorwire.stream.live_channels");
    }

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

    public RazorWireStreamAdmissionResult TryAcquire(string channel)
    {
        RazorWireStreamAdmissionResult result;

        lock (_sync)
        {
            result = TryAcquireCore(channel);
        }

        if (!result.Accepted && result.RejectionReason is { } reason)
        {
            _rejections.Add(1, KeyValuePair.Create<string, object?>("reason", reason.ToString()));
        }

        return result;
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

    public sealed class RazorWireStreamAdmissionLease : IDisposable
    {
        private readonly RazorWireStreamAdmissionController _owner;
        private readonly string _channel;
        private int _disposed;

        internal RazorWireStreamAdmissionLease(RazorWireStreamAdmissionController owner, string channel)
        {
            _owner = owner;
            _channel = channel;
        }

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

internal sealed record RazorWireStreamAdmissionResult(
    bool Accepted,
    RazorWireStreamAdmissionController.RazorWireStreamAdmissionLease? Lease,
    RazorWireStreamAdmissionRejectionReason? RejectionReason,
    int ChannelLength,
    int Current,
    RazorWireStreamAdmissionSnapshot Snapshot)
{
    public static RazorWireStreamAdmissionResult Accept(
        RazorWireStreamAdmissionController.RazorWireStreamAdmissionLease lease,
        RazorWireStreamAdmissionSnapshot snapshot)
    {
        return new RazorWireStreamAdmissionResult(true, lease, null, 0, 0, snapshot);
    }

    public static RazorWireStreamAdmissionResult Rejected(
        RazorWireStreamAdmissionRejectionReason reason,
        int channelLength,
        int current,
        RazorWireStreamAdmissionSnapshot snapshot)
    {
        return new RazorWireStreamAdmissionResult(false, null, reason, channelLength, current, snapshot);
    }
}

internal readonly record struct RazorWireStreamAdmissionSnapshot(int LiveSubscriptions, int LiveChannels);

internal enum RazorWireStreamAdmissionRejectionReason
{
    InvalidChannelName = 0,
    ChannelNameTooLong = 1,
    TooManyLiveSubscriptions = 2,
    TooManyLiveSubscriptionsPerChannel = 3,
    TooManyLiveChannels = 4
}
