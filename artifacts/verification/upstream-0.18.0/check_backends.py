"""Repeatable isolated worker/backend checks. Run from the repository root with uv run --no-project."""
import json
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path.cwd() / "scripts/verification"))
from isolated_app import IsolatedApp, write_json
from hidden_desktop import HiddenDesktop

out = Path(__file__).resolve().parent
clip = Path("/tmp/typewhisper-benchmark/1462-170145-0012.wav")
results = []
with HiddenDesktop(out / "backend-desktop") as desktop:
    for plugin, engine, model, backend in [
        ("com.typewhisper.whisper-cpp", "whisper-cpp", "base", "cpu"),
        ("com.typewhisper.whisper-cpp", "whisper-cpp", "base", "vulkan"),
        ("com.typewhisper.sherpa-onnx", "sherpa-onnx", "parakeet-tdt-0.6b", "webgpu"),
    ]:
        with IsolatedApp(out / f"backend-{engine}-{backend}", configuration="Release",
                         plugin_data=[plugin], virtual_mic=True, display="none",
                         extra_env=desktop.app_env(), settings={
                             "selectedModelId": f"plugin:{plugin}:{model}",
                             "localModelAcceleration": backend,
                             "liveTranscriptionEnabled": False,
                             "cleanupLevel": 0,
                         }) as app:
            load = app.load_model(engine, model)
            result = app.api("/v1/transcribe/local-file", {
                "path": str(clip), "engine": engine, "model": model,
                "language": "en", "applyCorrections": False,
            }, timeout=180)
            status = app.api("/v1/status")
            children = subprocess.run(["pgrep", "-a", "-P", str(app.process.pid)],
                                      capture_output=True, text=True).stdout
            row = {"engine": engine, "requested_backend": backend, "load": load,
                   "result": result, "acceleration": status.get("acceleration"),
                   "children": children}
            results.append(row)
            write_json(out / "backends.json", results)
            assert result.get("text", "").strip(), row
            assert "--transcription-worker" in children, row
            assert status["acceleration"]["active_backend"] == backend, row
print(json.dumps(results, indent=2))
