using System.Collections.Concurrent;

namespace ForgeTrust.AppSurface.Auth.Tests;

/// <summary>
/// Proves the host lifecycle the passive contracts are designed to describe. The in-memory host is test-only; the
/// package continues to leave receipt storage, revocation, policy, audit delivery, and execution to real hosts.
/// </summary>
public sealed class AgentApprovalLifecycleProofTests
{
    [Fact]
    public void ConfirmationDenial_DoesNotIssueAReceiptOrExecuteTheTransition()
    {
        var host = new InMemoryApprovalHost(new FakeClock(Utc(8, 0)));
        var decision = host.DenyConfirmation(Request());

        Assert.True(decision.IsDenied);
        Assert.Equal(AgentApprovalDiagnosticCodes.Denied, decision.Code);
        Assert.Equal(0, host.ExecutedTransitionCount);
    }

    [Fact]
    public void Receipt_FirstConsumptionExecutesOnce_AndReplayFailsClosed()
    {
        var host = new InMemoryApprovalHost(new FakeClock(Utc(8, 0)));
        var receipt = host.Approve(Request());

        var first = host.Consume(receipt, receipt.Binding);
        var replay = host.Consume(receipt, receipt.Binding);

        Assert.True(first.IsConsumed);
        Assert.Equal(AgentApprovalConsumptionOutcome.AlreadyConsumed, replay.Outcome);
        Assert.Equal(AgentApprovalDiagnosticCodes.AlreadyConsumed, replay.Code);
        Assert.Equal(1, host.ExecutedTransitionCount);
    }

