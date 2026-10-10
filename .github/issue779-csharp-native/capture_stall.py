"""Fixed root-selected N11 capture entry; cannot issue admission."""
import json
import os
import pathlib
import stat
import sys

# The fixture rechecks all four fixed module SHA/modes before isolated Python.
# Admit only that actual root-owned, root-private directory to module imports.
directory = pathlib.Path(__file__).parent
metadata = directory.lstat()
if (os.geteuid() != 0 or not stat.S_ISDIR(metadata.st_mode) or metadata.st_uid != 0
        or metadata.st_gid != 0 or stat.S_IMODE(metadata.st_mode) != 0o700):
    raise SystemExit(65)
sys.path.insert(0, str(directory))
from stall_capture import capture

try:
    if len(sys.argv) != 10:
        raise ValueError("stall-capture-rejected")
    result = capture(*sys.argv[1:9], end_ms=int(sys.argv[9]))
    raw = (json.dumps(result, sort_keys=True, separators=(",", ":")) + "\n").encode()
    if len(raw) > 8192:
        raise ValueError("stall-capture-rejected")
    sys.stdout.buffer.write(raw)
    sys.stdout.buffer.flush()
except (ValueError, TypeError, OSError, KeyError, UnicodeError, OverflowError, RecursionError):
    sys.stderr.write("STALL_CAPTURE_REJECTED\n")
    raise SystemExit(65) from None
