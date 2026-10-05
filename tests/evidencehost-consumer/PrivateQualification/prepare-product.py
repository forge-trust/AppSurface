"""Root-selected product build data; no enrollment, worker grant or compatibility authority.

The parent authenticates the SDK and selects the private inspector before these
functions run. New sources and controls have not executed merely by existing.
"""
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import stat
import sys
import time
import zipfile

PRODUCT_COMMIT = "171f53911c7c1fec08bf0676b321539567f97c5d"
LIBRARIES = ("Cli", "Aspire", "Coverage")
PARTITION_STAGES = ("Contracts.Inventory", "Contracts.Common", "Planner", "Cli", "Aspire", "Coverage.Inventory", "Coverage.Common")
METADATA_MODE = "three-pairs-contracts-coverage-inventory-common"
PACKAGE_URL = "https://api.nuget.org/v3-flatcontainer/coverlet.msbuild/10.0.1/coverlet.msbuild.10.0.1.nupkg"
PACKAGE_SHA256 = "a7d412ffc03d14f4d884788009f569c7df00346ac5662a3a93ccaa855656bc77"
MAXIMUM_FILE = 32*1024*1024


def require(value):
    if not value:
        raise ValueError("private-product-preparation-rejected")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def remaining(deadline):
    require(time.monotonic() < deadline)


def identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_gid,
            info.st_nlink, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def read_regular(path, maximum, *, deadline=None, expected_owner_uid=0):
    """Return bounded selected file data only, after actual descriptor closure.

    Production uses owner UID 0. The optional UID is solely a portable file-data
    control seam; it performs no build, root action, admission or enrollment.
    Reads/close failures propagate without printing their exception messages.
    """
    require(type(maximum) is int and 0 < maximum <= MAXIMUM_FILE
            and type(expected_owner_uid) is int and expected_owner_uid >= 0)
    check_time = lambda: require(deadline is None or time.monotonic() < deadline)
    path = Path(path)
    check_time()
    selected = path.lstat()
    require(stat.S_ISREG(selected.st_mode) and selected.st_nlink == 1
            and selected.st_uid == expected_owner_uid and 0 <= selected.st_size <= maximum)
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    error, result = None, None
    try:
        before = os.fstat(fd)
        require(identity(selected) == identity(before))
        data = bytearray()
        while len(data) < before.st_size:
            check_time()
            part = os.read(fd, min(65536, before.st_size-len(data)))
            require(bool(part) and len(data)+len(part) <= before.st_size)
            data.extend(part)
        check_time()
        require(os.read(fd, 1) == b"" and len(data) == before.st_size
                and identity(before) == identity(os.fstat(fd))
                == identity(path.stat(follow_symlinks=False)))
        result = bytes(data)
    except BaseException as failure:
        error = failure
    finally:
        try:
            os.close(fd)
        except BaseException as failure:
            if error is None:
                error = failure
    if error is not None:
        raise error
    check_time()
    return result


def unique_json(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result)
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=pairs)


def relative_name(name):
    require(type(name) is str and 0 < len(name.encode()) <= 4096 and "\\" not in name
            and "\0" not in name and not name.startswith("/")
            and all(part not in ("", ".", "..") for part in name.split("/")))
    return name


def no_link_parents(path, stop):
    for parent in path.parents:
        require(not parent.is_symlink() and parent.is_dir())
        if parent == stop:
            return
    require(False)


def bounded_tree(root, maximum, deadline):
    """Enumerate no-follow regular files/directories before bounded sorting.

    Charges each entry before adding it to the retained list or directory stack.
    All enumeration descriptors close before the final deadline/publication check.
    This reads file-data topology only; it grants no root or worker authority.
    """
    require(type(maximum) is int and 0 < maximum <= 10000)
    root = Path(root)
    nodes, pending = [], [root]
    while pending:
        remaining(deadline)
        directory = pending.pop()
        selected = directory.lstat()
        require(stat.S_ISDIR(selected.st_mode))
        fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        try:
            require(identity(selected) == identity(os.fstat(fd)))
            with os.scandir(fd) as entries:
                for entry in entries:
                    remaining(deadline)
                    require(len(nodes) < maximum)
                    info = entry.stat(follow_symlinks=False)
                    require(stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode))
                    path = directory / entry.name
                    relative_name(path.relative_to(root).as_posix())
                    nodes.append(path)
                    if stat.S_ISDIR(info.st_mode):
                        pending.append(path)
            require(identity(selected) == identity(os.fstat(fd))
                    == identity(directory.stat(follow_symlinks=False)))
        finally:
            os.close(fd)
    remaining(deadline)
    result = sorted(nodes)
    remaining(deadline)
    return result


def validate_product_source(productroot, manifest, runner):
    """Verify the fixed pristine checkout and all 2814 SHA/mode rows before archive/build.

    ``manifest`` is the parent's fixed {commit, files} dictionary, or its selected
    root-owned JSON path (1 MiB maximum). It is never supplied by a subject.
    """
    require(os.geteuid() == 0)
    productroot = Path(productroot)
    require(productroot.is_absolute() and productroot.is_dir() and not productroot.is_symlink())
    if type(manifest) is not dict:
        manifest = unique_json(read_regular(manifest, 1024*1024, deadline=runner.deadline))
    require(set(manifest) == {"commit", "files"} and manifest["commit"] == PRODUCT_COMMIT
            and type(manifest["files"]) is dict and len(manifest["files"]) == 2814)
    for name, row in manifest["files"].items():
        relative_name(name)
        require(type(row) is dict and set(row) == {"sha256", "mode"}
                and row["mode"] in ("0644", "0755") and type(row["sha256"]) is str
                and len(row["sha256"]) == 64 and all(c in "0123456789abcdef" for c in row["sha256"]))
    require(runner.run(["git", "rev-parse", "HEAD"], productroot, capture=True).decode().strip() == PRODUCT_COMMIT)
    require(runner.run(["git", "status", "--porcelain"], productroot, capture=True) == b"")
    names = runner.run(["git", "ls-files", "-z"], productroot, capture=True).decode().split("\0")
    require(names[-1] == "" and len(names[:-1]) == 2814 and set(names[:-1]) == set(manifest["files"]))
    for name, expected in manifest["files"].items():
        path = productroot / name
        no_link_parents(path, productroot)
        info = path.lstat()
        require(format(stat.S_IMODE(info.st_mode), "04o") == expected["mode"])
        data = read_regular(path, MAXIMUM_FILE, deadline=runner.deadline)
        require(sha(data) == expected["sha256"])
    remaining(runner.deadline)
    return manifest


def build_product_inputs(source, workspace, runner, dotnet, productroot, manifest):
    """Build only the three selected pristine libraries using locked dependencies.

    ``source`` is the parent's private source root, retained as provenance only.
    ``productroot`` is the fixed pristine checkout; ``workspace/product-build``
    must be fresh. The parent supplies an already-authenticated SDK host and
    cumulative process owner. No subject or taskhost is launched here.

    Returns {source_commit, source_files, build_root, private_source_root,
    candidates, baselines}, where candidates has exactly Cli/Aspire/Coverage,
    baselines also has pristine Contracts/Planner, each containing
    dll/pdb {path, sha256}. Generated PDB documents are emitted under each project
    obj/Debug/net10.0 and receive no checked-in product-source credit.
    """
    manifest = validate_product_source(productroot, manifest, runner)
    source, workspace, dotnet, productroot = map(Path, (source, workspace, dotnet, productroot))
    require(source.is_absolute() and workspace.is_absolute() and dotnet.is_absolute())
    destination, archive = workspace / "product-build", workspace / "product-source.tar"
    require(not destination.exists() and not destination.is_symlink()
            and not archive.exists() and not archive.is_symlink())
    destination.mkdir(mode=0o700)
    runner.run(["git", "archive", "--format=tar", "--output="+str(archive), PRODUCT_COMMIT], productroot)
    runner.run(["tar", "-xf", str(archive), "-C", str(destination)], productroot)
    archive.unlink()
    # Only the authenticated commit was archived. Verify its copied bytes before
    # MSBuild; Git archive's group-write metadata is not an accepted source mode.
    copied = set()
    for path in bounded_tree(destination, 10000, runner.deadline):
        remaining(runner.deadline)
        require(not path.is_symlink())
        if path.is_dir():
            require(path.stat().st_uid == 0)
            continue
        name = path.relative_to(destination).as_posix()
        require(name in manifest["files"])
        data = read_regular(path, MAXIMUM_FILE, deadline=runner.deadline)
        require(sha(data) == manifest["files"][name]["sha256"])
        os.chmod(path, int(manifest["files"][name]["mode"], 8))
        copied.add(name)
    require(copied == set(manifest["files"]))
    for name in LIBRARIES:
        project = destination / f"Evidence/ForgeTrust.AppSurface.Evidence.{name}/ForgeTrust.AppSurface.Evidence.{name}.csproj"
        runner.run([str(dotnet), "restore", str(project), "--locked-mode"], destination)
        runner.run([str(dotnet), "build", str(project), "--no-restore", "-p:UseSharedCompilation=false",
                    "-p:EmitCompilerGeneratedFiles=true", "-p:CompilerGeneratedFilesOutputPath=obj/Debug/net10.0"], destination)
    baselines = {}
    for name in ("Contracts", "Planner")+LIBRARIES:
        stem = f"ForgeTrust.AppSurface.Evidence.{name}"
        directory = destination / f"Evidence/{stem}/bin/Debug/net10.0"
        baselines[name] = {}
        for extension in ("dll", "pdb"):
            path = directory / (stem+"."+extension)
            data = read_regular(path, (32 if extension == "dll" else 16)*1024*1024, deadline=runner.deadline)
            baselines[name][extension] = {"path": str(path), "sha256": sha(data)}
    candidates = {name: baselines[name] for name in LIBRARIES}
    remaining(runner.deadline)
    return {"source_commit": PRODUCT_COMMIT, "source_files": manifest["files"],
            "build_root": str(destination), "private_source_root": str(source),
            "candidates": candidates, "baselines": baselines}