    [Fact]
    public async Task Receipt_ConcurrentConsumptionHasExactlyOneWinner()
    {
        var host = new InMemoryApprovalHost(new FakeClock(Utc(8, 0)));
        var receipt = host.Approve(Request());
        using var start = new ManualResetEventSlim(false);

        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return host.Consume(receipt, receipt.Binding);
            }))
            .ToArray();

        start.Set();
        var outcomes = (await Task.WhenAll(attempts)).Select(result => result.Outcome).ToArray();

        Assert.Equal(1, outcomes.Count(outcome => outcome == AgentApprovalConsumptionOutcome.Consumed));
        Assert.Equal(7, outcomes.Count(outcome => outcome == AgentApprovalConsumptionOutcome.AlreadyConsumed));
        Assert.Equal(1, host.ExecutedTransitionCount);
    }

    [Fact]
    public void Receipt_ExpiredBeforeConsumption_FailsClosed()
    {
        var clock = new FakeClock(Utc(8, 0));
        var host = new InMemoryApprovalHost(clock);
        var receipt = host.Approve(Request(), lifetime: TimeSpan.FromMinutes(1));
        clock.UtcNow = Utc(8, 1);

        var result = host.Consume(receipt, receipt.Binding);

        Assert.Equal(AgentApprovalConsumptionOutcome.Expired, result.Outcome);
        Assert.Equal(0, host.ExecutedTransitionCount);
    }

    [Fact]
    public void Receipt_RevokedBeforeConsumption_FailsClosed()
    {
        var host = new InMemoryApprovalHost(new FakeClock(Utc(8, 0)));
        var receipt = host.Approve(Request());
        host.Revoke(receipt);

        var result = host.Consume(receipt, receipt.Binding);

        Assert.Equal(AgentApprovalConsumptionOutcome.Revoked, result.Outcome);
        Assert.Equal(0, host.ExecutedTransitionCount);
    }

    [Fact]
    public void Receipt_NotIssuedByTheHost_FailsClosedAsDenied()
    {
        var host = new InMemoryApprovalHost(new FakeClock(Utc(8, 0)));
        var forgedReceipt = new AgentApprovalReceipt(
            "forged-receipt",
            Binding(),
            new AgentIdentityReference("harness:local"),
            new AgentApproverReference("subject:andrew"),
            "correlation-1",
            Utc(8, 0),
            Utc(8, 5));

        var result = host.Consume(forgedReceipt, forgedReceipt.Binding);

        Assert.Equal(AgentApprovalConsumptionOutcome.Denied, result.Outcome);
        Assert.Equal(AgentApprovalDiagnosticCodes.ConsumptionDenied, result.Code);
        Assert.Equal(0, host.ExecutedTransitionCount);
    }

    [Fact]
    public void Receipt_WithAnIssuedReferenceButChangedBinding_FailsClosedAsDenied()
    {
        var host = new InMemoryApprovalHost(new FakeClock(Utc(8, 0)));
        var issuedReceipt = host.Approve(Request());
        var tamperedReceipt = new AgentApprovalReceipt(
            issuedReceipt.ReceiptId,
            Binding(safeIntentDigest: "sha256:changed"),
            issuedReceipt.Agent,
            issuedReceipt.Approver,
            issuedReceipt.CorrelationId,
            issuedReceipt.IssuedAt,
            issuedReceipt.ExpiresAt);

        var result = host.Consume(tamperedReceipt, issuedReceipt.Binding);

        Assert.Equal(AgentApprovalConsumptionOutcome.Denied, result.Outcome);
        Assert.Equal(AgentApprovalDiagnosticCodes.ConsumptionDenied, result.Code);
        Assert.Equal(0, host.ExecutedTransitionCount);
    }

    [Fact]
    public void Receipt_StateVersionChange_FailsClosedAsStale()
    {
        var host = new InMemoryApprovalHost(new FakeClock(Utc(8, 0)));
        var receipt = host.Approve(Request());
        var changedState = Binding(expectedStateVersion: "version-2");

        var result = host.Consume(receipt, changedState);

        Assert.Equal(AgentApprovalConsumptionOutcome.Stale, result.Outcome);
        Assert.Equal(0, host.ExecutedTransitionCount);
    }

    [Fact]
    public void Receipt_IntentDigestChange_FailsClosedAsBindingMismatch()
    {
        var host = new InMemoryApprovalHost(new FakeClock(Utc(8, 0)));
        var receipt = host.Approve(Request());
        var changedIntent = Binding(safeIntentDigest: "sha256:changed");

        var result = host.Consume(receipt, changedIntent);

        Assert.Equal(AgentApprovalConsumptionOutcome.BindingMismatch, result.Outcome);
        Assert.Equal(0, host.ExecutedTransitionCount);
    }

    [Theory]
    [InlineData(false, true, "lost human authority")]
    [InlineData(true, false, "missing agent grant")]
    public void Receipt_LostAuthorityOrGrant_FailsClosedAsDenied(
        bool humanAuthorityPresent,
        bool agentGrantPresent,
        string _)
    {
        var host = new InMemoryApprovalHost(new FakeClock(Utc(8, 0)));
        var receipt = host.Approve(Request());

        var result = host.Consume(receipt, receipt.Binding, humanAuthorityPresent, agentGrantPresent);

        Assert.Equal(AgentApprovalConsumptionOutcome.Denied, result.Outcome);
        Assert.Equal(AgentApprovalDiagnosticCodes.ConsumptionDenied, result.Code);
        Assert.Equal(0, host.ExecutedTransitionCount);
    }

    private static DateTimeOffset Utc(int hour, int minute) =>
        new(2026, 8, 11, hour, minute, 0, TimeSpan.Zero);

    private static AgentActionRequest Request() =>
        new(
            new AgentActionMetadata(
                "workflow.approve",
                "Approve workflow",
                AgentActionRisk.High,
                AgentConfirmationPosture.AlwaysRequireHuman,
                AgentActionRedaction.DoNotExposeArguments),
            Binding(),
            new AgentIdentityReference("harness:local"),
            "correlation-1",
            Utc(8, 0),
            "Approve the production workflow.");

    private static AgentActionBinding Binding(
        string expectedStateVersion = "version-1",
        string safeIntentDigest = "sha256:abc") =>
        new(
            "workflow.approve",
            "task-1",
            "workflow-1",
            "pending",
            expectedStateVersion,
            "approve",
            "workflow-approval/v1",
            safeIntentDigest);

    private sealed class FakeClock(DateTimeOffset utcNow)
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class InMemoryApprovalHost
    {
        private readonly FakeClock _clock;
        private readonly ConcurrentDictionary<string, AgentApprovalReceipt> _issuedReceipts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _consumedReceiptIds = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _revokedReceiptIds = new(StringComparer.Ordinal);

        public InMemoryApprovalHost(FakeClock clock)
        {
            _clock = clock;
        }

        public int ExecutedTransitionCount { get; private set; }

        public AgentAuthorizationDecision DenyConfirmation(AgentActionRequest request)
        {
            return AgentAuthorizationDecision.Denied(request.CorrelationId, "Human denied the confirmation.");
        }

        public AgentApprovalReceipt Approve(AgentActionRequest request, TimeSpan? lifetime = null)
        {
            var expiresAt = _clock.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(5));
            var confirmation = new AgentConfirmationRequest(
                request,
                new AgentApproverReference("subject:andrew"),
                expiresAt);
            var receipt = AgentApprovalReceipt.FromConfirmedRequest(
                $"receipt-{Guid.NewGuid():N}",
                confirmation,
                _clock.UtcNow,
                expiresAt);
            if (!_issuedReceipts.TryAdd(receipt.ReceiptId, receipt))
            {
                throw new InvalidOperationException("Receipt identifiers must be unique.");
            }

            return receipt;
        }

        public void Revoke(AgentApprovalReceipt receipt)
        {
            _revokedReceiptIds.TryAdd(receipt.ReceiptId, 0);
        }

        public AgentApprovalConsumptionResult Consume(
            AgentApprovalReceipt receipt,
            AgentActionBinding currentBinding,
            bool humanAuthorityPresent = true,
            bool agentGrantPresent = true)
        {
            lock (_consumedReceiptIds)
            {
                if (!_issuedReceipts.TryGetValue(receipt.ReceiptId, out var issuedReceipt)
                    || !ReceiptMatchesIssuedRecord(issuedReceipt, receipt))
                {
                    return AgentApprovalConsumptionResult.Denied(receipt.CorrelationId);
                }

                if (_clock.UtcNow >= issuedReceipt.ExpiresAt)
                {
                    return AgentApprovalConsumptionResult.Expired(issuedReceipt.CorrelationId);
                }

                if (_revokedReceiptIds.ContainsKey(issuedReceipt.ReceiptId))
                {
                    return AgentApprovalConsumptionResult.Revoked(issuedReceipt.CorrelationId);
                }

                if (!humanAuthorityPresent || !agentGrantPresent)
                {
                    return AgentApprovalConsumptionResult.Denied(issuedReceipt.CorrelationId);
                }

                if (!string.Equals(issuedReceipt.Binding.ExpectedState, currentBinding.ExpectedState, StringComparison.Ordinal)
                    || !string.Equals(issuedReceipt.Binding.ExpectedStateVersion, currentBinding.ExpectedStateVersion, StringComparison.Ordinal))
                {
                    return AgentApprovalConsumptionResult.Stale(issuedReceipt.CorrelationId);
                }

                if (!issuedReceipt.Binding.Matches(currentBinding))
                {
                    return AgentApprovalConsumptionResult.BindingMismatch(issuedReceipt.CorrelationId);
                }

                if (!_consumedReceiptIds.TryAdd(issuedReceipt.ReceiptId, 0))
                {
                    return AgentApprovalConsumptionResult.AlreadyConsumed(issuedReceipt.CorrelationId);
                }

                ExecutedTransitionCount++;
                return AgentApprovalConsumptionResult.Consumed(issuedReceipt.CorrelationId);
            }
        }

        private static bool ReceiptMatchesIssuedRecord(AgentApprovalReceipt expected, AgentApprovalReceipt current)
        {
            return string.Equals(expected.ReceiptId, current.ReceiptId, StringComparison.Ordinal)
                && expected.Binding.Matches(current.Binding)
                && expected.Agent == current.Agent
                && expected.Approver == current.Approver
                && string.Equals(expected.CorrelationId, current.CorrelationId, StringComparison.Ordinal)
                && expected.IssuedAt == current.IssuedAt
                && expected.ExpiresAt == current.ExpiresAt;
        }
    }
}
