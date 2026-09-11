# Packed negative proof

This directory contains the eight P02 package consumer fixtures. Each fixture
has one deliberately invalid statement marked with
expected-compiler-error: CODE, a matching positive control, a package project
template, and a manifest that pins the exact marked line and allowed
diagnostic code.

Durable/verify-packed-consumers.sh restores every positive and negative
consumer against the temporary local NuGet feed. It then builds the positive
control successfully and builds the negative control with --no-restore,
UseSharedCompilation=false, and compiler SARIF 2.1 output. The checker
requires a nonzero negative build, an error at the exact marked absolute
source path and line, and exactly the manifest's diagnostic code. Restore
failures, missing or malformed SARIF, stale manifests, unrelated errors,
same-basename files, and positive-control failures fail the proof.

Observed with .NET SDK 10.0.102 on September 10, 2026:

| Fixture group | Diagnostic |
| --- | --- |
| Wrong ordinary executor Work/result | CS0311 |
| Wrong reconciler Work/result | CS0311 |
| Wrong exit executor Work/result | CS0311 |
| Non-class executor | CS0452 |
| Reconciler on exit binding | CS1061 |

No fixture-specific additional diagnostic allowance is used.
