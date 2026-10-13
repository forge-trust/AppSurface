#!/usr/bin/env python3
"""Small fixed-file CLI for the pure reviewed helper receipt adapter."""
from __future__ import annotations

import importlib.util
from pathlib import Path
import sys

MAX_BYTES = 1024 * 1024


def _read(path: str) -> bytes:
    candidate = Path(path)
    if candidate.is_symlink() or not candidate.is_file():
        raise ValueError("input-kind")
    with candidate.open("rb") as stream:
        raw = stream.read(MAX_BYTES + 1)
    if not 0 < len(raw) <= MAX_BYTES:
        raise ValueError("input-size")
    return raw


def main() -> int:
    if len(sys.argv) != 5:
        return 2
    case, receipt_path, diagnostics_path, recipe_sha256 = sys.argv[1:]
    if case not in ("N03", "N07"):
        return 2
    adapter_path = Path(__file__).with_name("helper_receipt_adapter.py")
    spec = importlib.util.spec_from_file_location("helper_receipt_adapter", adapter_path)
    if spec is None or spec.loader is None:
        return 2
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    try:
        receipt = module.decode_json(_read(receipt_path))
        module.validate_helper_receipt(receipt, case, recipe_sha256, _read(diagnostics_path))
    except (OSError, ValueError):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
