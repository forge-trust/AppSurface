using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests;

/// <summary>Checks source-bound fixture observations; JSON cannot establish workspace or account authority.</summary>
internal static class WorkspaceGuardMeasurement
{
    private static readonly string[] Cases = ["bundle-extra-directory", "bundle-input-bytes", "bundle-input-mode",
        "scratch-fifo", "scratch-hardlink", "scratch-oversize"];

    /// <summary>Requires all six distinct native rejections alongside the unchanged seven consumer controls.</summary>
    /// <remarks>First-fault phase, real unsealed node checks and account quarantine are measured by the root fixture.
    /// This consistency check cannot issue custody, clear a failed fixture or enroll a consumer proof.</remarks>
    internal static void Validate(JsonElement native)
    {
        var rows = native.GetProperty("workspace_guards").EnumerateArray().ToArray();
        Assert.Equal(Cases, rows.Select(static row => row.GetProperty("case").GetString()));
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            Assert.True(row.GetProperty("passed").GetBoolean());
            Assert.True(row.GetProperty("groups_empty").GetBoolean());
            Assert.True(row.GetProperty("final_groups_empty").GetBoolean());
            Assert.False(row.GetProperty("native_acceptance").GetBoolean());
            Assert.True(row.GetProperty("quarantined").GetBoolean());
            Assert.True(row.GetProperty("outside_sentinel_unchanged").GetBoolean());
            Assert.NotEqual(0, row.GetProperty("root_exit").GetInt32());
            var accounts = row.GetProperty("accounts").EnumerateArray().ToArray();
            Assert.Equal(8, accounts.Length);
            Assert.All(accounts, static account => Assert.True(account.GetProperty("present").GetBoolean()));
            var first = index < 3 ? row.GetProperty("first_failure") : row.GetProperty("workspace_guard").GetProperty("first_failure");
            Assert.Equal(index < 3 ? "WorkspaceCreate" : "ProducerSeal", first.GetProperty("phase").GetString());
            Assert.Equal(index < 3 ? "ASEVD410" : "ASEVD402", first.GetProperty("diagnostic_code").GetString());
            Assert.Equal("Admission", first.GetProperty("error_kind").GetString());
            if (index >= 3)
            {
                var guard = row.GetProperty("workspace_guard");
                Assert.True(guard.GetProperty("passing_procedure_before_failed_seal").GetBoolean());
                Assert.True(guard.GetProperty("quarantined").GetBoolean());
                Assert.True(guard.GetProperty("uid").GetUInt32() > 0);
                Assert.True(guard.GetProperty("gid").GetUInt32() > 0);
                Assert.True(guard.GetProperty("inode").GetUInt64() > 0);
            }
        }
    }
}
