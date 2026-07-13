using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal sealed record PostgreSqlResolvedSchedule(
    DurableSchedule Definition,
    DateTimeOffset? AtUtc,
    TimeSpan? Delay,
    TimeSpan? EveryInterval,
    DateTimeOffset? AnchorUtc,
    string? CronExpression,
    string? CronDialect,
    string? CronGrammar,
    string? IanaTimeZoneId,
    string? CronEvaluatorVersion,
    int? CronJitterSeed,
    string? TimeZoneRulesFingerprint,
    DateTimeOffset? NextNominalDueUtc);

internal sealed record PostgreSqlEncodedScheduleTarget(
    DurableScheduleTargetKind Kind,
    string RegisteredName,
    string RegisteredVersion,
    DurableProviderSafety? ProviderSafety,
    DurableEncodedPayload Input);

internal sealed record PostgreSqlScheduleScopeState(long Generation, bool IsActive);

internal sealed record PostgreSqlScheduleCommandResult(
    DurableScheduleId ScheduleId,
    DurableCommandId CommandId,
    byte[] RequestSha256,
    DurableScheduleMutationCode Code,
    long Generation,
    long Revision,
    DateTimeOffset CommittedAtUtc);

internal static class PostgreSqlScheduleStorage
{
    internal const string SnapshotColumns = """
        current.schedule_id,
        current.display_name,
        current.state,
        current.generation,
        current.revision,
        current.schedule_kind,
        current.at_utc,
        current.delay,
        current.every_interval,
        current.anchor_utc,
        current.overlap_kind,
        current.overlap_maximum,
        current.misfire_kind,
        current.misfire_maximum,
        current.cron_expression,
        current.cron_dialect,
        current.cron_grammar,
        current.iana_time_zone_id,
        current.target_kind,
        current.target_name,
        current.target_version,
        current.target_provider_safety,
        current.target_contract_name,
        current.target_contract_version,
        current.target_classification,
        current.target_payload,
        current.target_payload_sha256,
        current.next_nominal_due_utc,
        current.target_retention_policy_id
        """;

    internal static PostgreSqlResolvedSchedule Resolve(
        DurableScheduleId scheduleId,
        DurableSchedule schedule,
        DateTimeOffset acceptedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var accepted = acceptedAtUtc.ToUniversalTime();
        var calculator = ScheduleOccurrenceCalculatorFactory.Create(scheduleId, schedule, accepted);
        return schedule switch
        {
            DurableAtSchedule at => new PostgreSqlResolvedSchedule(
                schedule,
                at.AtUtc,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                at.AtUtc),
            DurableAfterSchedule after => ResolveAfter(schedule, after, accepted, calculator),
            DurableEverySchedule every => ResolveEvery(schedule, every, accepted),
            DurableCronSchedule cron => ResolveCron(schedule, cron, accepted, calculator),
            _ => throw new ScheduleDefinitionException(
                ScheduleDefinitionError.UnsupportedScheduleKind,
                $"Schedule kind '{schedule.Kind}' is not supported."),
        };
    }