def prepare_coverlet_package(workspace, runner):
    """Authenticate fixed Coverlet bytes before ZIP parsing/extraction; never run tasks.

    Returns (package_root, receipt). Receipt also names tasks_directory separately.
    The frozen host currently expects PackagePath=package_root because its hint
    paths append tasks/net10.0; passing tasks_directory would duplicate the suffix.
    """
    require(os.geteuid() == 0)
    workspace = Path(workspace)
    raw = runner.run(["curl", "--fail", "--silent", "--show-error", "--location", "--max-time", "30",
                      "--max-filesize", "8388608", PACKAGE_URL], workspace,
                     capture=True, capture_limit=8*1024*1024)
    require(len(raw) <= 8*1024*1024 and sha(raw) == PACKAGE_SHA256)
    remaining(runner.deadline)
    destination = workspace / "coverlet-public-package"
    destination.mkdir(mode=0o700)
    rows, total = [], 0
    with zipfile.ZipFile(io.BytesIO(raw)) as archive:
        items = archive.infolist()
        require(len(items) <= 256 and len({item.filename for item in items}) == len(items))
        for item in items:
            remaining(runner.deadline)
            relative_name(item.filename.rstrip("/") if item.is_dir() else item.filename)
            require(not item.flag_bits & 1)
            if not item.filename.startswith("tasks/net10.0/") or item.is_dir():
                continue
            parts = PurePosixPath(item.filename).parts
            require(3 <= len(parts) <= 8 and stat.S_IFMT(item.external_attr >> 16) in (0, stat.S_IFREG)
                    and 0 <= item.file_size <= 16*1024*1024 and total+item.file_size <= 32*1024*1024)
            path, parent = destination / item.filename, destination
            for part in parts[:-1]:
                parent = parent / part
                parent.mkdir(mode=0o700, exist_ok=True)
                info = parent.lstat()
                require(stat.S_ISDIR(info.st_mode) and info.st_uid == 0 and stat.S_IMODE(info.st_mode) == 0o700)
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
            digest, written = hashlib.sha256(), 0
            with os.fdopen(fd, "wb") as output:
                with archive.open(item) as input_stream:
                    while True:
                        remaining(runner.deadline)
                        block = input_stream.read(65536)
                        if not block:
                            break
                        require(written+len(block) <= item.file_size)
                        output.write(block)
                        digest.update(block)
                        written += len(block)
                require(written == item.file_size)
            total += written
            rows.append({"path": item.filename, "bytes": written, "sha256": digest.hexdigest()})
    require((destination / "tasks/net10.0/coverlet.msbuild.tasks.dll").is_file())
    remaining(runner.deadline)
    return destination, {"package_sha256": PACKAGE_SHA256, "compressed_bytes": len(raw), "tasks": rows,
                         "tasks_directory": str(destination / "tasks/net10.0"), "package_root": str(destination)}


def build_product_taskhost(source, workspace, runner, dotnet, package_root):
    """Compile/deploy the fixed official host under workspace/product-coverage-taskhost.

    ``source`` is the parent's authenticated private build copy. Uses its existing
    locked project, the authenticated package root, and the selected SDK; no
    worker, subject, instrumentation or host subprocess is executed here.
    The returned file map is {relative_path: sha256}, matching launcher data shape.
    Runtime resource completeness still requires the parent's actual native run.
    """
    require(os.geteuid() == 0)
    source, workspace, dotnet, package_root = map(Path, (source, workspace, dotnet, package_root))
    require(all(p.is_absolute() for p in (source, workspace, dotnet, package_root)))
    project = source / "tests/evidencehost-consumer/PrivateProductCoverageTaskHost/PrivateProductCoverageTaskHost.csproj"
    property_argument = "-p:PackagePath="+str(package_root)
    runner.run([str(dotnet), "restore", str(project), "--locked-mode", property_argument], source)
    runner.run([str(dotnet), "build", str(project), "--no-restore", "-p:UseSharedCompilation=false",
                property_argument], source)
    compiled = project.parent / "bin/Debug/net10.0"
    require(all((compiled / name).is_file() for name in (
        "OfficialTaskHost.dll", "OfficialTaskHost.runtimeconfig.json", "OfficialTaskHost.deps.json")))
    destination = workspace / "product-coverage-taskhost"
    require(not destination.exists() and not destination.is_symlink())
    destination.mkdir(mode=0o700)
    file_map, total, directories = {}, 0, []
    for path in bounded_tree(compiled, 256, runner.deadline):
        remaining(runner.deadline)
        info = path.lstat()
        require(info.st_uid == 0 and not path.is_symlink())
        relative = path.relative_to(compiled).as_posix()
        relative_name(relative)
        target = destination / relative
        if stat.S_ISDIR(info.st_mode):
            target.mkdir(mode=0o700)
            directories.append(target)
            continue
        data = read_regular(path, MAXIMUM_FILE, deadline=runner.deadline)
        require(total+len(data) <= 128*1024*1024)
        total += len(data)
        fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o444)
        with os.fdopen(fd, "wb") as stream:
            stream.write(data)
        file_map[relative] = sha(data)
    for path in reversed(directories):
        remaining(runner.deadline)
        os.chmod(path, 0o555)
    os.chmod(destination, 0o700)
    remaining(runner.deadline)
    return {"path": str(destination), "assembly": "OfficialTaskHost.dll", "sha256": file_map,
            "package_root": str(package_root), "runtime_compatibility_claim": False,
            "native_execution": False}


def assembly_identity(name):
    """Fixed emitted unsigned product identity, not a loader compatibility claim."""
    return {"name": "ForgeTrust.AppSurface.Evidence."+name, "version": "0.2.0.0",
            "culture": "", "public_key_or_token": "", "flags": 0}


def validate_generic_parameters(rows, *, budget=None):
    """Require K's actual generic row schema; do not infer substituted TypeSpecs."""
    require(type(rows) is list and len(rows) <= 4096)
    for position, row in enumerate(rows):
        if budget is not None:
            budget.row()
        require(set(row) == {"position", "name", "attributes", "constraints"}
                and type(row["position"]) is int and row["position"] == position
                and type(row["name"]) is str and len(row["name"]) <= 4096
                and type(row["attributes"]) is int and 0 <= row["attributes"] <= 31
                and type(row["constraints"]) is list and len(row["constraints"]) <= 4096
                and all(type(value) is str and len(value) <= 4096 for value in row["constraints"]))
        for value in row["constraints"]:
            if budget is not None:
                budget.row()
            canonical_text(value)


def metadata_access(image, member, caller, *, type_index=None, friend_names=None, budget=None):
    """Classify direct assembly/public access from actual definitions and IVT.

    Family-only, private, generic substitution and other unresolved access reject.
    FamORAssem is accepted only through its independently verified assembly arm.
    No inheritance, effective runtime dispatch or synthetic friend is inferred.
    The fixed five images are unsigned; qualified/strong-name IVT is unsupported.
    """
    expected_caller = caller["assembly"]["name"]
    friend = expected_caller in friend_names if friend_names is not None else False
    for row in image["internals_visible_to"] if friend_names is None else []:
        require(row["attribute_type"].endswith("System.Runtime.CompilerServices.InternalsVisibleToAttribute"))
        if row["name"] == expected_caller:
            friend = True
    def visible(access):
        return visibility_classification(access, friend)
    classifications = [visible(member["access"])]
    types = type_index if type_index is not None else {row["name"]: row for row in image["types"]}
    require(len(types) == len(image["types"]))
    name = member["type"]
    while True:
        if budget is not None:
            budget.row()
        require(name in types)
        classifications.append(visible(types[name]["access"]))
        if "+" not in name:
            break
        name = name.rsplit("+", 1)[0]
    return classifications


class _ReconciliationBytes:
    """One byte ledger and original deadline shared by both work phases."""
    def __init__(self, deadline, report_bytes, maximum_bytes):
        self.deadline = min(deadline, time.monotonic()+30)
        self.maximum_bytes, self.bytes = maximum_bytes, report_bytes


class ReconciliationBudget:
    """Separate Common and definition/dependency work; shared bytes/deadline.

    Each work counter is at most100K. Creating the one Common phase never
    resets a counter, creates another byte allowance or renews the deadline.
    """
    def __init__(self, deadline, report_bytes=0, *, maximum_work=100000,
                 maximum_bytes=MAXIMUM_FILE, _ledger=None, _phase="definition-dependency"):
        require(type(maximum_work) is int and 0 < maximum_work <= 100000
                and type(maximum_bytes) is int and 0 < maximum_bytes <= MAXIMUM_FILE
                and type(report_bytes) is int and 0 <= report_bytes <= maximum_bytes)
        require(_phase in ("definition-dependency", "common-completeness"))
        if _ledger is None:
            require(_phase == "definition-dependency")
            _ledger = _ReconciliationBytes(deadline, report_bytes, maximum_bytes)
        else:
            require(type(_ledger) is _ReconciliationBytes and report_bytes == 0
                    and deadline == _ledger.deadline and maximum_bytes == _ledger.maximum_bytes
                    and _phase == "common-completeness")
        self._ledger, self._phase, self._common = _ledger, _phase, None
        self.deadline, self.maximum_bytes = _ledger.deadline, _ledger.maximum_bytes
        self.maximum_work, self.work = maximum_work, 0
        self.check()

    @property
    def bytes(self):
        return self._ledger.bytes

    def common_verification_phase(self):
        """Create exactly one Common counter with the retained shared ledger."""
        self.check()
        require(self._phase == "definition-dependency" and self._common is None)
        self._common = ReconciliationBudget(self.deadline, maximum_work=self.maximum_work,
            maximum_bytes=self.maximum_bytes, _ledger=self._ledger, _phase="common-completeness")
        return self._common

    def work_counts(self):
        self.check()
        require(self._phase == "definition-dependency")
        common_work = 0 if self._common is None else self._common.work
        require(0 <= self.work <= self.maximum_work and 0 <= common_work <= self.maximum_work
                and self.work+common_work <= 200000)
        return {"common_verification_work": common_work, "definition_dependency_work": self.work,
                "total_python_verification_work": self.work+common_work}

    def check(self):
        remaining(self._ledger.deadline)

    def row(self):
        self.check()
        require(self.work < self.maximum_work)
        self.work += 1

    def reserve(self, count):
        self.check()
        require(type(count) is int and 0 <= count <= self.maximum_bytes-self.bytes)
        self._ledger.bytes += count

    def store(self, value):
        # All Common and definition/index records reserve from this same ledger.
        raw = json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
        self.reserve(len(raw))
        self.check()


