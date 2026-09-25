using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Testing;

/// <summary>Immutable public facts observed from a typed Work definition.</summary>
public sealed record DurableWorkDefinitionObservation(
    string WorkName, string WorkVersion, string WorkCodecName, string WorkCodecVersion,
    Type WorkPayloadType, DurableDataClassification WorkClassification, string WorkRetentionPolicyId,
    string ResultCodecName, string ResultCodecVersion, Type ResultPayloadType,
    DurableDataClassification ResultClassification, string ResultRetentionPolicyId,
    DurableProviderSafety ProviderSafety, DurableWorkRetryPolicy DefaultRetryPolicy)
{
    /// <summary>Captures definition identity, codec metadata, safety, and retry defaults without asserting.</summary>
    public static DurableWorkDefinitionObservation Capture<TWork, TResult>(DurableWorkDefinition<TWork, TResult> definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var input = definition.WorkCodec;
        var result = definition.ResultCodec;
        return new(definition.WorkName, definition.WorkVersion, input.ContractName, input.ContractVersion,
            input.PayloadType, input.Classification, input.RetentionPolicyId, result.ContractName, result.ContractVersion,
            result.PayloadType, result.Classification, result.RetentionPolicyId, definition.ProviderSafety, definition.DefaultRetryPolicy);
    }
}

/// <summary>Immutable observation of an exact registry lookup.</summary>
public sealed record DurableWorkRegistryObservation(string WorkName, string WorkVersion, DurableWorkRegistration Registration)
{
    /// <summary>Performs the production exact lookup; missing-registration exceptions propagate unchanged.</summary>
    public static DurableWorkRegistryObservation Capture(IDurableWorkRegistry registry, string workName, string workVersion)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var registration = registry.GetRequired(workName, workVersion);
        return new(registration.WorkName, registration.WorkVersion, registration);
    }
}

/// <summary>Immutable observation of a binding's exact definition reference.</summary>
public sealed record DurableWorkBindingObservation(object Definition, string WorkName, string WorkVersion)
{
    /// <summary>Captures binding identity without resolving services or invoking work.</summary>
    public static DurableWorkBindingObservation Capture<TWork, TResult, TExecutor>(DurableWorkBinding<TWork, TResult, TExecutor> binding)
        where TExecutor : class, ForgeTrust.AppSurface.Workers.IDurableWorkerExecutor<TWork, TResult>
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new(binding.Definition, binding.Definition.WorkName, binding.Definition.WorkVersion);
    }
}
