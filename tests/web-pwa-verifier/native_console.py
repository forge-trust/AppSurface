"""Portable process helpers for native-console cancellation checks."""

from __future__ import annotations

import os
import signal
import subprocess
import sys
from collections.abc import Sequence
from typing import Any


_WINDOWS_CTRL_C_SCRIPT = r"""
import ctypes
import sys
from ctypes import wintypes

ERROR_INVALID_HANDLE = 6
CTRL_C_EVENT = 0

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
kernel32.FreeConsole.argtypes = ()
kernel32.FreeConsole.restype = wintypes.BOOL
kernel32.AttachConsole.argtypes = (wintypes.DWORD,)
kernel32.AttachConsole.restype = wintypes.BOOL
kernel32.SetConsoleCtrlHandler.argtypes = (ctypes.c_void_p, wintypes.BOOL)
kernel32.SetConsoleCtrlHandler.restype = wintypes.BOOL
kernel32.GenerateConsoleCtrlEvent.argtypes = (wintypes.DWORD, wintypes.DWORD)
kernel32.GenerateConsoleCtrlEvent.restype = wintypes.BOOL


def fail(operation):
    error = ctypes.get_last_error()
    detail = ctypes.FormatError(error).strip()
    print(f"{operation} failed for PID {pid} with WinError {error}: {detail}", file=sys.stderr)
    raise SystemExit(1)


pid = int(sys.argv[1])
if not kernel32.FreeConsole():
    error = ctypes.get_last_error()
    if error != ERROR_INVALID_HANDLE:
        fail("FreeConsole")

if not kernel32.AttachConsole(pid):
    fail("AttachConsole")

# AttachConsole resets this helper's handler table, so ignore CTRL+C only after attaching.
if not kernel32.SetConsoleCtrlHandler(None, True):
    fail("SetConsoleCtrlHandler(NULL, TRUE)")

if not kernel32.GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0):
    fail("GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0)")

if not kernel32.FreeConsole():
    fail("FreeConsole after CTRL_C_EVENT")
"""


def launch_native_console(command: Sequence[str], **popen_options: Any) -> subprocess.Popen[str]:
    """Launch a command in a fresh Windows console or a new POSIX session.

    The caller supplies the command so the runner retains control of its exact
    CLI arguments. On Windows, ``CREATE_NEW_CONSOLE`` gives the process a real,
    isolated console that a helper can attach to when delivering CTRL+C. On
    POSIX, ``start_new_session`` makes the child PID the process-group ID used
    for group-wide SIGINT delivery.
    """
    if os.name == "nt":
        creationflags = popen_options.get("creationflags", 0)
        popen_options["creationflags"] = creationflags | subprocess.CREATE_NEW_CONSOLE
    else:
        popen_options["start_new_session"] = True

    return subprocess.Popen(command, **popen_options)


def send_ctrl_c(process: subprocess.Popen[Any], timeout: float = 10.0) -> None:
    """Send a real CTRL_C_EVENT or SIGINT to the process's isolated console/session."""
    if os.name == "nt":
        _send_windows_ctrl_c(process.pid, timeout)
        return

    try:
        os.killpg(process.pid, signal.SIGINT)
    except OSError as error:
        raise RuntimeError(
            f"Failed to send SIGINT to process group {process.pid}: {error}"
        ) from error


def process_is_running(pid: int) -> bool:
    """Check whether a PID still exists without sending it a terminating signal."""
    if pid <= 0:
        return False

    if os.name == "nt":
        return _windows_process_is_running(pid)

    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


def _send_windows_ctrl_c(pid: int, timeout: float) -> None:
    try:
        result = subprocess.run(
            [
                sys.executable,
                "-c",
                _WINDOWS_CTRL_C_SCRIPT,
                str(pid),
            ],
            capture_output=True,
            text=True,
            timeout=timeout,
            creationflags=subprocess.CREATE_NO_WINDOW,
            check=False,
        )
    except subprocess.TimeoutExpired as error:
        stdout = _output_text(error.stdout)
        stderr = _output_text(error.stderr)
        raise RuntimeError(
            f"Timed out after {timeout:g}s while attaching to PID {pid}'s console "
            f"to send CTRL_C_EVENT. stdout={stdout!r} stderr={stderr!r}"
        ) from error
    except OSError as error:
        raise RuntimeError(
            f"Could not start the Windows console-control helper for PID {pid}: {error}"
        ) from error

    if result.returncode != 0:
        stdout = result.stdout.strip()
        stderr = result.stderr.strip()
        raise RuntimeError(
            f"Windows console-control helper failed for PID {pid} with exit code "
            f"{result.returncode}. stdout={stdout!r} stderr={stderr!r}"
        )


def _windows_process_is_running(pid: int) -> bool:
    import ctypes
    from ctypes import wintypes

    SYNCHRONIZE = 0x00100000
    WAIT_OBJECT_0 = 0x00000000
    WAIT_TIMEOUT = 0x00000102
    WAIT_FAILED = 0xFFFFFFFF
    ERROR_INVALID_PARAMETER = 87

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.argtypes = (
        wintypes.DWORD,
        wintypes.BOOL,
        wintypes.DWORD,
    )
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.WaitForSingleObject.argtypes = (wintypes.HANDLE, wintypes.DWORD)
    kernel32.WaitForSingleObject.restype = wintypes.DWORD
    kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
    kernel32.CloseHandle.restype = wintypes.BOOL

    handle = kernel32.OpenProcess(SYNCHRONIZE, False, pid)
    if not handle:
        error = ctypes.get_last_error()
        if error == ERROR_INVALID_PARAMETER:
            return False
        raise OSError(error, f"OpenProcess failed for PID {pid}: {ctypes.FormatError(error).strip()}")

    try:
        wait_result = kernel32.WaitForSingleObject(handle, 0)
        if wait_result == WAIT_OBJECT_0:
            return False
        if wait_result == WAIT_TIMEOUT:
            return True
        if wait_result == WAIT_FAILED:
            error = ctypes.get_last_error()
            raise OSError(
                error,
                f"WaitForSingleObject failed for PID {pid}: {ctypes.FormatError(error).strip()}",
            )
        raise OSError(
            f"WaitForSingleObject returned unexpected result {wait_result:#x} for PID {pid}"
        )
    finally:
        kernel32.CloseHandle(handle)


def _output_text(value: str | bytes | None) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.decode(errors="replace").strip()
    return value.strip()