def canonical_text(value):
    require(type(value) is str and len(value) <= 32768
            and "unresolved-" not in value and "unresolved:" not in value)


def validate_image_shape(image, budget):
    require(type(image) is dict and set(image) == {"dll_path", "pdb_path", "dll_sha256", "pdb_sha256",
            "assembly", "mvid", "assembly_references", "type_references", "types", "members", "member_references",
            "internals_visible_to", "exported_types", "debug_directory", "pdb"})
    for field in ("assembly_references", "type_references", "types", "members", "member_references",
                  "internals_visible_to", "exported_types", "debug_directory"):
        require(type(image[field]) is list and len(image[field]) <= 100000)
    require(set(image["pdb"]) == {"id", "guid", "stamp", "code_view_count", "matching_code_view_count",
            "one_matching_code_view", "checksums", "documents", "sequence_points"})
    for field in ("checksums", "documents", "sequence_points"):
        require(type(image["pdb"][field]) is list and len(image["pdb"][field]) <= 100000)
    for member in image["members"]:
        budget.row()
        require(set(member) == {"token", "kind", "type", "name", "signature", "attributes", "access", "is_static",
                "impl_attributes", "il_sha256", "il_bytes", "parameters", "generic_parameters", "accessors"}
                and type(member["token"]) is int and type(member["is_static"]) is bool
                and type(member["attributes"]) is int and type(member["impl_attributes"]) is int
                and type(member["access"]) is str
                and type(member["parameters"]) is list and len(member["parameters"]) <= 4096)
        canonical_text(member["type"])
        canonical_text(member["signature"])
        for parameter in member["parameters"]:
            budget.row()
            require(set(parameter) == {"sequence", "name", "attributes", "default_value"})
            if parameter["default_value"] is not None:
                require(set(parameter["default_value"]) == {"type_code", "value_hex"})
    for row in image["types"]:
        budget.row()
        require(set(row) == {"token", "name", "attributes", "access", "base_type", "interfaces", "generic_parameters"})
        canonical_text(row["name"])
        if row["base_type"] is not None:
            canonical_text(row["base_type"])


def validate_inspector_report(report, requested, input_sha256, deadline):
    """Retain the monolithic contract; do not reinterpret a native null as zero."""
    require(set(report) == {"schema", "input_sha256", "pairs", "direct_dependency_reference_matches",
            "unresolved_direct_dependency_rows", "charged_rows", "input_bytes", "runtime_compatibility_proven",
            "coverage_credit", "qualification_claim", "limitations"})
    require(report["schema"] == "issue779-product-abi-metadata-v1" and report["input_sha256"] == input_sha256
            and report["runtime_compatibility_proven"] is False and report["coverage_credit"] is False
            and report["qualification_claim"] is False and type(report["charged_rows"]) is int
            and 0 < report["charged_rows"] <= 100000 and type(report["input_bytes"]) is int
            and 0 < report["input_bytes"] <= 128*1024*1024
            and type(report["unresolved_direct_dependency_rows"]) is int and report["unresolved_direct_dependency_rows"] == 0)
    budget = ReconciliationBudget(deadline)
    return validate_image_pairs(report["pairs"], requested, budget,
                                dependency_rows=report["direct_dependency_reference_matches"])


def logical_inventory_pair(pair, selected):
    """Validate inventory-v2's exclusive representation before constructing a view.

    True requires native candidate=null, exact identical requested DLL/PDB paths
    AND digests, actual baseline pins and both native byte-equality flags. The
    returned candidate is the SAME retained dict, not a copied/decoded image.
    False requires a full actual candidate. Common remains null until the separate
    digest-bound private inspector's Common result arrives. This is data, not ABI authority.
    """
    require(type(pair) is dict and set(pair) == {"name", "baseline", "candidate", "dll_bytes_equal",
            "pdb_bytes_equal", "common", "candidate_is_baseline"}
            and type(selected) is dict and pair["name"] == selected["name"]
            and pair["name"] in ("Contracts", "Coverage") and pair["common"] is None
            and type(pair["candidate_is_baseline"]) is bool and type(pair["baseline"]) is dict
            and type(selected) is dict and set(selected) == {"name", "baseline", "candidate"})
    for side in ("baseline", "candidate"):
        require(type(selected[side]) is dict and set(selected[side]) == {"dll", "pdb", "dll_sha256", "pdb_sha256"})
    for extension in ("dll", "pdb"):
        require(pair[extension+"_bytes_equal"] is (selected["baseline"][extension+"_sha256"]
                                                  == selected["candidate"][extension+"_sha256"]))
        require(pair["baseline"][extension+"_path"] == selected["baseline"][extension]
                and pair["baseline"][extension+"_sha256"] == selected["baseline"][extension+"_sha256"])
    if pair["candidate_is_baseline"]:
        require(pair["candidate"] is None and selected["baseline"] == selected["candidate"]
                and pair["dll_bytes_equal"] is True and pair["pdb_bytes_equal"] is True)
        candidate = pair["baseline"]
    else:
        require(type(pair["candidate"]) is dict)
        candidate = pair["candidate"]
        for extension in ("dll", "pdb"):
            require(candidate[extension+"_path"] == selected["candidate"][extension]
                    and candidate[extension+"_sha256"] == selected["candidate"][extension+"_sha256"])
    return {"name": pair["name"], "baseline": pair["baseline"], "candidate": candidate,
            "dll_bytes_equal": pair["dll_bytes_equal"], "pdb_bytes_equal": pair["pdb_bytes_equal"], "common": None}


def validate_partition_envelopes(records, requested, manifest_sha256, input_bytes, deadline, *, budget=None):
    """Three pairs and two complete inventory/Common joins, with fixed provenance.

    Contracts and Coverage use separate complete native inventory/comparison jobs.
    Every job retains the same 100K cap; no report, side or comparison is omitted.
    All seven reports share the one reconciliation byte ledger and deadline.
    """
    require(type(records) is list and len(records) == len(PARTITION_STAGES)
            and [r["name"] for r in records] == list(PARTITION_STAGES)
            and type(requested) is list and [r["name"] for r in requested] == list(("Contracts", "Planner")+LIBRARIES)
            and type(input_bytes) is int and 0 < input_bytes <= 128*1024*1024)
    if budget is None:
        budget = ReconciliationBudget(deadline)
    requested_by_name = {row["name"]: row for row in requested}
    pairs, pins, total_rows, inventories = [], [], 0, {}
    for number, record in enumerate(records):
        budget.row()
        require(set(record) == {"name", "input_sha256", "report_sha256", "raw"}
                and type(record["raw"]) is bytes and 0 < len(record["raw"]) <= MAXIMUM_FILE)
        budget.reserve(len(record["raw"]))
        require(sha(record["raw"]) == record["report_sha256"])
        report = unique_json(record["raw"])
        budget.check()
        common_stage = record["name"].endswith(".Common")
        inventory_stage = record["name"].endswith(".Inventory")
        selected_name = record["name"].split(".", 1)[0]
        fields = {"schema", "input_sha256", "partition_name", "manifest_sha256", "charged_rows", "input_bytes",
                  "runtime_compatibility_proven", "coverage_credit", "qualification_claim", "limitations"}
        fields |= {"inventory_sha256", "common"} if common_stage else {
                  "pairs", "direct_dependency_reference_matches", "unresolved_direct_dependency_rows"}
        require(type(report) is dict and set(report) == fields
                and report["manifest_sha256"] == manifest_sha256
                and report["input_sha256"] == record["input_sha256"]
                and report["partition_name"] == selected_name
                and report["runtime_compatibility_proven"] is False and report["coverage_credit"] is False
                and report["qualification_claim"] is False and type(report["input_bytes"]) is int
                and report["input_bytes"] == (len(records[number-1]["raw"]) if common_stage else input_bytes)
                and type(report["charged_rows"]) is int
                and 0 < report["charged_rows"] <= 100000 and type(report["limitations"]) is list)
        total_rows += report["charged_rows"]
        require(total_rows <= 700000)
        if common_stage:
            require(report["schema"] == "issue779-product-abi-common-metadata-v1"
                    and selected_name in inventories
                    and records[number-1]["name"] == selected_name+".Inventory"
                    and report["inventory_sha256"] == records[number-1]["report_sha256"]
                    and type(report["common"]) is dict)
            # Join only actual native outputs; no Common rows or flags are invented.
            pairs.append({**inventories.pop(selected_name), "common": report["common"]})
        else:
            expected_schema = "issue779-product-abi-inventory-metadata-v2" if inventory_stage else "issue779-product-abi-pair-metadata-v1"
            require(report["schema"] == expected_schema and type(report["pairs"]) is list and len(report["pairs"]) == 1
                    and report["pairs"][0]["name"] == selected_name
                    and report["direct_dependency_reference_matches"] == [] and report["unresolved_direct_dependency_rows"] is None)
            if inventory_stage:
                require(selected_name not in inventories)
                inventories[selected_name] = logical_inventory_pair(report["pairs"][0], requested_by_name[selected_name])
            else:
                pairs.append(report["pairs"][0])
        pin = {"name": record["name"], "input_sha256": record["input_sha256"],
               "report_sha256": record["report_sha256"], "report_length": len(record["raw"]),
               "charged_rows": report["charged_rows"], "native_unresolved_direct_dependency_rows": None}
        if inventory_stage:
            pin["candidate_is_baseline"] = report["pairs"][0]["candidate_is_baseline"]
            pin["logical_candidate_origin"] = "same-image-reference" if report["pairs"][0]["candidate_is_baseline"] else "full-native-candidate"
        budget.store(pin)
        pins.append(pin)
    require(not inventories and len(pairs) == 5 and [row["name"] for row in pairs] == [row["name"] for row in requested])
    budget.check()
    return pairs, pins, total_rows


