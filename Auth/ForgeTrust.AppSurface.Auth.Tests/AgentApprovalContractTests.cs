namespace ForgeTrust.AppSurface.Auth.Tests;

public sealed class AgentApprovalContractTests
{
    [Theory]
    [InlineData(AgentActionRisk.Low, 0)]
    [InlineData(AgentActionRisk.Elevated, 1)]
    [InlineData(AgentActionRisk.High, 2)]
    public void AgentActionRisk_NumericValues_AreStable(AgentActionRisk value, int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(AgentConfirmationPosture.HostDetermines, 0)]
    [InlineData(AgentConfirmationPosture.AlwaysRequireHuman, 1)]
    public void AgentConfirmationPosture_NumericValues_AreStable(AgentConfirmationPosture value, int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(AgentActionRedaction.SafeSummaryOnly, 0)]
    [InlineData(AgentActionRedaction.RequireHostRedaction, 1)]
    [InlineData(AgentActionRedaction.DoNotExposeArguments, 2)]
    public void AgentActionRedaction_NumericValues_AreStable(AgentActionRedaction value, int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(AgentAuthorizationDecisionKind.Allowed, 0)]
    [InlineData(AgentAuthorizationDecisionKind.Denied, 1)]
    [InlineData(AgentAuthorizationDecisionKind.ConfirmationRequired, 2)]
    public void AgentAuthorizationDecisionKind_NumericValues_AreStable(
        AgentAuthorizationDecisionKind value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(AgentApprovalConsumptionOutcome.Consumed, 0)]
    [InlineData(AgentApprovalConsumptionOutcome.AlreadyConsumed, 1)]
    [InlineData(AgentApprovalConsumptionOutcome.Expired, 2)]
    [InlineData(AgentApprovalConsumptionOutcome.Revoked, 3)]
    [InlineData(AgentApprovalConsumptionOutcome.Stale, 4)]
    [InlineData(AgentApprovalConsumptionOutcome.BindingMismatch, 5)]
    [InlineData(AgentApprovalConsumptionOutcome.Denied, 6)]
    public void AgentApprovalConsumptionOutcome_NumericValues_AreStable(
        AgentApprovalConsumptionOutcome value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(AgentAuthorizationAuditEventKind.Proposed, 0)]
    [InlineData(AgentAuthorizationAuditEventKind.Allowed, 1)]
    [InlineData(AgentAuthorizationAuditEventKind.Denied, 2)]
    [InlineData(AgentAuthorizationAuditEventKind.ConfirmationRequired, 3)]
    [InlineData(AgentAuthorizationAuditEventKind.Approved, 4)]
    [InlineData(AgentAuthorizationAuditEventKind.Revoked, 5)]
    [InlineData(AgentAuthorizationAuditEventKind.ConsumptionAttempted, 6)]
    [InlineData(AgentAuthorizationAuditEventKind.Consumed, 7)]
    [InlineData(AgentAuthorizationAuditEventKind.AlreadyConsumed, 8)]
    [InlineData(AgentAuthorizationAuditEventKind.Expired, 9)]
    [InlineData(AgentAuthorizationAuditEventKind.Stale, 10)]
    [InlineData(AgentAuthorizationAuditEventKind.BindingMismatch, 11)]
    [InlineData(AgentAuthorizationAuditEventKind.ConsumptionDenied, 12)]
    public void AgentAuthorizationAuditEventKind_NumericValues_AreStable(
        AgentAuthorizationAuditEventKind value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Fact]
    public void AgentApprovalDiagnosticCodes_AreStable()
    {
        Assert.Equal("agent-approval.proposed", AgentApprovalDiagnosticCodes.Proposed);
        Assert.Equal("agent-approval.allowed", AgentApprovalDiagnosticCodes.Allowed);
        Assert.Equal("agent-approval.denied", AgentApprovalDiagnosticCodes.Denied);
        Assert.Equal("agent-approval.confirmation-required", AgentApprovalDiagnosticCodes.ConfirmationRequired);
        Assert.Equal("agent-approval.consumption-attempted", AgentApprovalDiagnosticCodes.ConsumptionAttempted);
        Assert.Equal("agent-approval.approved", AgentApprovalDiagnosticCodes.Approved);
        Assert.Equal("agent-approval.consumed", AgentApprovalDiagnosticCodes.Consumed);
        Assert.Equal("agent-approval.already-consumed", AgentApprovalDiagnosticCodes.AlreadyConsumed);
        Assert.Equal("agent-approval.expired", AgentApprovalDiagnosticCodes.Expired);
        Assert.Equal("agent-approval.revoked", AgentApprovalDiagnosticCodes.Revoked);
        Assert.Equal("agent-approval.stale", AgentApprovalDiagnosticCodes.Stale);
        Assert.Equal("agent-approval.binding-mismatch", AgentApprovalDiagnosticCodes.BindingMismatch);
        Assert.Equal("agent-approval.consumption-denied", AgentApprovalDiagnosticCodes.ConsumptionDenied);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void References_WhenValueIsMissing_Throw(string? value)
    {
        if (value is null)
        {
            Assert.Throws<ArgumentNullException>(() => new AgentIdentityReference(value!));
            Assert.Throws<ArgumentNullException>(() => new AgentApproverReference(value!));
            return;
        }

        Assert.Throws<ArgumentException>(() => new AgentIdentityReference(value));
        Assert.Throws<ArgumentException>(() => new AgentApproverReference(value));
    }

    [Fact]
    public void References_UseOrdinalEqualityAndRedactToString()
    {
        var agent = new AgentIdentityReference("harness:local");
        var sameAgent = new AgentIdentityReference("harness:local");
        var differentAgent = new AgentIdentityReference("HARNESS:LOCAL");
        var approver = new AgentApproverReference("subject:andrew");
        var sameApprover = new AgentApproverReference("subject:andrew");
        var defaultAgent = default(AgentIdentityReference);
        var defaultApprover = default(AgentApproverReference);

        Assert.Equal(agent, sameAgent);
        Assert.NotEqual(agent, differentAgent);
        Assert.True(defaultAgent == default(AgentIdentityReference));
        Assert.False(defaultAgent != default(AgentIdentityReference));
        Assert.Equal(0, defaultAgent.GetHashCode());
        Assert.Equal(0, defaultApprover.GetHashCode());
        Assert.Equal(agent.GetHashCode(), sameAgent.GetHashCode());
        Assert.Equal(approver.GetHashCode(), sameApprover.GetHashCode());
        Assert.False(agent.Equals((object)"harness:local"));
        Assert.False(agent.Equals(null));
        Assert.False(approver.Equals((object)"subject:andrew"));
        Assert.False(approver.Equals(null));
        Assert.Contains("<redacted>", agent.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(agent.Value, agent.ToString(), StringComparison.Ordinal);
        Assert.Contains("<redacted>", approver.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(approver.Value, approver.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("actionId")]
    [InlineData("taskId")]
    [InlineData("workflowInstanceId")]
    [InlineData("expectedState")]
    [InlineData("expectedStateVersion")]
    [InlineData("transition")]
    [InlineData("bindingProfile")]
    [InlineData("safeIntentDigest")]
    public void Binding_WhenRequiredTextIsBlank_Throws(string parameterName)
    {
        var exception = Assert.Throws<ArgumentException>(() => parameterName switch
        {
            "actionId" => Binding(actionId: " "),
            "taskId" => Binding(taskId: " "),
            "workflowInstanceId" => Binding(workflowInstanceId: " "),
            "expectedState" => Binding(expectedState: " "),
            "expectedStateVersion" => Binding(expectedStateVersion: " "),
            "transition" => Binding(transition: " "),
            "bindingProfile" => Binding(bindingProfile: " "),
            "safeIntentDigest" => Binding(safeIntentDigest: " "),
            _ => throw new InvalidOperationException("Unknown parameter."),
        });

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public void Binding_PreservesFieldsAndRedactsToString()
    {
        var binding = Binding();
        var bindingText = binding.ToString();

        Assert.Equal("workflow.approve", binding.ActionId);
        Assert.Equal("task-1", binding.TaskId);
        Assert.Equal("workflow-1", binding.WorkflowInstanceId);
        Assert.Equal("pending", binding.ExpectedState);
        Assert.Equal("version-1", binding.ExpectedStateVersion);
        Assert.Equal("approve", binding.Transition);
        Assert.Equal("workflow-approval/v1", binding.BindingProfile);
        Assert.Equal("sha256:abc", binding.SafeIntentDigest);
        Assert.Contains("<redacted>", bindingText, StringComparison.Ordinal);
        foreach (var value in new[]
                 {
                     binding.ActionId,
                     binding.TaskId,
                     binding.WorkflowInstanceId,
                     binding.ExpectedState,
                     binding.ExpectedStateVersion,
                     binding.Transition,
                     binding.BindingProfile,
                     binding.SafeIntentDigest,
                 })
        {
            Assert.DoesNotContain(value, bindingText, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("actionId")]
    [InlineData("taskId")]
    [InlineData("workflowInstanceId")]
    [InlineData("expectedState")]
    [InlineData("expectedStateVersion")]
    [InlineData("transition")]
    [InlineData("bindingProfile")]
    [InlineData("safeIntentDigest")]
    public void Binding_MatchesEveryApprovalRelevantFieldWithOrdinalSemantics(string field)
    {
        var binding = Binding();
        var changed = field switch
        {
            "actionId" => Binding(actionId: "workflow.reject"),
            "taskId" => Binding(taskId: "task-2"),
            "workflowInstanceId" => Binding(workflowInstanceId: "workflow-2"),
            "expectedState" => Binding(expectedState: "approved"),
            "expectedStateVersion" => Binding(expectedStateVersion: "VERSION-1"),
            "transition" => Binding(transition: "reject"),
            "bindingProfile" => Binding(bindingProfile: "workflow-approval/v2"),
            "safeIntentDigest" => Binding(safeIntentDigest: "sha256:changed"),
            _ => throw new InvalidOperationException("Unknown field."),
        };

        Assert.True(binding.Matches(Binding()));
        Assert.False(binding.Matches(changed));
        Assert.False(binding.Matches(null));
    }

    [Fact]
    public void ActionMetadata_UsesDefaultsAndSnapshotsMetadata()
    {
        var metadata = new Dictionary<string, string> { ["safe"] = "value" };
        var action = new AgentActionMetadata("workflow.approve", "Approve workflow", metadata: metadata);
        metadata["safe"] = "changed";

        Assert.Equal(AgentActionRisk.Elevated, action.Risk);
        Assert.Equal(AgentConfirmationPosture.HostDetermines, action.ConfirmationPosture);
        Assert.Equal(AgentActionRedaction.SafeSummaryOnly, action.Redaction);
        Assert.Equal("value", action.Metadata["safe"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)action.Metadata).Add("another", "value"));
    }

    [Theory]
    [InlineData("actionId")]
    [InlineData("displayName")]
    [InlineData("risk")]
    [InlineData("confirmationPosture")]
    [InlineData("redaction")]
    public void ActionMetadata_WhenInputIsInvalid_Throws(string parameterName)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => parameterName switch
        {
            "actionId" => new AgentActionMetadata(" ", "Approve workflow"),
            "displayName" => new AgentActionMetadata("workflow.approve", " "),
            "risk" => new AgentActionMetadata("workflow.approve", "Approve workflow", (AgentActionRisk)99),
            "confirmationPosture" => new AgentActionMetadata(
                "workflow.approve",
                "Approve workflow",
                confirmationPosture: (AgentConfirmationPosture)99),
            "redaction" => new AgentActionMetadata(
                "workflow.approve",
                "Approve workflow",
                redaction: (AgentActionRedaction)99),
            _ => throw new InvalidOperationException("Unknown parameter."),
        });

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public void ActionRequest_PreservesSafeFieldsAndNormalizesOptionalRationale()
    {
        var requestedAt = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        var metadata = new Dictionary<string, string> { ["safe"] = "original" };
        var request = new AgentActionRequest(
            Action(),
            Binding(),
            new AgentIdentityReference("harness:local"),
            "correlation-1",
            requestedAt,
            "Approve the production workflow.",
            rationale: " ",
            metadata);
        metadata["safe"] = "changed";

        Assert.Equal("workflow.approve", request.Action.ActionId);
        Assert.Equal("task-1", request.Binding.TaskId);
        Assert.Equal("harness:local", request.Agent.Value);
        Assert.Equal("correlation-1", request.CorrelationId);
        Assert.Equal(requestedAt, request.RequestedAt);
        Assert.Equal("Approve the production workflow.", request.SafeSummary);
        Assert.Null(request.Rationale);
        Assert.Equal("original", request.Metadata["safe"]);
    }

    [Fact]
    public void ActionRequest_WhenActionAndBindingActionIdentifiersDiffer_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => new AgentActionRequest(
            Action(),
            Binding(actionId: "workflow.reject"),
            new AgentIdentityReference("harness:local"),
            "correlation-1",
            DateTimeOffset.UtcNow,
            "Approve the production workflow."));

        Assert.Equal("binding", exception.ParamName);
    }

    [Theory]
    [InlineData("displayName")]
    [InlineData("safeSummary")]
    [InlineData("metadata")]
    public void DisplaySafeFields_WhenTheyContainControlCharacters_Throw(string parameterName)
    {
        var exception = Assert.Throws<ArgumentException>(() => parameterName switch
        {
            "displayName" => new AgentActionMetadata("workflow.approve", "Approve\nworkflow"),
            "safeSummary" => new AgentActionRequest(
                Action(),
                Binding(),
                new AgentIdentityReference("harness:local"),
                "correlation-1",
                DateTimeOffset.UtcNow,
                "Approve\nworkflow"),
            "metadata" => new AgentActionMetadata(
                "workflow.approve",
                "Approve workflow",
                metadata: new Dictionary<string, string> { ["safe"] = "value\n" }),
            _ => throw new InvalidOperationException("Unknown parameter."),
        });

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Theory]
    [InlineData("displayName")]
    [InlineData("safeSummary")]
    [InlineData("metadata")]
    [InlineData("receiptId")]
    [InlineData("auditSummary")]
    public void DisplaySafeFields_WhenTheyContainUnicodeFormatCharacters_Throw(string parameterName)
    {
        const string bidiOverride = "\u202E";
        var exception = Assert.Throws<ArgumentException>(() => parameterName switch
        {
            "displayName" => new AgentActionMetadata("workflow.approve", "Approve" + bidiOverride + "workflow"),
            "safeSummary" => new AgentActionRequest(
                Action(),
                Binding(),
                new AgentIdentityReference("harness:local"),
                "correlation-1",
                DateTimeOffset.UtcNow,
                "Approve" + bidiOverride + "workflow"),
            "metadata" => new AgentActionMetadata(
                "workflow.approve",
                "Approve workflow",
                metadata: new Dictionary<string, string> { ["safe"] = "value" + bidiOverride }),
            "receiptId" => new AgentApprovalReceipt(
                "receipt" + bidiOverride + "-1",
                Binding(),
                new AgentIdentityReference("harness:local"),
                new AgentApproverReference("subject:andrew"),
                "correlation-1",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5)),
            "auditSummary" => new AgentAuthorizationAuditEvent(
                AgentAuthorizationAuditEventKind.Proposed,
                DateTimeOffset.UtcNow,
                AgentApprovalDiagnosticCodes.Proposed,
                "correlation-1",
                Binding(),
                new AgentIdentityReference("harness:local"),
                safeSummary: "Approve" + bidiOverride + "workflow"),
            _ => throw new InvalidOperationException("Unknown parameter."),
        });

        Assert.NotNull(exception.ParamName);
    }

    [Fact]
    public void DisplaySafeFields_WhenTheyContainSupplementaryUnicodeFormatCharacters_Throw()
    {
        const string languageTag = "\U000E0001";

        var exception = Assert.Throws<ArgumentException>(() => new AgentActionMetadata(
            "workflow.approve",
            "Approve" + languageTag + "workflow"));

        Assert.Equal("displayName", exception.ParamName);
    }

    [Theory]
    [InlineData("rationale")]
    [InlineData("decisionMessage")]
    [InlineData("consumptionMessage")]
    [InlineData("auditSummary")]
    public void OptionalDisplaySafeFields_WhenTheyContainControlCharacters_Throw(string scenario)
    {
        var exception = Assert.Throws<ArgumentException>(() => scenario switch
        {
            "rationale" => new AgentActionRequest(
                Action(),
                Binding(),
                new AgentIdentityReference("harness:local"),
                "correlation-1",
                DateTimeOffset.UtcNow,
                "Approve workflow",
                rationale: "Untrusted\ntext"),
            "decisionMessage" => AgentAuthorizationDecision.Allowed("correlation-1", "Untrusted\ntext"),
            "consumptionMessage" => AgentApprovalConsumptionResult.Consumed("correlation-1", "Untrusted\ntext"),
            "auditSummary" => new AgentAuthorizationAuditEvent(
                AgentAuthorizationAuditEventKind.Proposed,
                DateTimeOffset.UtcNow,
                AgentApprovalDiagnosticCodes.Proposed,
                "correlation-1",
                Binding(),
                new AgentIdentityReference("harness:local"),
                safeSummary: "Untrusted\ntext"),
            _ => throw new InvalidOperationException("Unknown scenario."),
        });

        Assert.NotNull(exception.ParamName);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("confirmation")]
    [InlineData("decision")]
    [InlineData("receipt")]
    [InlineData("consumption")]
    [InlineData("audit")]
    public void Metadata_WhenItsKeyContainsControlCharacters_Throws(string contract)
    {
        var metadata = new Dictionary<string, string> { ["unsafe\nkey"] = "value" };
        var requestedAt = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        var request = Request(requestedAt);
        var confirmation = new AgentConfirmationRequest(
            request,
            new AgentApproverReference("subject:andrew"),
            requestedAt.AddMinutes(5));

        var exception = Assert.Throws<ArgumentException>(() => contract switch
        {
            "request" => new AgentActionRequest(
                Action(),
                Binding(),
                new AgentIdentityReference("harness:local"),
                "correlation-1",
                requestedAt,
                "Approve workflow",
                metadata: metadata),
            "confirmation" => new AgentConfirmationRequest(
                request,
                new AgentApproverReference("subject:andrew"),
                requestedAt.AddMinutes(5),
                metadata),
            "decision" => AgentAuthorizationDecision.Allowed("correlation-1", metadata: metadata),
            "receipt" => new AgentApprovalReceipt(
                "receipt-1",
                Binding(),
                new AgentIdentityReference("harness:local"),
                new AgentApproverReference("subject:andrew"),
                "correlation-1",
                requestedAt,
                requestedAt.AddMinutes(5),
                metadata),
            "consumption" => AgentApprovalConsumptionResult.Consumed("correlation-1", metadata: metadata),
            "audit" => new AgentAuthorizationAuditEvent(
                AgentAuthorizationAuditEventKind.Proposed,
                requestedAt,
                AgentApprovalDiagnosticCodes.Proposed,
                "correlation-1",
                Binding(),
                new AgentIdentityReference("harness:local"),
                metadata: metadata),
            _ => throw new InvalidOperationException("Unknown contract."),
        });

        Assert.Equal("metadata", exception.ParamName);
    }

    [Theory]
    [InlineData("display")]
    [InlineData("metadataEntries")]
    [InlineData("metadataKey")]
    [InlineData("metadataValue")]
    [InlineData("metadataTotal")]
    public void DisplaySafeFieldsAndMetadata_WhenTheyExceedBounds_Throw(string scenario)
    {
        var oversizedDisplay = new string('a', 4097);
        var oversizedKey = new string('k', 129);
        var oversizedValue = new string('v', 1025);
        var tooManyEntries = Enumerable.Range(0, 33).ToDictionary(index => $"key-{index}", _ => "value");
        var excessiveTotal = Enumerable.Range(0, 32).ToDictionary(index => $"key-{index}", _ => new string('v', 512));

        var exception = Assert.Throws<ArgumentException>(() => scenario switch
        {
            "display" => new AgentActionMetadata("workflow.approve", oversizedDisplay),
            "metadataEntries" => new AgentActionMetadata("workflow.approve", "Approve workflow", metadata: tooManyEntries),
            "metadataKey" => new AgentActionMetadata(
                "workflow.approve",
                "Approve workflow",
                metadata: new Dictionary<string, string> { [oversizedKey] = "value" }),
            "metadataValue" => new AgentActionMetadata(
                "workflow.approve",
                "Approve workflow",
                metadata: new Dictionary<string, string> { ["safe"] = oversizedValue }),
            "metadataTotal" => new AgentActionMetadata("workflow.approve", "Approve workflow", metadata: excessiveTotal),
            _ => throw new InvalidOperationException("Unknown scenario."),
        });

        Assert.NotNull(exception.ParamName);
    }

    [Fact]
    public void DisplaySafeFieldsAndMetadata_AtTheirBounds_ArePreserved()
    {
        var maximumKey = new string('k', 128);
        var maximumValue = new string('v', 1024);
        var metadata = Enumerable.Range(0, 31).ToDictionary(index => $"key-{index}", _ => "value");
        metadata.Add(maximumKey, maximumValue);

        var action = new AgentActionMetadata(
            "workflow.approve",
            new string('d', 4096),
            metadata: metadata);

        Assert.Equal(4096, action.DisplayName.Length);
        Assert.Equal(32, action.Metadata.Count);
        Assert.Equal(maximumValue, action.Metadata[maximumKey]);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("binding")]
    [InlineData("agent")]
    [InlineData("correlationId")]
    [InlineData("safeSummary")]
    public void ActionRequest_WhenRequiredInputIsInvalid_Throws(string parameterName)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => parameterName switch
        {
            "action" => new AgentActionRequest(
                null!,
                Binding(),
                new AgentIdentityReference("harness:local"),
                "correlation-1",
                DateTimeOffset.UtcNow,
                "Approve the production workflow."),
            "binding" => new AgentActionRequest(
                Action(),
                null!,
                new AgentIdentityReference("harness:local"),
                "correlation-1",
                DateTimeOffset.UtcNow,
                "Approve the production workflow."),
            "agent" => new AgentActionRequest(
                Action(),
                Binding(),
                default,
                "correlation-1",
                DateTimeOffset.UtcNow,
                "Approve the production workflow."),
            "correlationId" => new AgentActionRequest(
                Action(),
                Binding(),
                new AgentIdentityReference("harness:local"),
                " ",
                DateTimeOffset.UtcNow,
                "Approve the production workflow."),
            "safeSummary" => new AgentActionRequest(
                Action(),
                Binding(),
                new AgentIdentityReference("harness:local"),
                "correlation-1",
                DateTimeOffset.UtcNow,
                " "),
            _ => throw new InvalidOperationException("Unknown parameter."),
        });

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public void ConfirmationRequest_RequiresFutureExpiryAndSnapshotsMetadata()
    {
        var requestedAt = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        var metadata = new Dictionary<string, string> { ["card"] = "approval" };
        var confirmation = new AgentConfirmationRequest(
            Request(requestedAt),
            new AgentApproverReference("subject:andrew"),
            requestedAt.AddMinutes(5),
            metadata);
        metadata["card"] = "changed";

        Assert.Equal("subject:andrew", confirmation.Approver.Value);
        Assert.Equal(requestedAt.AddMinutes(5), confirmation.ExpiresAt);
        Assert.Equal("approval", confirmation.Metadata["card"]);

        var exception = Assert.Throws<ArgumentException>(() => new AgentConfirmationRequest(
            Request(requestedAt),
            new AgentApproverReference("subject:andrew"),
            requestedAt));
        Assert.Equal("expiresAt", exception.ParamName);
    }

    [Fact]
    public void ConfirmationRequest_WhenActionRequestIsNull_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new AgentConfirmationRequest(
            null!,
            new AgentApproverReference("subject:andrew"),
            DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.Equal("actionRequest", exception.ParamName);
    }

    [Fact]
    public void Decision_FactoriesSetExpectedOutcomesAndHelpers()
    {
        var requestedAt = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        var confirmation = new AgentConfirmationRequest(
            Request(requestedAt),
            new AgentApproverReference("subject:andrew"),
            requestedAt.AddMinutes(5));
        var allowed = AgentAuthorizationDecision.Allowed("correlation-1");
        var denied = AgentAuthorizationDecision.Denied("correlation-1");
        var required = AgentAuthorizationDecision.ConfirmationRequired(confirmation);

        Assert.True(allowed.IsAllowed);
        Assert.Equal(AgentApprovalDiagnosticCodes.Allowed, allowed.Code);
        Assert.True(denied.IsDenied);
        Assert.Equal(AgentApprovalDiagnosticCodes.Denied, denied.Code);
        Assert.True(required.RequiresConfirmation);
        Assert.Same(confirmation, required.ConfirmationRequest);
        Assert.Equal(AgentApprovalDiagnosticCodes.ConfirmationRequired, required.Code);
    }

    [Theory]
    [InlineData("missingConfirmation")]
    [InlineData("unexpectedConfirmation")]
    [InlineData("mismatchedCorrelation")]
    [InlineData("kind")]
    public void Decision_WhenCombinationIsInvalid_Throws(string scenario)
    {
        var requestedAt = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        var confirmation = new AgentConfirmationRequest(
            Request(requestedAt),
            new AgentApproverReference("subject:andrew"),
            requestedAt.AddMinutes(5));
        var mismatchedConfirmation = new AgentConfirmationRequest(
            Request(requestedAt, correlationId: "correlation-2"),
            new AgentApproverReference("subject:andrew"),
            requestedAt.AddMinutes(5));

        var exception = Assert.ThrowsAny<ArgumentException>(() => scenario switch
        {
            "missingConfirmation" => new AgentAuthorizationDecision(
                AgentAuthorizationDecisionKind.ConfirmationRequired,
                "host.confirmation-required",
                "correlation-1"),
            "unexpectedConfirmation" => new AgentAuthorizationDecision(
                AgentAuthorizationDecisionKind.Allowed,
                "host.allowed",
                "correlation-1",
                confirmationRequest: confirmation),
            "mismatchedCorrelation" => new AgentAuthorizationDecision(
                AgentAuthorizationDecisionKind.ConfirmationRequired,
                "host.confirmation-required",
                "correlation-1",
                confirmationRequest: mismatchedConfirmation),
            "kind" => new AgentAuthorizationDecision(
                (AgentAuthorizationDecisionKind)99,
                "host.invalid",
                "correlation-1"),
            _ => throw new InvalidOperationException("Unknown scenario."),
        });

        Assert.NotNull(exception.ParamName);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("correlationId")]
    public void Decision_WhenIdentifierIsBlank_Throws(string parameterName)
    {
        var exception = Assert.Throws<ArgumentException>(() => parameterName switch
        {
            "code" => new AgentAuthorizationDecision(
                AgentAuthorizationDecisionKind.Allowed,
                " ",
                "correlation-1"),
            "correlationId" => new AgentAuthorizationDecision(
                AgentAuthorizationDecisionKind.Allowed,
                "host.allowed",
                " "),
            _ => throw new InvalidOperationException("Unknown parameter."),
        });

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public void Decision_FactorySnapshotsMetadata()
    {
        var metadata = new Dictionary<string, string> { ["safe"] = "original" };
        var decision = AgentAuthorizationDecision.Allowed("correlation-1", metadata: metadata);
        metadata["safe"] = "changed";

        Assert.Equal("original", decision.Metadata["safe"]);
    }

    [Fact]
    public void Decision_WhenCodeDoesNotMatchKind_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => new AgentAuthorizationDecision(
            AgentAuthorizationDecisionKind.Allowed,
            AgentApprovalDiagnosticCodes.Denied,
            "correlation-1"));

        Assert.Equal("code", exception.ParamName);
    }

