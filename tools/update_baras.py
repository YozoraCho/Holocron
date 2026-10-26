import argparse
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TARGET = ROOT / "src" / "TacticalReadout" / "Fights" / "BARAS"
REPOSITORY = "https://github.com/baras-app/baras"
SOURCE = Path("core") / "definitions" / "encounters"

README = """# BARAS boss encounters

These files are the boss encounter definitions from [BARAS]({repository}) (`core/definitions/encounters`),
copied unchanged from commit `{commit}` ({date}). Tactical Readout reads them directly for its boss timers, phases
and alerts.

They are under the MIT license in `LICENSE`, copyright The BARAS Authors.

Don't edit them here. Fixes belong upstream in BARAS; refresh this folder with
`python tools/update_baras.py`. To change one for yourself, copy it to the same place under your own fights folder
(`%APPDATA%\\Ashla\\pluginConfigs\\TacticalReadout\\Fights\\BARAS\\...`); a file there replaces the built-in one.
"""


def git(*args, cwd=None):
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True).stdout.strip()


def copy(checkout: Path):
    source = checkout / SOURCE
    if not source.is_dir():
        raise SystemExit(f"{SOURCE} isn't in that checkout.")
    commit = git("rev-parse", "HEAD", cwd=checkout)
    date = git("log", "-1", "--format=%cs", cwd=checkout)
    if TARGET.exists():
        shutil.rmtree(TARGET)
    shutil.copytree(source, TARGET, ignore=shutil.ignore_patterns("*.md", ".*"))
    shutil.copyfile(checkout / "LICENSE.txt", TARGET / "LICENSE")
    (TARGET / "README.md").write_text(README.format(repository=REPOSITORY, commit=commit[:12], date=date), encoding="utf-8", newline="\n")
    files = sorted(p.relative_to(TARGET).as_posix() for p in TARGET.rglob("*.toml"))
    print(f"Copied {len(files)} encounter files from BARAS {commit[:12]} ({date}).")


def main(argv=None):
    parser = argparse.ArgumentParser(description="Copy BARAS's boss encounter files into Tactical Readout.")
    parser.add_argument("--ref", default="main", help="BARAS branch, tag or commit (default: main)")
    parser.add_argument("--checkout", type=Path, help="use this local BARAS checkout instead of cloning")
    args = parser.parse_args(argv)
    if args.checkout:
        copy(args.checkout.resolve())
        return
    with tempfile.TemporaryDirectory() as temp:
        checkout = Path(temp) / "baras"
        git("clone", "--filter=blob:none", "--no-checkout", REPOSITORY, str(checkout))
        git("checkout", args.ref, cwd=checkout)
        copy(checkout)


if __name__ == "__main__":
    sys.exit(main())
