import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SCRIPT = Path(__file__).parents[2] / "scripts/package-transcribe-plugin.py"
spec = importlib.util.spec_from_file_location("plugin_package", SCRIPT)
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


class TranscribePluginPackageTests(unittest.TestCase):
    def fixture(self, root):
        payload = root / "payload"
        payload.mkdir()
        files = {
            "libtranscribe.so": b"native fixture",
            "contract.json": b'{"version":"0.4.1","header_hash":"header"}',
            "licenses/LICENSE": b"MIT fixture",
        }
        for name, data in files.items():
            target = payload / "native/linux-x64" / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        (payload / "manifest.json").write_text(json.dumps({
            "id": "com.typewhisper.transcribe-cpp", "assemblyName": "TypeWhisper.Plugin.TranscribeCpp.dll",
            "pluginClass": "TypeWhisper.Plugin.TranscribeCpp.TranscribeCppPlugin", "version": "1.0.0",
        }))
        (payload / "TypeWhisper.Plugin.TranscribeCpp.dll").write_bytes(b"managed fixture")
        (payload / "TypeWhisper.Plugin.TranscribeCpp.deps.json").write_text("{}")
        pin = {"version": "0.4.1", "headerHash": "header", "files": {
            name: hashlib.sha256(data).hexdigest() for name, data in files.items()}}
        return payload, pin

    def test_archive_is_independent_reproducible_and_preserves_licenses(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            payload, pin = self.fixture(root)
            first, second = root / "first.zip", root / "second.zip"
            package.build_package(payload, first, pin)
            package.build_package(payload, second, pin)
            self.assertEqual(first.read_bytes(), second.read_bytes())
            with zipfile.ZipFile(first) as archive:
                self.assertIn("manifest.json", archive.namelist())
                self.assertEqual(b"MIT fixture", archive.read("native/linux-x64/licenses/LICENSE"))
                self.assertNotIn("TypeWhisper.PluginSDK.dll", archive.namelist())

    def test_missing_or_corrupt_pinned_files_never_replace_existing_zip(self):
        for change in ("missing", "corrupt"):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                payload, pin = self.fixture(root)
                output = root / "plugin.zip"
                output.write_bytes(b"previous good archive")
                license_path = payload / "native/linux-x64/licenses/LICENSE"
                if change == "missing":
                    license_path.unlink()
                else:
                    license_path.write_bytes(b"corrupt")
                with self.assertRaises(ValueError):
                    package.build_package(payload, output, pin)
                self.assertEqual(b"previous good archive", output.read_bytes())

    def test_changed_file_between_validation_and_archiving_is_not_published(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            payload, pin = self.fixture(root)
            validate = package.validate_payload

            def mutate_after_validation(source, expected):
                names = validate(source, expected)
                (source / "native/linux-x64/libtranscribe.so").write_bytes(b"changed after validation")
                return names

            with patch.object(package, "validate_payload", side_effect=mutate_after_validation):
                with self.assertRaises(ValueError):
                    package.build_package(payload, root / "plugin.zip", pin)
            self.assertFalse((root / "plugin.zip").exists())

    def test_rejects_stale_native_private_sdk_and_unreviewed_dependencies(self):
        for extra in ("native/linux-x64/libggml-cuda.so", "TypeWhisper.PluginSDK.dll", "node_modules/index.js", "other.dll"):
            with self.subTest(extra=extra), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                payload, pin = self.fixture(root)
                target = payload / extra
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(b"unexpected")
                with self.assertRaises(ValueError):
                    package.build_package(payload, root / "plugin.zip", pin)
                self.assertFalse((root / "plugin.zip").exists())

    def test_rejects_symlinks_even_when_target_matches_pin(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            payload, pin = self.fixture(root)
            target = payload / "native/linux-x64/libtranscribe.so"
            outside = root / "external.so"
            target.rename(outside)
            target.symlink_to(outside)
            with self.assertRaises(ValueError):
                package.build_package(payload, root / "plugin.zip", pin)

    def test_rejects_wrong_header_or_entry_point(self):
        for change in ("header", "entry"):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                payload, pin = self.fixture(root)
                if change == "header":
                    pin["headerHash"] = "wrong"
                else:
                    manifest = payload / "manifest.json"
                    manifest.write_text(manifest.read_text().replace("TranscribeCppPlugin", "MissingPlugin"))
                with self.assertRaises(ValueError):
                    package.build_package(payload, root / "plugin.zip", pin)
