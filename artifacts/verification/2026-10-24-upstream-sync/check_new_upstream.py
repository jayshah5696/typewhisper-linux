"""Drive new upstream controls, loopback aliases, and CJK History in a private desktop."""
import argparse
import json
import socket
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

sys.path.insert(0, str(Path.cwd() / "scripts/verification"))
from hidden_desktop import HiddenDesktop, all_names
from isolated_app import IsolatedApp, write_json, run_manifest
from recovery_smoke import X11Clipboard, checkpoint, click, private_x11_env, wait_for, wait_clipboard

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--out", type=Path, required=True)
out = parser.parse_args().out.resolve()
if out.exists():
    raise RuntimeError("Choose a fresh output directory")
out.mkdir(parents=True)
catalog_path = Path("src/TypeWhisper.Linux/Resources/Localization")
catalogs = {lang: json.loads((catalog_path / (lang + ".json")).read_text()) for lang in ["en", "ja", "zh"]}
requests = []
rejected = []
checks = {}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, value):
        data = json.dumps(value).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path != "/v1/models":
            rejected.append(self.path)
            self.send_error(404)
            return
        self.reply({"data": [{"id": "fixture-alias", "object": "model"}]})

    def do_POST(self):
        if self.path != "/v1/chat/completions":
            rejected.append(self.path)
            self.send_error(404)
            return
        payload = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        requests.append(payload)
        self.reply({"choices": [{"message": {"role": "assistant", "content": '["fix ture", "fiksture"]'}}]})


def status(app):
    with socket.socket(socket.AF_UNIX) as control:
        control.settimeout(5)
        control.connect(str(app.runtime / "typewhisper/control.sock"))
        control.sendall(b'{"v":1,"cmd":"status"}\n')
        return json.loads(control.recv(16384))


def focus_text(desktop, name, combo=False):
    script = r'''
import gi,json,sys
gi.require_version('Atspi','2.0')
from gi.repository import Atspi
def focus(node):
    if node is None: return False
    roles=('combo box',) if sys.argv[2]=='combo' else ('text','entry')
    if node.get_name()==sys.argv[1] and node.get_role_name() in roles:
        if sys.argv[2]=='combo':
            action=node.get_action_iface()
            if action is not None and action.get_n_actions()>0 and action.do_action(0): return True
        else:
            component=node.get_component_iface()
            if component is not None and component.grab_focus(): return True
    return any(focus(node.get_child_at_index(i)) for i in range(node.get_child_count()))
print(json.dumps({'focused':focus(Atspi.get_desktop(0))}))
'''
    value = subprocess.check_output(["/usr/bin/python3", "-c", script, name, "combo" if combo else "text"],
                                    env=desktop.env, text=True, timeout=10)
    assert json.loads(value)["focused"], value


def simple_names(desktop):
    # Some Avalonia combo peers expose an unsupported Text interface. Read names
    # and children directly so their selected model label remains observable.
    script = r'''
import gi,json
gi.require_version('Atspi','2.0')
from gi.repository import Atspi
def names(node,depth=0):
    if node is None or depth>40: return []
    result=[]
    try:
        if node.get_name(): result.append(node.get_name())
        for i in range(node.get_child_count()): result.extend(names(node.get_child_at_index(i),depth+1))
    except Exception: pass
    return result
print(json.dumps(names(Atspi.get_desktop(0))))
'''
    return json.loads(subprocess.check_output(["/usr/bin/python3", "-c", script], env=desktop.env,
                                             text=True, timeout=15))


