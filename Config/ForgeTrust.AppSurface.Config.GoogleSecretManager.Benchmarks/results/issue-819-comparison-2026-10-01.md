# Issue #819 fake-client comparison

Rerun on October 1, 2026 using the exact `origin/main` base commit
`4aa8329c76f4279ad319747f9f3c14b9b8de51ef` for both sides. The baseline was built from that clean source snapshot.
The candidate used the same base plus the provider/request patch after the cleanup-scheduler and audit-admission coverage fixes. The v3 harness source was
copied unchanged into the baseline snapshot, and its source fingerprints and workload controls match the candidate.
The complete unedited records are pinned to the commit that captured them:
[baseline JSONL](https://github.com/forge-trust/AppSurface/blob/1843abd85e97c21ea5830cd78b300c567ba32495/Config/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks/results/issue-819-origin-main-2026-10-01.jsonl),
[candidate JSONL](https://github.com/forge-trust/AppSurface/blob/1843abd85e97c21ea5830cd78b300c567ba32495/Config/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks/results/issue-819-candidate-2026-10-01.jsonl), and
[16-resource/4-caller validation JSONL](https://github.com/forge-trust/AppSurface/blob/1843abd85e97c21ea5830cd78b300c567ba32495/Config/ForgeTrust.AppSurface.Config.GoogleSecretManager.Benchmarks/results/issue-819-caller-count-4-verification-2026-10-01.jsonl).

| Field | Baseline | Candidate |
| --- | --- | --- |
| Source | Clean `4aa8329c76f4279ad319747f9f3c14b9b8de51ef` (`origin/main` at run) | Same base plus the committed #819 provider/request patch |
| Provider/request patch SHA-256 against base | — | `fd9bb2b1b5be8f337504299e2e5c8ece6e3061f836ddc3a0407213e9c3addaea` |
| Harness ID | `issue-819-google-mapped-fake-v3` | Same |
| Harness `Program.cs` SHA-256 | `091ab949e6de34613992f405e55bcb51ef024a32fbd990fe36ca9aefa1209675` | Same |
| Harness project SHA-256 | `c7923f7a0afc71bfc1343efdbd24aa85a9bea06be26ffe52f135494bc5b85507` | Same |
| Harness package-lock SHA-256 | `7e059293f9543d333326d31b00d9b38ac59a4d7d397b6c906780898e8bdd5d52` | Same |
| Runtime / processors | .NET 10.0.2 / 14 | Same |
| ThreadPool minimum | 16 workers / 1 I/O | Same |
| Workload | 16 distinct resources / 16 callers; 16 same-resource callers; 50 ms fake lookup; 10 ms sampling; 9 repetitions; warm-cache control | Same |
| Exact remote-call validation | 16 distinct / 1 same-resource / 0 warm-cache, every repetition | Same |
| Adopter calibration | Not supplied; provisional | Not supplied; provisional |

Reproduce the candidate patch fingerprint from the repository root with:

```bash
git diff --binary 4aa8329c76f4279ad319747f9f3c14b9b8de51ef -- Config/ForgeTrust.AppSurface.Config.GoogleSecretManager/GoogleSecretManagerConfigProvider.cs Config/ForgeTrust.AppSurface.Config/ConfigProviderRequest.cs | shasum -a 256
```

Elapsed times and min-to-max ranges are milliseconds. The suite-wide thread maxima combine sequential scenarios and
repetitions in one process. Raw per-scenario thread arrays are retained but labeled non-isolated; do not attribute
them to individual scenarios. Per-scenario summaries contain no ThreadPool or process thread counts.

| Scenario | Baseline elapsed median (min–max; spread) | Candidate elapsed median (min–max; spread) | Max active fake calls, baseline/candidate |
| --- | ---: | ---: | ---: |
| Distinct-resource cold | 51.4551 (51.3630–72.4516; 21.0886) | 52.4571 (52.1586–75.3689; 23.2103) | 16 / 16 |
| Same-resource cold contention | 50.8377 (50.5025–51.5718; 1.0693) | 51.6095 (50.5687–52.5882; 2.0195) | 1 / 1 |
| Warm-cache control | 0.5006 (0.3490–2.2559; 1.9069) | 0.4912 (0.2587–1.0788; 0.8201) | 0 / 0 |

Suite-wide maximum sampled ThreadPool count: **17 baseline / 4 candidate**. Suite-wide maximum sampled process-thread
count: **46 baseline / 33 candidate**. These are process-level indicators and do not identify which thread called the
fake client. The separate [mapped proof](../README.md) checks cold-owner managed-thread identity and shared-flight
behavior directly.

The distinct-cold medians are close, and the baseline/candidate ranges overlap in all three scenarios. The lower
suite-wide thread maxima are consistent with the intended scheduling change, but this synthetic blocking fake-client
workload is structural evidence only. It does not establish a production startup speedup; adopter-specific resource
count, simultaneous resolutions, and startup measurement boundary remain unavailable.

The candidate 16-resource/4-caller check reports 16 distinct reads, 4 observed resolver threads, one same-resource
call, and zero warm-cache calls. The harness validates each expected call count and captures non-isolated per-scenario
thread samples for transparency.
