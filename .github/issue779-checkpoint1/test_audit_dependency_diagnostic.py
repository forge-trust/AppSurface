"""Detached ELF/cache metadata controls; no ELF, loader, OS audit or native claim.

The real inspect_elf candidate algorithm runs against modeled readelf/cache
metadata. Its I/O boundaries are mocked; these tests issue no root or ownership
capability and never establish a physical dependency resolution.
"""

import importlib.util
import json
from pathlib import Path
import time
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("audit_dependency_data", ROOT / "procedures/os_audit.py")
audit = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(audit)

REQUESTER = "/metadata/runtime/requester"
SONAME = "libmetadata.so.1"
LOCAL = "/metadata/runtime/" + SONAME
CACHED = "/metadata/cache/" + SONAME
DIGEST = "a" * 64
PREFIX = b"\x7fELF\x02\x01\x01" + b"\x00" * 11 + b"\x3e\x00"


class AuditDependencyDiagnosticControls(unittest.TestCase):
    def inspect(self, *, cache=(), runpath=False):
        """Call actual candidate selection with explicit metadata-only I/O models."""
        owner = audit.Audit(time.monotonic() + 30)
        owner.commands["readelf"] = "/metadata/readelf"
        owner.cache = {SONAME: list(cache)}
        dynamic = "(NEEDED) Shared library: [" + SONAME + "]\n"
        if runpath:
            dynamic += "(RUNPATH) Library runpath: [$ORIGIN]\n"
        def readelf(argv):
            self.assertEqual(["/metadata/readelf", argv[1], "--", REQUESTER], argv)
            return {"-h": "ELF64 Advanced Micro Devices X86-64\n", "-d": dynamic, "-l": ""}[argv[1]]
        def exists(path):
            self.assertEqual(LOCAL, path)
            return runpath
        with mock.patch.object(owner, "pin_os", return_value=(DIGEST, PREFIX)) as pin, \
             mock.patch.object(owner, "run", side_effect=readelf), \
             mock.patch.object(owner, "resolve_directory") as resolve, \
             mock.patch.object(audit.os.path, "isdir", return_value=True), \
             mock.patch.object(audit.os.path, "lexists", side_effect=exists):
            try:
                result = owner.inspect_elf(REQUESTER)
            except audit.AuditError as error:
                return owner, error, pin.call_args_list, resolve.call_args_list
            return owner, result, pin.call_args_list, resolve.call_args_list

    def assert_original_error(self, owner, error):
        self.assertIsInstance(error, audit.AuditError)
        self.assertEqual(("dependency-unresolved-or-ambiguous",), error.args)
        self.assertEqual("dependency-unresolved-or-ambiguous", str(error))
        self.assertIs(error.failure_deadline, owner.deadline)
        self.assertIs(error.failure_context, owner.failure_context)
        self.assertNotIn(REQUESTER, str(error))
        self.assertNotIn(SONAME, repr(error))

    def test_ambiguity_carries_original_requester_candidates_counts_and_deadline(self):
        owner, error, pins, directories = self.inspect(cache=[CACHED], runpath=True)
        self.assert_original_error(owner, error)
        self.assertEqual([mock.call(REQUESTER)], pins)
        self.assertEqual([mock.call("/metadata/runtime")], directories)
        self.assertLessEqual(len(error.failure_context), 32768)
        context = json.loads(error.failure_context)
        self.assertEqual({"schema": "issue779-os-audit-dependency-failure-context-v1",
            "category": "dependency-unresolved-or-ambiguous", "requester": REQUESTER,
            "soname": SONAME, "candidate_count": 2, "runpath_count": 1, "cache_count": 1,
            "truncated": False, "runpath_candidates": [LOCAL], "cache_candidates": [CACHED],
            "combined_candidates": sorted([LOCAL, CACHED])}, context)
        self.assertEqual([CACHED], owner.cache[SONAME])

    def test_missing_dependency_carries_zero_counts_and_same_deadline(self):
        owner, error, pins, directories = self.inspect()
        self.assert_original_error(owner, error)
        context = json.loads(error.failure_context)
        self.assertEqual(REQUESTER, context["requester"])
        self.assertEqual(SONAME, context["soname"])
        for field in ("candidate_count", "runpath_count", "cache_count"):
            self.assertEqual(0, context[field])
        for field in ("runpath_candidates", "cache_candidates", "combined_candidates"):
            self.assertEqual([], context[field])
        self.assertEqual([mock.call(REQUESTER)], pins)
        self.assertEqual([], directories)

    def test_encoding_bound_keeps_original_rejection_without_partial_context(self):
        paths = ["/" + "a" * 4080 + str(index) for index in range(5)]
        owner, error, pins, _ = self.inspect(cache=paths)
        self.assert_original_error(owner, error)
        self.assertIsNone(error.failure_context)
        self.assertEqual([mock.call(REQUESTER)], pins)

    def test_context_row_bound_keeps_original_rejection(self):
        paths = ["/metadata/cache/row" + str(index) for index in range(audit.MAX_FILES + 1)]
        owner, error, pins, _ = self.inspect(cache=paths)
        self.assert_original_error(owner, error)
        self.assertIsNone(error.failure_context)
        self.assertEqual([mock.call(REQUESTER)], pins)

    def test_one_candidate_neighbor_returns_unchanged_resolution(self):
        owner, result, pins, directories = self.inspect(cache=[CACHED])
        self.assertEqual({"path": REQUESTER, "sha256": DIGEST, "interpreter": None,
            "resolved": [{"soname": SONAME, "path": CACHED, "sha256": DIGEST}]}, result)
        self.assertIsNone(owner.failure_context)
        self.assertEqual([mock.call(REQUESTER), mock.call(CACHED), mock.call(REQUESTER)], pins)
        self.assertEqual([], directories)

    def test_existing_identical_literal_deduplication_is_unchanged(self):
        owner, result, pins, _ = self.inspect(cache=[LOCAL], runpath=True)
        self.assertIsInstance(result, dict)
        self.assertEqual([{"soname": SONAME, "path": LOCAL, "sha256": DIGEST}], result["resolved"])
        self.assertIsNone(owner.failure_context)
        self.assertEqual([mock.call(REQUESTER), mock.call(LOCAL), mock.call(REQUESTER)], pins)

    def test_other_errors_default_diagnostics_to_none_and_keep_category_only(self):
        error = audit.AuditError("unsupported-ELF-ABI")
        self.assertEqual(("unsupported-ELF-ABI",), error.args)
        self.assertIsNone(error.failure_context)
        self.assertIsNone(error.failure_deadline)
        owner = audit.Audit(time.monotonic() + 30)
        owner.commands["readelf"] = "/metadata/readelf"
        with mock.patch.object(owner, "pin_os", return_value=(DIGEST, b"not ELF")):
            with self.assertRaises(audit.AuditError) as caught:
                owner.inspect_elf(REQUESTER)
        self.assertEqual(("unsupported-ELF-ABI",), caught.exception.args)
        self.assertIsNone(caught.exception.failure_context)
        self.assertIsNone(caught.exception.failure_deadline)


if __name__ == "__main__":
    unittest.main()
