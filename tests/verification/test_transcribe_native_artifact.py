import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("native_artifact", Path(__file__).parents[2] / "scripts/prepare-transcribe-native.py")
artifact = importlib.util.module_from_spec(spec)
spec.loader.exec_module(artifact)


class NativeArtifactTests(unittest.TestCase):
    def bundle(self, root, *, unsafe=False):
        archive = root / "fixture.tgz"
        files = {"libtranscribe.so": b"ELF fixture", "contract.json": b'{"version":"0.4.1","header_hash":"f99ad4169374b8f3"}', "licenses/LICENSE": b"MIT fixture"}
        with tarfile.open(archive, "w:gz") as output:
            for name, data in files.items():
                info = tarfile.TarInfo("package/" + name)
                info.size = len(data)
                output.addfile(info, io.BytesIO(data))
            if unsafe:
                info = tarfile.TarInfo("package/../outside")
                info.size = 1
                output.addfile(info, io.BytesIO(b"x"))
        pin = {"version": "0.4.1", "headerHash": "f99ad4169374b8f3", "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
               "files": {name: hashlib.sha256(data).hexdigest() for name, data in files.items()}}
        return archive, pin, files

    def test_verified_bundle_preserves_licenses_and_detects_later_tampering(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, pin, files = self.bundle(root)
            output = root / "native"
            artifact.extract_verified(archive, output, pin)
            self.assertTrue(artifact.is_ready(output, pin))
            self.assertEqual(files["licenses/LICENSE"], (output / "licenses/LICENSE").read_bytes())
            (output / "libtranscribe.so").write_bytes(b"wrong")
            self.assertFalse(artifact.is_ready(output, pin))

    def test_corrupt_archive_does_not_publish_files(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, pin, _ = self.bundle(root)
            archive.write_bytes(b"corrupt")
            with self.assertRaises(ValueError):
                artifact.extract_verified(archive, root / "native", pin)
            self.assertFalse((root / "native/libtranscribe.so").exists())

    def test_unlisted_paths_and_unsafe_members_are_rejected_before_publication(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, pin, _ = self.bundle(root, unsafe=True)
            with self.assertRaises(ValueError):
                artifact.extract_verified(archive, root / "native", pin)
            self.assertFalse((root / "outside").exists())
            self.assertFalse((root / "native/libtranscribe.so").exists())

    def test_wrong_header_contract_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, pin, _ = self.bundle(root)
            pin["headerHash"] = "wrong"
            with self.assertRaises(ValueError):
                artifact.extract_verified(archive, root / "native", pin)

    def test_file_digest_mismatch_never_replaces_a_good_bundle(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, pin, files = self.bundle(root)
            output = root / "native"
            artifact.extract_verified(archive, output, pin)
            pin["files"]["libtranscribe.so"] = "0" * 64
            with self.assertRaises(ValueError):
                artifact.extract_verified(archive, output, pin)
            self.assertEqual(files["libtranscribe.so"], (output / "libtranscribe.so").read_bytes())
