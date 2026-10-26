import argparse
import hashlib
import io
import json
import posixpath
import re
import subprocess
import sys
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PLUGINS_DIR = ROOT / "plugins"
INDEX_FILE = ROOT / "repo.json"

FORMAT = 1
NAME = "Holocron"
API_LEVEL = 1
OLDEST_API_LEVEL = 1
MAX_DOWNLOAD = 50 * 1024 * 1024
MAX_UNPACKED = 200 * 1024 * 1024
MAX_FILES = 500
MAX_CHANGES = 300
ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
SHA_PATTERN = re.compile(r"^[0-9a-f]{64}$")
LICENSES = {
    "AGPL-3.0-only", "AGPL-3.0-or-later",
    "GPL-3.0-only", "GPL-3.0-or-later",
    "LGPL-3.0-only", "LGPL-3.0-or-later",
    "Apache-2.0", "MPL-2.0",
    "MIT", "BSD-2-Clause", "BSD-3-Clause", "ISC", "Zlib", "0BSD", "Unlicense",
}
FIELDS = ["Id", "Name", "Author", "Description", "Version", "ApiLevel", "Tags", "License", "Source", "Download",
          "Sha256", "Files", "Changes"]
FROM_MANIFEST = ["Name", "Author", "Description", "Version", "ApiLevel", "Tags", "Files"]
REQUIRED = ["Id", "Version", "ApiLevel", "License", "Source", "Download", "Sha256", "Files"]


class Problem(Exception):
    pass


def fetch(url):
    request = urllib.request.Request(url, headers={"User-Agent": "Holocron-tools"})
    with urllib.request.urlopen(request, timeout=60) as response:
        data = response.read(MAX_DOWNLOAD + 1)
    if len(data) > MAX_DOWNLOAD:
        raise Problem(f"the download is larger than {MAX_DOWNLOAD // (1024 * 1024)} MB")
    return data


def parse_version(text):
    if not isinstance(text, str) or not text.strip():
        return None
    core = text.strip().lstrip("vV")
    core = re.split(r"[-+ ]", core, maxsplit=1)[0]
    parts = core.split(".")
    if len(parts) == 1:
        parts.append("0")
    if len(parts) > 4 or not all(p.isdigit() for p in parts):
        return None
    numbers = [int(p) for p in parts]
    return tuple(numbers + [-1] * (4 - len(numbers)))


def compare_versions(a, b):
    left, right = parse_version(a), parse_version(b)
    if left is not None and right is not None:
        return (left > right) - (left < right)
    a, b = (a or "").lower(), (b or "").lower()
    return (a > b) - (a < b)


def is_https(url):
    return isinstance(url, str) and re.match(r"^https://[^/\s]+\.[^/\s]+/\S*$", url) is not None


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def normalize_files(files):
    return {name.replace("\\", "/").lower(): value.lower() for name, value in files.items()}


def check_entry_shape(entry, file_name=None):
    problems = []
    if not isinstance(entry, dict):
        return ["the entry isn't a JSON object"]
    for field in REQUIRED:
        if field not in entry or entry[field] in (None, "", [], {}):
            problems.append(f"{field} is missing")
    for field in entry:
        if field not in FIELDS:
            problems.append(f"{field} isn't a field Holocron knows")
    plugin_id = entry.get("Id")
    if isinstance(plugin_id, str) and plugin_id:
        if not ID_PATTERN.match(plugin_id):
            problems.append("Id must be 1-64 letters, digits, '.', '_' or '-', starting with a letter or digit")
        if file_name is not None and file_name != f"{plugin_id}.json":
            problems.append(f"the file must be named {plugin_id}.json")
    version = entry.get("Version")
    if version and parse_version(version) is None:
        problems.append(f"Version {version!r} isn't a version number like 1.2.0")
    api_level = entry.get("ApiLevel")
    if api_level is not None:
        if not isinstance(api_level, int) or isinstance(api_level, bool):
            problems.append("ApiLevel must be a whole number")
        elif api_level < OLDEST_API_LEVEL or api_level > API_LEVEL:
            problems.append(f"ApiLevel {api_level} isn't one a released Ashla supports ({OLDEST_API_LEVEL}-{API_LEVEL})")
    license_id = entry.get("License")
    if license_id and license_id not in LICENSES:
        problems.append(f"License {license_id!r} isn't on the list of AGPL-3.0-compatible licenses (see README)")
    for field in ("Source", "Download"):
        if entry.get(field) and not is_https(entry[field]):
            problems.append(f"{field} must be an https:// address")
    digest = entry.get("Sha256")
    if digest and (not isinstance(digest, str) or not SHA_PATTERN.match(digest)):
        problems.append("Sha256 must be 64 lowercase hex characters")
    files = entry.get("Files")
    if files is not None:
        if not isinstance(files, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in files.items()):
            problems.append("Files must map file names to SHA-256 hashes")
        else:
            for name, value in files.items():
                if not SHA_PATTERN.match(value.lower()):
                    problems.append(f"Files has a bad hash for {name}")
            if isinstance(plugin_id, str) and plugin_id and f"{plugin_id}.dll".lower() not in normalize_files(files):
                problems.append(f"Files doesn't list {plugin_id}.dll")
    tags = entry.get("Tags")
    if tags is not None and (not isinstance(tags, list) or not all(isinstance(t, str) for t in tags)):
        problems.append("Tags must be a list of words")
    for field in ("Name", "Author", "Description", "Changes"):
        if field in entry and entry[field] is not None and not isinstance(entry[field], str):
            problems.append(f"{field} must be text")
    if isinstance(entry.get("Changes"), str) and len(entry["Changes"]) > MAX_CHANGES:
        problems.append(f"Changes is longer than {MAX_CHANGES} characters")
    return problems