def reconcile_partition_reports(records, requested, manifest_sha256, input_bytes, deadline, *, budget=None):
    """Derive complete references only after seven authentic native stages validate."""
    if budget is None:
        budget = ReconciliationBudget(deadline)
    pairs, pins, total_rows = validate_partition_envelopes(
        records, requested, manifest_sha256, input_bytes, deadline, budget=budget)
    observations = {}
    images, summaries, access_records = validate_image_pairs(pairs, requested, budget, observations=observations)
    budget.check()
    work_counts = budget.work_counts()
    return images, summaries, access_records, {"schema": "issue779-python-pair-reconciliation-v1",
            "manifest_sha256": manifest_sha256, "partition_reports": pins, "inspector_charged_rows": total_rows, "maximum_total_stage_work": 900000,
            "maximum_common_verification_work": 100000, "maximum_definition_dependency_work": 100000,
            "coverage_pair_origin": "python-joined-native-inventory-v2-and-native-common",
            "contracts_pair_origin": "python-joined-native-inventory-v2-and-native-common",
            "direct_reference_count": len(access_records), "observations": observations, **work_counts,
            "python_work": work_counts["total_python_verification_work"],
            "conservative_accounted_bytes": budget.bytes, "runtime_compatibility_claim": False,
            "coverage_credit": False, "qualification_claim": False}


def declaring_owner(reference):
    """Read only the emitted outer owner, never a generic argument substring.

    TypeSpecs must have the official provider's kind:17/18 named generic owner.
    Unknown/modifier/function-pointer owners are unresolved and reject. Named
    argument types are audited independently through ALL actual TypeRef records.
    """
    require(set(reference) == {"token", "kind", "type", "parent_kind", "name", "signature"})
    text = reference["type"]
    canonical_text(text)
    canonical_text(reference["signature"])
    if reference["parent_kind"] == "TypeSpecification":
        require(text.startswith(("kind:17:", "kind:18:")))
        text = text[8:]
    else:
        require(reference["parent_kind"] in ("TypeReference", "TypeDefinition"))
    require(text.startswith("[") and "]" in text)
    owner, named = text[1:].split("]", 1)
    require(bool(owner) and bool(named) and "[" not in owner and "]" not in owner)
    return owner, text


def resolve_generic_owner(encoded, actual_names, budget):
    """Resolve the owner against real names, including generated and nested names.

    Angle brackets can belong to a compiler-generated name. A nested type can
    inherit its parent's generic arity. Only an exact observed name immediately
    preceding an instantiation delimiter is eligible; ambiguity rejects.
    """
    canonical_text(encoded)
    require(encoded.endswith(">"))
    matches, attempts = [], 0
    for position, character in enumerate(encoded):
        if character != "<":
            continue
        budget.row()
        require(attempts < 64)
        attempts += 1
        name = encoded[:position]
        if name in actual_names:
            matches.append(name)
    require(len(matches) == 1)
    return matches[0]


def direct_dependency_reference(reference, prefix, refs):
    """Direct members belong to the owner assembly; dependency generic owners reject."""
    owner, _ = declaring_owner(reference)
    selected = owner == prefix[1:-1]
    if selected:
        require(reference["parent_kind"] in ("TypeReference", "TypeDefinition") and len(refs) == 1)
    return selected


def visibility_classification(access, compiled_friend):
    if access in ("Public", "NestedPublic"):
        return "public"
    require(access in ("NotPublic", "NestedAssembly", "NestedFamORAssem", "Assembly", "FamORAssem") and compiled_friend)
    return "compiled-friend-assembly-arm"


def index_type_definitions(rows, budget):
    """Unique actual TypeDef names; duplicate definitions never become a last-wins map."""
    require(type(rows) is list and len(rows) <= 100000)
    result, tokens = {}, set()
    for row in rows:
        budget.row()
        require(set(row) == {"token", "name", "attributes", "access", "base_type", "interfaces", "generic_parameters"}
                and type(row["token"]) is int and type(row["attributes"]) is int and type(row["access"]) is str
                and row["token"] not in tokens and row["name"] not in result)
        canonical_text(row["name"])
        validate_generic_parameters(row["generic_parameters"], budget=budget)
        require(type(row["interfaces"]) is list)
        for value in row["interfaces"]:
            budget.row()
            canonical_text(value)
        budget.store([row["name"], row["token"]])
        result[row["name"]] = row
        tokens.add(row["token"])
    return result


def audit_named_dependency_type(name, images, indexes, common, caller, friends, budget):
    """Audit a real named TypeRef and its full enclosing TypeDef chain on BOTH sides.

    Inputs are emitted definitions/common records and actual caller identity/IVT.
    This data procedure provides no lease, root admission or runtime access proof.
    """
    canonical_text(name)
    require(set(images) == set(indexes) == set(friends) == {"baseline", "candidate"})
    for side in ("baseline", "candidate"):
        require(len(indexes[side]) == len(images[side]["types"]))
    chain, current = [], name
    while True:
        budget.row()
        require(current in indexes["baseline"] and current in indexes["candidate"] and current in common)
        left, right, comparison = indexes["baseline"][current], indexes["candidate"][current], common[current]
        require(all(left[field] == right[field] for field in
                    ("attributes", "access", "base_type", "interfaces", "generic_parameters"))
                and comparison["type"] == current
                and all(comparison[field] is True for field in ("attributes_equal", "base_type_equal", "interfaces_equal",
                    "generic_parameters_equal", "generic_parameter_names_equal"))
                and comparison["baseline_access"] == left["access"]
                and comparison["candidate_access"] == right["access"])
        classifications = []
        for side, definition in (("baseline", left), ("candidate", right)):
            budget.row()
            classifications.append(visibility_classification(definition["access"], caller["assembly"]["name"] in friends[side]))
        chain.append({"name": current, "baseline_token": left["token"], "candidate_token": right["token"],
                      "baseline_access": left["access"], "candidate_access": right["access"],
                      "access_classifications": classifications})
        if "+" not in current:
            break
        current = current.rsplit("+", 1)[0]
    return {"name": name, "enclosing_types": chain}


def audit_caller_type_references(images, type_indexes, common_types, friends, budget):
    """Exhaustive Contracts/Planner TypeRef audit; retain every original token."""
    observations, named_by_caller = [], {}
    for caller in LIBRARIES:
        for side in ("baseline", "candidate"):
            image = images[caller, side]
            names, tokens = {}, {}
            for reference in image["type_references"]:
                budget.row()
                require(set(reference) == {"token", "name", "scope_kind", "scope_token"}
                        and type(reference["token"]) is int and reference["token"] not in tokens)
                canonical_text(reference["name"])
                require(reference["name"].startswith("[") and "]" in reference["name"])
                names.setdefault(reference["name"], []).append(reference)
                tokens[reference["token"]] = reference
                budget.store([caller, side, reference["token"], reference["name"]])
            named_by_caller[caller, side] = names
            cache = {}
            for reference in image["type_references"]:
                budget.row()
                for dependency in ("Contracts", "Planner"):
                    prefix = "["+assembly_identity(dependency)["name"]+"]"
                    if not reference["name"].startswith(prefix):
                        continue
                    require(reference["scope_kind"] in ("AssemblyReference", "TypeReference"))
                    refs = [row for row in image["assembly_references"] if row["name"] == assembly_identity(dependency)["name"]]
                    require(len(refs) == 1 and refs[0] == assembly_identity(dependency))
                    if reference["scope_kind"] == "TypeReference":
                        parent = tokens.get(reference["scope_token"])
                        require(parent is not None and reference["name"].startswith(parent["name"]+"+"))
                    if reference["name"] not in cache:
                        cache[reference["name"]] = audit_named_dependency_type(reference["name"],
                            {key: images[dependency, key] for key in ("baseline", "candidate")},
                            {key: type_indexes[dependency, key] for key in ("baseline", "candidate")},
                            common_types[dependency], image,
                            {key: friends[dependency, key] for key in ("baseline", "candidate")}, budget)
                    observation = {"caller": caller, "side": side, "dependency": dependency,
                                   "type_reference": dict(reference), "definitions": cache[reference["name"]],
                                   "origin": "python-audited-emitted-named-typeref"}
                    budget.store(observation)
                    observations.append(observation)
    return observations, named_by_caller


