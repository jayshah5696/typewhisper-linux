#!/usr/bin/env python3
"""Upgrade a scratch copy of the installed app, preserve user data, and verify Needle."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

from isolated_app import run_manifest, write_json

ROOT = Path(__file__).resolve().parents[2]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    out = args.out.resolve()
    if out.exists():
        raise RuntimeError(f"Use a fresh evidence directory: {out}")
    out.mkdir(parents=True)
    home = out / "home"
    data = home / ".local/share"
    installed = data / "typewhisper-app"
    source = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share")) / "typewhisper-app"
    # Copy only program files. User credentials, settings, and History never enter this fixture.
    if source.is_dir():
        installed.parent.mkdir(parents=True)
        subprocess.run(["cp", "-a", "--reflink=auto", str(source), str(installed)], check=True)
    else:
        home.mkdir(parents=True)
    sentinels = {"settings.json": '{"selectedModelId":"plugin:com.typewhisper.needle-whistle:whistle"}',
                 "Data/history.json": '[{"FinalText":"Keep this history"}]',
                 "PluginData/com.test/settings.json": '{"setting":"Keep this setting"}',
                 "Models/keep.model": "Keep this model", "Audio/keep.wav": "Keep this recording"}
    for name, text in sentinels.items():
        path = data / "TypeWhisper" / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)
    env = {**os.environ, "HOME": str(home), "XDG_DATA_HOME": str(data),
           "XDG_STATE_HOME": str(home / ".local/state"), "XDG_CONFIG_HOME": str(home / ".config")}
    with (out / "installer.log").open("w") as log:
        subprocess.run(["bash", str(ROOT / "scripts/install-linux-app.sh"), "--from-publish"],
                       cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
    checks = {name: (data / "TypeWhisper" / name).read_text() == text for name, text in sentinels.items()}
    desktop = (data / "applications/typewhisper.desktop").read_text()
    checks["desktop_launches_upgraded_payload"] = f"Exec={installed}/typewhisper\n" in desktop
    checks["launcher_targets_upgraded_payload"] = (home / ".local/bin/typewhisper").resolve() == installed / "typewhisper"
    write_json(out / "preservation.json", checks)
    if not all(checks.values()):
        raise RuntimeError(f"Upgrade changed user data or left an old launcher: {checks}")
    subprocess.run([sys.executable, str(ROOT / "scripts/verification/needle_smoke.py"),
                    "--audio", str(ROOT / "tests/fixtures/librispeech-1462-170145-0012.wav"),
                    "--out", str(out / "needle"), "--app-dir", str(installed),
                    "--desktop-file", str(data / "applications/typewhisper.desktop")], cwd=ROOT, check=True)
    write_json(out / "run.json", run_manifest({"kind": "local-install-upgrade", "previous_payload": str(source),
                                              "preservation": checks, "installed_payload": str(installed)}))
    print(json.dumps({"upgrade_checks": checks, "evidence": str(out)}, indent=2))


if __name__ == "__main__":
    main()
