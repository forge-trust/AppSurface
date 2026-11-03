# Account cleanup procedure

[`SupervisionAccountCleanup.RunAsync`](SupervisionAccountCleanup.cs) runs the exact reverse snapshot supplied by
[`LinuxRunAccountReservations.ReverseCleanup`](LinuxRunAccounts.cs). It performs one original attempt per command,
catches the original broad exception class, invokes the caller's existing failure capture, and runs the caller's
physical-settlement barrier after every attempt. A barrier failure propagates immediately and prevents later
dispatch; an attempt failure is captured and later commands are still attempted.

The native caller retains root teardown, owner and custody checks, the original cleanup token and deadline,
reverse command selection, name/NSS checks, utility creation and execution, account quarantine, and first-fault
stage capture. The procedure does not create or retain an owner, account, lease, native handle, timeout, retry,
detached task, or alternate command. Its input is an immutable caller snapshot; it cannot reverse or extend it.

The focused tests use existing generated command data and local task barriers and exceptions only. They cover empty
and partial snapshots, an original attempt remaining pending until its task completes, synchronous and asynchronous
faults, later-command continuation, first capture count, post-attempt settlement stopping dispatch, and callback
validation before work. They do not perform NSS lookup or create account utility owners.