def observe_framework_wrappers(images, named_by_caller, budget, *, type_indexes, common_types):
    """Keep indirect generic observations without inventing direct matches.

    Local generic owners resolve through the unchanged caller's actual TypeDefs
    and complete Common rows. External owners require real AssemblyRefs/TypeRefs.
    Dependency argument types have already received the exhaustive TypeRef audit.
    """
    observations = []
    for caller in LIBRARIES:
        for side in ("baseline", "candidate"):
            image = images[caller, side]
            for reference in image["member_references"]:
                budget.row()
                owner, encoded = declaring_owner(reference)
                if reference["parent_kind"] != "TypeSpecification":
                    continue
                require(owner not in {assembly_identity(name)["name"] for name in ("Contracts", "Planner")})
                local = owner == image["assembly"]["name"]
                named = resolve_generic_owner(encoded,
                    type_indexes[caller, side] if local else named_by_caller[caller, side], budget)
                if local:
                    left, right = images[caller, "baseline"], images[caller, "candidate"]
                    require(left["dll_sha256"] == right["dll_sha256"]
                            and left["pdb_sha256"] == right["pdb_sha256"])
                    current = named
                    while True:
                        budget.row()
                        require(current in type_indexes[caller, "baseline"]
                                and current in type_indexes[caller, "candidate"]
                                and current in common_types[caller])
                        original = type_indexes[caller, "baseline"][current]
                        actual = type_indexes[caller, "candidate"][current]
                        comparison = common_types[caller][current]
                        require(original == actual
                                and all(comparison[field] is True for field in
                                    ("attributes_equal", "base_type_equal", "interfaces_equal",
                                     "generic_parameters_equal", "generic_parameter_names_equal"))
                                and comparison["baseline_access"] == original["access"]
                                and comparison["candidate_access"] == actual["access"])
                        if "+" not in current:
                            break
                        current = current.rsplit("+", 1)[0]
                else:
                    require(named in named_by_caller[caller, side])
                    owner_refs = [row for row in image["assembly_references"] if row["name"] == owner]
                    require(len(owner_refs) == 1)
                mentions = [name for name in ("Contracts", "Planner")
                            if "["+assembly_identity(name)["name"]+"]" in reference["type"]
                            or "["+assembly_identity(name)["name"]+"]" in reference["signature"]]
                if mentions:
                    record = {"caller": caller, "side": side, "member_reference": dict(reference),
                              "dependency_type_mentions": mentions,
                              "origin": "local-generic-definition-observation" if local else "indirect-generic-type-observation"}
                    budget.store(record)
                    observations.append(record)
    return observations


def validate_common_complete(pair, member_indexes, type_indexes, budget):
    """Verify EVERY emitted Common row against complete real inventories.

    This validates private inspector's C# Comparisons.Common comparisons; it does not manufacture a Common
    report, drop unreferenced rows or compute a substitute report in Python.
    """
    common = pair["common"]
    require(type(common) is dict and set(common) == {"common_types", "candidate_members", "baseline_only_members"}
            and all(type(common[key]) is list for key in common))
    baseline, candidate = pair["baseline"], pair["candidate"]
    member_tokens, retained_tokens = {}, {}
    for side, image in (("baseline", baseline), ("candidate", candidate)):
        budget.row()
        if id(image) in retained_tokens:
            member_tokens[side] = retained_tokens[id(image)]
            budget.store([pair["name"], side, "same-validated-token-index-reference"])
            continue
        tokens = {}
        for member in image["members"]:
            budget.row()
            require(type(member["token"]) is int and member["token"] not in tokens)
            budget.store([pair["name"], side, "member-token", member["token"]])
            tokens[member["token"]] = member
        member_tokens[side] = tokens
        retained_tokens[id(image)] = tokens
    members, types, missing = {}, {}, {}
    for row in common["candidate_members"]:
        budget.row()
        require(type(row) is dict and set(row) == {"candidate_token", "kind", "type", "name", "signature",
                "candidate_access", "candidate_attributes", "candidate_static", "candidate_impl_attributes", "baseline_matches"}
                and type(row["candidate_token"]) is int and row["candidate_token"] not in members
                and type(row["baseline_matches"]) is list and row["candidate_token"] in member_tokens["candidate"])
        actual = member_tokens["candidate"][row["candidate_token"]]
        require(all(row[key] == actual[field] for key, field in (("kind", "kind"), ("type", "type"),
                ("name", "name"), ("signature", "signature"), ("candidate_access", "access"),
                ("candidate_attributes", "attributes"), ("candidate_static", "is_static"), ("candidate_impl_attributes", "impl_attributes"))))
        expected = member_indexes["baseline"].get(tuple(actual[key] for key in ("kind", "type", "name", "signature")), [])
        expected_tokens = {member["token"]: member for member in expected}
        require(len(expected_tokens) == len(expected) == len(row["baseline_matches"]))
        seen = set()
        for match in row["baseline_matches"]:
            budget.row()
            require(set(match) == {"token", "access", "attributes", "is_static", "impl_attributes",
                    "attributes_equal", "static_equal", "impl_attributes_equal", "generic_parameters_equal",
                    "generic_parameter_names_equal"} and match["token"] in expected_tokens and match["token"] not in seen)
            original = expected_tokens[match["token"]]
            require(all(match[key] == original[key] for key in ("token", "access", "attributes", "is_static", "impl_attributes")))
            shapes, names = generic_comparison_facts(original["generic_parameters"], actual["generic_parameters"], budget)
            for field, value in (("attributes_equal", original["attributes"] == actual["attributes"]),
                    ("static_equal", original["is_static"] == actual["is_static"]),
                    ("impl_attributes_equal", original["impl_attributes"] == actual["impl_attributes"]),
                    ("generic_parameters_equal", shapes), ("generic_parameter_names_equal", names)):
                require(match[field] is value)
            seen.add(match["token"])
        require(seen == set(expected_tokens))
        members[row["candidate_token"]] = row
        budget.store([pair["name"], row["candidate_token"]])
    require(set(members) == set(member_tokens["candidate"]))
    expected_types = set(type_indexes["baseline"]) & set(type_indexes["candidate"])
    for row in common["common_types"]:
        budget.row()
        require(set(row) == {"type", "attributes_equal", "base_type_equal", "interfaces_equal",
                "generic_parameters_equal", "generic_parameter_names_equal", "baseline_access", "candidate_access"}
                and row["type"] in expected_types and row["type"] not in types)
        original, actual = type_indexes["baseline"][row["type"]], type_indexes["candidate"][row["type"]]
        shapes, names = generic_comparison_facts(original["generic_parameters"], actual["generic_parameters"], budget)
        for field, value in (("attributes_equal", original["attributes"] == actual["attributes"]),
                ("base_type_equal", original["base_type"] == actual["base_type"]),
                ("interfaces_equal", original["interfaces"] == actual["interfaces"]),
                ("generic_parameters_equal", shapes), ("generic_parameter_names_equal", names)):
            require(row[field] is value)
        require(row["baseline_access"] == original["access"] and row["candidate_access"] == actual["access"])
        types[row["type"]] = row
        budget.store([pair["name"], row["type"]])
    require(set(types) == expected_types)
    expected_missing = {}
    for member in baseline["members"]:
        budget.row()
        key = tuple(member[field] for field in ("kind", "type", "name", "signature"))
        if key not in member_indexes["candidate"]:
            expected_missing[member["token"]] = member
    for row in common["baseline_only_members"]:
        budget.row()
        require(set(row) == {"baseline_token", "kind", "type", "name", "signature", "access"}
                and row["baseline_token"] in expected_missing and row["baseline_token"] not in missing)
        original = expected_missing[row["baseline_token"]]
        require(all(row[field] == original[field] for field in ("kind", "type", "name", "signature", "access")))
        missing[row["baseline_token"]] = row
    require(set(missing) == set(expected_missing))
    budget.check()
    return members, types


def generic_comparison_facts(left, right, budget):
    """Boolean checks from actual emitted generic rows; no report generation."""
    if len(left) != len(right):
        return False, False
    shape, names = True, True
    for original, actual in zip(left, right):
        budget.row()
        shape = shape and all(original[field] == actual[field] for field in ("position", "attributes", "constraints"))
        names = names and all(original[field] == actual[field] for field in ("position", "name"))
    return shape, names


