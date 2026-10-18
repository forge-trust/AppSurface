# Issue #819 fake-client comparison

Copy this template once per baseline/candidate pair. Attach the complete JSONL output from both runs; it contains only
the harness identity, workload controls, timings, counts, and thread samples. Identify a candidate from an uncommitted
worktree by its base commit and working-tree patch fingerprint; do not describe the base commit as current HEAD.

| Field | Baseline | Candidate |
| --- | --- | --- |
| Full commit ID |  |  |
| Worktree state / provider patch identifier |  |  |
| Harness ID | `issue-819-google-mapped-fake-v3` | `issue-819-google-mapped-fake-v3` |
| Harness `Program.cs` SHA-256 |  |  |
| Harness project SHA-256 |  |  |
| Harness package-lock SHA-256 |  |  |
| Runtime description |  |  |
| Processor count |  |  |
| ThreadPool worker minimum | 16 | 16 |
| ThreadPool I/O minimum |  |  |
| Distinct resources / resolver callers | 16 / 16 | 16 / 16 |
| Observed effective resolver threads / distinct reads | 16 / 16 | 16 / 16 |
| Same-resource callers | 16 | 16 |
| Fake lookup latency | 50 ms | 50 ms |
| Cache state | off / off / warm control on | off / off / warm control on |
| Sample interval | 10 ms | 10 ms |
| Repetitions per workload | 9 | 9 |
| Adopter calibration | unavailable / provisional | unavailable / provisional |

Paste the unedited JSONL output for each revision below. Preserve each repetition's raw elapsed time, measured fake-call
count, peak active fake calls, and explicitly non-isolated ThreadPool/process thread sample arrays. The run validates
exact calls in every scenario: distinct-cold equals `resource-count`, same-resource-cold equals `1`, and warm-cache
equals `0`. Per-scenario summaries contain elapsed and active-call metrics only; report ThreadPool/process thread counts
only as suite-wide maxima because sequential scenarios share one process.

For a separate caller-count check, run `--resource-count 16 --caller-count 4 --repetitions 1`. Preserve that JSONL too;
its distinct-cold sample should report 16 logical resources, 4 callers, 4 observed resolver threads, and 16 fake-client
calls. In general, expected distinct-cold resolver threads are `min(resource-count, caller-count)`; every
same-resource-cold and warm-cache caller resolves the one resource.

## Raw baseline output

```jsonl
```

## Raw candidate output

```jsonl
```

## Summary

For elapsed-time spread, use the runner's explicit min-to-max range. Do not infer thread identity from process or
ThreadPool counts.

| Workload | Baseline elapsed median / range | Candidate elapsed median / range | Baseline max active fake calls | Candidate max active fake calls |
| --- | --- | --- | ---: | ---: |
| Distinct-resource cold |  |  |  |  |
| Same-resource cold contention |  |  |  |  |
| Warm-cache control |  |  |  |  |

Suite-wide maxima (not attributable to an individual scenario): baseline ThreadPool / process threads: __ / __;
candidate ThreadPool / process threads: __ / __.

Adopter calibration inputs: distinct mapped version resources at startup: __; maximum simultaneous resolution calls:
__; end-to-end startup duration and measurement boundary: __. These values must remain value-free and must not include
secret names, resource names, or payloads.

Interpretation and limitations: __. Do not state or imply a production speedup from the fake-client workload.
