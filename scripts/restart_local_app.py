#!/usr/bin/env python3
"""Restart the app serving this session's control socket and verify its runtime identity."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import select
import signal
import socket
import struct
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def control_paths() -> list[Path]:
    data = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share"))
    paths = [data / "TypeWhisper/Runtime/control.sock"]
    if os.environ.get("XDG_RUNTIME_DIR"):
        paths.insert(0, Path(os.environ["XDG_RUNTIME_DIR"]) / "typewhisper/control.sock")
    usable = [path for path in paths if len(os.fsencode(path)) < 108]
    if not usable:
        raise RuntimeError("No control socket path fits the Unix socket limit. Set XDG_RUNTIME_DIR to a short private directory.")
    return usable


def read_status(path: Path) -> tuple[int, dict]:
    with socket.socket(socket.AF_UNIX) as client:
        client.settimeout(2)
        client.connect(str(path))
        pid, uid, _ = struct.unpack("3i", client.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
        if uid != os.getuid():
            raise RuntimeError("Control socket peer belongs to another user")
        client.sendall(b'{"v":1,"cmd":"status"}\n')
        response = bytearray()
        while b"\n" not in response:
            chunk = client.recv(4096)
            if not chunk or len(response) + len(chunk) > 65536:
                raise RuntimeError("Control socket returned an incomplete or oversized status")
            response.extend(chunk)
    status = json.loads(response.split(b"\n", 1)[0])
    if status.get("ok") is not True or status.get("process_id", pid) != pid:
        raise RuntimeError("Control status does not match its socket peer")
    return pid, status


def current_app() -> tuple[int, dict] | None:
    for path in control_paths():
        try:
            return read_status(path)
        except (FileNotFoundError, ConnectionRefusedError):
            continue
    return None


def require_idle(status: dict) -> None:
    if status.get("state") != "idle" or status.get("dictation_in_flight"):
        raise RuntimeError("TypeWhisper must be idle before an upgrade or restart. Finish dictation and retry.")


def stop_app(pid: int, status: dict) -> None:
    require_idle(status)
    # Pin the process identity before signalling; do not stop unrelated isolated app instances.
    descriptor = os.pidfd_open(pid)
    try:
        command = (Path("/proc") / str(pid) / "cmdline").read_bytes().split(b"\0")
        if not any(Path(part.decode(errors="replace")).name in ("typewhisper", "typewhisper.dll") for part in command if part):
            raise RuntimeError("Control socket peer is not a TypeWhisper process")
        signal.pidfd_send_signal(descriptor, signal.SIGTERM)
        if not select.select([descriptor], [], [], 15)[0]:
            raise RuntimeError(f"TypeWhisper PID {pid} did not exit after SIGTERM; no second app was started")
    finally:
        os.close(descriptor)


def validate_started(status: dict, pid: int, assembly: Path, version: str, plugins: Path) -> None:
    expected = {"process_id": pid, "app_version": version, "app_path": str(assembly.resolve()),
                "plugins_path": str(plugins.resolve())}
    for key, value in expected.items():
        if status.get(key) != value:
            raise RuntimeError(f"Started app identity mismatch for {key}: expected {value!r}, got {status.get(key)!r}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--installed", action="store_true")
    parser.add_argument("--check-idle", action="store_true")
    parser.add_argument("--stop-only", action="store_true")
    args = parser.parse_args()
    running = current_app()
    if running:
        require_idle(running[1])
    if args.check_idle:
        return 0
    if args.stop_only:
        if running:
            stop_app(*running)
        return 0

    data = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share"))
    if args.installed:
        directory = data / "typewhisper-app"
        command = [str(directory / "typewhisper")]
        plugins = data / "TypeWhisper/Plugins"
    else:
        directory = ROOT / "src/TypeWhisper.Linux/bin/Debug/net10.0"
        command = [os.environ.get("DOTNET", "dotnet"), str(directory / "typewhisper.dll")]
        plugins = ROOT / "artifacts/local-dev/Plugins"
    assembly = directory / "typewhisper.dll"
    if not assembly.is_file():
        raise RuntimeError(f"App assembly is missing: {assembly}")
    version = next(node.text.strip() for node in ET.parse(ROOT / "Directory.Build.props").getroot().iter("Version") if node.text)
    if running:
        stop_app(*running)

    env = dict(os.environ)
    if args.installed:
        env.pop("TYPEWHISPER_PLUGINS_PATH", None)
    else:
        env["TYPEWHISPER_PLUGINS_PATH"] = str(plugins)
    log = Path(os.environ.get("XDG_STATE_HOME", Path.home() / ".local/state")) / "typewhisper" / ("installed.log" if args.installed else "local-dev.log")
    log.parent.mkdir(parents=True, exist_ok=True)
    with log.open("wb") as output:
        process = subprocess.Popen(command, env=env, stdin=subprocess.DEVNULL, stdout=output,
                                   stderr=subprocess.STDOUT, start_new_session=True)
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"TypeWhisper exited during startup. See {log}")
        ready = current_app()
        if ready:
            validate_started(ready[1], process.pid, assembly, version, plugins)
            print(json.dumps({"started": ready[1], "log": str(log)}, indent=2))
            return 0
        time.sleep(.2)
    raise RuntimeError(f"TypeWhisper did not become ready within 30 seconds. See {log}")


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, RuntimeError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        sys.exit(1)