def validate_image_pairs(pairs, requested, budget, *, dependency_rows=None, observations=None):
    """Check actual definitions/access/common rows; optional original C# match rows."""
    deadline = budget.deadline
    names = ("Contracts", "Planner")+LIBRARIES
    require(type(pairs) is list and [row["name"] for row in pairs] == list(names)
            and [row["name"] for row in requested] == list(names))
    images, summaries, shape_cache = {}, [], set()
    for pair, selected in zip(pairs, requested):
        remaining(deadline)
        name = pair["name"]
        require(set(pair) == {"name", "baseline", "candidate", "dll_bytes_equal", "pdb_bytes_equal", "common"})
        for side in ("baseline", "candidate"):
            image, pins = pair[side], selected[side]
            require(image["dll_path"] == pins["dll"] and image["pdb_path"] == pins["pdb"])
            require(image["dll_sha256"] == pins["dll_sha256"] and image["pdb_sha256"] == pins["pdb_sha256"]
                    and image["assembly"] == assembly_identity(name))
            budget.row()  # EVERY shape-cache lookup, including an alias hit.
            if id(image) not in shape_cache:
                validate_image_shape(image, budget)
                shape_cache.add(id(image))
            pdb = image["pdb"]
            require(pdb["one_matching_code_view"] is True and type(pdb["code_view_count"]) is int
                    and pdb["code_view_count"] == 1 and pdb["matching_code_view_count"] == 1)
            require(type(pdb["checksums"]) is list and bool(pdb["checksums"])
                    and all(row["algorithm"] in ("SHA1", "SHA256")
                            and (row["raw_file_matches"] is True or row["zeroed_id_matches"] is True)
                            for row in pdb["checksums"]))
            require(type(image["members"]) is list and type(image["types"]) is list
                    and type(image["member_references"]) is list and type(image["assembly_references"]) is list
                    and type(image["internals_visible_to"]) is list and type(pdb["documents"]) is list)
            images[name, side] = image
        for extension in ("dll", "pdb"):
            require(pair[extension+"_bytes_equal"] is (selected["baseline"][extension+"_sha256"]
                                                      == selected["candidate"][extension+"_sha256"]))
        if name in LIBRARIES:
            require(pair["dll_bytes_equal"] is True and pair["pdb_bytes_equal"] is True)
        summaries.append({"name": name, "baseline": {key: pair["baseline"][key] for key in
                          ("assembly", "mvid", "dll_sha256", "pdb_sha256")},
                          "candidate": {key: pair["candidate"][key] for key in
                          ("assembly", "mvid", "dll_sha256", "pdb_sha256")}})
    indexes, type_indexes, friends, common_members, common_types, identity_cache = {}, {}, {}, {}, {}, {}
    for key, image in images.items():
        remaining(deadline)
        budget.row()  # Charge EVERY exact-object index lookup.
        if id(image) in identity_cache:
            indexes[key], type_indexes[key], friends[key] = identity_cache[id(image)]
            budget.store([*key, "same-validated-image-index-reference"])
            continue
        index, types = {}, {}
        for member in image["members"]:
            remaining(deadline)
            budget.row()
            validate_generic_parameters(member["generic_parameters"], budget=budget)
            member_key = tuple(member[field] for field in ("kind", "type", "name", "signature"))
            budget.store([*key, *member_key, member["token"]])
            index.setdefault(member_key, []).append(member)
        types = index_type_definitions(image["types"], budget)
        indexes[key], type_indexes[key] = index, types
        friends[key] = set()
        for row in image["internals_visible_to"]:
            budget.row()
            require(set(row) == {"attribute_type", "name"}
                    and row["attribute_type"].endswith("System.Runtime.CompilerServices.InternalsVisibleToAttribute"))
            budget.store([*key, row["name"]])
            friends[key].add(row["name"])
        identity_cache[id(image)] = indexes[key], type_indexes[key], friends[key]
    common_budget = budget.common_verification_phase()
    for pair in pairs:
        common_members[pair["name"]], common_types[pair["name"]] = validate_common_complete(pair,
            {side: indexes[pair["name"], side] for side in ("baseline", "candidate")},
            {side: type_indexes[pair["name"], side] for side in ("baseline", "candidate")}, common_budget)
    for caller in LIBRARIES:
        required_dependencies = {"Contracts"} if caller == "Coverage" else {"Contracts", "Planner"}
        for side in ("baseline", "candidate"):
            for row in images[caller, side]["assembly_references"]:
                budget.row()
            selected_refs = [row for row in images[caller, side]["assembly_references"]
                             if row["name"].startswith("ForgeTrust.AppSurface.Evidence.")]
            require(len(selected_refs) == len({row["name"] for row in selected_refs})
                    and all(row in [assembly_identity(name) for name in names] for row in selected_refs)
                    and {name for name in ("Contracts", "Planner") if assembly_identity(name) in selected_refs}
                    == required_dependencies)
    type_audits, named_by_caller = audit_caller_type_references(images, type_indexes, common_types, friends, budget)
    indirect = observe_framework_wrappers(images, named_by_caller, budget,
                                         type_indexes=type_indexes, common_types=common_types)
    if observations is not None:
        observations["named_type_references"] = type_audits
        observations["indirect_generic_type_observations"] = indirect
    expected = []
    for dependency in ("Contracts", "Planner"):
        prefix = "["+assembly_identity(dependency)["name"]+"]"
        for caller in LIBRARIES:
            for side in ("baseline", "candidate"):
                image = images[caller, side]
                refs = [row for row in image["assembly_references"] if row["name"] == assembly_identity(dependency)["name"]]
                require(len(refs) <= 1 and all(row == assembly_identity(dependency) for row in refs))
                for reference in image["member_references"]:
                    budget.row()
                    require(set(reference) == {"token", "kind", "type", "parent_kind", "name", "signature"})
                    if direct_dependency_reference(reference, prefix, refs):
                        require(reference["parent_kind"] in ("TypeReference", "TypeDefinition") and len(refs) == 1)
                        budget.row()
                        canonical_text(reference["type"])
                        canonical_text(reference["signature"])
                        budget.store([caller, side, dependency, reference["token"]])
                        expected.append((caller, side, dependency, reference))
    if dependency_rows is not None:
        require(type(dependency_rows) is list and len(dependency_rows) == len(expected))
    access_records = []
    for number, (caller, side, dependency, reference) in enumerate(expected):
        remaining(deadline)
        budget.row()
        if dependency_rows is not None:
            row = dependency_rows[number]
            require(row["caller"] == caller and row["side"] == side and row["dependency"] == dependency
                    and row["reference_token"] == reference["token"]
                    and all(row[key] == reference[key] for key in ("type", "name", "signature", "parent_kind"))
                    and type(row["baseline_matches"]) is int and row["baseline_matches"] == 1
                    and type(row["candidate_matches"]) is int and row["candidate_matches"] == 1
                    and row["resolution"] == "exact-direct-definitions")
        observations, classifications = [], []
        for definition_side in ("baseline", "candidate"):
            budget.row()
            dependency_image = images[dependency, definition_side]
            members = indexes[dependency, definition_side].get(tuple(reference[key] for key in ("kind", "type", "name", "signature")), [])
            require(len(members) == 1)
            member = members[0]
            observations.append({"side": definition_side, "token": member["token"],
                                 "access": member["access"], "is_static": member["is_static"]})
            classifications.append(metadata_access(dependency_image, member, images[caller, side],
                                                     type_index=type_indexes[dependency, definition_side],
                                                     friend_names=friends[dependency, definition_side], budget=budget))
        baseline_member = indexes[dependency, "baseline"][tuple(reference[key] for key in ("kind", "type", "name", "signature"))][0]
        candidate_member = indexes[dependency, "candidate"][tuple(reference[key] for key in ("kind", "type", "name", "signature"))][0]
        require(all(baseline_member[field] == candidate_member[field] for field in
                    ("attributes", "access", "is_static", "impl_attributes", "generic_parameters")))
        require([(row["sequence"], row["attributes"], row["default_value"]) for row in baseline_member["parameters"]]
                == [(row["sequence"], row["attributes"], row["default_value"]) for row in candidate_member["parameters"]])
        common_match = common_members[dependency].get(candidate_member["token"])
        require(common_match is not None and len(common_match["baseline_matches"]) == 1
                and all(common_match[field] == candidate_member[image_field] for field, image_field in
                        (("kind", "kind"), ("type", "type"), ("name", "name"), ("signature", "signature"),
                         ("candidate_access", "access"), ("candidate_attributes", "attributes"),
                         ("candidate_static", "is_static"), ("candidate_impl_attributes", "impl_attributes"))))
        match = common_match["baseline_matches"][0]
        require(match["token"] == baseline_member["token"]
                and all(match[field] is True for field in ("attributes_equal", "static_equal", "impl_attributes_equal",
                                                          "generic_parameters_equal", "generic_parameter_names_equal")))
        type_name = reference["type"]
        while True:
            baseline_type, candidate_type = type_indexes[dependency, "baseline"][type_name], type_indexes[dependency, "candidate"][type_name]
            require(all(baseline_type[field] == candidate_type[field] for field in
                        ("attributes", "access", "base_type", "interfaces", "generic_parameters")))
            budget.row()
            common_type = common_types[dependency].get(type_name)
            require(common_type is not None and all(common_type[field] is True for field in
                    ("attributes_equal", "base_type_equal", "interfaces_equal", "generic_parameters_equal",
                     "generic_parameter_names_equal"))
                    and common_type["baseline_access"] == baseline_type["access"]
                    and common_type["candidate_access"] == candidate_type["access"])
            if "+" not in type_name:
                break
            type_name = type_name.rsplit("+", 1)[0]
        if dependency_rows is not None:
            require(row["access_observations"] == observations)
        derived = {"caller": caller, "side": side, "dependency": dependency,
                   "reference_token": reference["token"], "type": reference["type"], "name": reference["name"],
                   "signature": reference["signature"], "parent_kind": reference["parent_kind"],
                   "baseline_matches": len(indexes[dependency, "baseline"].get(tuple(reference[key] for key in
                                           ("kind", "type", "name", "signature")), [])),
                   "candidate_matches": len(indexes[dependency, "candidate"].get(tuple(reference[key] for key in
                                            ("kind", "type", "name", "signature")), [])),
                   "access_observations": observations, "definition_access": classifications,
                   "origin": "python-reconciled-emitted-definitions"}
        budget.store(derived)
        access_records.append(derived)
    budget.check()
    return images, summaries, access_records


