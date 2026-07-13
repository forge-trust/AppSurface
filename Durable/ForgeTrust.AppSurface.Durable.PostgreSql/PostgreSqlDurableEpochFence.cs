using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal static class PostgreSqlDurableEpochFence
{
    internal static async ValueTask EnsureCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runtimeEpoch,
        CancellationToken cancellationToken)
    {
        if (runtimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("The durable runtime epoch must not be empty.", nameof(runtimeEpoch));
        }

        await using var command = new NpgsqlCommand(
            "SELECT appsurface_durable.initialize_runtime_epoch(@runtime_epoch);",
            connection,
            transaction);
        command.Parameters.AddWithValue("runtime_epoch", runtimeEpoch);
        var active = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (active is not Guid activeEpoch || activeEpoch != runtimeEpoch)
        {
            throw new InvalidOperationException(
                $"{DurableProblemCodes.RecoveryEpochRequired}: The configured runtime epoch is not the store's active recovery epoch. Stop the old fleet, rotate the store epoch with the deployment operator, then start the new fleet.");
        }
    }
}
