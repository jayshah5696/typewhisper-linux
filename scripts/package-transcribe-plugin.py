#!/usr/bin/env python3
"""Validate a deployed transcribe.cpp payload and create its independent plugin ZIP.

Publish with the same Version as the host, exclude the host-provided PluginSDK,
then run the host's plugin compatibility check against the extracted archive.
This checks file identity, not managed ABI compatibility or GPU execution.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import tempfile
import zipfile

ASSEMBLY = "TypeWhisper.Plugin.TranscribeCpp.dll"
ENTRY_POINT = "TypeWhisper.Plugin.TranscribeCpp.TranscribeCppPlugin"
NATIVE_PREFIX = "native/linux-x64/"


def validate_payload(payload, pin):
    expected = {"manifest.json", ASSEMBLY, "TypeWhisper.Plugin.TranscribeCpp.deps.json"}
    expected.update(NATIVE_PREFIX + name for name in pin["files"])
    actual = set()
    for path in payload.rglob("*"):
        if path.is_symlink():
            raise ValueError("Plugin payload must not contain symlinks: " + str(path))
        if path.is_file():
            actual.add(path.relative_to(payload).as_posix())
    if actual != expected:
        raise ValueError(f"Plugin payload differs from the reviewed closure. Missing: {sorted(expected - actual)}; extra: {sorted(actual - expected)}")
    validate_contents(lambda name: (payload / name).open("rb"), pin)
    return sorted(expected)


def validate_contents(open_file, pin):
    with open_file("manifest.json") as handle:
        manifest = json.load(handle)
    if (manifest.get("id") != "com.typewhisper.transcribe-cpp"
            or manifest.get("assemblyName") != ASSEMBLY or manifest.get("pluginClass") != ENTRY_POINT):
        raise ValueError("Plugin manifest does not name the reviewed entry point")
    for name, expected_digest in pin["files"].items():
        with open_file(NATIVE_PREFIX + name) as handle:
            if hashlib.file_digest(handle, "sha256").hexdigest() != expected_digest:
                raise ValueError("Native file digest mismatch: " + name)
    with open_file(NATIVE_PREFIX + "contract.json") as handle:
        contract = json.load(handle)
    if contract.get("version") != pin["version"] or contract.get("header_hash") != pin["headerHash"]:
        raise ValueError("Native header contract does not match the binding")


def build_package(payload, output, pin):
    names = validate_payload(payload, pin)
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=output.parent, suffix=".zip", delete=False) as temporary:
        staging = Path(temporary.name)
    try:
        with zipfile.ZipFile(staging, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name in names:
                info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                archive.writestr(info, (payload / name).read_bytes())
        # Check the bytes that will ship, including the complete license set.
        with zipfile.ZipFile(staging) as archive:
            if archive.testzip() is not None or archive.namelist() != names:
                raise ValueError("Plugin ZIP verification failed")
            validate_contents(archive.open, pin)
        os.replace(staging, output)
    finally:
        staging.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("payload", type=Path, help="Deployed plugin directory without PluginSDK or PDB files")
    parser.add_argument("output", type=Path, help="Independent plugin ZIP")
    args = parser.parse_args()
    pin_path = Path(__file__).resolve().parents[1] / "plugins/TypeWhisper.Plugin.TranscribeCpp/Native/runtime.json"
    build_package(args.payload.resolve(), args.output.resolve(), json.loads(pin_path.read_text()))
    print(args.output)


if __name__ == "__main__":
    main()
