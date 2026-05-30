namespace ForgeTrust.RazorWire.Streams;

/// <summary>
/// Options that control how a RazorWire stream message is published.
/// </summary>
/// <remarks>
/// Replay is disabled by default so existing channels remain live-only. Enable <see cref="Replay"/> only for
/// idempotent state snapshots or bounded progress events that are safe for late subscribers to receive after publish.
/// The in-memory hub retains at most 25 messages per replay channel and prunes inactive replay channels when more than
/// 256 replay channels are retained.
/// </remarks>
public sealed record RazorWireStreamPublishOptions
{
    /// <summary>
    /// Gets a value indicating whether this message should be retained in the bounded replay buffer for its channel.
    /// Retained messages survive live subscriber disconnects until normal replay pruning removes them.
    /// </summary>
    public bool Replay { get; init; }
}

/// <summary>
/// Options that control how a RazorWire stream subscription is created.
/// </summary>
/// <remarks>
/// Replay is opt-in per subscription. A replaying subscriber first receives the channel's retained messages, then receives
/// live messages like any other subscriber. The in-memory hub bounds replay storage and drops the oldest retained messages
/// when the channel exceeds its replay capacity of 25 messages, and prunes inactive replay channels when more than 256
/// replay channels are retained.
/// </remarks>
public sealed record RazorWireStreamSubscribeOptions
{
    /// <summary>
    /// Gets a value indicating whether retained channel messages should be delivered before live messages. A replay
    /// subscription to a channel with no retained messages does not create a durable replay buffer.
    /// </summary>
    public bool Replay { get; init; }
}
