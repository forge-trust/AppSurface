using System.Threading.Channels;

namespace ForgeTrust.RazorWire.Streams;

/// <summary>
/// Defines the contract for a message hub that supports pub/sub operations over named channels.
/// </summary>
public interface IRazorWireStreamHub
{
    /// <summary>
    /// Publishes a string message to the specified channel.
    /// </summary>
    /// <param name="channel">The name of the channel to publish the message to.</param>
    /// <param name="message">The message payload to publish.</param>
    /// <returns>A <see cref="ValueTask"/> that completes when the publish operation has finished.</returns>
    ValueTask PublishAsync(string channel, string message);

    /// <summary>
    /// Publishes a string message to the specified channel with explicit delivery options.
    /// </summary>
    /// <param name="channel">The name of the channel to publish the message to.</param>
    /// <param name="message">The message payload to publish.</param>
    /// <param name="options">Optional publish behavior. When omitted, publishing remains live-only.</param>
    /// <returns>A <see cref="ValueTask"/> that completes when the publish operation has finished.</returns>
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
    /// <param name="options">Optional subscription behavior. When omitted, the subscription receives only live messages.</param>
    /// <returns>A <see cref="ChannelReader{String}"/> that yields replayed and live messages for the specified channel.</returns>
    ChannelReader<string> Subscribe(string channel, RazorWireStreamSubscribeOptions? options)
    {
        return Subscribe(channel);
    }

    /// <summary>
    /// Unsubscribes the specified reader from the named channel so it no longer receives messages from that channel.
    /// </summary>
    /// <param name="channel">The name of the channel to unsubscribe from.</param>
    /// <param name="reader">The ChannelReader&lt;string&gt; instance to detach from the channel.</param>
    void Unsubscribe(string channel, ChannelReader<string> reader);
}