    [Theory]
    [InlineData("agent approval")]
    [InlineData("agent-approval.allowed\nsubcode")]
    public void Decision_WhenCodeContainsWhitespaceOrControlCharacters_Throws(string code)
    {
        var exception = Assert.Throws<ArgumentException>(() => new AgentAuthorizationDecision(
            AgentAuthorizationDecisionKind.Allowed,
            code,
            "correlation-1"));

        Assert.Equal("code", exception.ParamName);
    }

    [Fact]
    public void Decision_AcceptsStableSubcodeForItsKind()
    {
        var decision = new AgentAuthorizationDecision(
            AgentAuthorizationDecisionKind.Allowed,
            "agent-approval.allowed.host-policy",
            "correlation-1");

        Assert.True(decision.IsAllowed);
    }

    [Theory]
    [InlineData("agent-approval.allowed.")]
    [InlineData("agent-approval.allowed..host-policy")]
    public void Decision_RejectsEmptyDiagnosticSubcodeSegments(string code)
    {
        var exception = Assert.Throws<ArgumentException>(() => new AgentAuthorizationDecision(
            AgentAuthorizationDecisionKind.Allowed,
            code,
            "correlation-1"));

        Assert.Equal("code", exception.ParamName);
    }

    [Fact]
    public void Receipt_RequiresFutureExpiry_RedactsOpaqueValues_AndSnapshotsMetadata()
    {
        var issuedAt = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        var metadata = new Dictionary<string, string> { ["safe"] = "original" };
        var receipt = new AgentApprovalReceipt(
            "receipt-1",
            Binding(),
            new AgentIdentityReference("harness:local"),
            new AgentApproverReference("subject:andrew"),
            "correlation-1",
            issuedAt,
            issuedAt.AddMinutes(5),
            metadata);
        metadata["safe"] = "changed";
        var receiptText = receipt.ToString();

        Assert.Equal("receipt-1", receipt.ReceiptId);
        Assert.Equal("subject:andrew", receipt.Approver.Value);
        Assert.Contains("<redacted>", receiptText, StringComparison.Ordinal);
        foreach (var value in new[]
                 {
                     receipt.ReceiptId,
                     receipt.Binding.ActionId,
                     receipt.Binding.TaskId,
                     receipt.Binding.WorkflowInstanceId,
                     receipt.Binding.ExpectedState,
                     receipt.Binding.ExpectedStateVersion,
                     receipt.Binding.Transition,
                     receipt.Binding.BindingProfile,
                     receipt.Binding.SafeIntentDigest,
                     receipt.Agent.Value,
                     receipt.Approver.Value,
                     receipt.CorrelationId,
                 })
        {
            Assert.DoesNotContain(value, receiptText, StringComparison.Ordinal);
        }
        Assert.Equal("original", receipt.Metadata["safe"]);

        var exception = Assert.Throws<ArgumentException>(() => Receipt(issuedAt, expiresAt: issuedAt));
        Assert.Equal("expiresAt", exception.ParamName);
    }