server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
try:
    with HiddenDesktop(out / "desktop") as desktop:
        settings = {"selectedModelId": "plugin:com.typewhisper.whisper-cpp:base", "cleanupLevel": 0,
                    "saveToHistoryEnabled": True,
                    "defaultLlmProvider": "plugin:com.typewhisper.openai-compatible:fixture-alias",
                    "pluginEnabledState": {"com.typewhisper.openai-compatible": True}}
        provider = {"baseUrl": f"http://127.0.0.1:{server.server_address[1]}", "selectedLlmModel": "fixture-alias"}
        with IsolatedApp(out / "app", configuration="Release", minimized=False, settings=settings,
                         plugin_settings={"com.typewhisper.openai-compatible": provider},
                         plugin_data=["com.typewhisper.whisper-cpp"], virtual_mic=True, display="none",
                         extra_env=private_x11_env(desktop.app_env())) as app:
            app.load_model("whisper-cpp", "base")
            click(desktop, "Dictionary")
            click(desktop, catalogs["en"]["Dictionary.SuggestAliases"])
            click(desktop, catalogs["en"]["Dictionary.AliasesModel"])
            wait_for(desktop, lambda names: any("fixture-alias" in name for name in names), "loopback alias model")
            checks["loopback_model_offered_in_ui"] = True
            desktop.input([{"keys": [0xff1b]}])
            focus_text(desktop, catalogs["en"]["Dictionary.AliasesTerm"])
            desktop.input([{"keys": [0xff51]}, {"keys": [0xffe3, ord('a')]}]
                          + [{"keys": [ord(c)]} for c in "fixture"])
            click(desktop, catalogs["en"]["Dictionary.AliasesGenerate"])
            wait_for(desktop, lambda names: "fix ture" in names and "fiksture" in names, "generated alias choices")
            dictionary = app.instance / "data/TypeWhisper/Data/dictionary.json"
            checks["generation_did_not_save_dictionary"] = not dictionary.exists()
            checkpoint(desktop, out, "loopback-aliases")
            click(desktop, "fix ture")
            click(desktop, "fiksture")
            click(desktop, catalogs["en"]["Dictionary.AliasesSave"])
            deadline = time.monotonic() + 5
            while not dictionary.exists():
                if time.monotonic() > deadline:
                    raise AssertionError("Alias save did not write the isolated dictionary")
                time.sleep(.1)
            saved = json.loads(dictionary.read_text())
            checks["reviewed_aliases_saved_to_dictionary"] = len(saved) == 3 and {
                (item["Original"], item.get("Replacement")) for item in saved} == {
                    ("fixture", None), ("fix ture", "fixture"), ("fiksture", "fixture")}
            checks["one_local_alias_request"] = len(requests) == 1 and json.loads(requests[0]["messages"][-1]["content"])["word"] == "fixture"
            assert checks["reviewed_aliases_saved_to_dictionary"] and checks["one_local_alias_request"], saved
            click(desktop, "Dictation")
            click(desktop, catalogs["en"]["Dictation.SpokenLanguage"])
            wait_for(desktop, lambda names: "Azərbaycan" in names, "Whisper Azerbaijani language choice")
            checks["engine_declared_language_visible"] = True
            desktop.input([{"keys": [0xff1b]}])
            clipboard = X11Clipboard(app.env)
            started = app.api("/v1/dictation/start", {})
            sources = app.capture_sources()
            checks["only_virtual_microphone"] = sources == [app.mic.source]
            player = subprocess.Popen(["paplay", f"--device={app.mic.name}", "tests/fixtures/librispeech-1462-170145-0012.wav"])
            current = "en"
            for language, option in [("ja", "日本語"), ("zh", "简体中文")]:
                click(desktop, catalogs[current]["Nav.General"])
                time.sleep(.3)
                focus_text(desktop, catalogs[current]["General.UiLanguage"], combo=True)
                wait_for(desktop, lambda names: option in names, "interface language choice")
                click(desktop, option)
                prefix = catalogs[language]["General.ApiStatusRunning"].split("{0}")[0]
                wait_for(desktop, lambda names: any(prefix in n and str(app.port) in n for n in names), "translated live API status")
                # AT-SPI selects an option without dismissing Avalonia's popup.
                if "Deutsch" in all_names(desktop.accessibility()["tree"]):
                    focus_text(desktop, catalogs[language]["General.UiLanguage"], combo=True)
                checks[language + "_language_and_api_status_refreshed"] = True
                checks[language + "_recording_preserved"] = status(app)["state"] == "recording"
                checkpoint(desktop, out, language + "-general-recording")
                current = language
            player.wait(timeout=30)
            app.api("/v1/dictation/stop", {})
            path = f"/v1/dictation/transcription?sessionId={started['session_id']}"
            deadline = time.monotonic() + 60
            while True:
                result = app.api(path)
                if result.get("performance"):
                    break
                if time.monotonic() > deadline:
                    raise AssertionError(result)
                time.sleep(.2)
            checks["public_audio_transcribed"] = "bent his face over her hair" in result["text"].lower()
            checks["dictation_destination_read_back"] = clipboard.read().decode().strip() == result["text"].strip()
            for language, option in [("zh", None), ("ja", "日本語")]:
                if option is not None:
                    click(desktop, catalogs[current]["Nav.General"])
                    time.sleep(.3)
                    focus_text(desktop, catalogs[current]["General.UiLanguage"], combo=True)
                    click(desktop, option)
                    current = language
                click(desktop, catalogs[language]["Nav.History"])
                names = all_names(desktop.accessibility()["tree"])
                if catalogs[language]["Recovery.CopyOriginal"] not in names:
                    click(desktop, result["text"])
                wait_for(desktop, lambda names: catalogs[language]["Recovery.CopyOriginal"] in names,
                         "translated fork History recovery")
                clipboard.write("private CJK copy canary")
                click(desktop, catalogs[language]["Recovery.CopyOriginal"])
                wait_clipboard(clipboard, result["raw_text"])
                checks[language + "_history_copy_read_back"] = True
                checkpoint(desktop, out, language + "-history")
            backend = app.api("/v1/status")["acceleration"]
            checks["active_cpu_backend"] = backend["active_backend"] == "cpu"
            write_json(out / "dictation.json", result)
        with IsolatedApp(out / "wizard", configuration="Release", minimized=False, display="none", virtual_mic=True,
                         settings={"hasCompletedOnboarding": False,
                                   "pluginEnabledState": {"com.typewhisper.sherpa-onnx": True}},
                         extra_env=private_x11_env(desktop.app_env())) as app:
            deadline = time.monotonic() + 15
            while not any("Parakeet Ultra" in n for n in simple_names(desktop)):
                if time.monotonic() > deadline:
                    checkpoint(desktop, out, "wizard-failure")
                    raise AssertionError("Parakeet Ultra is not the displayed wizard default")
                time.sleep(.2)
            checks["wizard_defaults_to_parakeet_ultra"] = True
            checkpoint(desktop, out, "wizard-default")
        checks["no_unexpected_provider_routes"] = not rejected
        write_json(out / "result.json", {"checks": checks, "local_requests": len(requests), "backend": backend})
        assert all(checks.values()), checks
finally:
    server.shutdown()
    server.server_close()
write_json(out / "run.json", run_manifest({"kind": "upstream-languages-loopback-aliases", "desktop": "private",
                                           "audio": "public LibriSpeech fixture", "provider": "local fixture"}))
print(json.dumps(checks, indent=2))
