using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Receipt consistency data only; these fixtures create no live process, account or custody.</summary>
public sealed class WorkspaceGuardMeasurementTests
{
    [Fact]
    public void CompleteSeparateSixGuardFixtureIsRequired()
    {
        var fixture = Fixture();
        WorkspaceGuardMeasurement.Validate(System.Text.Json.JsonSerializer.SerializeToElement(fixture));
    }

    [Theory]
    [InlineData("passed")]
    [InlineData("groups_empty")]
    [InlineData("final_groups_empty")]
    [InlineData("quarantined")]
    [InlineData("outside_sentinel_unchanged")]
    [InlineData("native_acceptance")]
    [InlineData("root_exit")]
    [InlineData("account-presence")]
    [InlineData("account-count")]
    [InlineData("first-phase")]
    [InlineData("first-code")]
    [InlineData("first-kind")]
    [InlineData("producer-procedure")]
    [InlineData("producer-quarantine")]
    [InlineData("producer-uid")]
    [InlineData("producer-gid")]
    [InlineData("producer-inode")]
    [InlineData("omitted-case")]
    [InlineData("foreign-case")]
    public void OneInvalidObservationCannotBecomeACompleteNativeReceipt(string field)
    {
        var fixture = Fixture();
        WorkspaceGuardMeasurement.Validate(System.Text.Json.JsonSerializer.SerializeToElement(fixture));
        var rows = fixture["workspace_guards"]!.AsArray();
        var row = rows[0]!.AsObject();
        var guard = rows[3]!["workspace_guard"]!.AsObject();
        switch (field)
        {
            case "native_acceptance": row[field] = true; break;
            case "root_exit": row[field] = 0; break;
            case "account-presence": row["accounts"]![0]!["present"] = false; break;
            case "account-count": row["accounts"]!.AsArray().RemoveAt(0); break;
            case "first-phase": row["first_failure"]!["phase"] = "ProducerSeal"; break;
            case "first-code": row["first_failure"]!["diagnostic_code"] = "ASEVD402"; break;
            case "first-kind": row["first_failure"]!["error_kind"] = "Io"; break;
            case "producer-procedure": guard["passing_procedure_before_failed_seal"] = false; break;
            case "producer-quarantine": guard["quarantined"] = false; break;
            case "producer-uid": guard["uid"] = 0u; break;
            case "producer-gid": guard["gid"] = 0u; break;
            case "producer-inode": guard["inode"] = 0UL; break;
            case "omitted-case": rows.RemoveAt(5); break;
            case "foreign-case": row["case"] = "private-canary"; break;
            default: row[field] = false; break;
        }
        Assert.ThrowsAny<Exception>(() => WorkspaceGuardMeasurement.Validate(
            System.Text.Json.JsonSerializer.SerializeToElement(fixture)));
    }

    private static JsonObject Fixture()
    {
        var cases = new[] { "bundle-extra-directory", "bundle-input-bytes", "bundle-input-mode",
            "scratch-fifo", "scratch-hardlink", "scratch-oversize" };
        var rows = new JsonArray();
        for (var index = 0; index < cases.Length; index++)
        {
            var first = new JsonObject { ["phase"] = index < 3 ? "WorkspaceCreate" : "ProducerSeal",
                ["diagnostic_code"] = index < 3 ? "ASEVD410" : "ASEVD402", ["error_kind"] = "Admission" };
            var accounts = new JsonArray();
            for (var account = 0; account < 8; account++) accounts.Add(new JsonObject { ["present"] = true });
            var row = new JsonObject { ["case"] = cases[index], ["passed"] = true, ["root_exit"] = 1,
                ["native_acceptance"] = false, ["quarantined"] = true, ["groups_empty"] = true,
                ["final_groups_empty"] = true, ["outside_sentinel_unchanged"] = true, ["accounts"] = accounts };
            if (index < 3) row["first_failure"] = first;
            else row["workspace_guard"] = new JsonObject { ["first_failure"] = first,
                ["passing_procedure_before_failed_seal"] = true, ["quarantined"] = true,
                ["uid"] = 12345u, ["gid"] = 12346u, ["inode"] = 1UL };
            rows.Add(row);
        }
        return new JsonObject { ["workspace_guards"] = rows };
    }
}
