"""Run the workflow's ZIP creation step locally without publishing anything."""
import argparse
import hashlib
import json
import os
import subprocess
import zipfile
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--out", type=Path, required=True)
parser.add_argument("--pwsh", default="pwsh")
args = parser.parse_args()
root = Path.cwd()
out = args.out.resolve()
out.mkdir(parents=True, exist_ok=False)
workflow = (root / ".github/workflows/publish-plugins.yml").read_text()
step = workflow.split("      - name: Create ZIP\n", 1)[1].split("      - name:", 1)[0]
body = step.split("        run: |\n", 1)[1]
body = "\n".join(line[10:] for line in body.splitlines())
body = body.replace(". ./scripts/publish-plugin-release.ps1", ". '" + str(root / "scripts/publish-plugin-release.ps1") + "'")
assert "Invoke-PluginReleaseTransaction" not in body
inventories = []
for project, required in [
    ("SherpaOnnx", ["Runtimes/linux-x64/sherpa-webgpu/" + name for name in [
        "libsherpa-onnx-c-api.so", "libsherpa-onnx-cxx-api.so", "libonnxruntime.so.1",
        "libonnxruntime_providers_shared.so", "libonnxruntime_providers_webgpu.so"]]
        + ["Runtimes/linux-x64/sherpa-migraphx/" + name for name in ["libsherpa-onnx-c-api.so", "libsherpa-onnx-cxx-api.so"]]),
    ("WhisperCpp", ["runtimes/vulkan/linux-x64/" + name for name in [
        "libwhisper.so", "libggml-whisper.so", "libggml-vulkan-whisper.so", "libggml-cpu-whisper.so", "libggml-base-whisper.so"]]),
]:
    directory = root / "plugins" / ("TypeWhisper.Plugin." + project)
    manifest = json.loads((directory / "manifest.json").read_text())
    env = {**os.environ, "PROJECT_DIRECTORY": str(directory), "PLUGIN_RID": "linux-x64",
           "PLUGIN_VERSION": manifest["version"], "GITHUB_ENV": str(out / "workflow-env.txt")}
    result = subprocess.run([args.pwsh, "-NoLogo", "-NoProfile", "-Command",
                             "$ErrorActionPreference = 'Stop'\n" + body], cwd=out, env=env,
                            capture_output=True, text=True)
    (out / (project + ".log")).write_text(result.stdout + result.stderr)
    result.check_returncode()
    archive = out / (manifest["id"] + "-" + manifest["version"] + ".zip")
    with zipfile.ZipFile(archive) as zipped:
        names = zipped.namelist()
        missing = sorted(set(required) - set(names))
        hashes = {name: hashlib.sha256(zipped.read(name)).hexdigest() for name in required if name in names}
        assert all(hashlib.sha256((directory / "bin/Release/net10.0" / name).read_bytes()).hexdigest() == value
                   for name, value in hashes.items()), "ZIP native payload differs from built plugin"
        inventories.append({"plugin": manifest["id"], "archive": str(archive), "entries": names,
                            "required_native_sha256": hashes, "missing": missing})
    (out / "inventory.json").write_text(json.dumps(inventories, indent=2) + "\n")
    assert not missing, (manifest["id"], missing)
    destination = out / "extracted" / manifest["id"]
    with zipfile.ZipFile(archive) as zipped:
        zipped.extractall(destination)
print(json.dumps({"archives": len(inventories), "all_required_native_files_present": True}, indent=2))
