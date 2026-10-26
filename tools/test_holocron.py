import hashlib
import io
import json
import subprocess
import sys
import tempfile
import unittest
import zipfile
from contextlib import redirect_stdout
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import holocron

DOWNLOAD = "https://github.com/someone/CombatCounter/releases/download/v1.0.0/CombatCounter-1.0.0.zip"


def plugin_files(version="1.0.0", api_level=1, plugin_id="CombatCounter"):
    files = {
        f"{plugin_id}.dll": b"MZ fake plugin " + version.encode(),
        f"{plugin_id}.deps.json": b"{}",
    }
    manifest = {
        "Id": plugin_id,
        "Name": "Combat Counter",
        "Version": version,
        "Author": "Someone",
        "Description": "Counts combat events.",
        "ApiLevel": api_level,
        "Repository": "https://github.com/someone/CombatCounter",
        "Tags": ["combat"],
        "Files": {name: hashlib.sha256(data).hexdigest() for name, data in files.items()},
    }
    files["manifest.json"] = json.dumps(manifest).encode()
    return files


def make_zip(files, folder=None):
    memory = io.BytesIO()
    with zipfile.ZipFile(memory, "w") as archive:
        for name, data in files.items():
            archive.writestr(f"{folder}/{name}" if folder else name, data)
    return memory.getvalue()


class HolocronToolTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        (root / "plugins").mkdir()
        self.saved = holocron.ROOT, holocron.PLUGINS_DIR, holocron.INDEX_FILE, holocron.fetch
        holocron.ROOT, holocron.PLUGINS_DIR, holocron.INDEX_FILE = root, root / "plugins", root / "repo.json"
        self.web = {}
        holocron.fetch = lambda url: self.web[url] if url in self.web else (_ for _ in ()).throw(OSError("not found"))

    def tearDown(self):
        holocron.ROOT, holocron.PLUGINS_DIR, holocron.INDEX_FILE, holocron.fetch = self.saved
        self.temp.cleanup()

    def run_tool(self, *args):
        output = io.StringIO()
        with redirect_stdout(output):
            code = holocron.main(list(args))
        return code, output.getvalue()

    def entry(self):
        return json.loads((holocron.PLUGINS_DIR / "CombatCounter.json").read_text())

    def write_entry(self, entry, name="CombatCounter.json"):
        (holocron.PLUGINS_DIR / name).write_text(json.dumps(entry, indent=2))

    def test_add_fills_the_entry_from_the_zip(self):
        self.web[DOWNLOAD] = make_zip(plugin_files(), folder="CombatCounter")
        code, output = self.run_tool("add", DOWNLOAD, "--license", "AGPL-3.0-or-later", "--changes", "First release.")
        self.assertEqual(code, 0, output)
        entry = self.entry()
        self.assertEqual(entry["Version"], "1.0.0")
        self.assertEqual(entry["Source"], "https://github.com/someone/CombatCounter")
        self.assertEqual(entry["Sha256"], hashlib.sha256(self.web[DOWNLOAD]).hexdigest())
        self.assertEqual(list(entry)[0], "Id")
        self.assertEqual(self.run_tool("validate", "CombatCounter.json")[0], 0)

    def test_add_needs_a_license(self):
        self.web[DOWNLOAD] = make_zip(plugin_files())
        code, output = self.run_tool("add", DOWNLOAD)
        self.assertEqual(code, 1)
        self.assertIn("License is missing", output)

    def test_add_keeps_license_and_source_for_updates_and_needs_a_newer_version(self):
        self.web[DOWNLOAD] = make_zip(plugin_files())
        self.run_tool("add", DOWNLOAD, "--license", "MIT", "--source", "https://example.org/src")
        newer = DOWNLOAD.replace("1.0.0", "1.1.0")
        self.web[newer] = make_zip(plugin_files("1.1.0"))
        self.assertEqual(self.run_tool("add", newer, "--changes", "Faster.")[0], 0)
        entry = self.entry()
        self.assertEqual((entry["Version"], entry["License"], entry["Source"]), ("1.1.0", "MIT", "https://example.org/src"))

        same = DOWNLOAD.replace("1.0.0", "1.1.0-rebuilt")
        self.web[same] = make_zip(plugin_files("1.1.0") | {"extra.txt": b"x"})
        code, output = self.run_tool("add", same)
        self.assertEqual(code, 1)
        self.assertIn("needs a newer Version than 1.1.0", output)

    def test_validate_catches_a_wrong_checksum_and_mismatched_fields(self):
        data = make_zip(plugin_files())
        self.web[DOWNLOAD] = data
        self.run_tool("add", DOWNLOAD, "--license", "MIT")
        entry = self.entry()
        entry["Sha256"] = "0" * 64
        entry["Version"] = "2.0.0"
        entry["Description"] = "Something else."
        self.write_entry(entry)
        code, output = self.run_tool("validate", "CombatCounter.json")
        self.assertEqual(code, 1)
        self.assertIn("Sha256 doesn't match", output)
        self.assertIn("the download is version 1.0.0, but the entry says 2.0.0", output)
        self.assertIn("Description doesn't match", output)

    def test_validate_catches_bad_zips(self):
        files = plugin_files()
        bad = {
            "slip": make_zip(files | {"../evil.txt": b"x"}),
            "unlisted": make_zip(files | {"Extra.dll": b"x"}),
            "changed": make_zip(files | {"CombatCounter.dll": b"changed"}),
            "nomanifest": make_zip({k: v for k, v in files.items() if k != "manifest.json"}),
            "notzip": b"hello",
        }
        expected = {
            "slip": "outside its folder",
            "unlisted": "Extra.dll isn't listed",
            "changed": "doesn't match its hash",
            "nomanifest": "no manifest.json",
            "notzip": "isn't a zip",
        }
        for key, data in bad.items():
            with self.subTest(key):
                problems = holocron.check_download({"Id": "CombatCounter", "Sha256": hashlib.sha256(data).hexdigest()}, data)
                self.assertTrue(any(expected[key] in p for p in problems), problems)

    def test_shape_rules(self):
        self.web[DOWNLOAD] = make_zip(plugin_files())
        self.run_tool("add", DOWNLOAD, "--license", "MIT")
        entry = self.entry()
        bad = dict(entry, License="Proprietary", Download="http://example.org/x.zip", ApiLevel=99, Extra=1)
        self.write_entry(bad, "Wrong.json")
        code, output = self.run_tool("validate")
        self.assertEqual(code, 1)
        for text in ["isn't on the list", "Download must be an https://", "ApiLevel 99", "Extra isn't a field",
                     "must be named CombatCounter.json", "already used by CombatCounter.json"]:
            self.assertIn(text, output)

    def test_build_writes_a_sorted_index_that_ashla_can_read(self):
        for plugin_id in ("Zeta", "alpha"):
            url = f"https://example.org/{plugin_id}.zip"
            self.web[url] = make_zip(plugin_files(plugin_id=plugin_id))
            self.assertEqual(self.run_tool("add", url, "--license", "MIT")[0], 0)
        self.assertEqual(self.run_tool("build", "--check")[0], 0)
        holocron.INDEX_FILE.write_text("{}")
        self.assertEqual(self.run_tool("build", "--check")[0], 1)
        self.assertEqual(self.run_tool("validate", "--check-index")[0], 1)
        self.assertEqual(self.run_tool("build")[0], 0)
        index = json.loads(holocron.INDEX_FILE.read_text())
        self.assertEqual(index["Format"], 1)
        self.assertEqual([p["Id"] for p in index["Plugins"]], ["alpha", "Zeta"])
        self.assertEqual(self.run_tool("build", "--check")[0], 0)
        self.assertEqual(self.run_tool("validate", "--check-index")[0], 0)

    def test_validate_against_a_base_checks_only_changed_entries_and_version_rules(self):
        root = holocron.ROOT
        git = lambda *args: subprocess.run(["git", *args], cwd=root, check=True, capture_output=True)
        git("init", "-q")
        git("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "start")
        self.web[DOWNLOAD] = make_zip(plugin_files())
        self.run_tool("add", DOWNLOAD, "--license", "MIT")
        git("add", ".")
        git("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "add")
        self.assertEqual(self.run_tool("validate", "--base", "HEAD")[1].strip(), "1 entry OK.")

        rebuilt = DOWNLOAD.replace(".zip", "-b.zip")
        self.web[rebuilt] = make_zip(plugin_files() | {"readme.txt": b"x"})
        entry = self.entry()
        entry["Download"], entry["Sha256"] = rebuilt, hashlib.sha256(self.web[rebuilt]).hexdigest()
        self.write_entry(entry)
        code, output = self.run_tool("validate", "--base", "HEAD")
        self.assertEqual(code, 1)
        self.assertIn("needs a newer Version than 1.0.0", output)

    def test_versions_compare_like_ashla(self):
        self.assertEqual(holocron.compare_versions("0.10.0", "0.9.5"), 1)
        self.assertEqual(holocron.compare_versions("v1.2", "1.1.9"), 1)
        self.assertEqual(holocron.compare_versions("1.0.0-beta", "1.0.0"), 0)
        self.assertEqual(holocron.compare_versions("2", "1.9"), 1)
        self.assertEqual(holocron.compare_versions("1.0", "1.0.0"), -1)


if __name__ == "__main__":
    unittest.main()
