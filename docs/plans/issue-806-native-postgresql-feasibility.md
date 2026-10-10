# #806 native PostgreSQL feasibility evidence

Result: **PASS on the local macOS arm64 host** after an approved filesystem/process sandbox override for this private temporary experiment. This is native-cluster feasibility evidence; candidate template/provider and GitHub-runner proofs remain required.

The earlier sandbox attempt configured, compiled and installed PostgreSQL 16.5 but stopped at a denied `shmget` during initdb. No server started in that attempt. Its UNAVAILABLE result is retained separately. A retry outside that restriction completed each assertion below; the parent observed the process's terminal exit 0 and read its full JSON receipt.

Official PostgreSQL 16.5 source archive and published checksum:
[archive](https://ftp.postgresql.org/pub/source/v16.5/postgresql-16.5.tar.bz2), [checksum](https://ftp.postgresql.org/pub/source/v16.5/postgresql-16.5.tar.bz2.sha256).
SHA-256: `a6cbbb7037f98cb8afa7d3970b7c48040cf02b115e39253a0c037a8bb8e778f0`.

The source, install, data, log and socket directories were under one unique owner-only temporary root. No shared installation/service or Homebrew state was changed. Four native tools reported 16.5. initdb used UTF-8/C locale, SCRAM host/local authentication and two redirected password prompt lines in a process with no controlling TTY. The random secret remained in memory/stdin/child-only environment, absent from command arguments, password files and captured/server output.

The server listened on loopback with a private Unix socket. pg_ctl used a private -l server log, explicit wait bounds and the owned data directory. The authenticated query asserted `160005|review_owner|1`; a wrong-password connection exited 2 and reported password authentication failure. Fast stop returned 0, subsequent status returned 3, the PID file disappeared, the owned postmaster PID no longer existed and the owned root was removed.

## Actual experiment stages

| Stage | Measured seconds | Exit/result |
| --- | ---: | --- |
| download-and-verify | 1.164 | checksum matched |
| configure | 19.007 | 0 |
| compile | 37.450 | 0 |
| install | 3.183 | 0 |
| version-postgres | 0.191 | 0 |
| version-initdb | 0.227 | 0 |
| version-pg_ctl | 0.116 | 0 |
| version-psql | 0.124 | 0 |
| initdb | 1.522 | 0 |
| start | 0.119 | 0 |
| authenticated-query | 0.024 | 0 |
| wrong-password-query | 0.022 | 2 |
| stop | 0.112 | 0 |
| stopped-status | 0.009 | 3 |

These durations describe this experiment only. They are neither the product's primed/cold measurements nor a hosted-runner acquisition/startup guarantee.

## Coverage limits

No provider migrations/epoch/role recipe, restricted-role generated host, template install/generation, Linux/Windows execution, Homebrew bottle, hosted macos-15 runner, timeout/late-start/cleanup failure schedule, export proof or five-run benchmark ran. None receives completion credit. Actual candidate jobs on all three OS and every original release/adopter/doctor gate remain mandatory.

The monitoring agent could not access the parent process session and returned UNAVAILABLE before the result file existed. That watcher limitation is retained separately; terminal success was subsequently verified by the parent, not inferred from that watcher.