def swap_product_pairs(tool, candidates, deadline, *, expected_owner_uid=0):
    """Replace six fixed file-data targets using retained verified writable FDs.

    All candidate bytes and all six O_RDWR/NONBLOCK/NOFOLLOW target descriptors
    are preflighted before the first write. Their common parent FD stays retained.
    Named and descriptor identities, owner, type and nlink1 are rechecked as a
    complete set before mutation and individually before each ftruncate. No path
    is reopened for writing. A later failure can leave earlier verified swaps,
    and rejects preparation; this procedure is not an atomic six-file transaction.

    Production selects UID0. The optional UID is an ordinary file-data test seam;
    it supplies no root execution, admission, lease, worker grant or ABI authority.
    Every owned descriptor closes on failure before any successful return.
    """
    require(type(expected_owner_uid) is int and expected_owner_uid >= 0
            and type(candidates) is dict and set(candidates) == set(LIBRARIES))
    tool = Path(tool)
    require(tool.is_absolute())
    remaining(deadline)
    parent_before = tool.lstat()
    require(stat.S_ISDIR(parent_before.st_mode) and parent_before.st_uid == expected_owner_uid
            and parent_before.st_mode & 0o022 == 0)
    parent_fd = os.open(tool, os.O_RDONLY | os.O_DIRECTORY | os.O_NONBLOCK | os.O_NOFOLLOW | os.O_CLOEXEC)
    owned, swaps, verified, result, error = [], [], [], None, None
    try:
        require(identity(parent_before) == identity(os.fstat(parent_fd)))
        for name in LIBRARIES:
            require(set(candidates[name]) == {"dll", "pdb"})
            for extension in ("dll", "pdb"):
                remaining(deadline)
                basename = f"ForgeTrust.AppSurface.Evidence.{name}.{extension}"
                source = Path(candidates[name][extension]["path"])
                maximum = (32 if extension == "dll" else 16)*1024*1024
                require(source.is_absolute() and source.name == basename)
                selected = os.stat(basename, dir_fd=parent_fd, follow_symlinks=False)
                require(stat.S_ISREG(selected.st_mode) and selected.st_uid == expected_owner_uid
                        and selected.st_nlink == 1 and 0 <= selected.st_size <= maximum)
                fd = os.open(basename, os.O_RDWR | os.O_NONBLOCK | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent_fd)
                owned.append(fd)
                before = os.fstat(fd)
                require(identity(selected) == identity(before))
                # Validate the original target through the SAME retained writable
                # descriptor. Do not replace its identity with a later lstat.
                digest, read_count = hashlib.sha256(), 0
                while read_count < before.st_size:
                    remaining(deadline)
                    block = os.read(fd, min(65536, before.st_size-read_count))
                    require(bool(block) and read_count+len(block) <= before.st_size)
                    digest.update(block)
                    read_count += len(block)
                remaining(deadline)
                require(os.read(fd, 1) == b"" and identity(before) == identity(os.fstat(fd))
                        == identity(os.stat(basename, dir_fd=parent_fd, follow_symlinks=False)))
                data = read_regular(source, maximum, deadline=deadline, expected_owner_uid=expected_owner_uid)
                expected_sha256 = candidates[name][extension]["sha256"]
                require(sha(data) == expected_sha256)
                swaps.append((basename, fd, before, data, digest.hexdigest(), expected_sha256))
        require(len(swaps) == 6)
        remaining(deadline)
        require(identity(parent_before) == identity(os.fstat(parent_fd))
                == identity(tool.stat(follow_symlinks=False)))
        # Complete all six final custody checks before ANY truncate/write/chmod.
        for basename, fd, before, data, old_sha256, expected_sha256 in swaps:
            remaining(deadline)
            current = os.fstat(fd)
            require(stat.S_ISREG(current.st_mode) and current.st_uid == expected_owner_uid and current.st_nlink == 1
                    and identity(before) == identity(current)
                    == identity(os.stat(basename, dir_fd=parent_fd, follow_symlinks=False)))
        result = []
        for basename, fd, before, data, old_sha256, expected_sha256 in swaps:
            remaining(deadline)
            current = os.fstat(fd)
            require(stat.S_ISREG(current.st_mode) and current.st_uid == expected_owner_uid and current.st_nlink == 1
                    and identity(before) == identity(current)
                    == identity(os.stat(basename, dir_fd=parent_fd, follow_symlinks=False))
                    and identity(parent_before) == identity(os.fstat(parent_fd))
                    == identity(tool.stat(follow_symlinks=False)))
            os.ftruncate(fd, 0)
            os.lseek(fd, 0, os.SEEK_SET)
            written = 0
            while written < len(data):
                remaining(deadline)
                count = os.write(fd, data[written:written+65536])
                require(count > 0)
                written += count
            os.fchmod(fd, 0o444)
            os.fsync(fd)
            remaining(deadline)
            after = os.fstat(fd)
            require(stat.S_ISREG(after.st_mode) and after.st_uid == expected_owner_uid and after.st_nlink == 1
                    and after.st_dev == before.st_dev and after.st_ino == before.st_ino
                    and after.st_size == len(data) and stat.S_IMODE(after.st_mode) == 0o444
                    and identity(after) == identity(os.stat(basename, dir_fd=parent_fd, follow_symlinks=False)))
            os.lseek(fd, 0, os.SEEK_SET)
            digest, read_count = hashlib.sha256(), 0
            while read_count < len(data):
                remaining(deadline)
                block = os.read(fd, min(65536, len(data)-read_count))
                require(bool(block) and read_count+len(block) <= len(data))
                digest.update(block)
                read_count += len(block)
            require(os.read(fd, 1) == b"" and digest.hexdigest() == expected_sha256
                    and identity(after) == identity(os.fstat(fd))
                    == identity(os.stat(basename, dir_fd=parent_fd, follow_symlinks=False)))
            verified.append((basename, fd, after))
            result.append({"path": basename, "before_sha256": old_sha256,
                           "sha256": expected_sha256, "bytes": len(data), "mode": "0444"})
        remaining(deadline)
        require(identity(parent_before) == identity(os.fstat(parent_fd))
                == identity(tool.stat(follow_symlinks=False)))
        for basename, fd, after in verified:
            remaining(deadline)
            require(identity(after) == identity(os.fstat(fd))
                    == identity(os.stat(basename, dir_fd=parent_fd, follow_symlinks=False)))
    except BaseException as failure:
        error = failure
    finally:
        for fd in list(reversed(owned))+[parent_fd]:
            try:
                os.close(fd)
            except BaseException as failure:
                if error is None:
                    error = failure
    if error is not None:
        raise error
    remaining(deadline)
    return result


def write_private_json_bytes(path, raw, deadline):
    """Exclusive selected JSON bytes; no success publication after FD close deadline."""
    remaining(deadline)
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    with os.fdopen(fd, "wb") as stream:
        stream.write(raw)
    remaining(deadline)


def replace_and_inspect(tool, product, runner, dotnet, inspector, receipt_path, *,
                        inspector_sha256, inspector_contract_confirmed=False):
    """Authenticate one full manifest; run seven bounded native stages; then swap three.

    Inspector argv is EXACTLY dotnet inspector --input absolute --output absolute.
    Baselines are all five pristine171f images; candidates are private Contracts/
    Planner and unchanged pristine Cli/Aspire/Coverage. Default confirmation remains
    False until parent review. Root owns selected parents and the compiled inspector.
    Exclusive manifest/partition inputs/reports and the compact receipt remain private600.
    Jobs each use <=30 seconds and <=100K work inside the original Runner deadline;
    reconciliation uses <=30 seconds and two separate <=100K work phases, with <=32MiB raw reports/index/receipt bytes.
    Generated PDB document hashes are audited but receive no product-source credit.
    """
    require(inspector_contract_confirmed is True and os.geteuid() == 0)
    tool, dotnet, inspector, receipt_path = map(Path, (tool, dotnet, inspector, receipt_path))
    require(all(path.is_absolute() for path in (tool, dotnet, inspector, receipt_path))
            and product["source_commit"] == PRODUCT_COMMIT
            and set(product["candidates"]) == set(LIBRARIES)
            and set(product["baselines"]) == set(("Contracts", "Planner")+LIBRARIES))
    require(sha(read_regular(inspector, MAXIMUM_FILE, deadline=runner.deadline)) == inspector_sha256)
    requested, input_bytes = [], 0
    for name in ("Contracts", "Planner")+LIBRARIES:
        pair = {"name": name}
        for side in ("baseline", "candidate"):
            pins = {}
            for extension in ("dll", "pdb"):
                row = product["baselines"][name][extension] if side == "baseline" else (
                    product["candidates"][name][extension] if name in LIBRARIES else None)
                path = Path(row["path"]) if row is not None else tool / f"ForgeTrust.AppSurface.Evidence.{name}.{extension}"
                require(path.is_absolute() and path.name == f"ForgeTrust.AppSurface.Evidence.{name}.{extension}")
                data = read_regular(path, (32 if extension == "dll" else 16)*1024*1024, deadline=runner.deadline)
                require(bool(data) and input_bytes+len(data) <= 128*1024*1024)
                input_bytes += len(data)
                digest = sha(data)
                if row is not None:
                    require(digest == row["sha256"])
                pins[extension], pins[extension+"_sha256"] = str(path), digest
            pair[side] = pins
        requested.append(pair)
    input_path = receipt_path.with_name(receipt_path.stem+".pairs.json")
    require(not os.path.lexists(receipt_path))
    encoded_input = (json.dumps({"schema": "issue779-product-abi-input-v1", "pairs": requested},
                               sort_keys=True, separators=(",", ":"))+"\n").encode()
    require(len(encoded_input) <= 128*1024)
    manifest_sha256 = sha(encoded_input)
    write_private_json_bytes(input_path, encoded_input, runner.deadline)
    records, report_bytes = [], 0
    original_deadline = runner.deadline
    # ALL twenty file hashes/lengths and the <=128MiB aggregate above precede
    # the first dispatch. Each child additionally authenticates that full manifest.
    for name in PARTITION_STAGES:
        input_part = receipt_path.with_name(receipt_path.stem+"."+name+".input.json")
        output_part = receipt_path.with_name(receipt_path.stem+"."+name+".metadata.json")
        require(not os.path.lexists(output_part))
        selected_name = name.split(".", 1)[0]
        stage_schema = ("issue779-product-abi-common-input-v1" if name.endswith(".Common") else
                        "issue779-product-abi-inventory-input-v1" if name.endswith(".Inventory") else
                        "issue779-product-abi-pair-input-v1")
        stage_input = {"schema": stage_schema, "manifest": str(input_path),
                       "manifest_sha256": manifest_sha256, "pair_name": selected_name}
        if name.endswith(".Common"):
            require(records[-1]["name"] == selected_name+".Inventory")
            stage_input.update({"inventory": records[-1]["path"], "inventory_sha256": records[-1]["report_sha256"]})
        encoded_part = (json.dumps(stage_input, sort_keys=True, separators=(",", ":"))+"\n").encode()
        require(len(encoded_part) <= 128*1024)
        write_private_json_bytes(input_part, encoded_part, original_deadline)
        runner.run([str(dotnet), str(inspector), "--input", str(input_part), "--output", str(output_part)],
                   tool, maximum_seconds=30)
        remaining(original_deadline)
        require(report_bytes < MAXIMUM_FILE)
        raw = read_regular(output_part, MAXIMUM_FILE-report_bytes, deadline=original_deadline)
        require(bool(raw))
        report_bytes += len(raw)
        record = {"name": name, "input_sha256": sha(encoded_part), "report_sha256": sha(raw),
                  "path": str(output_part), "length": len(raw)}
        remaining(original_deadline)
        records.append(record)
    spec_path = receipt_path.with_name(receipt_path.stem+".reconciliation-input.json")
    spec = {"schema": "issue779-private-pair-reconciliation-input-v1", "manifest": str(input_path),
            "manifest_sha256": manifest_sha256, "input_bytes": input_bytes, "reports": records,
            "source_commit": PRODUCT_COMMIT, "source_files": product["source_files"],
            "build_root": product["build_root"], "inspector_sha256": inspector_sha256}
    spec_raw = (json.dumps(spec, sort_keys=True, separators=(",", ":"))+"\n").encode()
    require(len(spec_raw) <= 1024*1024)
    write_private_json_bytes(spec_path, spec_raw, original_deadline)
    runner.run([sys.executable, "-B", str(Path(__file__).resolve()), "--reconcile", str(spec_path),
                sha(spec_raw), str(receipt_path)], tool, maximum_seconds=30)
    raw_receipt = read_regular(receipt_path, 512*1024, deadline=original_deadline)
    receipt = unique_json(raw_receipt)
    require(receipt["schema"] == "issue779-private-product-binary-input-v1"
            and receipt["input_sha256"] == manifest_sha256
            and receipt["reconciliation_input_sha256"] == sha(spec_raw)
            and receipt["source_commit"] == PRODUCT_COMMIT
            and receipt["runtime_compatibility_claim"] is False and receipt["native_execution"] is False
            and receipt["metadata_mode"] == METADATA_MODE
            and [row["name"] for row in receipt["partition_reports"]] == list(PARTITION_STAGES)
            and all(row["report_sha256"] == record["report_sha256"] and row["input_sha256"] == record["input_sha256"]
                    for row, record in zip(receipt["partition_reports"], records)))
    receipt_sha256 = sha(raw_receipt)
    swap_product_pairs(tool, product["candidates"], original_deadline)
    remaining(original_deadline)
    return {"receipt_sha256": receipt_sha256, "source_commit": PRODUCT_COMMIT,
            "binary_inputs": product["candidates"], "runtime_compatibility_claim": False,
            "native_execution": False}


