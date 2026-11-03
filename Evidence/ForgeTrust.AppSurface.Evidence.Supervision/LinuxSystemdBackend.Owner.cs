using Tmds.DBus.Protocol;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

internal sealed partial class LinuxSystemdBackend
{
    /// <summary>Reads the current service's termination, activation-condition and startup facts from the pinned manager.</summary>
    /// <remarks>These are sampled data; only <see cref="LinuxOwnerActivation"/> can bind them to the live owner and consume its guard.</remarks>
    internal async Task<LinuxOwnerFacts> ReadOwnerAsync(CancellationToken token)
    {
        var path = await CallAsync(ManagerRequest("GetUnitByPID", "u", processId: (uint)Environment.ProcessId),
            "o", static message => message.GetBodyReader().ReadObjectPath().ToString(), token).ConfigureAwait(false);
        if (!path.StartsWith(ManagerPath + "/unit/", StringComparison.Ordinal)
            || path.Length > 4096 || path.Any(char.IsControl)) throw InvalidControl();
        var unit = await ReadInterfaceAsync(path, UnitInterface, token).ConfigureAwait(false);
        var service = await ReadInterfaceAsync(path, ServiceInterface, token).ConfigureAwait(false);
        RequireOwnerWireShape(unit, service);
        return LinuxOwnerFacts.Parse(unit, service);
    }

    /// <summary>Rejects variant/nonstruct arrays before native owner facts can reach activation.</summary>
    /// <remarks>Empty EnvironmentFiles establishes no configured files, not a proof of an unobserved element's full signature.</remarks>
    internal static void RequireOwnerWireShape(IReadOnlyDictionary<string, VariantValue> unit,
        IReadOnlyDictionary<string, VariantValue> service)
    {
        try
        {
            foreach (var value in new[] { unit["Conditions"], service["ExecStart"], service["EnvironmentFiles"] })
                RequireOwnerStructArray(value);
        }
        catch (Exception error) when (error is KeyNotFoundException or ArgumentException or InvalidOperationException)
        { throw LinuxOwnerFacts.Invalid(); }
    }

    /// <summary>Checks a compound-array wire kind as data; this cannot construct backend or owner authority.</summary>
    internal static void RequireOwnerStructArray(VariantValue value)
    {
        if (value.Type != VariantValueType.Array || value.ItemType != VariantValueType.Struct)
            throw LinuxOwnerFacts.Invalid();
    }
}