    internal static async ValueTask SetScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<DateTimeOffset> ReadTransactionTimeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT transaction_timestamp();", connection, transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DateTime timestamp
            ? new DateTimeOffset(timestamp, TimeSpan.Zero)
            : throw new InvalidDataException("PostgreSQL did not return a transaction timestamp.");
    }

    internal static async ValueTask<long?> EnsureActiveScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        if (createIfMissing)
        {
            const string insertSql = """
                INSERT INTO appsurface_durable.scope (scope_id)
                VALUES (@scope_id)
                ON CONFLICT (scope_id) DO NOTHING;
                """;
            await using var insert = new NpgsqlCommand(insertSql, connection, transaction);
            insert.Parameters.AddWithValue("scope_id", scopeId.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string selectSql = """
            SELECT generation
            FROM appsurface_durable.scope
            WHERE scope_id = @scope_id
              AND state = 'active'
            FOR SHARE;
            """;
        await using var select = new NpgsqlCommand(selectSql, connection, transaction);
        select.Parameters.AddWithValue("scope_id", scopeId.Value);
        var value = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long generation ? generation : null;
    }

    internal static async ValueTask<PostgreSqlScheduleScopeState?> LockScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT generation, state
            FROM appsurface_durable.scope
            WHERE scope_id = @scope_id
            FOR SHARE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PostgreSqlScheduleScopeState(
                reader.GetInt64(0),
                string.Equals(reader.GetString(1), "active", StringComparison.Ordinal))
            : null;
    }

    internal static async ValueTask<PostgreSqlScheduleCommandResult?> ReadCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId,
        DurableCommandId commandId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT schedule_id, command_id, request_sha256, result_code,
                   resulting_generation, resulting_revision, committed_at
            FROM appsurface_durable.schedule_command
            WHERE scope_id = @scope_id
              AND (command_id = @command_id
                   OR (@idempotency_key IS NOT NULL AND idempotency_key = @idempotency_key))
            ORDER BY CASE WHEN command_id = @command_id THEN 0 ELSE 1 END
            LIMIT 1;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("command_id", commandId.Value);
        AddNullable(command, "idempotency_key", NpgsqlDbType.Text, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PostgreSqlScheduleCommandResult(
            new DurableScheduleId(reader.GetString(0)),
            new DurableCommandId(reader.GetString(1)),
            reader.GetFieldValue<byte[]>(2),
            ParseMutationCode(reader.GetString(3)),
            reader.GetInt64(4),
            reader.GetInt64(5),
            ReadUtc(reader, 6));
    }

    internal static DurableScheduleSnapshot ReadSnapshot(NpgsqlDataReader reader)
    {
        var scheduleId = new DurableScheduleId(reader.GetString(0));
        var displayName = reader.IsDBNull(1) ? null : reader.GetString(1);
        var state = ParseState(reader.GetString(2));
        var generation = reader.GetInt64(3);
        var revision = reader.GetInt64(4);
        var scheduleKind = reader.GetString(5);
        var atUtc = ReadNullableUtc(reader, 6);
        TimeSpan? delay = reader.IsDBNull(7) ? null : reader.GetTimeSpan(7);
        TimeSpan? everyInterval = reader.IsDBNull(8) ? null : reader.GetTimeSpan(8);
        var anchorUtc = ReadNullableUtc(reader, 9);
        var overlap = ParseOverlap(reader.GetString(10), reader.GetInt32(11));
        var misfire = ParseMisfire(reader.GetString(12), reader.GetInt32(13));
        var schedule = BuildSchedule(
                scheduleKind,
                atUtc,
                delay,
                everyInterval,
                anchorUtc,
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17))
            .WithOverlap(overlap)
            .WithMisfire(misfire);
        var targetKind = ParseTargetKind(reader.GetString(18));
        DurableProviderSafety? targetSafety = reader.IsDBNull(21) ? null : ParseProviderSafety(reader.GetString(21));
        var payloadBytes = reader.GetFieldValue<byte[]>(25);
        var payload = new DurableEncodedPayload(
            reader.GetString(22),
            reader.GetString(23),
            (DurableDataClassification)reader.GetInt16(24),
            payloadBytes,
            reader.GetString(28));
        var storedHash = reader.GetFieldValue<byte[]>(26);
        if (!storedHash.AsSpan().SequenceEqual(Convert.FromHexString(payload.Sha256)))
        {
            throw new InvalidDataException("The durable schedule target payload hash does not match its authoritative bytes.");
        }

        var target = new DurableScheduleTargetSnapshot(
            targetKind,
            reader.GetString(19),
            reader.GetString(20),
            payload,
            targetSafety);
        return new DurableScheduleSnapshot(
            scheduleId,
            displayName,
            state,
            generation,
            revision,
            schedule,
            target,
            ReadNullableUtc(reader, 27));
    }

    internal static void AddDefinitionParameters(
        NpgsqlCommand command,
        PostgreSqlResolvedSchedule schedule,
        PostgreSqlEncodedScheduleTarget target)
    {
        command.Parameters.AddWithValue("schedule_kind", FormatScheduleKind(schedule.Definition.Kind));
        AddNullable(command, "at_utc", NpgsqlDbType.TimestampTz, schedule.AtUtc);
        AddNullable(command, "delay", NpgsqlDbType.Interval, schedule.Delay);
        AddNullable(command, "every_interval", NpgsqlDbType.Interval, schedule.EveryInterval);
        AddNullable(command, "anchor_utc", NpgsqlDbType.TimestampTz, schedule.AnchorUtc);
        command.Parameters.AddWithValue("overlap_kind", FormatOverlap(schedule.Definition.OverlapPolicy.Kind));
        command.Parameters.AddWithValue("overlap_maximum", schedule.Definition.OverlapPolicy.MaximumConcurrentRuns);
        command.Parameters.AddWithValue("misfire_kind", FormatMisfire(schedule.Definition.MisfirePolicy.Kind));
        command.Parameters.AddWithValue("misfire_maximum", schedule.Definition.MisfirePolicy.MaximumOccurrences);
        AddNullable(command, "cron_expression", NpgsqlDbType.Text, schedule.CronExpression);
        AddNullable(command, "cron_dialect", NpgsqlDbType.Text, schedule.CronDialect);
        AddNullable(command, "cron_grammar", NpgsqlDbType.Text, schedule.CronGrammar);
        AddNullable(command, "iana_time_zone_id", NpgsqlDbType.Text, schedule.IanaTimeZoneId);
        AddNullable(command, "cron_evaluator_version", NpgsqlDbType.Text, schedule.CronEvaluatorVersion);
        AddNullable(command, "cron_jitter_seed", NpgsqlDbType.Integer, schedule.CronJitterSeed);
        AddNullable(command, "time_zone_rules_fingerprint", NpgsqlDbType.Char, schedule.TimeZoneRulesFingerprint);
        command.Parameters.AddWithValue("target_kind", FormatTargetKind(target.Kind));
        command.Parameters.AddWithValue("target_name", target.RegisteredName);
        command.Parameters.AddWithValue("target_version", target.RegisteredVersion);
        AddNullable(
            command,
            "target_provider_safety",
            NpgsqlDbType.Text,
            target.ProviderSafety.HasValue ? FormatProviderSafety(target.ProviderSafety.Value) : null);
        command.Parameters.AddWithValue("target_contract_name", target.Input.ContractName);
        command.Parameters.AddWithValue("target_contract_version", target.Input.ContractVersion);
        command.Parameters.AddWithValue("target_classification", (short)target.Input.Classification);
        command.Parameters.AddWithValue("target_retention_policy_id", target.Input.RetentionPolicyId);
        command.Parameters.AddWithValue("target_payload", target.Input.Content.ToArray());
        command.Parameters.AddWithValue("target_payload_sha256", Convert.FromHexString(target.Input.Sha256));
        AddNullable(command, "next_nominal_due_utc", NpgsqlDbType.TimestampTz, schedule.NextNominalDueUtc);
    }

    internal static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
    }

    internal static string FormatState(DurableScheduleState state) => state switch
    {
        DurableScheduleState.Active => "active",
        DurableScheduleState.Paused => "paused",
        DurableScheduleState.Deleted => "deleted",
        DurableScheduleState.Suspended => "suspended",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    internal static DurableScheduleState ParseState(string value) => value switch
    {
        "active" => DurableScheduleState.Active,
        "paused" => DurableScheduleState.Paused,
        "deleted" => DurableScheduleState.Deleted,
        "suspended" => DurableScheduleState.Suspended,
        _ => throw new InvalidDataException($"Unknown persisted schedule state '{value}'."),
    };

    internal static string FormatTargetKind(DurableScheduleTargetKind kind) => kind switch
    {
        DurableScheduleTargetKind.Work => "work",
        DurableScheduleTargetKind.Flow => "flow",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static DurableScheduleTargetKind ParseTargetKind(string value) => value switch
    {
        "work" => DurableScheduleTargetKind.Work,
        "flow" => DurableScheduleTargetKind.Flow,
        _ => throw new InvalidDataException($"Unknown persisted schedule target kind '{value}'."),
    };

    internal static string FormatProviderSafety(DurableProviderSafety value) => value switch
    {
        DurableProviderSafety.Idempotent => "idempotent",
        DurableProviderSafety.ProviderKeyed => "provider_keyed",
        DurableProviderSafety.ReconcileBeforeRetry => "reconcile_before_retry",
        DurableProviderSafety.ManualResolution => "manual_resolution",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    internal static DurableProviderSafety ParseProviderSafety(string value) => value switch
    {
        "idempotent" => DurableProviderSafety.Idempotent,
        "provider_keyed" => DurableProviderSafety.ProviderKeyed,
        "reconcile_before_retry" => DurableProviderSafety.ReconcileBeforeRetry,
        "manual_resolution" => DurableProviderSafety.ManualResolution,
        _ => throw new InvalidDataException($"Unknown persisted provider safety '{value}'."),
    };

    internal static string FormatMutationCode(DurableScheduleMutationCode code) => code switch
    {
        DurableScheduleMutationCode.Created => "created",
        DurableScheduleMutationCode.Updated => "updated",
        DurableScheduleMutationCode.Paused => "paused",
        DurableScheduleMutationCode.Resumed => "resumed",
        DurableScheduleMutationCode.Deleted => "deleted",
        DurableScheduleMutationCode.Unchanged => "unchanged",
        DurableScheduleMutationCode.RecoveryReleased => "recovery_released",
        DurableScheduleMutationCode.Duplicate => throw new ArgumentException(
            "Duplicate is a read-time outcome and is not persisted as the original command result.",
            nameof(code)),
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };

    internal static DurableScheduleMutationCode ParseMutationCode(string value) => value switch
    {
        "created" => DurableScheduleMutationCode.Created,
        "updated" => DurableScheduleMutationCode.Updated,
        "paused" => DurableScheduleMutationCode.Paused,
        "resumed" => DurableScheduleMutationCode.Resumed,
        "deleted" => DurableScheduleMutationCode.Deleted,
        "unchanged" => DurableScheduleMutationCode.Unchanged,
        "recovery_released" => DurableScheduleMutationCode.RecoveryReleased,
        _ => throw new InvalidDataException($"Unknown persisted schedule mutation result '{value}'."),
    };

    internal static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) =>
        new(reader.GetFieldValue<DateTime>(ordinal), TimeSpan.Zero);

    internal static DateTimeOffset? ReadNullableUtc(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadUtc(reader, ordinal);

    private static PostgreSqlResolvedSchedule ResolveAfter(
        DurableSchedule schedule,
        DurableAfterSchedule after,
        DateTimeOffset accepted,
        IScheduleOccurrenceCalculator calculator)
    {
        var next = calculator.GetNextOccurrence(accepted, inclusive: true)
            ?? throw new ScheduleDefinitionException(
                ScheduleDefinitionError.InstantOutOfRange,
                "The delayed schedule has no reachable occurrence.");
        return new PostgreSqlResolvedSchedule(
            schedule,
            null,
            after.Delay,
            null,
            accepted,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            next);
    }

    private static PostgreSqlResolvedSchedule ResolveEvery(
        DurableSchedule schedule,
        DurableEverySchedule every,
        DateTimeOffset accepted)
    {
        var anchor = every.AnchorUtc ?? accepted;
        return new PostgreSqlResolvedSchedule(
            schedule,
            null,
            null,
            every.Interval,
            anchor,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            anchor);
    }

    private static PostgreSqlResolvedSchedule ResolveCron(
        DurableSchedule schedule,
        DurableCronSchedule cron,
        DateTimeOffset accepted,
        IScheduleOccurrenceCalculator calculator)
    {
        var cronos = (CronosV1ScheduleCalculator)calculator;
        var next = cronos.GetNextOccurrence(accepted, inclusive: true)
            ?? throw new ScheduleDefinitionException(
                ScheduleDefinitionError.InvalidCronExpression,
                "The CronosV1 expression has no reachable future occurrence.");
        return new PostgreSqlResolvedSchedule(
            schedule,
            null,
            null,
            null,
            null,
            cron.Expression,
            "cronos_v1",
            cron.Grammar == CronGrammar.Standard ? "standard" : "include_seconds",
            cron.IanaTimeZoneId,
            CronosV1ScheduleCalculator.EvaluatorVersion,
            cronos.JitterSeed,
            cronos.TimeZoneRulesFingerprint,
            next);
    }

    private static DurableSchedule BuildSchedule(
        string scheduleKind,
        DateTimeOffset? atUtc,
        TimeSpan? delay,
        TimeSpan? everyInterval,
        DateTimeOffset? anchorUtc,
        string? cronExpression,
        string? cronDialect,
        string? cronGrammar,
        string? ianaTimeZoneId) => scheduleKind switch
        {
            "at" => DurableSchedule.At(atUtc ?? throw Corrupt("At instant")),
            "after" => DurableSchedule.After(delay ?? throw Corrupt("After delay")),
            "every" => DurableSchedule.Every(
                everyInterval ?? throw Corrupt("Every interval"),
                anchorUtc ?? throw Corrupt("Every anchor")),
            "cron" => new DurableCronSchedule(
                cronExpression ?? throw Corrupt("Cron expression"),
                ianaTimeZoneId ?? throw Corrupt("Cron time zone"),
                cronGrammar switch
                {
                    "standard" => CronGrammar.Standard,
                    "include_seconds" => CronGrammar.IncludeSeconds,
                    _ => throw Corrupt("Cron grammar"),
                },
                cronDialect == "cronos_v1"
                    ? CronDialect.CronosV1
                    : throw Corrupt("Cron dialect")),
            _ => throw Corrupt("Schedule kind"),
        };

    internal static ScheduleOverlapPolicy ParseOverlap(string value, int maximum) => value switch
    {
        "queue_one" => ScheduleOverlapPolicy.QueueOne,
        "skip" => ScheduleOverlapPolicy.Skip,
        "allow_concurrent" => ScheduleOverlapPolicy.AllowConcurrent(maximum),
        _ => throw new InvalidDataException($"Unknown persisted overlap policy '{value}'."),
    };

    internal static ScheduleMisfirePolicy ParseMisfire(string value, int maximum) => value switch
    {
        "run_once" => ScheduleMisfirePolicy.RunOnce,
        "skip" => ScheduleMisfirePolicy.Skip,
        "catch_up" => ScheduleMisfirePolicy.CatchUp(maximum),
        _ => throw new InvalidDataException($"Unknown persisted misfire policy '{value}'."),
    };

    internal static DurableScheduleKind ParseScheduleKind(string value) => value switch
    {
        "at" => DurableScheduleKind.At,
        "after" => DurableScheduleKind.After,
        "every" => DurableScheduleKind.Every,
        "cron" => DurableScheduleKind.Cron,
        _ => throw new InvalidDataException($"Unknown persisted schedule kind '{value}'."),
    };

    private static string FormatScheduleKind(DurableScheduleKind kind) => kind switch
    {
        DurableScheduleKind.At => "at",
        DurableScheduleKind.After => "after",
        DurableScheduleKind.Every => "every",
        DurableScheduleKind.Cron => "cron",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string FormatOverlap(ScheduleOverlapPolicyKind kind) => kind switch
    {
        ScheduleOverlapPolicyKind.QueueOne => "queue_one",
        ScheduleOverlapPolicyKind.Skip => "skip",
        ScheduleOverlapPolicyKind.AllowConcurrent => "allow_concurrent",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string FormatMisfire(ScheduleMisfirePolicyKind kind) => kind switch
    {
        ScheduleMisfirePolicyKind.RunOnce => "run_once",
        ScheduleMisfirePolicyKind.Skip => "skip",
        ScheduleMisfirePolicyKind.CatchUp => "catch_up",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static InvalidDataException Corrupt(string field) =>
        new($"Persisted schedule has an invalid or missing {field}.");
}
