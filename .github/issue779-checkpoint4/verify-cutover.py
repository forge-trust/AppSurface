"""Repository cutover invariant; no process execution or runtime admission."""
from pathlib import Path
import ast
import sys

ROOT = Path(__file__).resolve().parents[2]
RETIRED = (
    "scripts/evidencehost-linux-launcher.py",
    "scripts/evidencehost_linux_application.py",
    "scripts/verify-evidencehost-linux-runtime.sh",
    "scripts/verify-evidencehost-linux-mechanism.sh",
    "tests/evidencehost-consumer/runtime-proof.py",
    "tests/evidencehost-consumer/test_runtime_proof.py",
    "tests/evidencehost-consumer/test_linux_launcher.py",
    "tests/evidencehost-consumer/test_linux_application.py",
    ".github/workflows/evidencehost-mechanism-proof.yml",
)


def verify(root: Path) -> None:
    for name in RETIRED:
        path = root / name
        if path.exists() or path.is_symlink():
            raise ValueError("The legacy EvidenceHost runtime is present.")
    entry = (root / "Cli/ForgeTrust.AppSurface.Cli/EvidenceProcessEntryPoint.cs").read_text()
    if "LinuxEmptyObservationExecution.RunAsync" not in entry or "EvidenceWorkerCommand" not in entry:
        raise ValueError("The same-product CSharp role dispatch is missing.")
    for folder in (root / "Cli", root / "Evidence", root / ".github/workflows"):
        for path in folder.rglob("*"):
            if path.is_file() and path.suffix in (".cs", ".csproj", ".yml"):
                value = path.read_text()
                if any(name in value for name in RETIRED[:5]):
                    raise ValueError("An active consumer references the retired runtime.")
    for name in (".github/issue779-checkpoint2/run-native.py", ".github/issue779-checkpoint3/run-native.py"):
        ast.parse((root / name).read_text())


if __name__ == "__main__":
    try:
        verify(ROOT)
    except (OSError, ValueError, SyntaxError):
        print("EvidenceHost CSharp cutover verification failed.", file=sys.stderr)
        raise SystemExit(1)
    print("EvidenceHost runtime uses the same-product CSharp roles; legacy runtime absent.")
