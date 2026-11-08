using ForgeTrust.AppSurface.Auth.Keycloak;
using ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

/// <summary>Verifies cumulative invocation ceilings before writes, including nested and concurrent phases.</summary>
public sealed class TemporaryDiskBudgetTests
{
    [Fact]
    public void Nested_phases_share_the_exact_ceiling_without_resetting_it()
    {
        using (TemporaryDiskBudget.BeginInvocation(3))
        {
            TemporaryDiskBudget.Account(1);
            using (TemporaryDiskBudget.BeginInvocation()) TemporaryDiskBudget.Account(2);
            Assert.Throws<InvalidDataException>(() => TemporaryDiskBudget.Account(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => TemporaryDiskBudget.Account(-1));
        }
        using (TemporaryDiskBudget.BeginInvocation(1)) TemporaryDiskBudget.Account(1);
        TemporaryDiskBudget.Account(long.MaxValue); // No operation is active; no ambient global quota.
        Assert.Throws<ArgumentOutOfRangeException>(() => TemporaryDiskBudget.BeginInvocation(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TemporaryDiskBudget.BeginInvocation(long.MaxValue));
    }

    [Fact]
    public async Task Concurrent_phases_cannot_both_spend_the_same_remaining_allocation()
    {
        using var invocation = TemporaryDiskBudget.BeginInvocation(1);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> Write() => Task.Run(async () =>
        {
            await barrier.Task;
            try { TemporaryDiskBudget.Account(1); return true; }
            catch (InvalidDataException) { return false; }
        });
        var first = Write(); var second = Write(); barrier.SetResult();
        Assert.Single(await Task.WhenAll(first, second), accepted => accepted);
    }

    [Fact]
    public async Task Independent_invocations_keep_their_own_allocations()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Write() => Task.Run(async () =>
        {
            using var invocation = TemporaryDiskBudget.BeginInvocation(1);
            await barrier.Task;
            TemporaryDiskBudget.Account(1);
        });
        var first = Write(); var second = Write(); barrier.SetResult();
        await Task.WhenAll(first, second);
    }
    [Fact]
    public async Task Public_capture_rejects_exhaustion_without_committing_a_snapshot()
    {
        using var fixture = TinyImageFixture.Create();
        using var disk = TemporaryDiskBudget.BeginInvocation(1);
        var destination = System.IO.Path.Combine(fixture.Root, "budget-rejected-snapshot");
        var failure = await Assert.ThrowsAsync<KeycloakSecurityException>(() =>
            KeycloakSecurity.CaptureAsync(fixture.Recipe, fixture.Policy, fixture.Bindings, destination));
        Assert.False(Directory.Exists(destination));
        Assert.Equal(KeycloakEffectState.None, failure.Effect);
        Assert.DoesNotContain(Directory.EnumerateDirectories(fixture.Root), path => System.IO.Path.GetFileName(path).StartsWith(".capture-", StringComparison.Ordinal));
    }

    [Fact]
    public void RepeatedDisposalDoesNotResetAnotherInvocation()
    {
        var first = TemporaryDiskBudget.BeginInvocation(1);
        first.Dispose();
        using var second = TemporaryDiskBudget.BeginInvocation(1);
        TemporaryDiskBudget.Account(1);
        first.Dispose();
        Assert.Throws<InvalidDataException>(() => TemporaryDiskBudget.Account(1));
    }

}
