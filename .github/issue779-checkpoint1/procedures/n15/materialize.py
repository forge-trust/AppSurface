"""N15 fixed workspace staging with its already-running original 240s clock.

Uses the shared original sealed-copy/custody functions. Unlike the N13/N14 entry,
this function never samples a fresh job deadline; execute sampled it before staging.
No worker is launched here. Current parent/source replace historical fixed hashes.
"""
import json
import os,re,stat,subprocess,time
from pathlib import Path

def materialize(context, prepared, owner, *, deadline, job_utc, boot_end):
    """Create the fixed historical empty-observation workspace/request, without launch.

    Reuse only for a procedure whose original policy/request is identical. The
    original 240s job clock was sampled once by execute before verification/staging.
    Its original 60s request cleanup reserve and UTC/boottime expiry are retained.
    """
    capture, build = prepared["capture"], prepared["build"]
    workspace = owner._load(owner.HERE / "workspace.py", capture["files"][owner.PREFIX + "workspace.py"]["sha256"], deadline=deadline)
    parent = subprocess.run(["git", "--no-optional-locks", "-c", "core.fsmonitor=false",
        "-c", "safe.directory=" + build["source_root"], "rev-parse", context["source"] + "^"],
        cwd=build["source_root"], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        timeout=min(15, owner.remaining(deadline)), check=False)
    owner.need(parent.returncode == 0 and re.fullmatch(b"[0-9a-f]{40}\\n", parent.stdout), "current-source-parent")
    base_revision = parent.stdout.decode().strip()
    now_wall, now_boot = time.time(), workspace.boottime_ms()
    work_end = min(workspace.calculate_work_deadline(now_wall, now_boot, workspace.parse_deadline(job_utc), boot_end),
                   now_boot + int(owner.remaining(deadline) * 1000))
    owner.need("fixture-policy.json" not in prepared["nodes"]["tool"]["files"], "policy-collision")
    stage, private = workspace.FIXTURE_ROOT / context["generation"], workspace.PRIVATE_ROOT / context["generation"]
    for root in (workspace.FIXTURE_ROOT, workspace.PRIVATE_ROOT):
        workspace.safe_ancestors(root.parent)
        if not os.path.lexists(root):
            root.mkdir(mode=0o755)
            os.chmod(root, 0o755)
        s = root.lstat()
        owner.need(stat.S_ISDIR(s.st_mode) and s.st_uid == s.st_gid == 0
             and stat.S_IMODE(s.st_mode) == 0o755 and not root.is_symlink(), "fixture-parent-custody")
    owner.need(not os.path.lexists(stage) and not os.path.lexists(private), "workspace-generation-exists")
    stage.mkdir(mode=0o700)
    private.mkdir(mode=0o700)
    for role, name in (("tool", "tool"), ("runtime", "runtime"), ("source", "subject")):
        workspace.copy_role(prepared["roots"][role], stage / name, prepared["nodes"][role], work_end)
    os.chmod(stage / "tool", 0o755)
    policy = {"Id":"checkpoint-empty","Version":"1","ConservativeProfileId":"conservative",
        "Profiles":[{"Id":"empty","Scope":"Targeted","Resources":[],"Producers":[],"Obligations":[]},
                    {"Id":"conservative","Scope":"Release","Resources":[{"Id":"build","Readiness":"completion","DeadlineSeconds":1,"Requires":[]}],"Producers":[],"Obligations":[]}],
        "Rules":[{"Id":"docs","Pattern":"docs/**","ProfileId":"empty","Precedence":0}]}
    policy_path = stage / "tool/fixture-policy.json"
    policy_raw = json.dumps(policy, sort_keys=True, separators=(",", ":")).encode()
    owner.save(policy_path, policy_raw)
    os.chmod(policy_path, 0o444)
    os.chmod(stage / "tool", 0o555)
    os.chmod(stage, 0o555)
    for role, name in (("tool", "tool"), ("runtime", "runtime"), ("source", "subject")):
        workspace.verify_materialized(stage / name, prepared["nodes"][role], work_end,
            allow_policy=(role == "tool"), policy_path=policy_path)
        workspace.verify_materialized(prepared["roots"][role], prepared["nodes"][role], work_end)
    owner.need(all(n in prepared["nodes"]["tool"]["files"] for n in
             ("ForgeTrust.AppSurface.Cli.dll", "ForgeTrust.AppSurface.Cli.deps.json", "ForgeTrust.AppSurface.Cli.runtimeconfig.json"))
         and "dotnet" in prepared["nodes"]["runtime"]["files"], "normal-fdd-inputs")
    request = {"schema":"evidence-supervisor-linux-v1","mode":"observation",
        "tool_root":str(stage / "tool"),"runtime_root":str(stage / "runtime"),
        "runtime_host":str(stage / "runtime/dotnet"),"entry_path":str(stage / "tool/ForgeTrust.AppSurface.Cli.dll"),
        "policy_file":str(policy_path),"subject_root":str(stage / "subject"),
        "base_revision":base_revision,"subject_revision":context["source"],"workflow_identity":context["workflow_identity"],
        "paths":["docs/designs/issue-779-csharp-supervision-core.md"],
        "observation_profile_ids":["empty"],"observation_producer_ids":[],
        "admission_seconds":10,"start_seconds":30,"collection_seconds":10,
        "cleanup_seconds":60,"stopping_seconds":5,"job_deadline_utc":job_utc}
    request_raw = owner.canonical(request)
    request_path = private / "request.json"
    owner.need(len(request_raw) <= 65536 and not (stage / "subject" / request["paths"][0]).is_symlink(), "request-bound")
    owner.save(request_path, request_raw)
    checksums = f"{owner.digest(request_raw)}  {request_path}\n{owner.digest(policy_raw)}  {policy_path}\n".encode()
    owner.save(private / "request-policy.sha256", checksums)
    owner.need(workspace.read_pinned(request_path, owner.digest(request_raw), 65536, work_end)[1] == request_raw
         and workspace.read_pinned(policy_path, owner.digest(policy_raw), 4096, work_end)[1] == policy_raw
         and workspace.read_pinned(private / "request-policy.sha256", owner.digest(checksums), 4096, work_end)[1] == checksums,
         "request-policy-final-binding")
    return {"stage": stage, "private": private, "request": request_path,
            "request_sha256": owner.digest(request_raw), "policy_sha256": owner.digest(policy_raw), "job_deadline_utc": job_utc}