def unpack(data):
    try:
        archive = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile:
        raise Problem("the download isn't a zip file")
    with archive:
        items = archive.infolist()
        if len(items) > MAX_FILES:
            raise Problem(f"the zip has more than {MAX_FILES} files")
        files = {}
        total = 0
        for item in items:
            name = item.filename.replace("\\", "/")
            parts = name.split("/")
            if name.startswith("/") or re.match(r"^[A-Za-z]:", name) or ".." in parts:
                raise Problem(f"the zip has a path outside its folder: {item.filename}")
            if item.is_dir():
                continue
            total += item.file_size
            if total > MAX_UNPACKED:
                raise Problem(f"the zip is larger than {MAX_UNPACKED // (1024 * 1024)} MB unpacked")
            files[posixpath.normpath(name)] = archive.read(item)
    tops = {name.split("/", 1)[0] for name in files}
    if len(tops) == 1 and all("/" in name for name in files):
        prefix = next(iter(tops)) + "/"
        files = {name[len(prefix):]: content for name, content in files.items()}
    return files


def read_manifest(files, plugin_id):
    if "manifest.json" not in files:
        raise Problem("the zip has no manifest.json next to the plugin's DLL")
    try:
        manifest = json.loads(files["manifest.json"].decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise Problem(f"manifest.json can't be read: {error}")
    if not isinstance(manifest, dict):
        raise Problem("manifest.json isn't a JSON object")
    manifest = {key[:1].upper() + key[1:]: value for key, value in manifest.items()}
    if plugin_id is not None and str(manifest.get("Id", "")).lower() != plugin_id.lower():
        raise Problem(f"manifest.json belongs to {manifest.get('Id')!r}, not {plugin_id!r}")
    listed = manifest.get("Files")
    if not isinstance(listed, dict) or not listed:
        raise Problem("manifest.json lists no files")
    present = {name.lower(): name for name in files}
    problems = []
    for name, expected in listed.items():
        key = name.replace("\\", "/").lower()
        if key not in present:
            problems.append(f"{name} is listed in manifest.json but isn't in the zip")
        elif sha256(files[present[key]]) != str(expected).lower():
            problems.append(f"{name} doesn't match its hash in manifest.json")
    listed_keys = normalize_files(listed)
    for name in files:
        if name.lower().endswith(".dll") and name.lower() not in listed_keys:
            problems.append(f"{name} isn't listed in manifest.json")
    main_dll = f"{manifest.get('Id')}.dll".lower()
    if main_dll not in present:
        problems.append(f"the zip has no {manifest.get('Id')}.dll")
    if problems:
        raise Problem("; ".join(problems))
    return manifest


def entry_from_download(data, download, source=None, license_id=None, changes=None):
    files = unpack(data)
    manifest = read_manifest(files, None)
    entry = {
        "Id": manifest.get("Id"),
        "Name": manifest.get("Name"),
        "Author": manifest.get("Author"),
        "Description": manifest.get("Description"),
        "Version": manifest.get("Version"),
        "ApiLevel": manifest.get("ApiLevel"),
        "Tags": manifest.get("Tags") or [],
        "License": license_id,
        "Source": source or manifest.get("Repository"),
        "Download": download,
        "Sha256": sha256(data),
        "Files": dict(sorted(manifest["Files"].items(), key=lambda item: item[0].lower())),
        "Changes": changes,
    }
    return {key: value for key, value in entry.items() if value not in (None, "")}


def check_download(entry, data):
    problems = []
    if len(data) > MAX_DOWNLOAD:
        return [f"the download is larger than {MAX_DOWNLOAD // (1024 * 1024)} MB"]
    if sha256(data) != entry.get("Sha256", "").lower():
        problems.append(f"Sha256 doesn't match the download (the download is {sha256(data)})")
    try:
        manifest = read_manifest(unpack(data), entry.get("Id"))
    except Problem as problem:
        return problems + [str(problem)]
    if compare_versions(manifest.get("Version"), entry.get("Version")) != 0:
        problems.append(f"the download is version {manifest.get('Version')}, but the entry says {entry.get('Version')}")
    if manifest.get("ApiLevel") != entry.get("ApiLevel"):
        problems.append(f"the download is for ApiLevel {manifest.get('ApiLevel')}, but the entry says {entry.get('ApiLevel')}")
    if normalize_files(manifest["Files"]) != normalize_files(entry.get("Files") or {}):
        problems.append("Files doesn't match the files in the download's manifest.json")
    for field in ("Name", "Author", "Description"):
        if manifest.get(field) and entry.get(field) != manifest.get(field):
            problems.append(f"{field} doesn't match manifest.json ({manifest.get(field)!r})")
    if (manifest.get("Tags") or []) != (entry.get("Tags") or []):
        problems.append(f"Tags don't match manifest.json ({manifest.get('Tags') or []!r})")
    return problems


def check_update(entry, previous):
    if previous is None:
        return []
    problems = []
    if previous.get("Id") != entry.get("Id"):
        problems.append("an existing entry's Id can't be changed")
    order = compare_versions(entry.get("Version"), previous.get("Version"))
    changed = previous.get("Download") != entry.get("Download") or previous.get("Sha256") != entry.get("Sha256")
    if changed and order <= 0:
        problems.append(f"a new download needs a newer Version than {previous.get('Version')}")
    if order < 0:
        problems.append(f"Version can't go back from {previous.get('Version')} to {entry.get('Version')}")
    return problems


def load_entries(directory=None):
    directory = directory or PLUGINS_DIR
    entries, problems = {}, {}
    seen = {}
    for path in sorted(directory.glob("*.json"), key=lambda p: p.name.lower()):
        try:
            entry = json.loads(path.read_text(encoding="utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            problems[path.name] = [f"it isn't valid JSON: {error}"]
            continue
        found = check_entry_shape(entry, path.name)
        plugin_id = entry.get("Id") if isinstance(entry, dict) else None
        if isinstance(plugin_id, str) and plugin_id:
            if plugin_id.lower() in seen:
                found.append(f"Id {plugin_id} is already used by {seen[plugin_id.lower()]}")
            seen.setdefault(plugin_id.lower(), path.name)
        if found:
            problems[path.name] = found
        entries[path.name] = entry
    return entries, problems


def build_index(entries):
    plugins = []
    for entry in sorted(entries.values(), key=lambda e: str(e.get("Id", "")).lower()):
        plugins.append({field: entry[field] for field in FIELDS if field in entry})
    return {"Format": FORMAT, "Name": NAME, "Plugins": plugins}


def index_text(index):
    return json.dumps(index, indent=2, ensure_ascii=False) + "\n"


def previous_entry(base_ref, file_name):
    try:
        text = subprocess.run(["git", "show", f"{base_ref}:plugins/{file_name}"], cwd=ROOT, check=True,
                              capture_output=True, text=True, encoding="utf-8").stdout
    except (subprocess.CalledProcessError, FileNotFoundError):
        return None
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return None


def changed_files(base_ref):
    result = subprocess.run(["git", "diff", "--name-only", "--diff-filter=ACMR", base_ref, "--", "plugins/"],
                            cwd=ROOT, check=True, capture_output=True, text=True)
    return {Path(line).name for line in result.stdout.splitlines() if line.endswith(".json")}


def report(problems):
    for name, found in problems.items():
        for problem in found:
            print(f"{name}: {problem}")


def command_validate(args):
    entries, problems = load_entries()
    if args.base:
        names = changed_files(args.base)
    elif args.files:
        names = {Path(f).name for f in args.files}
    else:
        names = set(entries) if args.download else set()
    for name in sorted(names):
        if name not in entries:
            problems.setdefault(name, []).append("there's no such entry in plugins/")
            continue
        if name in problems:
            continue
        entry = entries[name]
        found = check_update(entry, previous_entry(args.base, name)) if args.base else []
        try:
            found += check_download(entry, fetch(entry["Download"]))
        except (Problem, OSError) as error:
            found.append(f"downloading {entry['Download']} failed: {error}")
        if found:
            problems[name] = found
    if args.check_index and INDEX_FILE.exists() and not args.base:
        if INDEX_FILE.read_text(encoding="utf-8") != index_text(build_index(entries)):
            problems["repo.json"] = ["repo.json is out of date; run: python tools/holocron.py build"]
    report(problems)
    if problems:
        return 1
    checked = f", {len(names)} download{'' if len(names) == 1 else 's'} checked" if names else ""
    print(f"{len(entries)} entr{'y' if len(entries) == 1 else 'ies'} OK{checked}.")
    return 0


def command_build(args):
    entries, problems = load_entries()
    if problems:
        report(problems)
        return 1
    text = index_text(build_index(entries))
    if args.check:
        if not INDEX_FILE.exists() or INDEX_FILE.read_text(encoding="utf-8") != text:
            print("repo.json is out of date; run: python tools/holocron.py build")
            return 1
        print("repo.json is up to date.")
        return 0
    INDEX_FILE.write_text(text, encoding="utf-8", newline="\n")
    print(f"Wrote repo.json with {len(entries)} plugin{'' if len(entries) == 1 else 's'}.")
    return 0


def command_add(args):
    try:
        data = fetch(args.download)
        entry = entry_from_download(data, args.download, args.source, args.license, args.changes)
    except (Problem, OSError) as error:
        print(f"Couldn't use {args.download}: {error}")
        return 1
    path = PLUGINS_DIR / f"{entry.get('Id')}.json"
    previous = json.loads(path.read_text(encoding="utf-8")) if path.exists() else None
    if previous is not None:
        entry["License"] = args.license or previous.get("License")
        entry["Source"] = args.source or previous.get("Source") or entry.get("Source")
        entry = {field: entry[field] for field in FIELDS if entry.get(field) not in (None, "")}
    problems = check_entry_shape(entry, path.name) + check_update(entry, previous)
    if problems:
        report({path.name: problems})
        return 1
    PLUGINS_DIR.mkdir(exist_ok=True)
    path.write_text(json.dumps(entry, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(f"{'Updated' if previous else 'Wrote'} plugins/{path.name} ({entry.get('Name', entry['Id'])} {entry['Version']}).")
    entries, problems = load_entries()
    if problems:
        report(problems)
        print("repo.json wasn't rebuilt because of the problems above.")
        return 1
    INDEX_FILE.write_text(index_text(build_index(entries)), encoding="utf-8", newline="\n")
    print("Rebuilt repo.json.")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(prog="holocron", description="Checks and builds the Holocron plugin list.")
    commands = parser.add_subparsers(dest="command", required=True)

    add = commands.add_parser("add", help="write or update plugins/<Id>.json from a release zip")
    add.add_argument("download", help="https address of the release zip")
    add.add_argument("--license", help="SPDX id of the plugin's license, e.g. AGPL-3.0-or-later")
    add.add_argument("--source", help="https address of the source code (default: Repository in manifest.json)")
    add.add_argument("--changes", help="one line about what's new in this version")
    add.set_defaults(run=command_add)

    validate = commands.add_parser("validate", help="check the entries in plugins/")
    validate.add_argument("files", nargs="*", help="entries whose downloads should be checked")
    validate.add_argument("--download", action="store_true", help="download and check every entry's zip")
    validate.add_argument("--base", help="git ref to compare against; checks downloads of changed entries")
    validate.add_argument("--check-index", action="store_true", help="also fail if repo.json is out of date")
    validate.set_defaults(run=command_validate)

    build = commands.add_parser("build", help="write repo.json from plugins/")
    build.add_argument("--check", action="store_true", help="fail instead of writing if repo.json is out of date")
    build.set_defaults(run=command_build)

    args = parser.parse_args(argv)
    return args.run(args)


if __name__ == "__main__":
    sys.exit(main())
