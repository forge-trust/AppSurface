"""New real-file data controls, not root SDK/build/consumer or compatibility proof."""
import importlib.util
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("qualification_recovered_product", Path(__file__).with_name("prepare-product.py"))
product = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(product)


class ProductInputFileControls(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="issue779-product-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.path = self.root / "input"
        self.path.write_bytes(b"abcd")

    def read(self, maximum=4, **kwargs):
        return product.read_regular(self.path, maximum, expected_owner_uid=os.geteuid(), **kwargs)

    def test_exact_real_bytes_and_adjacent_oversize_rejected_before_open(self):
        self.assertEqual(b"abcd", self.read())
        self.path.write_bytes(b"abcde")
        with patch.object(product.os, "open") as opened:
            with self.assertRaises(ValueError):
                self.read()
            opened.assert_not_called()

    def test_links_and_fifo_reject_before_open_without_changing_outside_bytes(self):
        outside = self.root / "outside"
        outside.write_bytes(b"outside-canary")
        for shape in ("symlink", "hardlink", "fifo"):
            with self.subTest(shape=shape):
                self.path.unlink()
                if shape == "symlink":
                    self.path.symlink_to(outside)
                elif shape == "hardlink":
                    os.link(outside, self.path)
                else:
                    os.mkfifo(self.path)
                with patch.object(product.os, "open") as opened:
                    with self.assertRaises(ValueError):
                        self.read(64)
                    opened.assert_not_called()
                self.assertEqual(b"outside-canary", outside.read_bytes())

    def test_growth_or_named_replacement_during_real_read_rejects(self):
        real_read = os.read
        for shape in ("growth", "replacement"):
            with self.subTest(shape=shape):
                self.path.write_bytes(b"abcd")
                changed = False
                def read(fd, count):
                    nonlocal changed
                    data = real_read(fd, count)
                    if not changed:
                        changed = True
                        if shape == "growth":
                            with self.path.open("ab") as stream:
                                stream.write(b"e")
                        else:
                            adjacent = self.root / "replacement"
                            adjacent.write_bytes(b"abcd")
                            os.replace(adjacent, self.path)
                    return data
                with patch.object(product.os, "read", side_effect=read):
                    with self.assertRaises(ValueError):
                        self.read()

    def test_read_error_closes_actual_selected_fd(self):
        selected = []
        real_open = os.open
        def opened(*args, **kwargs):
            fd = real_open(*args, **kwargs)
            selected.append(fd)
            return fd
        with patch.object(product.os, "open", side_effect=opened), patch.object(product.os, "read", side_effect=OSError("private-read-canary")):
            with self.assertRaises(OSError):
                self.read()
        self.assertEqual(1, len(selected))
        with self.assertRaises(OSError):
            os.fstat(selected[0])

    def test_expired_or_final_read_deadline_cannot_publish_bytes(self):
        with patch.object(product.os, "open") as opened:
            with self.assertRaises(ValueError):
                self.read(deadline=time.monotonic()-1)
            opened.assert_not_called()
        with patch.object(product.time, "monotonic", side_effect=[0, 0, 0, 2]):
            with self.assertRaises(ValueError):
                self.read(deadline=1)

    def test_invalid_caps_reject_before_real_open(self):
        with patch.object(product.os, "open") as opened:
            for maximum in (0, -1, True, None, "4", product.MAXIMUM_FILE+1):
                with self.subTest(maximum=maximum), self.assertRaises(ValueError):
                    self.read(maximum)
            opened.assert_not_called()

    def test_real_fd_close_deadline_or_error_cannot_publish_bytes(self):
        real_close = os.close
        for shape in ("deadline", "close-error"):
            with self.subTest(shape=shape):
                clock, selected = [0], []
                def close(fd):
                    real_close(fd)
                    selected.append(fd)
                    if shape == "deadline":
                        clock[0] = 2
                    else:
                        raise OSError("private-close-canary")
                with patch.object(product.time, "monotonic", side_effect=lambda: clock[0]), patch.object(product.os, "close", side_effect=close):
                    with self.assertRaises(ValueError if shape == "deadline" else OSError):
                        self.read(deadline=1)
                self.assertEqual(1, len(selected))
                with self.assertRaises(OSError):
                    os.fstat(selected[0])

    def test_different_real_fd_substitution_rejects_before_read_and_closes(self):
        outside = self.root / "outside"
        outside.write_bytes(b"abcd")
        real_open, selected = os.open, []
        def opened(*args, **kwargs):
            fd = real_open(outside, args[1])
            selected.append(fd)
            return fd
        with patch.object(product.os, "open", side_effect=opened), patch.object(product.os, "read") as read:
            with self.assertRaises(ValueError):
                self.read()
            read.assert_not_called()
        with self.assertRaises(OSError):
            os.fstat(selected[0])
        self.assertEqual(b"abcd", outside.read_bytes())

    def test_original_read_error_survives_secondary_close_error_after_real_close(self):
        original, selected = OSError("original-private-canary"), []
        real_close = os.close
        def close(fd):
            real_close(fd)
            selected.append(fd)
            raise OSError("secondary-private-canary")
        with patch.object(product.os, "read", side_effect=original), patch.object(product.os, "close", side_effect=close):
            with self.assertRaises(OSError) as raised:
                self.read()
        self.assertIs(original, raised.exception)
        self.assertEqual(1, len(selected))
        with self.assertRaises(OSError):
            os.fstat(selected[0])

    def test_tree_first_excess_stops_enumeration_before_sort_with_valid_neighbor(self):
        for name in ("second", "third"):
            (self.root / name).write_bytes(b"data")
        real_scandir, consumed = os.scandir, []
        class Entries:
            def __init__(self, fd):
                self.entries = real_scandir(fd)
            def __enter__(self):
                return self
            def __exit__(self, *args):
                self.entries.close()
            def __iter__(self):
                return self
            def __next__(self):
                entry = next(self.entries)
                consumed.append(entry.name)
                return entry
        with patch.object(product.os, "scandir", side_effect=Entries):
            with self.assertRaises(ValueError):
                product.bounded_tree(self.root, 1, time.monotonic()+5)
        self.assertEqual(2, len(consumed))
        self.assertEqual(sorted((self.path, self.root / "second", self.root / "third")),
                         product.bounded_tree(self.root, 3, time.monotonic()+5))

    def test_tree_link_or_final_descriptor_close_deadline_rejects(self):
        self.path.unlink()
        outside = self.root.parent / (self.root.name+"-outside")
        outside.mkdir()
        try:
            (outside / "sentinel").write_bytes(b"outside-canary")
            self.path.symlink_to(outside, target_is_directory=True)
            with self.assertRaises(ValueError):
                product.bounded_tree(self.root, 3, time.monotonic()+5)
            self.assertEqual(b"outside-canary", (outside / "sentinel").read_bytes())
            self.path.unlink()
            self.path.write_bytes(b"abcd")
            real_close, clock, closed = os.close, [0.0], []
            def close(fd):
                real_close(fd)
                closed.append(fd)
                clock[0] = 2.0
            with patch.object(product.time, "monotonic", side_effect=lambda: clock[0]), patch.object(
                    product.os, "close", side_effect=close):
                with self.assertRaises(ValueError):
                    product.bounded_tree(self.root, 1, 1.0)
            self.assertEqual(1, len(closed))
            with self.assertRaises(OSError):
                os.fstat(closed[0])
        finally:
            (outside / "sentinel").unlink()
            outside.rmdir()

    def test_metadata_internal_access_needs_actual_caller_friend_and_rejects_family(self):
        type_name = "[ForgeTrust.AppSurface.Evidence.Contracts]Example"
        image = {"internals_visible_to": [], "types": [{"name": type_name, "access": "Public"}]}
        member = {"type": type_name, "access": "Assembly"}
        caller = {"assembly": product.assembly_identity("Cli")}
        with self.assertRaises(ValueError):
            product.metadata_access(image, member, caller)
        image["internals_visible_to"] = [{"attribute_type": "System.Runtime.CompilerServices.InternalsVisibleToAttribute",
                                         "name": caller["assembly"]["name"]}]
        self.assertEqual(["compiled-friend-assembly-arm", "public"], product.metadata_access(image, member, caller))
        for access in ("Family", "FamANDAssem", "Private"):
            member["access"] = access
            with self.subTest(access=access), self.assertRaises(ValueError):
                product.metadata_access(image, member, caller)

    def make_swap_files(self, tag):
        root = self.root / tag
        root.mkdir(mode=0o700)
        tool, sources = root / "tool", root / "candidates"
        tool.mkdir(mode=0o700)
        sources.mkdir(mode=0o700)
        candidates, before = {}, {}
        for name in product.LIBRARIES:
            candidates[name] = {}
            for extension in ("dll", "pdb"):
                basename = f"ForgeTrust.AppSurface.Evidence.{name}.{extension}"
                target, source = tool / basename, sources / basename
                original, replacement = ("old-"+basename).encode(), ("new-"+basename).encode()
                target.write_bytes(original)
                source.write_bytes(replacement)
                os.chmod(target, 0o644)
                before[basename] = (original, target.stat().st_mode & 0o777, target.stat().st_ino)
                candidates[name][extension] = {"path": str(source), "sha256": product.sha(replacement)}
        return root, tool, candidates, before

    def test_swap_actual_fd_or_late_named_hardlink_substitution_mutates_nothing(self):
        for shape in ("fd-substitution", "named-hardlink"):
            with self.subTest(shape=shape):
                root, tool, candidates, before = self.make_swap_files(shape)
                outside = root / "outside"
                first = "ForgeTrust.AppSurface.Evidence.Cli.dll"
                outside.write_bytes(before[first][0] if shape == "fd-substitution" else b"outside-canary")
                os.chmod(outside, 0o644 if shape == "fd-substitution" else 0o640)
                outside_before = outside.read_bytes(), outside.stat().st_mode & 0o777
                real_open, real_read, selected, changed = os.open, product.read_regular, [], [False]
                def opened(path, flags, *args, **kwargs):
                    fd = real_open(outside, flags) if shape == "fd-substitution" and path == first and flags & os.O_RDWR else real_open(path, flags, *args, **kwargs)
                    selected.append(fd)
                    return fd
                def read(path, maximum, **kwargs):
                    data = real_read(path, maximum, **kwargs)
                    if shape == "named-hardlink" and not changed[0]:
                        changed[0] = True
                        replacement = tool / "temporary-hardlink"
                        os.link(outside, replacement)
                        os.replace(replacement, tool / first)
                    return data
                with patch.object(product.os, "open", side_effect=opened), patch.object(
                        product, "read_regular", side_effect=read), patch.object(product.os, "ftruncate") as truncate, patch.object(
                        product.os, "write") as write:
                    with self.assertRaises(ValueError):
                        product.swap_product_pairs(tool, candidates, time.monotonic()+5, expected_owner_uid=os.geteuid())
                    truncate.assert_not_called()
                    write.assert_not_called()
                self.assertEqual(outside_before, (outside.read_bytes(), outside.stat().st_mode & 0o777))
                for basename, (data, mode, inode) in before.items():
                    if shape == "named-hardlink" and basename == first:
                        self.assertEqual(outside.stat().st_ino, (tool / basename).stat().st_ino)
                        continue
                    self.assertEqual((data, mode, inode), ((tool / basename).read_bytes(), (tool / basename).stat().st_mode & 0o777,
                                                         (tool / basename).stat().st_ino))
                for fd in set(selected):
                    with self.assertRaises(OSError):
                        os.fstat(fd)

    def test_swap_failed_sixth_preflight_writes_none_and_closes_all_real_fds(self):
        root, tool, candidates, before = self.make_swap_files("later-failure")
        candidates["Coverage"]["pdb"]["sha256"] = "0"*64
        real_open, selected = os.open, []
        def opened(*args, **kwargs):
            fd = real_open(*args, **kwargs)
            selected.append(fd)
            return fd
        with patch.object(product.os, "open", side_effect=opened), patch.object(product.os, "ftruncate") as truncate, patch.object(
                product.os, "write") as write:
            with self.assertRaises(ValueError):
                product.swap_product_pairs(tool, candidates, time.monotonic()+5, expected_owner_uid=os.geteuid())
            truncate.assert_not_called()
            write.assert_not_called()
        for basename, (data, mode, inode) in before.items():
            target = tool / basename
            self.assertEqual((data, mode, inode), (target.read_bytes(), target.stat().st_mode & 0o777, target.stat().st_ino))
        for fd in set(selected):
            with self.assertRaises(OSError):
                os.fstat(fd)

    def test_swap_valid_six_targets_use_retained_verified_fds_and_exact_bytes(self):
        root, tool, candidates, before = self.make_swap_files("valid-swap")
        real_open, real_truncate, writable, selected, writes = os.open, os.ftruncate, [], [], []
        def opened(path, flags, *args, **kwargs):
            fd = real_open(path, flags, *args, **kwargs)
            selected.append(fd)
            if flags & os.O_RDWR:
                self.assertTrue(flags & os.O_NOFOLLOW)
                self.assertTrue(flags & os.O_NONBLOCK)
                writable.append((fd, os.fstat(fd).st_ino))
            return fd
        def truncate(fd, size):
            self.assertEqual(6, len(writable))
            self.assertIn((fd, os.fstat(fd).st_ino), writable)
            writes.append(fd)
            return real_truncate(fd, size)
        with patch.object(product.os, "open", side_effect=opened), patch.object(product.os, "ftruncate", side_effect=truncate):
            rows = product.swap_product_pairs(tool, candidates, time.monotonic()+5, expected_owner_uid=os.geteuid())
        self.assertEqual(6, len(rows))
        self.assertEqual([fd for fd, inode in writable], writes)
        for name in product.LIBRARIES:
            for extension in ("dll", "pdb"):
                basename = f"ForgeTrust.AppSurface.Evidence.{name}.{extension}"
                target, source = tool / basename, Path(candidates[name][extension]["path"])
                self.assertEqual(source.read_bytes(), target.read_bytes())
                self.assertEqual(0o444, target.stat().st_mode & 0o777)
                self.assertEqual(before[basename][2], target.stat().st_ino)
        for fd in set(selected):
            with self.assertRaises(OSError):
                os.fstat(fd)


class PartitionMetadataDataControls(unittest.TestCase):
    """Actual emitted-row rejection controls, not ABI/root/native acceptance."""
    # Exact rows captured from the actual native Cli partition; no file dependency.
    # Digests describe provenance/data integrity, not ABI, admission or native proof.
    SOURCE_REPORT_SHA256 = 'c92539a814b6ee9d5a6d89e5acce0da0f673cbc8607dc14c794bae376f19ccfe'
    SOURCE_REPORT_BYTES = 2358753
    CAPTURED_ROWS_SHA256 = '223b8fd809f71b3de4534efbe3fe14b5dd3b25cc01b7bcd0567791ddd97919e5'
    CAPTURED_ROWS_BYTES = 539
    CAPTURED_ROWS = {'member_references': [{'token': 167772250, 'kind': 'method', 'type': '[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceCoverageGateRequirements', 'parent_kind': 'TypeReference', 'name': 'get_MinPatchLinePercent', 'signature': 'header:32;generic:0;required:0;return:kind:17:[System.Runtime]System.Nullable`1<kind:17:[System.Runtime]System.Decimal>;parameters:()'}], 'assembly_references': [{'name': 'ForgeTrust.AppSurface.Evidence.Contracts', 'version': '0.2.0.0', 'culture': '', 'public_key_or_token': '', 'flags': 0}]}

    def actual_cli(self):
        raw = product.json.dumps(self.CAPTURED_ROWS, sort_keys=True, separators=(",", ":")).encode()
        self.assertEqual(self.CAPTURED_ROWS_BYTES, len(raw))
        self.assertEqual(self.CAPTURED_ROWS_SHA256, product.sha(raw))
        self.assertEqual(64, len(self.SOURCE_REPORT_SHA256))
        return raw, product.unique_json(raw)

    def test_actual_direct_reference_rejects_typespec_unresolved_and_undeclared_dependency(self):
        raw, cli = self.actual_cli()
        prefix = "["+product.assembly_identity("Contracts")["name"]+"]"
        reference = next(row for row in cli["member_references"] if row["type"].startswith(prefix)
                         and row["parent_kind"] == "TypeReference")
        refs = [row for row in cli["assembly_references"] if row["name"] == product.assembly_identity("Contracts")["name"]]
        self.assertTrue(product.direct_dependency_reference(reference, prefix, refs))
        for shape in ("typespec", "unresolved", "undeclared"):
            changed = dict(reference)
            if shape == "typespec": changed["parent_kind"] = "TypeSpecification"
            if shape == "unresolved": changed["signature"] += " unresolved-parent:TypeSpecification"
            with self.subTest(shape=shape), self.assertRaises(ValueError):
                product.direct_dependency_reference(changed, prefix, [] if shape == "undeclared" else refs)

    def test_actual_metadata_index_byte_work_and_original_deadline_bounds(self):
        raw, cli = self.actual_cli()
        budget = product.ReconciliationBudget(time.monotonic()+5, len(raw), maximum_bytes=len(raw))
        with self.assertRaises(ValueError): budget.store(cli["member_references"][0])
        budget = product.ReconciliationBudget(time.monotonic()+5, maximum_work=2)
        budget.row(); budget.row()
        with self.assertRaises(ValueError): budget.row()
        clock = [1.0]
        with patch.object(product.time, "monotonic", side_effect=lambda: clock[0]):
            budget = product.ReconciliationBudget(2.0)
            self.assertEqual(2.0, budget.deadline)
            clock[0] = 2.0
            with self.assertRaises(ValueError): budget.store(cli["member_references"][0])


    def test_common_phase_independent_work_shared_bytes_and_original_deadline(self):
        raw, cli = self.actual_cli()
        row = cli["member_references"][0]
        with self.subTest(bound="independent-work"):
            budget = product.ReconciliationBudget(time.monotonic()+5, maximum_work=2)
            budget.row(); budget.row()
            common = budget.common_verification_phase()
            self.assertEqual(2, budget.work)
            self.assertEqual(0, common.work)
            common.row(); common.row()
            for selected in (budget, common):
                with self.assertRaises(ValueError): selected.row()
            self.assertEqual({"common_verification_work": 2, "definition_dependency_work": 2,
                              "total_python_verification_work": 4}, budget.work_counts())
            with self.assertRaises(ValueError): budget.common_verification_phase()
            with self.assertRaises(ValueError): common.common_verification_phase()
            self.assertEqual((2, 2), (budget.work, common.work))
        with self.subTest(bound="one-byte-ledger"):
            # Obtain only this captured row's actual serialized accounting size.
            measured = product.ReconciliationBudget(time.monotonic()+5)
            measured.store(row)
            row_bytes = measured.bytes
            budget = product.ReconciliationBudget(time.monotonic()+5, len(raw),
                maximum_bytes=len(raw)+2*row_bytes)
            common = budget.common_verification_phase()
            common.store(row); budget.store(row)
            self.assertEqual(len(raw)+2*row_bytes, budget.bytes)
            self.assertEqual(budget.bytes, common.bytes)
            for selected in (budget, common):
                with self.assertRaises(ValueError): selected.reserve(1)
            self.assertEqual(len(raw)+2*row_bytes, common.bytes)
        with self.subTest(bound="one-original-deadline"):
            clock = [1.0]
            with patch.object(product.time, "monotonic", side_effect=lambda: clock[0]):
                budget = product.ReconciliationBudget(2.0)
                budget.row()
                clock[0] = 1.5
                common = budget.common_verification_phase()
                self.assertEqual(2.0, budget.deadline)
                self.assertEqual(budget.deadline, common.deadline)
                clock[0] = 2.0
                for selected in (budget, common):
                    with self.assertRaises(ValueError): selected.row()
                    with self.assertRaises(ValueError): selected.store(row)
                self.assertEqual((1, 0, 0), (budget.work, common.work, budget.bytes))


class NamedTypeAndCommonDataControls(unittest.TestCase):
    """Small exact emitted rows; only data guards, never a passed ABI report."""
    REPORT_PROVENANCE = {'Contracts': 'a925c89ac55ba61da67f5e2cb68b913f0219997ec3c5eae670e49ff534a7fafe', 'Cli': 'c92539a814b6ee9d5a6d89e5acce0da0f673cbc8607dc14c794bae376f19ccfe'}
    CAPTURED_SHA256 = 'e427aa4e8d59506d1170a3d0d993ea931c440484f5ded15776b050e1553586bd'
    CAPTURED = {'caller': {'assembly': {'name': 'ForgeTrust.AppSurface.Evidence.Cli', 'version': '0.2.0.0', 'culture': '', 'public_key_or_token': '', 'flags': 0}}, 'type_reference': {'token': 16777245, 'name': '[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult', 'scope_kind': 'AssemblyReference', 'scope_token': 587202563}, 'types': {'baseline': [{'token': 33554469, 'name': '[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult', 'attributes': 1048833, 'access': 'Public', 'base_type': '[System.Runtime]System.Object', 'interfaces': ['kind:18:[System.Runtime]System.IEquatable`1<kind:18:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult>'], 'generic_parameters': []}], 'candidate': [{'token': 33554469, 'name': '[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult', 'attributes': 1048833, 'access': 'Public', 'base_type': '[System.Runtime]System.Object', 'interfaces': ['kind:18:[System.Runtime]System.IEquatable`1<kind:18:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult>'], 'generic_parameters': []}]}, 'common_types': {'[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult': {'type': '[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult', 'attributes_equal': True, 'base_type_equal': True, 'interfaces_equal': True, 'generic_parameters_equal': True, 'generic_parameter_names_equal': True, 'baseline_access': 'Public', 'candidate_access': 'Public'}}, 'nested_types': {'baseline': [{'token': 33554509, 'name': '[ForgeTrust.AppSurface.Evidence.Contracts]<PrivateImplementationDetails>', 'attributes': 256, 'access': 'NotPublic', 'base_type': '[System.Runtime]System.Object', 'interfaces': [], 'generic_parameters': []}, {'token': 33554589, 'name': '[ForgeTrust.AppSurface.Evidence.Contracts]<PrivateImplementationDetails>+__StaticArrayInitTypeSize=24', 'attributes': 277, 'access': 'NestedAssembly', 'base_type': '[System.Runtime]System.ValueType', 'interfaces': [], 'generic_parameters': []}], 'candidate': [{'token': 33554509, 'name': '[ForgeTrust.AppSurface.Evidence.Contracts]<PrivateImplementationDetails>', 'attributes': 256, 'access': 'NotPublic', 'base_type': '[System.Runtime]System.Object', 'interfaces': [], 'generic_parameters': []}, {'token': 33554589, 'name': '[ForgeTrust.AppSurface.Evidence.Contracts]<PrivateImplementationDetails>+__StaticArrayInitTypeSize=24', 'attributes': 277, 'access': 'NestedAssembly', 'base_type': '[System.Runtime]System.ValueType', 'interfaces': [], 'generic_parameters': []}]}, 'nested_common': {'[ForgeTrust.AppSurface.Evidence.Contracts]<PrivateImplementationDetails>': {'type': '[ForgeTrust.AppSurface.Evidence.Contracts]<PrivateImplementationDetails>', 'attributes_equal': True, 'base_type_equal': True, 'interfaces_equal': True, 'generic_parameters_equal': True, 'generic_parameter_names_equal': True, 'baseline_access': 'NotPublic', 'candidate_access': 'NotPublic'}, '[ForgeTrust.AppSurface.Evidence.Contracts]<PrivateImplementationDetails>+__StaticArrayInitTypeSize=24': {'type': '[ForgeTrust.AppSurface.Evidence.Contracts]<PrivateImplementationDetails>+__StaticArrayInitTypeSize=24', 'attributes_equal': True, 'base_type_equal': True, 'interfaces_equal': True, 'generic_parameters_equal': True, 'generic_parameter_names_equal': True, 'baseline_access': 'NestedAssembly', 'candidate_access': 'NestedAssembly'}}, 'friends': {'baseline': ['ForgeTrust.AppSurface.Evidence.Aspire', 'ForgeTrust.AppSurface.Evidence.Cli', 'ForgeTrust.AppSurface.Evidence.Coverage', 'ForgeTrust.AppSurface.Evidence.Planner', 'ForgeTrust.AppSurface.Cli', 'ForgeTrust.AppSurface.Cli.Tests', 'ForgeTrust.AppSurface.Aspire.Tests', 'EvidenceHost.LifecycleWorker', 'EvidenceHost.ControlProtocolWorker'], 'candidate': ['ForgeTrust.AppSurface.Evidence.Aspire', 'ForgeTrust.AppSurface.Evidence.Cli', 'ForgeTrust.AppSurface.Evidence.Coverage', 'ForgeTrust.AppSurface.Evidence.Planner', 'ForgeTrust.AppSurface.Cli', 'ForgeTrust.AppSurface.Cli.Tests', 'ForgeTrust.AppSurface.Aspire.Tests', 'EvidenceHost.LifecycleWorker', 'EvidenceHost.ControlProtocolWorker']}, 'nested_name': '[ForgeTrust.AppSurface.Evidence.Contracts]<PrivateImplementationDetails>+__StaticArrayInitTypeSize=24', 'wrapper': {'token': 167772238, 'kind': 'method', 'type': 'kind:17:[System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<kind:18:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult>', 'parent_kind': 'TypeSpecification', 'name': 'Create', 'signature': 'header:0;generic:0;required:0;return:kind:17:[System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<!0>;parameters:()'}, 'assembly_reference': {'name': 'ForgeTrust.AppSurface.Evidence.Contracts', 'version': '0.2.0.0', 'culture': '', 'public_key_or_token': '', 'flags': 0}, 'pair': {'name': 'Contracts', 'baseline': {'types': [{'token': 33554469, 'name': '[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult', 'attributes': 1048833, 'access': 'Public', 'base_type': '[System.Runtime]System.Object', 'interfaces': ['kind:18:[System.Runtime]System.IEquatable`1<kind:18:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult>'], 'generic_parameters': []}], 'members': [{'token': 100663727, 'kind': 'method', 'type': '[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult', 'name': '.ctor', 'signature': 'header:32;generic:0;required:6;return:primitive:Void;parameters:(primitive:String,kind:17:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerOutcome,kind:18:[System.Runtime]System.Collections.Generic.IReadOnlyList`1<primitive:String>,primitive:String,kind:18:[System.Runtime]System.Collections.Generic.IReadOnlyList`1<kind:18:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceArtifactResult>,primitive:Int64)', 'attributes': 6278, 'access': 'Public', 'is_static': False, 'impl_attributes': 0, 'il_sha256': '67f7a8782c44204f4ce2c7a9d8ce296c12e8b46e8a99c2133868c66feea829f7', 'il_bytes': 53, 'parameters': [{'sequence': 1, 'name': 'ProducerId', 'attributes': 0, 'default_value': None}, {'sequence': 2, 'name': 'Outcome', 'attributes': 0, 'default_value': None}, {'sequence': 3, 'name': 'SatisfiedAssertionIds', 'attributes': 0, 'default_value': None}, {'sequence': 4, 'name': 'Diagnostic', 'attributes': 4112, 'default_value': {'type_code': 'NullReference', 'value_hex': '00000000'}}, {'sequence': 5, 'name': 'Artifacts', 'attributes': 4112, 'default_value': {'type_code': 'NullReference', 'value_hex': '00000000'}}, {'sequence': 6, 'name': 'ElapsedMilliseconds', 'attributes': 4112, 'default_value': {'type_code': 'Int64', 'value_hex': '0000000000000000'}}], 'generic_parameters': [], 'accessors': None}]}, 'candidate': {'types': [{'token': 33554469, 'name': '[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult', 'attributes': 1048833, 'access': 'Public', 'base_type': '[System.Runtime]System.Object', 'interfaces': ['kind:18:[System.Runtime]System.IEquatable`1<kind:18:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult>'], 'generic_parameters': []}], 'members': [{'token': 100663727, 'kind': 'method', 'type': '[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult', 'name': '.ctor', 'signature': 'header:32;generic:0;required:6;return:primitive:Void;parameters:(primitive:String,kind:17:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerOutcome,kind:18:[System.Runtime]System.Collections.Generic.IReadOnlyList`1<primitive:String>,primitive:String,kind:18:[System.Runtime]System.Collections.Generic.IReadOnlyList`1<kind:18:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceArtifactResult>,primitive:Int64)', 'attributes': 6278, 'access': 'Public', 'is_static': False, 'impl_attributes': 0, 'il_sha256': '67f7a8782c44204f4ce2c7a9d8ce296c12e8b46e8a99c2133868c66feea829f7', 'il_bytes': 53, 'parameters': [{'sequence': 1, 'name': 'ProducerId', 'attributes': 0, 'default_value': None}, {'sequence': 2, 'name': 'Outcome', 'attributes': 0, 'default_value': None}, {'sequence': 3, 'name': 'SatisfiedAssertionIds', 'attributes': 0, 'default_value': None}, {'sequence': 4, 'name': 'Diagnostic', 'attributes': 4112, 'default_value': {'type_code': 'NullReference', 'value_hex': '00000000'}}, {'sequence': 5, 'name': 'Artifacts', 'attributes': 4112, 'default_value': {'type_code': 'NullReference', 'value_hex': '00000000'}}, {'sequence': 6, 'name': 'ElapsedMilliseconds', 'attributes': 4112, 'default_value': {'type_code': 'Int64', 'value_hex': '0000000000000000'}}], 'generic_parameters': [], 'accessors': None}]}, 'common': {'common_types': [{'type': '[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult', 'attributes_equal': True, 'base_type_equal': True, 'interfaces_equal': True, 'generic_parameters_equal': True, 'generic_parameter_names_equal': True, 'baseline_access': 'Public', 'candidate_access': 'Public'}], 'candidate_members': [{'candidate_token': 100663727, 'kind': 'method', 'type': '[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerResult', 'name': '.ctor', 'signature': 'header:32;generic:0;required:6;return:primitive:Void;parameters:(primitive:String,kind:17:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceProducerOutcome,kind:18:[System.Runtime]System.Collections.Generic.IReadOnlyList`1<primitive:String>,primitive:String,kind:18:[System.Runtime]System.Collections.Generic.IReadOnlyList`1<kind:18:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceArtifactResult>,primitive:Int64)', 'candidate_access': 'Public', 'candidate_attributes': 6278, 'candidate_static': False, 'candidate_impl_attributes': 0, 'baseline_matches': [{'token': 100663727, 'access': 'Public', 'attributes': 6278, 'is_static': False, 'impl_attributes': 0, 'attributes_equal': True, 'static_equal': True, 'impl_attributes_equal': True, 'generic_parameters_equal': True, 'generic_parameter_names_equal': True}]}], 'baseline_only_members': []}}}

    def fixture(self):
        raw = product.json.dumps(self.CAPTURED, sort_keys=True, separators=(",", ":")).encode()
        self.assertEqual(self.CAPTURED_SHA256, product.sha(raw))
        return product.unique_json(raw)

    def type_audit(self, fixture, *, nested=False, drop_friends=False):
        budget = product.ReconciliationBudget(time.monotonic()+5)
        rows = fixture["nested_types"] if nested else fixture["types"]
        indexes = {side: product.index_type_definitions(rows[side], budget) for side in rows}
        images = {side: {"types": rows[side]} for side in rows}
        friends = {side: set() if drop_friends else set(fixture["friends"][side]) for side in rows}
        return product.audit_named_dependency_type(fixture["nested_name"] if nested else fixture["type_reference"]["name"],
            images, indexes, fixture["nested_common"] if nested else fixture["common_types"], fixture["caller"], friends, budget)

    def common_audit(self, fixture):
        pair = fixture["pair"]
        budget = product.ReconciliationBudget(time.monotonic()+5)
        members, types = {}, {}
        for side in ("baseline", "candidate"):
            members[side] = {}
            for row in pair[side]["members"]:
                members[side].setdefault(tuple(row[key] for key in ("kind", "type", "name", "signature")), []).append(row)
            types[side] = product.index_type_definitions(pair[side]["types"], budget)
        return product.validate_common_complete(pair, members, types, budget)

    def test_actual_named_type_missing_changed_common_and_private_access_reject(self):
        fixture = self.fixture()
        self.assertEqual(fixture["type_reference"]["name"], self.type_audit(fixture)["name"])
        for shape in ("missing", "common", "shape", "private-access"):
            changed = self.fixture()
            name = changed["type_reference"]["name"]
            if shape == "missing": changed["types"]["candidate"] = []
            if shape == "common": changed["common_types"][name]["interfaces_equal"] = False
            if shape == "shape": changed["types"]["candidate"][0]["attributes"] ^= 1
            if shape == "private-access":
                for side in ("baseline", "candidate"):
                    changed["types"][side][0]["access"] = "Private"
                    changed["common_types"][name][side+"_access"] = "Private"
            with self.subTest(shape=shape), self.assertRaises(ValueError): self.type_audit(changed)

    def test_actual_nested_enclosing_type_and_compiled_friend_required(self):
        fixture = self.fixture()
        self.assertEqual(2, len(self.type_audit(fixture, nested=True)["enclosing_types"]))
        changed = self.fixture()
        changed["nested_types"]["candidate"] = changed["nested_types"]["candidate"][1:]
        with self.assertRaises(ValueError): self.type_audit(changed, nested=True)
        with self.assertRaises(ValueError): self.type_audit(fixture, nested=True, drop_friends=True)

    def test_actual_definition_duplicate_name_or_token_never_last_wins(self):
        for shape in ("name", "token"):
            rows = self.fixture()["types"]["baseline"]
            duplicate = dict(rows[0])
            if shape == "token": duplicate["name"] += "+negative-data"
            rows.append(duplicate)
            with self.subTest(shape=shape), self.assertRaises(ValueError):
                product.index_type_definitions(rows, product.ReconciliationBudget(time.monotonic()+5))

    def test_actual_common_all_members_types_buckets_and_flags_complete(self):
        members, types = self.common_audit(self.fixture())
        self.assertEqual((1, 1), (len(members), len(types)))
        for shape in ("string", "member-missing", "type-missing", "duplicate", "flag", "bucket", "baseline-extra"):
            changed = self.fixture()
            common = changed["pair"]["common"]
            if shape == "string": common["baseline_only_members"] = ""
            if shape == "member-missing": common["candidate_members"] = []
            if shape == "type-missing": common["common_types"] = []
            if shape == "duplicate": common["candidate_members"] *= 2
            if shape == "flag": common["candidate_members"][0]["baseline_matches"][0]["attributes_equal"] = False
            if shape == "bucket": common["candidate_members"][0]["baseline_matches"] = []
            if shape == "baseline-extra":
                member = changed["pair"]["baseline"]["members"][0]
                common["baseline_only_members"] = [{"baseline_token": member["token"], **{key: member[key] for key in ("kind", "type", "name", "signature", "access")}}]
            with self.subTest(shape=shape), self.assertRaises(ValueError): self.common_audit(changed)

    def test_actual_framework_wrapper_not_direct_unknown_or_dependency_owner_reject(self):
        fixture = self.fixture()
        prefix = "[ForgeTrust.AppSurface.Evidence.Contracts]"
        wrapper, refs = fixture["wrapper"], [fixture["assembly_reference"]]
        self.assertFalse(product.direct_dependency_reference(wrapper, prefix, refs))
        for shape in ("unknown", "dependency"):
            changed = dict(wrapper)
            if shape == "unknown": changed["type"] = "modreq(negative-data)"+changed["type"]
            else: changed["type"] = "kind:18:"+prefix+fixture["type_reference"]["name"].split("]",1)[1]+"<!0>"
            with self.subTest(shape=shape), self.assertRaises(ValueError):
                product.direct_dependency_reference(changed, prefix, refs)


class CompactInventoryRepresentationDataControls(unittest.TestCase):
    """Exact native pin projection; no invented full image or accepted ABI report.

    The full captured native inventory remains separate. This small fixture tests
    ONLY exclusive representation/pin guards before full ImageReport validation.
    File path strings are observed data; these controls open no captured paths.
    """
    INSPECTOR_SOURCE_SHA256 = '89777911ad18859f477c66a56024762b0be78a4b22c6a831ff3b678917472a31'
    INVENTORY_REPORT_SHA256 = 'd0307bfc7675bafadc2e457c58874ebd8cf2f83b6586c34bfccbeef7fdd434e2'
    INVENTORY_REPORT_BYTES = 5788020
    BASELINE_CANONICAL_RECORD_SHA256 = '370908389e8fea687d3b40a47300afb616041a8b3f47b7a656ef06bea3bdf3d7'
    CAPTURED_PROJECTION_SHA256 = '1b17a7dffd30558806c92a41b154ab71621ac289a87e3ed1882c10fc5ec3f1cc'
    CAPTURED_PROJECTION = {'pair': {'name': 'Coverage', 'candidate': None, 'dll_bytes_equal': True, 'pdb_bytes_equal': True, 'candidate_is_baseline': True, 'common': None, 'baseline': {'dll_path': '/private/tmp/issue779-option2-recovery-j0gez4kc/product-source/Evidence/ForgeTrust.AppSurface.Evidence.Coverage/bin/Debug/net10.0/ForgeTrust.AppSurface.Evidence.Coverage.dll', 'pdb_path': '/private/tmp/issue779-option2-recovery-j0gez4kc/product-source/Evidence/ForgeTrust.AppSurface.Evidence.Coverage/bin/Debug/net10.0/ForgeTrust.AppSurface.Evidence.Coverage.pdb', 'dll_sha256': '83cb8c42defb01785f41b59ea5ed2fa4862974595325494f1d72594f4257ce84', 'pdb_sha256': '0f1404f22b13ee21d0bc2bba655429157eff25c9741affc351ba5dfc7f2bd66b'}}, 'selected': {'name': 'Coverage', 'baseline': {'dll': '/private/tmp/issue779-option2-recovery-j0gez4kc/product-source/Evidence/ForgeTrust.AppSurface.Evidence.Coverage/bin/Debug/net10.0/ForgeTrust.AppSurface.Evidence.Coverage.dll', 'pdb': '/private/tmp/issue779-option2-recovery-j0gez4kc/product-source/Evidence/ForgeTrust.AppSurface.Evidence.Coverage/bin/Debug/net10.0/ForgeTrust.AppSurface.Evidence.Coverage.pdb', 'dll_sha256': '83cb8c42defb01785f41b59ea5ed2fa4862974595325494f1d72594f4257ce84', 'pdb_sha256': '0f1404f22b13ee21d0bc2bba655429157eff25c9741affc351ba5dfc7f2bd66b'}, 'candidate': {'dll': '/private/tmp/issue779-option2-recovery-j0gez4kc/product-source/Evidence/ForgeTrust.AppSurface.Evidence.Coverage/bin/Debug/net10.0/ForgeTrust.AppSurface.Evidence.Coverage.dll', 'pdb': '/private/tmp/issue779-option2-recovery-j0gez4kc/product-source/Evidence/ForgeTrust.AppSurface.Evidence.Coverage/bin/Debug/net10.0/ForgeTrust.AppSurface.Evidence.Coverage.pdb', 'dll_sha256': '83cb8c42defb01785f41b59ea5ed2fa4862974595325494f1d72594f4257ce84', 'pdb_sha256': '0f1404f22b13ee21d0bc2bba655429157eff25c9741affc351ba5dfc7f2bd66b'}}}

    def fixture(self):
        raw = product.json.dumps(self.CAPTURED_PROJECTION, sort_keys=True, separators=(",", ":")).encode()
        self.assertEqual(self.CAPTURED_PROJECTION_SHA256, product.sha(raw))
        return product.unique_json(raw)

    def test_actual_compact_exclusive_flag_null_and_unknown_field_rejections(self):
        fixture = self.fixture()
        logical = product.logical_inventory_pair(fixture["pair"], fixture["selected"])
        self.assertIs(logical["candidate"], fixture["pair"]["baseline"])
        self.assertIs(logical["baseline"], logical["candidate"])
        self.assertIsNone(logical["common"])
        # Full-candidate representation is a valid DATA-only neighbor, not a
        # native report or byte-proof assertion: both projected images are exact.
        full = self.fixture()
        full["pair"]["candidate_is_baseline"] = False
        full["pair"]["candidate"] = dict(full["pair"]["baseline"])
        logical = product.logical_inventory_pair(full["pair"], full["selected"])
        self.assertIs(logical["candidate"], full["pair"]["candidate"])
        self.assertIsNot(logical["candidate"], logical["baseline"])
        for shape in ("flag-null", "flag-int", "flag-string", "extra-candidate", "false-missing", "unknown-field", "common-not-null"):
            changed = self.fixture()
            pair = changed["pair"]
            if shape == "flag-null": pair["candidate_is_baseline"] = None
            if shape == "flag-int": pair["candidate_is_baseline"] = 1
            if shape == "flag-string": pair["candidate_is_baseline"] = "true"
            if shape == "extra-candidate": pair["candidate"] = dict(pair["baseline"])
            if shape == "false-missing": pair["candidate_is_baseline"] = False
            if shape == "unknown-field": pair["unrecognized"] = "private-data-canary"
            if shape == "common-not-null": pair["common"] = {}
            with self.subTest(shape=shape), self.assertRaises(ValueError):
                product.logical_inventory_pair(pair, changed["selected"])

    def test_actual_compact_all_path_hash_and_byte_equality_pins_required(self):
        fixture = self.fixture()
        product.logical_inventory_pair(fixture["pair"], fixture["selected"])
        for field in ("dll_path", "pdb_path", "dll_sha256", "pdb_sha256"):
            changed = self.fixture()
            changed["pair"]["baseline"][field] += "-negative-data"
            with self.subTest(baseline=field), self.assertRaises(ValueError):
                product.logical_inventory_pair(changed["pair"], changed["selected"])
        for field in ("dll", "pdb", "dll_sha256", "pdb_sha256"):
            changed = self.fixture()
            changed["selected"]["candidate"][field] += "-negative-data"
            with self.subTest(candidate_pin=field), self.assertRaises(ValueError):
                product.logical_inventory_pair(changed["pair"], changed["selected"])
        for field in ("dll_bytes_equal", "pdb_bytes_equal"):
            changed = self.fixture()
            changed["pair"][field] = False
            with self.subTest(equality=field), self.assertRaises(ValueError):
                product.logical_inventory_pair(changed["pair"], changed["selected"])


class LocalGenericOwnerDataControls(unittest.TestCase):
    """Exact captured local definition data; no accepted ABI or runtime claim."""
    SOURCE_REPORT_SHA256 = 'c92539a814b6ee9d5a6d89e5acce0da0f673cbc8607dc14c794bae376f19ccfe'
    CAPTURED_SHA256 = 'b5a4f4ff6779b4d17ee7b090d829b7dd39b4a7d5580d874a739cdbcdb3829072'
    CAPTURED = {'reference': {'token': 167772830, 'kind': 'method', 'type': 'kind:18:[ForgeTrust.AppSurface.Evidence.Cli]<>f__AnonymousType2`7<kind:17:[System.Runtime]System.Nullable`1<kind:17:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceExecutionMode>,kind:17:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceClaimKind,kind:17:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceClaimEligibility,kind:17:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceExecutionVerdict,kind:17:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidenceEnvelopeStatus,primitive:String,primitive:Boolean>', 'parent_kind': 'TypeSpecification', 'name': '.ctor', 'signature': 'header:32;generic:0;required:7;return:primitive:Void;parameters:(!0,!1,!2,!3,!4,!5,!6)'}, 'name': '[ForgeTrust.AppSurface.Evidence.Cli]<>f__AnonymousType2`7', 'assembly': {'name': 'ForgeTrust.AppSurface.Evidence.Cli', 'version': '0.2.0.0', 'culture': '', 'public_key_or_token': '', 'flags': 0}, 'assembly_references': [{'name': 'System.Runtime', 'version': '10.0.0.0', 'culture': '', 'public_key_or_token': 'b03f5f7f11d50a3a', 'flags': 0}, {'name': 'ForgeTrust.AppSurface.Evidence.Coverage', 'version': '0.2.0.0', 'culture': '', 'public_key_or_token': '', 'flags': 0}, {'name': 'ForgeTrust.AppSurface.Evidence.Contracts', 'version': '0.2.0.0', 'culture': '', 'public_key_or_token': '', 'flags': 0}, {'name': 'ForgeTrust.AppSurface.Evidence.Planner', 'version': '0.2.0.0', 'culture': '', 'public_key_or_token': '', 'flags': 0}, {'name': 'System.Collections', 'version': '10.0.0.0', 'culture': '', 'public_key_or_token': 'b03f5f7f11d50a3a', 'flags': 0}, {'name': 'Microsoft.Win32.Primitives', 'version': '10.0.0.0', 'culture': '', 'public_key_or_token': 'b03f5f7f11d50a3a', 'flags': 0}, {'name': 'System.Linq', 'version': '10.0.0.0', 'culture': '', 'public_key_or_token': 'b03f5f7f11d50a3a', 'flags': 0}, {'name': 'System.Text.Encoding.Extensions', 'version': '10.0.0.0', 'culture': '', 'public_key_or_token': 'b03f5f7f11d50a3a', 'flags': 0}, {'name': 'System.Memory', 'version': '10.0.0.0', 'culture': '', 'public_key_or_token': 'cc7b13ffcd2ddd51', 'flags': 0}, {'name': 'System.Text.Json', 'version': '10.0.0.0', 'culture': '', 'public_key_or_token': 'cc7b13ffcd2ddd51', 'flags': 0}, {'name': 'System.Security.Cryptography', 'version': '10.0.0.0', 'culture': '', 'public_key_or_token': 'b03f5f7f11d50a3a', 'flags': 0}], 'dll_sha256': 'ec2cab70105de7cef1717c7cc22a014818406e22d48279f4e964c5d3406c6a12', 'pdb_sha256': '8d0fb7c8f082eac8785a02d91a75440c2b16e1743faee839940798bfce5a143d', 'types': {'[ForgeTrust.AppSurface.Evidence.Cli]<>f__AnonymousType2`7': {'token': 33554436, 'name': '[ForgeTrust.AppSurface.Evidence.Cli]<>f__AnonymousType2`7', 'attributes': 1048832, 'access': 'NotPublic', 'base_type': '[System.Runtime]System.Object', 'interfaces': [], 'generic_parameters': [{'position': 0, 'name': '<Mode>j__TPar', 'attributes': 0, 'constraints': []}, {'position': 1, 'name': '<ClaimKind>j__TPar', 'attributes': 0, 'constraints': []}, {'position': 2, 'name': '<Eligibility>j__TPar', 'attributes': 0, 'constraints': []}, {'position': 3, 'name': '<ExecutionVerdict>j__TPar', 'attributes': 0, 'constraints': []}, {'position': 4, 'name': '<EnvelopeStatus>j__TPar', 'attributes': 0, 'constraints': []}, {'position': 5, 'name': '<Procedure>j__TPar', 'attributes': 0, 'constraints': []}, {'position': 6, 'name': '<SandboxAttestation>j__TPar', 'attributes': 0, 'constraints': []}]}}, 'common': {'[ForgeTrust.AppSurface.Evidence.Cli]<>f__AnonymousType2`7': {'type': '[ForgeTrust.AppSurface.Evidence.Cli]<>f__AnonymousType2`7', 'attributes_equal': True, 'base_type_equal': True, 'interfaces_equal': True, 'generic_parameters_equal': True, 'generic_parameter_names_equal': True, 'baseline_access': 'NotPublic', 'candidate_access': 'NotPublic'}}}

    def fixture(self):
        raw = product.json.dumps(self.CAPTURED, sort_keys=True, separators=(",", ":")).encode()
        self.assertEqual(self.CAPTURED_SHA256, product.sha(raw))
        return product.unique_json(raw)

    def audit(self, data):
        image = {key: data[key] for key in ("assembly", "assembly_references", "dll_sha256", "pdb_sha256")}
        image["member_references"] = [data["reference"]]
        images, indexes, names = {}, {}, {}
        common = {}
        for caller in product.LIBRARIES:
            for side in ("baseline", "candidate"):
                images[caller, side] = image if caller == "Cli" else {"member_references": []}
                indexes[caller, side] = data["types"] if caller == "Cli" else {}
                names[caller, side] = {}
            common[caller] = data["common"] if caller == "Cli" else {}
        return product.observe_framework_wrappers(images, names,
            product.ReconciliationBudget(time.monotonic()+5), type_indexes=indexes, common_types=common)

    def test_actual_generated_name_and_local_definition_reject_missing_or_changed_common(self):
        data = self.fixture()
        owner, encoded = product.declaring_owner(data["reference"])
        self.assertEqual(data["assembly"]["name"], owner)
        self.assertEqual(data["name"], product.resolve_generic_owner(encoded, data["types"],
            product.ReconciliationBudget(time.monotonic()+5)))
        observations = self.audit(data)
        self.assertEqual(2, len(observations))
        self.assertEqual({"baseline", "candidate"}, {row["side"] for row in observations})
        for row in observations:
            self.assertEqual("local-generic-definition-observation", row["origin"])
            self.assertEqual(data["reference"], row["member_reference"])
            self.assertNotIn("baseline_matches", row)
        for shape in ("missing-definition", "missing-common", "changed-common"):
            changed = self.fixture()
            if shape == "missing-definition": del changed["types"][changed["name"]]
            if shape == "missing-common": del changed["common"][changed["name"]]
            if shape == "changed-common": changed["common"][changed["name"]]["interfaces_equal"] = False
            with self.subTest(shape=shape), self.assertRaises(ValueError): self.audit(changed)

    def test_observed_nested_framework_name_and_owner_substitution_reject(self):
        # This exact nested TypeRef prefix was emitted in the same Cli report.
        name = "[System.Runtime]System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter"
        encoded = name+"<kind:18:[ForgeTrust.AppSurface.Evidence.Contracts]ForgeTrust.AppSurface.Evidence.Contracts.EvidencePlan>"
        self.assertEqual(name, product.resolve_generic_owner(encoded, {name: None},
            product.ReconciliationBudget(time.monotonic()+5)))
        with self.assertRaises(ValueError):
            product.resolve_generic_owner(encoded, {}, product.ReconciliationBudget(time.monotonic()+5))
        changed = self.fixture()
        changed["reference"]["type"] = changed["reference"]["type"].replace(
            "[ForgeTrust.AppSurface.Evidence.Cli]", "[unknown-owner]", 1)
        with self.assertRaises(ValueError): self.audit(changed)


class ContractsPartitionDataControls(unittest.TestCase):
    """Captured pin projections/order guards only; no accepted ABI or lease."""
    SOURCE_INVENTORY_SHA256 = '0b2276b8c3197270b1b1055f774298a60862e6aa7e81cb5e4431af6ac698990d'
    CAPTURED_SHA256 = '76c6b3d7425dab3eec4b08db5298ca982354aae74b7e3657e4acfbad5a77a963'
    CAPTURED = {'pair': {'name': 'Contracts', 'dll_bytes_equal': False, 'pdb_bytes_equal': False, 'candidate_is_baseline': False, 'common': None, 'baseline': {'dll_path': '/private/tmp/issue779-option2-recovery-j0gez4kc/product-source/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/bin/Debug/net10.0/ForgeTrust.AppSurface.Evidence.Contracts.dll', 'pdb_path': '/private/tmp/issue779-option2-recovery-j0gez4kc/product-source/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/bin/Debug/net10.0/ForgeTrust.AppSurface.Evidence.Contracts.pdb', 'dll_sha256': '35b1c81150450ccb571a609fc9730141a250c5a51e76e2b4de31df748d999fae', 'pdb_sha256': '56e6c87021f8d5a5b5e29248c2879e511f2f601911a922b6c9b3b211252bd407'}, 'candidate': {'dll_path': '/private/tmp/issue779-mixed-abi-pektk238/repo/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/bin/Debug/net10.0/ForgeTrust.AppSurface.Evidence.Contracts.dll', 'pdb_path': '/private/tmp/issue779-mixed-abi-pektk238/repo/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/bin/Debug/net10.0/ForgeTrust.AppSurface.Evidence.Contracts.pdb', 'dll_sha256': 'f276333024d213969808908e58957bc4bbe0dfb97c66b098a212fad22b335ecb', 'pdb_sha256': '8f4605e44285715d871f58d3a7bed126b7f44bf71a48837741495e69ddea5cec'}}, 'selected': {'baseline': {'dll': '/private/tmp/issue779-option2-recovery-j0gez4kc/product-source/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/bin/Debug/net10.0/ForgeTrust.AppSurface.Evidence.Contracts.dll', 'dll_sha256': '35b1c81150450ccb571a609fc9730141a250c5a51e76e2b4de31df748d999fae', 'pdb': '/private/tmp/issue779-option2-recovery-j0gez4kc/product-source/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/bin/Debug/net10.0/ForgeTrust.AppSurface.Evidence.Contracts.pdb', 'pdb_sha256': '56e6c87021f8d5a5b5e29248c2879e511f2f601911a922b6c9b3b211252bd407'}, 'candidate': {'dll': '/private/tmp/issue779-mixed-abi-pektk238/repo/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/bin/Debug/net10.0/ForgeTrust.AppSurface.Evidence.Contracts.dll', 'dll_sha256': 'f276333024d213969808908e58957bc4bbe0dfb97c66b098a212fad22b335ecb', 'pdb': '/private/tmp/issue779-mixed-abi-pektk238/repo/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/bin/Debug/net10.0/ForgeTrust.AppSurface.Evidence.Contracts.pdb', 'pdb_sha256': '8f4605e44285715d871f58d3a7bed126b7f44bf71a48837741495e69ddea5cec'}, 'name': 'Contracts'}}

    def fixture(self):
        raw = product.json.dumps(self.CAPTURED, sort_keys=True, separators=(",", ":")).encode()
        self.assertEqual(self.CAPTURED_SHA256, product.sha(raw))
        return product.unique_json(raw)

    def test_actual_unshared_contracts_keeps_both_images_and_rejects_aliases(self):
        data = self.fixture()
        logical = product.logical_inventory_pair(data["pair"], data["selected"])
        self.assertIs(logical["baseline"], data["pair"]["baseline"])
        self.assertIs(logical["candidate"], data["pair"]["candidate"])
        self.assertIsNot(logical["baseline"], logical["candidate"])
        self.assertIsNone(logical["common"])
        # This is deliberately a pin projection, not a complete image/ABI report.
        with self.assertRaises(ValueError):
            product.validate_image_shape(logical["candidate"], product.ReconciliationBudget(time.monotonic()+5))
        for mutation in ("shared", "missing", "unknown-name", "candidate-path", "candidate-hash"):
            changed = self.fixture()
            pair, selected = changed["pair"], changed["selected"]
            if mutation == "shared": pair["candidate_is_baseline"] = True; pair["candidate"] = None
            if mutation == "missing": pair["candidate"] = None
            if mutation == "unknown-name": pair["name"] = selected["name"] = "Planner"
            if mutation == "candidate-path": pair["candidate"]["dll_path"] = pair["baseline"]["dll_path"]
            if mutation == "candidate-hash": pair["candidate"]["pdb_sha256"] = pair["baseline"]["pdb_sha256"]
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                product.logical_inventory_pair(pair, selected)

    def test_exact_seven_stage_order_precedes_json_consumption(self):
        requested = [{"name": name} for name in ("Contracts", "Planner", "Cli", "Aspire", "Coverage")]
        records = [{"name": name, "input_sha256": "0"*64,
                    "report_sha256": product.sha(b"[]"), "raw": b"[]"} for name in product.PARTITION_STAGES]
        class ParseReached(Exception): pass
        with patch.object(product, "unique_json", side_effect=ParseReached) as parsed:
            # The valid ordering reaches parsing; it never returns an accepted report.
            with self.assertRaises(ParseReached):
                product.validate_partition_envelopes(records, requested, "0"*64, 1, time.monotonic()+5)
            parsed.assert_called_once()
        invalid = [records[:-1], records+[records[-1]], records[1:]+records[:1],
                   [records[0], records[0]]+records[2:]]
        for changed in invalid:
            with self.subTest(names=[row["name"] for row in changed]), patch.object(product, "unique_json") as parsed:
                with self.assertRaises(ValueError):
                    product.validate_partition_envelopes(changed, requested, "0"*64, 1, time.monotonic()+5)
                parsed.assert_not_called()


if __name__ == "__main__":
    unittest.main()
