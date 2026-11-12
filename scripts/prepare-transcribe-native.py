#!/usr/bin/env python3
"""Prepare the pinned Linux x64 native artifact without Node or an installed T3 runtime."""
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import tarfile
import tempfile
import urllib.request


def digest(path):
    with path.open("rb") as handle:
        return hashlib.file_digest(handle, "sha256").hexdigest()


def is_ready(output, pin):
    return all(not (output / name).is_symlink() and (output / name).is_file()
               and digest(output / name) == expected for name, expected in pin["files"].items())


def extract_verified(archive, output, pin):
    if digest(archive) != pin["sha256"]:
        raise ValueError("transcribe.cpp archive SHA-256 does not match the reviewed artifact")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="transcribe-native-", dir=output.parent) as temp:
        staging = Path(temp)
        seen = set()
        with tarfile.open(archive, "r:gz") as source:
            for member in source.getmembers():
                path = PurePosixPath(member.name)
                if not member.isfile() or path.is_absolute() or ".." in path.parts or path.parts[0] != "package":
                    raise ValueError("Unsafe native artifact member: " + member.name)
                name = str(PurePosixPath(*path.parts[1:]))
                # package.json is NPM metadata, not needed at runtime. Everything else is pinned.
                if name == "package.json":
                    continue
                if name not in pin["files"] or name in seen or member.size > 100 * 1024 * 1024:
                    raise ValueError("Unexpected native artifact member: " + member.name)
                seen.add(name)
                target = staging / name
                target.parent.mkdir(parents=True, exist_ok=True)
                with source.extractfile(member) as payload, target.open("wb") as destination:
                    shutil.copyfileobj(payload, destination)
                if digest(target) != pin["files"][name]:
                    raise ValueError("Native file SHA-256 does not match: " + name)
        if seen != set(pin["files"]):
            raise ValueError("Native artifact is missing required dependencies or licenses")
        contract = json.loads((staging / "contract.json").read_text())
        if contract.get("version") != pin["version"] or contract.get("header_hash") != pin["headerHash"]:
            raise ValueError("Native header contract does not match the pinned binding")
        # Publish only after verifying the entire closure; never follow output symlinks.
        for name in pin["files"]:
            target = output / name
            target.parent.mkdir(parents=True, exist_ok=True)
            if target.is_symlink() or any(parent.is_symlink() for parent in target.parents):
                raise ValueError("Native artifact output must not contain symlinks")
        for name in pin["files"]:
            os.replace(staging / name, output / name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--archive", type=Path, help="Optional offline copy of the exact pinned archive")
    args = parser.parse_args()
    pin_path = Path(__file__).resolve().parents[1] / "plugins/TypeWhisper.Plugin.TranscribeCpp/Native/runtime.json"
    pin = json.loads(pin_path.read_text())
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    with (output.parent / "transcribe-native.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        if is_ready(output, pin):
            return
        archive = args.archive or output.parent / (pin["sha256"] + ".tgz")
        if not archive.exists():
            with tempfile.NamedTemporaryFile(dir=output.parent, delete=False) as temp:
                temporary = Path(temp.name)
                try:
                    with urllib.request.urlopen(pin["url"], timeout=120) as response:
                        total = 0
                        while chunk := response.read(1024 * 1024):
                            total += len(chunk)
                            if total > 100 * 1024 * 1024:
                                raise ValueError("Native artifact download exceeds the size ceiling")
                            temp.write(chunk)
                    temp.flush()
                    os.fsync(temp.fileno())
                    if digest(temporary) != pin["sha256"]:
                        raise ValueError("Downloaded native artifact SHA-256 does not match")
                    os.replace(temporary, archive)
                finally:
                    temporary.unlink(missing_ok=True)
        extract_verified(archive, output, pin)


if __name__ == "__main__":
    main()
