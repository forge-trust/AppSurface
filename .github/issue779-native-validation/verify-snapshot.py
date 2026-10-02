#!/usr/bin/env python3
"""Verify the source content frozen into this private validation checkout."""
from pathlib import Path
import hashlib
import json
import subprocess
import sys
root = Path(__file__).resolve().parent
repo = root / 'repo'
record = json.loads((root / 'snapshot.json').read_text())
bad = []
for name, expected in record['source_sha256'].items():
    path = repo / name
    if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
        bad.append(name)
for ref, expected in [('HEAD', record['validation_commit']), ('origin/main', record['comparison_base'])]:
    actual = subprocess.check_output(['git', 'rev-parse', ref], cwd=repo, text=True).strip()
    if actual != expected:
        bad.append(ref)
if bad:
    print('Snapshot mismatch: ' + ', '.join(bad), file=sys.stderr)
    sys.exit(1)
print(f"Verified {len(record['source_sha256'])} source files; HEAD/base match private snapshot.")
