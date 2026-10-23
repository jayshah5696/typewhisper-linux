"""Exercise new appearance controls and fork model/recovery settings in a private desktop."""
import json
import argparse
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path.cwd() / "scripts/verification"))
from hidden_desktop import HiddenDesktop, all_names
from isolated_app import IsolatedApp, write_json
from recovery_smoke import click, checkpoint, private_x11_env, wait_for

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--out", type=Path, default=Path(__file__).resolve().parent / "ui-live")
out = parser.parse_args().out.resolve()
if out.exists():
    raise RuntimeError("Choose a fresh UI evidence directory")
catalog = json.loads(Path("src/TypeWhisper.Linux/Resources/Localization/en.json").read_text())
checks = {}

with HiddenDesktop(out / "desktop") as desktop:
    with IsolatedApp(out / "app", configuration="Release", minimized=False,
                     plugin_data=["com.typewhisper.sherpa-onnx"], display="none",
                     settings={"selectedModelId": "plugin:com.typewhisper.sherpa-onnx:parakeet-tdt-0.6b"},
                     extra_env=private_x11_env(desktop.app_env())) as app:
        saved = app.instance / "data/TypeWhisper/settings.json"

        def settings():
            return json.loads(saved.read_text())

        def wait_setting(key, expected):
            deadline = time.monotonic() + 10
            while settings().get(key) != expected:
                if time.monotonic() > deadline:
                    checkpoint(desktop, out, "failure")
                    raise AssertionError((key, expected, settings().get(key)))
                time.sleep(.1)

        wait_for(desktop, lambda names: "Appearance" in names, "main navigation")
        click(desktop, "Appearance")
        wait_for(desktop, lambda names: catalog["Appearance.ThemeHint"] in names, "appearance settings")
        checkpoint(desktop, out, "appearance-dark")
        click(desktop, "Theme")
        wait_for(desktop, lambda names: "Light" in names, "theme choices")
        click(desktop, "Light")
        wait_setting("interfaceTheme", 1)
        checks["theme_light_saved_from_ui"] = True
        checkpoint(desktop, out, "appearance-light")
        click(desktop, "Live text position")
        wait_for(desktop, lambda names: "Floating window" in names, "live text choices")
        click(desktop, "Floating window")
        wait_setting("liveTextFloating", True)
        checks["floating_live_text_saved_from_ui"] = True
        click(desktop, "Preview")
        wait_for(desktop, lambda names: catalog["LiveText.PreviewSample"] in names, "floating preview text")
        checks["floating_preview_displays_sample_text"] = True
        checkpoint(desktop, out, "floating-preview")
        click(desktop, "Stop preview")
        wait_for(desktop, lambda names: catalog["LiveText.PreviewSample"] not in names, "closed preview")
        checks["preview_stopped_from_ui"] = True
        click(desktop, "Dictation")
        names = all_names(wait_for(desktop, lambda names: "Local models" in names, "local model cards"))
        checks["local_model_cards_visible"] = "Parakeet TDT 0.6B" in names or any("Parakeet" in name for name in names)
        checks["fork_wait_limit_control_visible"] = catalog["Dictation.CleanupWaitLimit"] in names
        checkpoint(desktop, out, "dictation-model-cards")
        click(desktop, catalog["Dictation.LocalModelAcceleration"])
        names = all_names(wait_for(desktop, lambda names: "WebGPU" in names, "GPU choices"))
        checks["fork_gpu_options_retained"] = all(name in names for name in [catalog["Dictation.AccelerationAmdMigraphx"], "WebGPU"])
        desktop.input([{"keys": [0xff1b]}])
        click(desktop, "Appearance")
        click(desktop, "Theme")
        click(desktop, "Dark")
        wait_setting("interfaceTheme", 2)
        checks["theme_dark_saved_from_ui"] = True
        write_json(out / "result.json", {"checks": checks, "saved_theme": settings().get("interfaceTheme")})
        assert all(checks.values()), checks
print(json.dumps(checks, indent=2))