def reconcile_files(spec_path, spec_sha256, receipt_path):
    """Root metadata-only child; externally supervised for <=30 seconds by Runner.

    This process performs no DLL replacement, subject evaluation or instrumentation.
    Its cooperative deadline is also 30 seconds; the parent's original deadline
    and owned process-group cleanup remain stricter external bounds.
    """
    require(os.geteuid() == 0)
    deadline = time.monotonic()+30
    spec_path, receipt_path = Path(spec_path), Path(receipt_path)
    require(spec_path.is_absolute() and receipt_path.is_absolute() and not os.path.lexists(receipt_path))
    raw_spec = read_regular(spec_path, 1024*1024, deadline=deadline)
    require(sha(raw_spec) == spec_sha256)
    spec = unique_json(raw_spec)
    require(set(spec) == {"schema", "manifest", "manifest_sha256", "input_bytes", "reports", "source_commit",
                         "source_files", "build_root", "inspector_sha256"}
            and spec["schema"] == "issue779-private-pair-reconciliation-input-v1"
            and spec["source_commit"] == PRODUCT_COMMIT and type(spec["source_files"]) is dict
            and len(spec["source_files"]) == 2814 and type(spec["reports"]) is list and len(spec["reports"]) == len(PARTITION_STAGES))
    raw_manifest = read_regular(Path(spec["manifest"]), 128*1024, deadline=deadline)
    require(sha(raw_manifest) == spec["manifest_sha256"])
    manifest = unique_json(raw_manifest)
    require(set(manifest) == {"schema", "pairs"} and manifest["schema"] == "issue779-product-abi-input-v1")
    records, report_bytes = [], 0
    for record in spec["reports"]:
        require(set(record) == {"name", "input_sha256", "report_sha256", "path", "length"}
                and type(record["length"]) is int and 0 < record["length"] <= MAXIMUM_FILE-report_bytes)
        raw = read_regular(Path(record["path"]), record["length"], deadline=deadline)
        require(len(raw) == record["length"] and sha(raw) == record["report_sha256"])
        report_bytes += len(raw)
        records.append({key: record[key] for key in ("name", "input_sha256", "report_sha256")}|{"raw": raw})
    budget = ReconciliationBudget(deadline)
    images, summaries, access_records, reconciliation = reconcile_partition_reports(
        records, manifest["pairs"], spec["manifest_sha256"], spec["input_bytes"], deadline, budget=budget)
    product = {"build_root": spec["build_root"], "source_files": spec["source_files"]}
    inspector_sha256, manifest_sha256 = spec["inspector_sha256"], spec["manifest_sha256"]
    documents, document_count = [], 0
    source_root = Path(product["build_root"])
    for name in LIBRARIES:
        for document in images[name, "candidate"]["pdb"]["documents"]:
            budget.row()
            require(document_count < 4096)
            document_count += 1
            path = Path(document["name"])
            require(path.is_absolute() and path.is_relative_to(source_root))
            relative = relative_name(path.relative_to(source_root).as_posix())
            no_link_parents(path, source_root)
            data = read_regular(path, 4*1024*1024, deadline=budget.deadline)
            algorithm = document["hash_algorithm"]
            require(algorithm in ("8829d00f-11b8-4213-878b-770e8597ac16", "ff1816ec-aa5e-4d10-87f7-6f4963833460"))
            checksum = sha(data) if algorithm.startswith("8829") else hashlib.sha1(data).hexdigest()
            require(checksum == document["hash"])
            checked_in = relative in product["source_files"]
            if checked_in:
                require(sha(data) == product["source_files"][relative]["sha256"])
            else:
                require("obj" in Path(relative).parts)
            document_record = {"name": name, "relative_path": relative, "sha256": sha(data), "product_source": checked_in}
            budget.store(document_record)
            documents.append(document_record)
    require(all(any(row["name"] == name and row["product_source"] for row in documents) for name in LIBRARIES))
    observations = reconciliation.pop("observations")
    index_raw = (json.dumps({"schema": "issue779-python-type-reference-index-v1", "manifest_sha256": manifest_sha256,
                **observations}, sort_keys=True, separators=(",", ":"))+"\n").encode()
    budget.reserve(len(index_raw))
    index_path = receipt_path.with_name(receipt_path.stem+".reference-index.json")
    index_sha256 = sha(index_raw)
    write_private_json_bytes(index_path, index_raw, budget.deadline)
    reconciliation["reference_index"] = {"path": str(index_path), "sha256": index_sha256, "length": len(index_raw),
        "named_type_reference_count": len(observations["named_type_references"]),
        "indirect_generic_observation_count": len(observations["indirect_generic_type_observations"])}
    receipt = {"schema": "issue779-private-product-binary-input-v1", "source_commit": PRODUCT_COMMIT, "reconciliation_input_sha256": spec_sha256,
               "input_sha256": manifest_sha256, "metadata_mode": METADATA_MODE,
               "partition_reports": reconciliation["partition_reports"], "report_length": report_bytes,
               "reconciliation": reconciliation,
               "inspector_sha256": inspector_sha256, "assemblies": summaries, "documents": documents,
               "dependency_reference_access": access_records, "python_unresolved_direct_dependency_rows": 0,
               "stage": "audited-before-replacement", "runtime_compatibility_claim": False, "native_execution": False}
    encoded = (json.dumps(receipt, sort_keys=True, separators=(",", ":"))+"\n").encode()
    require(len(encoded) <= 512*1024)
    before_receipt_bytes = budget.bytes
    # Include the complete receipt itself; settle its small self-accounting
    # length before reserving bytes and before any publication.
    for _ in range(4):
        reconciliation.update(budget.work_counts())
        reconciliation["python_work"] = reconciliation["total_python_verification_work"]
        reconciliation["conservative_accounted_bytes"] = before_receipt_bytes+len(encoded)
        encoded_next = (json.dumps(receipt, sort_keys=True, separators=(",", ":"))+"\n").encode()
        require(len(encoded_next) <= 512*1024)
        if len(encoded_next) == len(encoded):
            encoded = encoded_next
            break
        encoded = encoded_next
    else:
        require(False)
    budget.reserve(len(encoded))
    receipt_sha256 = sha(encoded)
    budget.check()
    fd = os.open(receipt_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    with os.fdopen(fd, "wb") as stream:
        stream.write(encoded)
    budget.check()
    return receipt_sha256


if __name__ == "__main__":
    try:
        require(len(sys.argv) == 5 and sys.argv[1] == "--reconcile")
        reconcile_files(sys.argv[2], sys.argv[3], sys.argv[4])
    except Exception:
        print("private-product-reconciliation-rejected", file=sys.stderr)
        raise SystemExit(1)
