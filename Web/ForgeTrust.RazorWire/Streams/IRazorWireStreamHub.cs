using System.Threading.Channels;

namespace ForgeTrust.RazorWire.Streams;

/// <summary>
/// Defines the contract for a message hub that supports pub/sub operations over named channels.
/// </summary>
/// <remarks>
/// Implementations may keep live subscriber state and replay retention state separately. Live subscriptions should be
/// released when readers disconnect, while opt-in replay buffers may outlive live subscribers until the implementation's
/// bounded retention policy prunes them.
/// </remarks>
public interface IRazorWireStreamHub
{
    /// <summary>
    /// Publishes a trusted stream message to the specified channel.
    /// </summary>
    /// <param name="channel">The name of the channel to publish the message to.</param>
    /// <param name="message">
    /// The trusted Turbo Stream payload to publish. The hub transports this string as-is; it does not encode or sanitize
    /// template content.
    /// </param>
    /// <returns>A <see cref="ValueTask"/> that completes when the publish operation has finished.</returns>
    /// <remarks>
    /// Prefer publishing output from <see cref="Bridge.RazorWireStreamBuilder"/> so plain-text values are encoded before
    /// the message reaches the hub. If composing a raw message, encode user-supplied values before publishing.
    /// </remarks>
    ValueTask PublishAsync(string channel, string message);

    /// <summary>
    /// Publishes a trusted stream message to the specified channel with explicit delivery options.
    /// </summary>
    /// <param name="channel">The name of the channel to publish the message to.</param>
    /// <param name="message">
    /// The trusted Turbo Stream payload to publish. The hub transports this string as-is; it does not encode or sanitize
    /// template content.
    /// </param>
    /// <param name="options">
    /// Optional publish behavior. The default interface implementation is a compatibility fallback and ignores this value
    /// by delegating to <see cref="PublishAsync(string, string)"/>. Implementations that support replay retention or other
    /// optioned semantics must override this overload.
    /// </param>
    /// <returns>A <see cref="ValueTask"/> that completes when the publish operation has finished.</returns>
    /// <remarks>
    /// Pitfall: callers should not expect <see cref="RazorWireStreamPublishOptions"/> to be honored unless the concrete
    /// <see cref="IRazorWireStreamHub"/> implementation overrides this member. The hub is also a raw transport boundary:
    /// use <see cref="Bridge.RazorWireStreamBuilder"/> or encode user-supplied values before publishing raw stream
    /// strings.
    /// </remarks>
    ValueTask PublishAsync(string channel, string message, RazorWireStreamPublishOptions? options)
    {
        return PublishAsync(channel, message);
    }

    /// <summary>
    /// Subscribes to a named message channel and provides a reader for incoming messages.
    /// </summary>
    /// <param name="channel">The name of the channel to subscribe to.</param>
    /// <returns>A <see cref="ChannelReader{String}"/> that yields messages published to the specified channel; the reader completes when the subscription is removed or the hub shuts down.</returns>
    ChannelReader<string> Subscribe(string channel);

    /// <summary>
    /// Subscribes to a named message channel with explicit subscription options.
    /// </summary>
    /// <param name="channel">The name of the channel to subscribe to.</param>
    /// <param name="options">
    /// Optional subscription behavior. The default interface implementation is a compatibility fallback and ignores this
    /// value by delegating to <see cref="Subscribe(string)"/>.
    /// </param>
    /// <returns>
    /// A <see cref="ChannelReader{String}"/> for the specified channel. Replay semantics are available only when the
    /// concrete implementation overrides this overload.
    /// </returns>
    /// <remarks>
    /// Pitfall: <see cref="RazorWireStreamSubscribeOptions.Replay"/> is an opt-in contract between caller and hub
    /// implementation; the interface fallback remains live-only for backward compatibility. Replay should be reserved for
    /// idempotent retained state, not one-shot commands or sensitive payloads.
    /// </remarks>
    ChannelReader<string> Subscribe(string channel, RazorWireStreamSubscribeOptions? options)
    {
        return Subscribe(channel);
    }

    /// <summary>
    /// Unsubscribes the specified reader from the named channel so it no longer receives messages from that channel.
    /// </summary>
    /// <param name="channel">
    /// The name of the channel to unsubscribe from. Normal callers should pass the same channel used for subscription.
    /// Endpoint cleanup paths usually have that value available; passing a different value can be surprising on
    /// implementations that do not keep their own subscription ownership map.
    /// </param>
    /// <param name="reader">The ChannelReader&lt;string&gt; instance to detach from the channel.</param>
    void Unsubscribe(string channel, ChannelReader<string> reader);
}