    [Theory]
    [InlineData("receiptId")]
    [InlineData("correlationId")]
    public void Receipt_WhenIdentifierIsBlank_Throws(string parameterName)
    {
        var issuedAt = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        var exception = Assert.Throws<ArgumentException>(() => parameterName switch
        {
            "receiptId" => new AgentApprovalReceipt(
                " ",
                Binding(),
                new AgentIdentityReference("harness:local"),
                new AgentApproverReference("subject:andrew"),
                "correlation-1",
                issuedAt,
                issuedAt.AddMinutes(5)),
            "correlationId" => new AgentApprovalReceipt(
                "receipt-1",
                Binding(),
                new AgentIdentityReference("harness:local"),
                new AgentApproverReference("subject:andrew"),
                " ",
                issuedAt,
                issuedAt.AddMinutes(5)),
            _ => throw new InvalidOperationException("Unknown parameter."),
        });

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public void Receipt_FromConfirmedRequest_BindsTheExactConfirmationAndCannotOutliveIt()
    {
        var requestedAt = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        var confirmation = new AgentConfirmationRequest(
            Request(requestedAt),
            new AgentApproverReference("subject:andrew"),
            requestedAt.AddMinutes(5));
        var receipt = AgentApprovalReceipt.FromConfirmedRequest(
            "receipt-1",
            confirmation,
            requestedAt.AddMinutes(1),
            requestedAt.AddMinutes(4));

        Assert.True(receipt.Binding.Matches(confirmation.ActionRequest.Binding));
        Assert.Equal(confirmation.ActionRequest.Agent, receipt.Agent);
        Assert.Equal(confirmation.Approver, receipt.Approver);
        Assert.Equal(confirmation.ActionRequest.CorrelationId, receipt.CorrelationId);

        var expiryException = Assert.Throws<ArgumentException>(() => AgentApprovalReceipt.FromConfirmedRequest(
            "receipt-2",
            confirmation,
            requestedAt.AddMinutes(1),
            confirmation.ExpiresAt.AddMinutes(1)));
        Assert.Equal("expiresAt", expiryException.ParamName);

        var issuanceException = Assert.Throws<ArgumentException>(() => AgentApprovalReceipt.FromConfirmedRequest(
            "receipt-3",
            confirmation,
            confirmation.ExpiresAt,
            confirmation.ExpiresAt));
        Assert.Equal("issuedAt", issuanceException.ParamName);

        var preRequestException = Assert.Throws<ArgumentException>(() => AgentApprovalReceipt.FromConfirmedRequest(
            "receipt-4",
            confirmation,
            requestedAt.AddMinutes(-1),
            requestedAt.AddMinutes(1)));
        Assert.Equal("issuedAt", preRequestException.ParamName);
    }

    [Theory]
    [InlineData(AgentApprovalConsumptionOutcome.Consumed, "agent-approval.consumed", true)]
    [InlineData(AgentApprovalConsumptionOutcome.AlreadyConsumed, "agent-approval.already-consumed", false)]
    [InlineData(AgentApprovalConsumptionOutcome.Expired, "agent-approval.expired", false)]
    [InlineData(AgentApprovalConsumptionOutcome.Revoked, "agent-approval.revoked", false)]
    [InlineData(AgentApprovalConsumptionOutcome.Stale, "agent-approval.stale", false)]
    [InlineData(AgentApprovalConsumptionOutcome.BindingMismatch, "agent-approval.binding-mismatch", false)]
    [InlineData(AgentApprovalConsumptionOutcome.Denied, "agent-approval.consumption-denied", false)]
    public void ConsumptionFactories_CreateExpectedTerminalResult(
        AgentApprovalConsumptionOutcome expectedOutcome,
        string expectedCode,
        bool expectedConsumed)
    {
        var result = expectedOutcome switch
        {
            AgentApprovalConsumptionOutcome.Consumed => AgentApprovalConsumptionResult.Consumed("correlation-1"),
            AgentApprovalConsumptionOutcome.AlreadyConsumed => AgentApprovalConsumptionResult.AlreadyConsumed("correlation-1"),
            AgentApprovalConsumptionOutcome.Expired => AgentApprovalConsumptionResult.Expired("correlation-1"),
            AgentApprovalConsumptionOutcome.Revoked => AgentApprovalConsumptionResult.Revoked("correlation-1"),
            AgentApprovalConsumptionOutcome.Stale => AgentApprovalConsumptionResult.Stale("correlation-1"),
            AgentApprovalConsumptionOutcome.BindingMismatch => AgentApprovalConsumptionResult.BindingMismatch("correlation-1"),
            AgentApprovalConsumptionOutcome.Denied => AgentApprovalConsumptionResult.Denied("correlation-1"),
            _ => throw new InvalidOperationException("Unknown outcome."),
        };

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(expectedConsumed, result.IsConsumed);
    }

    [Fact]
    public void ConsumptionResult_WhenOutcomeIsUndefined_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new AgentApprovalConsumptionResult(
            (AgentApprovalConsumptionOutcome)99,
            "host.invalid",
            "correlation-1"));

