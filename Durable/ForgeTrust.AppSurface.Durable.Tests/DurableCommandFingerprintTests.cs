using System.Text;
using ForgeTrust.AppSurface.Durable;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableCommandFingerprintTests
{
    private static readonly DurableScopeId Scope = new("scope");
    private static readonly DurableCommandId Command = new("command");
    private static readonly DurableEncodedPayload Payload = new(
        "test.payload",
        "v1",
        DurableDataClassification.Operational,
        "value"u8.ToArray());

    [Fact]
    public void Work_fingerprint_ignores_retry_identity_but_changes_with_semantics()
    {
        var first = new DurableWorkRequest(
            Scope, Command, "retry-a", "work", "v1", Payload, DurableProviderSafety.Idempotent);
        var replay = new DurableWorkRequest(
            Scope, new DurableCommandId("other-command"), "retry-b", "work", "v1", Payload, DurableProviderSafety.Idempotent);
        var changed = new DurableWorkRequest(
            Scope, Command, "retry-a", "work", "v2", Payload, DurableProviderSafety.Idempotent);

        Assert.Equal("appsurface.durable.work.enqueue.v1", first.Fingerprint.SchemaId);
        Assert.Equal(DurableCommandFingerprintMatch.Exact, first.Fingerprint.Compare(replay.Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Conflict, first.Fingerprint.Compare(changed.Fingerprint));
        Assert.Equal(64, first.Fingerprint.Sha256.Length);
    }

    [Fact]
    public void Flow_request_families_have_distinct_versioned_schemas()
    {
        var instance = new DurableFlowInstanceId("flow-instance");
        var start = new DurableFlowStartRequest(Scope, Command, "retry", instance, "flow", "v1", Payload);
        var flowEvent = new DurableFlowEventRequest(
            Scope, Command, new DurableFlowEventId("event"), instance, "approved", Payload, 1);
        var cancel = new DurableFlowCancelRequest(Scope, Command, instance, "operator", "cancel", 1);
        var release = new DurableFlowReleaseRequest(Scope, Command, instance, "operator", "repair", 1);

        Assert.Equal("appsurface.durable.flow.start.v1", start.Fingerprint.SchemaId);
        Assert.Equal("appsurface.durable.flow.event.v1", flowEvent.Fingerprint.SchemaId);
        Assert.Equal("appsurface.durable.flow.cancel.v1", cancel.Fingerprint.SchemaId);
        Assert.Equal("appsurface.durable.flow.release.v1", release.Fingerprint.SchemaId);
        Assert.Equal(DurableCommandFingerprintMatch.Exact, start.Fingerprint.Compare(
            new DurableFlowStartRequest(Scope, new DurableCommandId("replay"), "other-retry", instance, "flow", "v1", Payload).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Conflict, start.Fingerprint.Compare(
            new DurableFlowStartRequest(Scope, Command, "retry", instance, "flow", "v2", Payload).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Exact, flowEvent.Fingerprint.Compare(
            new DurableFlowEventRequest(Scope, new DurableCommandId("replay"), new DurableFlowEventId("event"), instance, "approved", Payload, 1).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Conflict, flowEvent.Fingerprint.Compare(
            new DurableFlowEventRequest(Scope, Command, new DurableFlowEventId("event"), instance, "rejected", Payload, 1).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Conflict, flowEvent.Fingerprint.Compare(
            new DurableFlowEventRequest(
                Scope,
                Command,
                new DurableFlowEventId("event"),
                instance,
                "approved",
                new DurableEncodedPayload("test.payload", "v1", DurableDataClassification.Operational, "other"u8.ToArray()),
                1).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Exact, cancel.Fingerprint.Compare(
            new DurableFlowCancelRequest(Scope, new DurableCommandId("replay"), instance, "operator", "cancel", 1).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Conflict, cancel.Fingerprint.Compare(
            new DurableFlowCancelRequest(Scope, Command, instance, "operator", "other", 1).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Exact, release.Fingerprint.Compare(
            new DurableFlowReleaseRequest(Scope, new DurableCommandId("replay"), instance, "operator", "repair", 1).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Conflict, release.Fingerprint.Compare(
            new DurableFlowReleaseRequest(Scope, Command, instance, "other-operator", "repair", 1).Fingerprint));
    }

    [Fact]
    public void Schedule_request_families_include_encoded_target_and_operation_kind()
    {
        var codec = new TestCodec();
        var target = DurableScheduleTarget.Work("work", "v1", "input", codec);
        var scheduleId = new DurableScheduleId("schedule");
        var schedule = DurableSchedule.Every(TimeSpan.FromHours(1));
        var create = new DurableScheduleCreateRequest(Scope, Command, "retry", scheduleId, schedule, target);
        var update = new DurableScheduleUpdateRequest(Scope, Command, scheduleId, 1, schedule, target);

        Assert.Equal("appsurface.durable.schedule.create.v1", create.Fingerprint.SchemaId);
        Assert.Equal("appsurface.durable.schedule.update.v1", update.Fingerprint.SchemaId);
        Assert.Equal(DurableCommandFingerprintMatch.Exact, create.Fingerprint.Compare(
            new DurableScheduleCreateRequest(Scope, new DurableCommandId("replay"), "other-retry", scheduleId, schedule, target).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Conflict, create.Fingerprint.Compare(
            new DurableScheduleCreateRequest(Scope, Command, "retry", scheduleId, schedule, target, "label").Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Conflict, create.Fingerprint.Compare(
            new DurableScheduleCreateRequest(
                Scope,
                Command,
                "retry",
                scheduleId,
                schedule,
                DurableScheduleTarget.Work("work", "v1", "other-input", codec)).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Exact, update.Fingerprint.Compare(
            new DurableScheduleUpdateRequest(Scope, new DurableCommandId("replay"), scheduleId, 1, schedule, target).Fingerprint));
        Assert.Equal(DurableCommandFingerprintMatch.Conflict, update.Fingerprint.Compare(
            new DurableScheduleUpdateRequest(Scope, Command, scheduleId, 2, schedule, target).Fingerprint));

        foreach (var (kind, schema) in new[]
        {
            (DurableScheduleCommandKind.Pause, "appsurface.durable.schedule.pause.v1"),
            (DurableScheduleCommandKind.Resume, "appsurface.durable.schedule.resume.v1"),
            (DurableScheduleCommandKind.Delete, "appsurface.durable.schedule.delete.v1"),
            (DurableScheduleCommandKind.ReleaseAfterRecovery, "appsurface.durable.schedule.recovery-release.v1"),
        })
        {
            var command = new DurableScheduleCommand(kind, Scope, Command, scheduleId, "operator", "reason", 1);
            Assert.Equal(schema, command.Fingerprint.SchemaId);
            Assert.Equal(DurableCommandFingerprintMatch.Exact, command.Fingerprint.Compare(
                new DurableScheduleCommand(kind, Scope, new DurableCommandId("replay"), scheduleId, "operator", "reason", 1).Fingerprint));
            Assert.Equal(DurableCommandFingerprintMatch.Conflict, command.Fingerprint.Compare(
                new DurableScheduleCommand(kind, Scope, Command, scheduleId, "operator", "other-reason", 1).Fingerprint));
        }
    }

    [Fact]
    public void Schedule_fingerprints_cover_every_schedule_shape()
    {
        var codec = new TestCodec();
        var target = DurableScheduleTarget.Flow("flow", "v1", "context", codec);
        var scheduleId = new DurableScheduleId("schedule");
        var instant = new DateTimeOffset(2026, 7, 15, 8, 30, 0, TimeSpan.FromHours(-4));

        var at = new DurableScheduleCreateRequest(
            Scope, Command, "at", scheduleId, DurableSchedule.At(instant), target);
        var after = new DurableScheduleCreateRequest(
            Scope, Command, "after", scheduleId, DurableSchedule.After(TimeSpan.FromMinutes(5)), target);
        var every = new DurableScheduleCreateRequest(
            Scope,
            Command,
            "every",
            scheduleId,
            DurableSchedule.Every(TimeSpan.FromHours(1), instant),
            target);
        var cron = new DurableScheduleCreateRequest(
            Scope,
            Command,
            "cron",
            scheduleId,
            DurableSchedule.Cron("0 8 * * *", "America/New_York"),
            target);

        Assert.Equal("appsurface.durable.schedule.create.v1", at.Fingerprint.SchemaId);
        Assert.NotEqual(at.Fingerprint.Sha256, after.Fingerprint.Sha256);
        Assert.NotEqual(after.Fingerprint.Sha256, every.Fingerprint.Sha256);
        Assert.NotEqual(every.Fingerprint.Sha256, cron.Fingerprint.Sha256);
    }

    [Fact]
    public void Work_fingerprint_includes_non_default_retry_and_due_time()
    {
        var retry = new DurableWorkRetryPolicy(
            3,
            TimeSpan.FromHours(2),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(5),
            "linear-v1");
        var due = new DateTimeOffset(2026, 7, 15, 8, 30, 0, TimeSpan.FromHours(-4));

        var first = new DurableWorkRequest(
            Scope, Command, "retry", "work", "v1", Payload, DurableProviderSafety.ProviderKeyed, retry, due);
        var normalized = new DurableWorkRequest(
            Scope,
            new DurableCommandId("replay"),
            "other-retry",
            "work",
            "v1",
            Payload,
            DurableProviderSafety.ProviderKeyed,
            retry,
            due.ToUniversalTime());

        Assert.Equal(DurableCommandFingerprintMatch.Exact, first.Fingerprint.Compare(normalized.Fingerprint));
    }

    [Fact]
    public void Unknown_fingerprint_schema_fails_closed()
    {
        var known = new DurableCommandFingerprint("appsurface.durable.work.enqueue.v1", new string('a', 64));
        var unknown = new DurableCommandFingerprint("appsurface.durable.work.enqueue.v2", new string('a', 64));

        Assert.Equal(DurableCommandFingerprintMatch.UnsupportedSchema, known.Compare(unknown));
        Assert.Throws<ArgumentNullException>(() => known.Compare(null!));
        Assert.Throws<ArgumentNullException>(() => new DurableCommandFingerprint("schema", null!));
        Assert.Throws<ArgumentException>(() => new DurableCommandFingerprint("schema", new string('a', 63)));
        Assert.Throws<ArgumentException>(() => new DurableCommandFingerprint("schema", new string('A', 64)));
        Assert.Throws<ArgumentException>(() => new DurableCommandFingerprint("schema", $"g{new string('a', 63)}"));
    }

    private sealed class TestCodec : IDurablePayloadCodec<string>
    {
        public Type PayloadType => typeof(string);
        public string ContractName => "schedule.input";
        public string ContractVersion => "v1";
        public DurableDataClassification Classification => DurableDataClassification.Operational;
        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;
        public DurableEncodedPayload Encode(string value) => new(
            ContractName,
            ContractVersion,
            Classification,
            Encoding.UTF8.GetBytes(value),
            RetentionPolicyId);
        public string Decode(DurableEncodedPayload payload) => Encoding.UTF8.GetString(payload.Content.Span);
        public DurableEncodedPayload EncodeObject(object value) => Encode((string)value);
        public object DecodeObject(DurableEncodedPayload payload) => Decode(payload);
    }
}
