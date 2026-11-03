"""Closed detached N08/N09 data consistency; never native qualification or authority.

The caller must independently retain original joined raw streams and authenticated
source/image/kernel samples. This module performs no OS sampling or execution.
"""
import hashlib
import json
import re
from check_kernel_record import check_kernel_record

MAX_WORKER_BYTES = 4096
PHASES = {"N08": "BeforeAllocation", "N09": "BeforeActivation"}
TERMINAL = (b"ASEVD409: Fresh output allocation or activation failed. Fix: use an explicit mode "
            b"and supported protected worker. See start-here/evidencehost.md.\n")


class CancellationRecordRejected(ValueError):
    """One fixed rejection, retaining no supplied text or chained exception."""


def _require(value):
    if not value:
        raise CancellationRecordRejected("cancellation-data-rejected")


def _pairs(pairs):
    result, folded = {}, set()
    for key, value in pairs:
        _require(key.casefold() not in folded)
        folded.add(key.casefold())
        result[key] = value
    return result


def _line(raw, keys):
    _require(type(raw) is bytes and 1 < len(raw) <= 1024 and raw.endswith(b"\n"))
    _require(raw.count(b"\n") == 1 and raw.startswith(b"{"))
    value = json.loads(raw[:-1].decode("utf-8", errors="strict"), object_pairs_hook=_pairs,
                       parse_constant=lambda _: _require(False))
    _require(type(value) is dict and set(value) == set(keys))
    return value


def check_worker_records(raw, *, case):
    """Require actual full bytes; success means only internal consistency of supplied data."""
    try:
        _require(case in PHASES and type(raw) is bytes and 0 < len(raw) <= MAX_WORKER_BYTES)
        lines = raw.splitlines(keepends=True)
        _require(len(lines) == 4 and b"".join(lines) == raw and lines[-1] == TERMINAL)
        phase = _line(lines[0], ("schema", "case", "phase", "native_authority"))
        _require(phase == {"schema": "issue779-cancellation-phase-v1", "case": case,
                           "phase": PHASES[case], "native_authority": False})
        # Equality alone would accept numeric zero in a boolean field.
        _require(phase["native_authority"] is False)
        allocation = _line(lines[1], ("phase", "operation", "stageOutcome", "terminalCode",
                                     "errorClass", "nativeErrno", "schema"))
        _require(allocation["schema"] == "evidence-allocation-failure-v1"
                 and allocation["phase"] == PHASES[case]
                 and allocation["operation"] == ("None" if case == "N08" else "Completed")
                 and allocation["stageOutcome"] == "Cancelled"
                 and allocation["terminalCode"] == "CallerCancelled"
                 and allocation["errorClass"] == "Cancelled"
                 and allocation["nativeErrno"] is None)
        cancellation = _line(lines[2], ("case", "phase", "callerTokenCancelled",
                                       "lifecycleCallerCancelled", "ownWorkStopped",
                                       "schema", "nativeAuthority"))
        _require(cancellation["schema"] == "issue779-original-cancellation-v1"
                 and cancellation["case"] == case and cancellation["phase"] == PHASES[case]
                 and cancellation["callerTokenCancelled"] is True
                 and cancellation["lifecycleCallerCancelled"] is True
                 and cancellation["ownWorkStopped"] is True
                 and cancellation["nativeAuthority"] is False)
        return True
    except (ValueError, TypeError, KeyError, UnicodeError, OverflowError, RecursionError):
        raise CancellationRecordRejected("cancellation-data-rejected") from None


def check_joined_records(*, case, signal_raw, kernel_raw, worker_stdout, worker_stderr,
                         expected_generation, expected_uid, expected_gid, expected_pid,
                         expected_starttime_ticks, expected_descriptor_sha256):
    """Require phase, SIGINT syscall data and original joined raw bytes to agree.

    Expected values remain detached data. This function does not authenticate the
    producer, image, credentials, pidfd, group, NSS disposition or filesystem state.
    No missing raw stream can be replaced with a guessed expected digest.
    """
    try:
        _require(type(worker_stdout) is bytes and worker_stdout == b"")
        check_worker_records(worker_stderr, case=case)
        signal = _line(signal_raw, ("schema", "generation", "case", "signal", "syscall_exit",
                                    "original_signal_task_joined", "ready_committed",
                                    "phase_observed", "native_authority"))
        _require(type(expected_generation) is str
                 and re.fullmatch("[0-9a-f]{32}", expected_generation) is not None)
        _require(signal["schema"] == "issue779-cancellation-signal-v1"
                 and signal["generation"] == expected_generation and signal["case"] == case
                 and type(signal["signal"]) is int and signal["signal"] == 2
                 and type(signal["syscall_exit"]) is int and signal["syscall_exit"] == 0
                 and signal["original_signal_task_joined"] is True
                 and signal["ready_committed"] is True and signal["phase_observed"] is True
                 and signal["native_authority"] is False)
        check_kernel_record(kernel_raw, expected_generation=expected_generation,
                            expected_uid=expected_uid, expected_gid=expected_gid,
                            expected_pid=expected_pid, expected_starttime_ticks=expected_starttime_ticks,
                            expected_descriptor_sha256=expected_descriptor_sha256,
                            expected_stdout_sha256=hashlib.sha256(worker_stdout).hexdigest(),
                            expected_stdout_bytes=0,
                            expected_stderr_sha256=hashlib.sha256(worker_stderr).hexdigest(),
                            expected_stderr_bytes=len(worker_stderr))
        return {"schema": "issue779-cancellation-data-consistency-v1", "case": case,
                "phase": PHASES[case], "raw_bytes_matched": True,
                "caller_cancelled_recorded": True, "native_authority": False,
                "native_acceptance": False}
    except (ValueError, TypeError, KeyError, UnicodeError, OverflowError, RecursionError):
        raise CancellationRecordRejected("cancellation-data-rejected") from None
