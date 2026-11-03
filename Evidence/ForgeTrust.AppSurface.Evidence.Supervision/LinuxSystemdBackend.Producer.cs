namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Typed producer start adapter sharing the backend's retained AddRef connection.</summary>
internal sealed partial class LinuxSystemdBackend
{
    /// <summary>Starts exactly the fixed recipe prepared for this retained producer.</summary>
    /// <remarks>The job reply is only ambiguous start data; the producer holder authenticates the actual unit and process.</remarks>
    internal Task<string> StartProducerAsync(LinuxProducerUnit producer, CancellationToken token)
    {
        if (producer is null) throw InvalidControl();
        return StartOwnedUnitAsync(producer.Unit, producer.Arguments, producer.Properties,
            producer.StandardOutput, producer.StandardError, token);
    }
}
