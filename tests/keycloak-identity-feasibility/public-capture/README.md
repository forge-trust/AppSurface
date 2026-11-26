# Ordinary public capture consumer

`PublicCaptureProbe.cs` is compiled outside the checkout against only the extracted pure-library DLL, under an
ordinary non-friend assembly name. See the [checkpoint](../../../docs/plans/issue-888-keycloak-identity-readiness.md#feasibility-results)
and [driver usage](../README.md#public-captureasync-from-the-extracted-payload). The fixture-generation project is
[preparation only](../public-capture-fixture-generator/README.md).

Fixture protocol: `probe <variant-directory> <private-case-output> <scenario>`. Every output directory is fresh and
caller-owned. Success returns `0`, typed public rejection `20`, unexpected probe infrastructure failure `70`, and
bad arguments `64`. Results include safe typed fields and public stage events, canonical identities, snapshot
assertions, elapsed time and loaded assembly/direct-reference inventory. Raw public failure causes are deliberately
not recovered with reflection or private seams. Removing a dependency is assessed by the known one-file payload
change, passing positive control, typed `CaptureInvalid` and no committed snapshot; that diagnostic alone is not
a unique cause classification. A missing pure assembly is reported by an allowlisted top-level missing-file field.

The `signal-cancel` observer pauses only at public Capture/Started before acquisition until SIGTERM or the bounded
experiment timer cancels. Its result must confirm actual signal receipt. It is not a mid-copy cancellation test.
All snapshot and bound-input files stay in the case root until the driver verifies every owned process stopped;
disposing the public plan intentionally preserves the caller-owned snapshot. No product ownership/recovery helper
or package publication is implemented here.
