# Keycloak extracted-package replay

The [#888 evidence summary](../../docs/plans/issue-888-keycloak-identity-readiness.md#feasibility-results) records the qualified profile, historical results, and retained concerns. These standalone fixture sources support local verification of a candidate package pair. Run the full repository gate separately.

The measured setup used macOS ARM64, Linux ARM64 Docker, .NET SDK 10.0.402, Aspire 13.6.5, pinned Keycloak 26.7.2, and Playwright Chromium headless shell 1254. Prepare the local SDK, Aspire binaries, browser, and pinned images before the offline consumer replay. The preparers expect the paths for the qualified local setup. The source pins for the Keycloak and worker images are in [the AppHost fixture](normal-supervised-apphost/Program.cs).

1. Pack the Keycloak and Aspire Keycloak projects at the exact candidate source commit and record the archive hashes. Build the [fixture AppHost](acceptance-apphost/AcceptanceAppHost.csproj) in Release to produce the base Aspire host closure. Restore the OIDC project needed for the prepared fixtures.
2. Use `prepare-supervised-composition.py` with the pinned local package inputs, then `prepare-normal-supervised.py` with the new pure and adapter archives and the resulting composition receipt. Keep the prepared inputs outside the checkout.
3. Copy `run-normal-supervised.py`, `managed-seed-readback.py`, `prime-legacy-volume.py`, and `cleanup-stopped-normal.py` to an external driver directory. Run `run-normal-supervised.py --preparation <receipt> --acceptance <group> --evidence <fresh-output>` for `cold`, `neutral`, `skoolit`, `tunnel`, and `legacy-uid1000`. The runtime uses the prepared payload, a private empty NuGet cache and disposable test-owned Docker resources. The legacy group first lets Keycloak create its test volume as UID 1000.
4. Check each run receipt. Run `cleanup-stopped-normal.py --receipt <run-receipt> --evidence <fresh-cleanup-output>` for a completed fixture. Retain any uncertain marker and its files. Preserve the package and driver hashes, source commit and tree, and the fixture and cleanup receipts locally.

## Public CaptureAsync from the extracted payload

The [public CaptureAsync fixture](public-capture/README.md) exercises an ordinary consumer boundary with frozen signed inputs. Pass the candidate adapter archive, which contains the recursive worker and pure-library payload.

```sh
python3 tests/keycloak-identity-feasibility/run-public-capture.py \
  --package <adapter-archive> --evidence-root <fresh-output> --phase all
```

The driver compiles and runs from copied sources and the extracted package with cleared feeds, an empty read-only NuGet cache, and networking disabled. It requires stopped verification within 30 seconds before cleanup. See the [sealed credential custody guide](../../docs/sealed-client-credential-reference.md) for the separate Linux ARM64 worker-anchor replay. The repository solution carries the unit, workflow, and native-lane tests. Keep historical experiments and the generated proof captures in their local archive.