        Assert.Equal("outcome", exception.ParamName);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("correlationId")]
    public void ConsumptionResult_WhenIdentifierIsBlank_Throws(string parameterName)
    {
        var exception = Assert.Throws<ArgumentException>(() => parameterName switch
        {
            "code" => new AgentApprovalConsumptionResult(
                AgentApprovalConsumptionOutcome.Consumed,
                " ",
                "correlation-1"),
            "correlationId" => new AgentApprovalConsumptionResult(
                AgentApprovalConsumptionOutcome.Consumed,
                "agent-approval.consumed",
                " "),
            _ => throw new InvalidOperationException("Unknown parameter."),
        });

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public void ConsumptionResult_FactorySnapshotsMetadata()
    {
        var metadata = new Dictionary<string, string> { ["safe"] = "original" };
        var result = AgentApprovalConsumptionResult.Consumed("correlation-1", metadata: metadata);
        metadata["safe"] = "changed";

        Assert.Equal("original", result.Metadata["safe"]);
    }

    [Fact]
    public void ConsumptionResult_WhenCodeDoesNotMatchOutcome_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => new AgentApprovalConsumptionResult(
            AgentApprovalConsumptionOutcome.Consumed,
            AgentApprovalDiagnosticCodes.AlreadyConsumed,
            "correlation-1"));

        Assert.Equal("code", exception.ParamName);
    }

    [Theory]
    [InlineData(AgentApprovalConsumptionOutcome.Consumed, AgentApprovalDiagnosticCodes.Consumed)]
    [InlineData(AgentApprovalConsumptionOutcome.AlreadyConsumed, AgentApprovalDiagnosticCodes.AlreadyConsumed)]
    [InlineData(AgentApprovalConsumptionOutcome.Expired, AgentApprovalDiagnosticCodes.Expired)]
    [InlineData(AgentApprovalConsumptionOutcome.Revoked, AgentApprovalDiagnosticCodes.Revoked)]
    [InlineData(AgentApprovalConsumptionOutcome.Stale, AgentApprovalDiagnosticCodes.Stale)]
    [InlineData(AgentApprovalConsumptionOutcome.BindingMismatch, AgentApprovalDiagnosticCodes.BindingMismatch)]
    [InlineData(AgentApprovalConsumptionOutcome.Denied, AgentApprovalDiagnosticCodes.ConsumptionDenied)]
    public void ConsumptionResult_AcceptsOnlySubcodesWithinItsOutcomeFamily(
        AgentApprovalConsumptionOutcome outcome,
        string canonicalCode)
    {
        var result = new AgentApprovalConsumptionResult(outcome, canonicalCode + ".host-policy", "correlation-1");
        Assert.Equal(canonicalCode + ".host-policy", result.Code);

        var exception = Assert.Throws<ArgumentException>(() => new AgentApprovalConsumptionResult(
            outcome,
            AgentApprovalDiagnosticCodes.Denied,
            "correlation-1"));
        Assert.Equal("code", exception.ParamName);
    }

    [Fact]
    public void AuditEvent_PreservesSafeProjectionAndSnapshotsMetadata()
    {
        var metadata = new Dictionary<string, string> { ["safe"] = "value" };
        var auditEvent = new AgentAuthorizationAuditEvent(
            AgentAuthorizationAuditEventKind.Consumed,
            new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),
            AgentApprovalDiagnosticCodes.Consumed,
            "correlation-1",
            Binding(),
            new AgentIdentityReference("harness:local"),
            new AgentApproverReference("subject:andrew"),
            "receipt-1",
            "Workflow approval executed.",
            metadata);
        metadata["safe"] = "changed";

        Assert.Equal(AgentAuthorizationAuditEventKind.Consumed, auditEvent.Kind);
        Assert.Equal("subject:andrew", auditEvent.Approver!.Value.Value);
        Assert.Equal("receipt-1", auditEvent.ReceiptId);
        Assert.Equal("value", auditEvent.Metadata["safe"]);
    }

    [Fact]
    public void AuditEvent_FromReceipt_PreservesTheReceiptProjection()
    {
        var issuedAt = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        var receipt = Receipt(issuedAt);

        var auditEvent = AgentAuthorizationAuditEvent.FromReceipt(
            AgentAuthorizationAuditEventKind.Consumed,
            issuedAt.AddMinutes(1),
            AgentApprovalDiagnosticCodes.Consumed,
            receipt,
            "Workflow approval executed.");

        Assert.Equal(receipt.CorrelationId, auditEvent.CorrelationId);
        Assert.True(receipt.Binding.Matches(auditEvent.Binding));
        Assert.Equal(receipt.Agent, auditEvent.Agent);
        Assert.Equal(receipt.Approver, auditEvent.Approver);
        Assert.Equal(receipt.ReceiptId, auditEvent.ReceiptId);
    }

    [Fact]
    public void AuditEvent_WhenKindIsUndefined_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new AgentAuthorizationAuditEvent(
            (AgentAuthorizationAuditEventKind)99,
            DateTimeOffset.UtcNow,
            "host.invalid",
            "correlation-1",
            Binding(),
            new AgentIdentityReference("harness:local")));

        Assert.Equal("kind", exception.ParamName);
    }

    [Fact]
    public void AuditEvent_WhenApproverIsDefault_Throws()
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => new AgentAuthorizationAuditEvent(
            AgentAuthorizationAuditEventKind.Consumed,
            DateTimeOffset.UtcNow,
            AgentApprovalDiagnosticCodes.Consumed,
            "correlation-1",
            Binding(),
            new AgentIdentityReference("harness:local"),
            default(AgentApproverReference)));

        Assert.Equal("approver", exception.ParamName);
    }

    [Fact]
    public void AuditEvent_RequiresItsCanonicalCodeAndLifecycleReferences()
    {
        var missingReceipt = Assert.Throws<ArgumentException>(() => new AgentAuthorizationAuditEvent(
            AgentAuthorizationAuditEventKind.Consumed,
            DateTimeOffset.UtcNow,
            AgentApprovalDiagnosticCodes.Consumed,
            "correlation-1",
            Binding(),
            new AgentIdentityReference("harness:local")));
        Assert.Equal("receiptId", missingReceipt.ParamName);

        var missingApprover = Assert.Throws<ArgumentException>(() => new AgentAuthorizationAuditEvent(
            AgentAuthorizationAuditEventKind.Approved,
            DateTimeOffset.UtcNow,
            AgentApprovalDiagnosticCodes.Approved,
            "correlation-1",
            Binding(),
            new AgentIdentityReference("harness:local")));
        Assert.Equal("approver", missingApprover.ParamName);

        var mismatchedCode = Assert.Throws<ArgumentException>(() => new AgentAuthorizationAuditEvent(
            AgentAuthorizationAuditEventKind.Consumed,
            DateTimeOffset.UtcNow,
            AgentApprovalDiagnosticCodes.AlreadyConsumed,
            "correlation-1",
            Binding(),
            new AgentIdentityReference("harness:local"),
            receiptId: "receipt-1"));
        Assert.Equal("code", mismatchedCode.ParamName);
    }

    [Theory]
    [InlineData(AgentAuthorizationAuditEventKind.Revoked, AgentApprovalDiagnosticCodes.Revoked)]
    [InlineData(AgentAuthorizationAuditEventKind.ConsumptionAttempted, AgentApprovalDiagnosticCodes.ConsumptionAttempted)]
    [InlineData(AgentAuthorizationAuditEventKind.Consumed, AgentApprovalDiagnosticCodes.Consumed)]
    [InlineData(AgentAuthorizationAuditEventKind.AlreadyConsumed, AgentApprovalDiagnosticCodes.AlreadyConsumed)]
    [InlineData(AgentAuthorizationAuditEventKind.Expired, AgentApprovalDiagnosticCodes.Expired)]
    [InlineData(AgentAuthorizationAuditEventKind.Stale, AgentApprovalDiagnosticCodes.Stale)]
    [InlineData(AgentAuthorizationAuditEventKind.BindingMismatch, AgentApprovalDiagnosticCodes.BindingMismatch)]
    [InlineData(AgentAuthorizationAuditEventKind.ConsumptionDenied, AgentApprovalDiagnosticCodes.ConsumptionDenied)]
    public void AuditEvent_ReceiptLifecycleKindsRequireReceiptAndAcceptCanonicalSubcodes(
        AgentAuthorizationAuditEventKind kind,
        string canonicalCode)
    {
        var missingReceipt = Assert.Throws<ArgumentException>(() => new AgentAuthorizationAuditEvent(
            kind,
            DateTimeOffset.UtcNow,
            canonicalCode,
            "correlation-1",
            Binding(),
            new AgentIdentityReference("harness:local")));
        Assert.Equal("receiptId", missingReceipt.ParamName);

        var auditEvent = new AgentAuthorizationAuditEvent(
            kind,
            DateTimeOffset.UtcNow,
            canonicalCode + ".host-policy",
            "correlation-1",
            Binding(),
            new AgentIdentityReference("harness:local"),
            receiptId: "receipt-1");
        Assert.Equal(canonicalCode + ".host-policy", auditEvent.Code);
    }

    [Theory]
    [InlineData(AgentAuthorizationAuditEventKind.Proposed, AgentApprovalDiagnosticCodes.Proposed, false)]
    [InlineData(AgentAuthorizationAuditEventKind.Allowed, AgentApprovalDiagnosticCodes.Allowed, false)]
    [InlineData(AgentAuthorizationAuditEventKind.Denied, AgentApprovalDiagnosticCodes.Denied, false)]
    [InlineData(AgentAuthorizationAuditEventKind.ConfirmationRequired, AgentApprovalDiagnosticCodes.ConfirmationRequired, false)]
    [InlineData(AgentAuthorizationAuditEventKind.Approved, AgentApprovalDiagnosticCodes.Approved, true)]
    public void AuditEvent_NonReceiptKindsAcceptCanonicalSubcodes(
        AgentAuthorizationAuditEventKind kind,
        string canonicalCode,
        bool requiresApprover)
    {
        var auditEvent = new AgentAuthorizationAuditEvent(
            kind,
            DateTimeOffset.UtcNow,
            canonicalCode + ".host-policy",
            "correlation-1",
            Binding(),
            new AgentIdentityReference("harness:local"),
            requiresApprover ? new AgentApproverReference("subject:andrew") : null);

        Assert.Equal(canonicalCode + ".host-policy", auditEvent.Code);
    }

    private static AgentActionMetadata Action() =>
        new(
            "workflow.approve",
            "Approve workflow",
            AgentActionRisk.High,
            AgentConfirmationPosture.AlwaysRequireHuman,
            AgentActionRedaction.DoNotExposeArguments);

    private static AgentActionBinding Binding(
        string actionId = "workflow.approve",
        string taskId = "task-1",
        string workflowInstanceId = "workflow-1",
        string expectedState = "pending",
        string expectedStateVersion = "version-1",
        string transition = "approve",
        string bindingProfile = "workflow-approval/v1",
        string safeIntentDigest = "sha256:abc") =>
        new(
            actionId,
            taskId,
            workflowInstanceId,
            expectedState,
            expectedStateVersion,
            transition,
            bindingProfile,
            safeIntentDigest);

    private static AgentActionRequest Request(
        DateTimeOffset requestedAt,
        string correlationId = "correlation-1",
        string? rationale = "Validated against the local release checklist.") =>
        new(
            Action(),
            Binding(),
            new AgentIdentityReference("harness:local"),
            correlationId,
            requestedAt,
            "Approve the production workflow.",
            rationale);

    private static AgentApprovalReceipt Receipt(DateTimeOffset issuedAt, DateTimeOffset? expiresAt = null) =>
        new(
            "receipt-1",
            Binding(),
            new AgentIdentityReference("harness:local"),
            new AgentApproverReference("subject:andrew"),
            "correlation-1",
            issuedAt,
            expiresAt ?? issuedAt.AddMinutes(5));
}
