#!/usr/bin/env python3
"""Exercise floating controls, public audio, and the original editor in isolation.

The X11 check needs Xvfb, Openbox, xclip, xdotool, GTK3 Python bindings, and the
accessibility dependencies. --desktop mutter uses the hidden GNOME compositor
and its private input API. It checks the copy/manual-paste fallback, not ydotool.
Both need the downloaded Needle Whistle model. Neither injects physical input.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import time

from hidden_desktop import HiddenDesktop
from isolated_app import IsolatedApp, run_manifest, write_json
from private_x11_desktop import PrivateX11Desktop
from recovery_smoke import checkpoint, click, private_x11_env, wait_for

SHAPES = r'''
import ctypes as C,json,sys
x=C.CDLL('libX11.so.6');e=C.CDLL('libXext.so.6')
x.XOpenDisplay.restype=C.c_void_p;x.XOpenDisplay.argtypes=[C.c_char_p]
x.XFree.argtypes=[C.c_void_p];x.XCloseDisplay.argtypes=[C.c_void_p]
x.XQueryTree.argtypes=[C.c_void_p,C.c_ulong,C.POINTER(C.c_ulong),C.POINTER(C.c_ulong),C.POINTER(C.POINTER(C.c_ulong)),C.POINTER(C.c_uint)]
e.XShapeGetRectangles.argtypes=[C.c_void_p,C.c_ulong,C.c_int,C.POINTER(C.c_int),C.POINTER(C.c_int)];e.XShapeGetRectangles.restype=C.c_void_p
class R(C.Structure): _fields_=[('x',C.c_short),('y',C.c_short),('width',C.c_ushort),('height',C.c_ushort)]
d=x.XOpenDisplay(None);assert d
result=[]
def read(w):
 n=C.c_int();order=C.c_int();p=e.XShapeGetRectangles(d,w,2,C.byref(n),C.byref(order))
 rects=C.cast(p,C.POINTER(R))
 result.append({'window':w,'input':[[rects[i].x,rects[i].y,rects[i].width,rects[i].height] for i in range(n.value)]})
 if p:x.XFree(p)
 root=C.c_ulong();parent=C.c_ulong();children=C.POINTER(C.c_ulong)();count=C.c_uint()
 x.XQueryTree(d,w,C.byref(root),C.byref(parent),C.byref(children),C.byref(count))
 for i in range(count.value):read(children[i])
 if children:x.XFree(children)
read(int(sys.argv[1]));x.XCloseDisplay(d);print(json.dumps(result))
'''

EDITOR = r'''
import gi,sys
from pathlib import Path
gi.require_version("Gtk","3.0")
from gi.repository import Gtk
window=Gtk.Window(title="Flow control verification editor")
window.set_default_size(800,360);window.move(200,180)
view=Gtk.TextView();view.set_wrap_mode(Gtk.WrapMode.WORD_CHAR)
view.get_accessible().set_name("Verification text field")
buffer=view.get_buffer()
def changed(buffer):
 Path(sys.argv[1]).write_text(buffer.get_text(buffer.get_start_iter(),buffer.get_end_iter(),True))
buffer.connect("changed",changed);window.add(view)
window.connect("destroy",Gtk.main_quit);window.show_all();view.grab_focus()
changed(buffer);Gtk.main()
'''


def wait_value(read, predicate, label, timeout=15):
    deadline = time.monotonic() + timeout
    value = None
    while time.monotonic() < deadline:
        value = read()
        if predicate(value):
            return value
        time.sleep(.1)
    raise AssertionError(f"Timed out waiting for {label}: {value}")


def find_node(tree, name):
    for node in tree:
        if node.get("name") == name and node.get("extents", [0, 0, 0, 0])[2] > 0:
            return node
        found = find_node(node.get("children", []), name)
        if found:
            return found
    return None


def run(out: Path, audio: Path, desktop_kind: str, app_dir: Path | None = None):
    if out.exists():
        raise ValueError("Choose a fresh evidence directory")
    labels = json.loads(Path("src/TypeWhisper.Linux/Resources/Localization/en.json").read_text())
    report = {"checks": {}, "desktop": desktop_kind, "destination": "private GTK text field",
              "delivery": "automatic paste" if desktop_kind == "x11" else "copy then manual paste",
              "audio": str(audio)}
    checks = report["checks"]
    settings = {
        "selectedModelId": "plugin:com.typewhisper.needle-whistle:whistle",
        "showFloatingControl": False, "autoPaste": desktop_kind == "x11", "cleanupLevel": 0,
        "liveTranscriptionEnabled": False, "liveCleanupEnabled": False,
        "pluginEnabledState": {"com.typewhisper.filler-words": False},
    }
    desktop_type = PrivateX11Desktop if desktop_kind == "x11" else HiddenDesktop
    with desktop_type(out / "desktop") as desktop:
        display_env = desktop.app_env() if desktop_kind == "x11" else private_x11_env(desktop.app_env())
        extra = {**display_env, "TYPEWHISPER_DISABLE_OVERLAYS": "0",
                 # This test's private system bus has no logind. Its virtual session
                 # uses the supported non-systemd mode, not the physical screen lock.
                 "DBUS_SYSTEM_BUS_ADDRESS": desktop.bus_address}
        with IsolatedApp(out / "app", configuration="Release", minimized=False, settings=settings,
                         plugin_data=["com.typewhisper.needle-whistle"], virtual_mic=True,
                         display="none", extra_env=extra,
                         assembly=app_dir / "typewhisper.dll" if app_dir else None,
                         executable=app_dir / "typewhisper" if app_dir else None) as app:
            report["payload"] = str(app_dir) if app_dir else "Release build"
            def xdo(*args):
                if desktop_kind == "x11":
                    desktop.validate(app.env)
                else:
                    private_x11_env(app.env)
                return subprocess.check_output(["xdotool", *map(str, args)], env=app.env, text=True).strip()

            def move(x, y):
                if desktop_kind == "x11":
                    xdo("mousemove", x, y)
                else:
                    desktop.input([{"move": [x, y]}], persistent=True)

            def geometry():
                ids = xdo("search", "--name", "^TypeWhisper Overlay$").splitlines()
                assert len(ids) == 1, ids
                values = dict(line.split("=", 1) for line in xdo("getwindowgeometry", "--shell", ids[0]).splitlines())
                return {key: int(values[key]) for key in ("WINDOW", "X", "Y", "WIDTH", "HEIGHT")}

            def shapes():
                return json.loads(subprocess.check_output(
                    ["python3", "-c", SHAPES, str(geometry()["WINDOW"])], env=app.env, text=True))

            def hover_launcher():
                move(10, 10)
                wait_value(lambda: find_node(desktop.accessibility()["tree"], labels["Overlay.ShowControls"]), bool, "idle control")
                box = wait_value(geometry, lambda b: b["WIDTH"] == 36, "collapsed control")
                move(box["X"] + box["WIDTH"] // 2, box["Y"] + box["HEIGHT"] // 2)
                wait_value(geometry, lambda b: 100 <= b["WIDTH"] <= 180, "expanded control")
                # Allow tooltip and collapse timers to fire while the pointer stays put.
                time.sleep(.8)
                box = geometry()
                assert box["WIDTH"] >= 100, box
                return box

            def click_control(name):
                def control_node():
                    frame = find_node(desktop.accessibility()["tree"], "TypeWhisper Overlay")
                    return find_node(frame.get("children", []), name) if frame else None
                node = wait_value(control_node, bool, name)
                x, y, width, height = node["extents"]
                point = [x + width // 2, y + height // 2]
                report.setdefault("clicks", []).append({"name": name, "extents": node["extents"]})
                if desktop_kind == "x11":
                    move(*point)
                    time.sleep(.15)
                    xdo("mousedown", 1)
                    time.sleep(.1)
                    xdo("mouseup", 1)
                else:
                    desktop.input([{"click": point}], persistent=True)

            editor = None
            try:
                saved = app.instance / "data/TypeWhisper/settings.json"
                wait_for(desktop, lambda names: "Appearance" in names, "main window")
                click(desktop, "Appearance")
                wait_for(desktop, lambda names: labels["Appearance.ShowFloatingControl"] in names, "floating-control setting")
                click(desktop, labels["Appearance.ShowFloatingControl"])
                wait_value(lambda: json.loads(saved.read_text())["showFloatingControl"], bool, "saved setting")
                checks["enabled_through_settings"] = True
                idle = wait_value(geometry, lambda b: b["WIDTH"] == 36 and b["HEIGHT"] == 18
                                  and abs(b["X"] + b["WIDTH"] / 2 - 720) <= 1 and b["Y"] + b["HEIGHT"] == 888,
                                  "small bottom-center control")
                report["idle_geometry"] = idle
                checkpoint(desktop, out, "idle")
                checks["idle_is_bottom_center"] = abs(idle["X"] + idle["WIDTH"] / 2 - 720) <= 1 and idle["Y"] + idle["HEIGHT"] == 888
                assert checks["idle_is_bottom_center"], idle
                report["load"] = app.load_model("needle-whistle", "whistle")
                editor_text = out / "editor-text.txt"
                editor = subprocess.Popen(["/usr/bin/python3", "-c", EDITOR, str(editor_text)],
                                          env={**app.env, "GDK_BACKEND": "x11"},
                                          stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
                wait_value(editor_text.exists, bool, "editor startup")
                editor_id = xdo("search", "--all", "--pid", editor.pid,
                                "--name", "^Flow control verification editor$").splitlines()[0]
                xdo("windowactivate", "--sync", editor_id)
                wait_value(lambda: xdo("getactivewindow"), lambda value: value == editor_id, "editor focus")
                checks["editor_focused_initially"] = True
                report["expanded_geometry"] = hover_launcher()
                checks["hover_preserves_editor_focus"] = xdo("getactivewindow") == editor_id
                checks["hover_never_records"] = not app.api("/v1/dictation/status")["is_recording"]
                checkpoint(desktop, out, "expanded")
                click_control(labels["Overlay.StartDictation"])
                wait_value(lambda: app.api("/v1/dictation/status"), lambda s: s["is_recording"], "recording")
                checks["start_preserves_editor_focus"] = xdo("getactivewindow") == editor_id
                checks["only_virtual_microphone"] = app.capture_sources() == [app.mic.source]
                move(10, 10)
                time.sleep(.5)
                checks["leaving_control_keeps_recording"] = app.api("/v1/dictation/status")["is_recording"]
                checkpoint(desktop, out, "recording")
                app.mic.play(audio)
                time.sleep(.3)
                click_control(labels["Overlay.StopDictation"])
                result = wait_value(lambda: app.api("/v1/dictation/transcription?sessionId=1"),
                                    lambda r: r.get("performance") is not None, "completed transcription", timeout=30)
                report["dictation"] = result
                checks["stop_preserves_editor_focus"] = xdo("getactivewindow") == editor_id
                if desktop_kind == "mutter":
                    assert result["performance"]["delivery_outcome"] == "CopiedToClipboard", result
                    desktop.input([{"keys": [0xffe3, ord("v")]}], persistent=True)
                wait_value(lambda: editor_text.read_text(), lambda t: t.strip() == result["text"].strip(),
                           "text in the original editor")
                checks["expected_public_speech"] = "bent his face over her hair" in result["text"].lower()
                checks["delivered_to_original_field"] = bool(result["text"].strip()) and editor_text.read_text().strip() == result["text"].strip()
                checkpoint(desktop, out, "delivered")

                before_cancel = editor_text.read_text()
                hover_launcher()
                click_control(labels["Overlay.StartDictation"])
                wait_value(lambda: app.api("/v1/dictation/status"), lambda s: s["is_recording"], "second recording")
                click_control(labels["Common.Cancel"])
                wait_value(lambda: app.api("/v1/dictation/status"), lambda s: not s["is_recording"], "canceled recording")
                time.sleep(.5)
                checks["cancel_does_not_insert"] = editor_text.read_text() == before_cancel
                checks["cancel_preserves_editor_focus"] = xdo("getactivewindow") == editor_id
                hover_launcher()
                click_control(labels["Recorder.Title"])
                wait_for(desktop, lambda names: "Recording session" in names, "recorder page")
                checks["recorder_shortcut_does_not_record"] = not app.api("/v1/dictation/status")["is_recording"] and not app.capture_sources()
                hover_launcher()
                click_control(labels["Tray.Settings"])
                wait_for(desktop, lambda names: labels["Appearance.ShowFloatingControl"] in names, "appearance page")
                checks["settings_shortcut_opens_appearance"] = True
                click(desktop, labels["Appearance.ShowFloatingControl"])
                wait_value(lambda: json.loads(saved.read_text())["showFloatingControl"], lambda v: not v, "disabled setting")
                report["disabled_input_shapes"] = wait_value(shapes, lambda rows: rows and not rows[0]["input"], "hidden top-level input region")
                checks["disabled_control_passes_clicks"] = True
                report["acceleration"] = app.api("/v1/status").get("acceleration")
                checks["active_cpu_backend"] = report["acceleration"]["active_backend"] == "cpu"
                assert all(checks.values()), checks
            except BaseException:
                checkpoint(desktop, out, "failure")
                raise
            finally:
                write_json(out / "result.json", report)
                write_json(out / "run.json", run_manifest({"kind": "floating-control-real-editor", "desktop": desktop_kind,
                                                           "backend": "cpu", "delivery": report["delivery"]}))
                if editor is not None:
                    editor.terminate()
                    editor.communicate(timeout=10)
    print(json.dumps(checks, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--audio", default=Path("tests/fixtures/librispeech-1462-170145-0012.wav"), type=Path)
    parser.add_argument("--desktop", choices=("x11", "mutter"), default="x11")
    parser.add_argument("--app-dir", type=Path, help="Verify this installed or published payload instead of the Release build")
    args = parser.parse_args()
    run(args.out.resolve(), args.audio.resolve(), args.desktop, args.app_dir.resolve() if args.app_dir else None)
