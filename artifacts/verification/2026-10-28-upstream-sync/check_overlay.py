"""Check real X11 overlay input regions before, during, and after virtual capture."""
import argparse
import json
import socket
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path.cwd() / "scripts/verification"))
from hidden_desktop import HiddenDesktop
from isolated_app import IsolatedApp, run_manifest, write_json
from recovery_smoke import checkpoint, private_x11_env

SHAPE = r'''
import ctypes as C,json,subprocess
x=C.CDLL('libX11.so.6'); e=C.CDLL('libXext.so.6')
x.XOpenDisplay.argtypes=[C.c_char_p]; x.XOpenDisplay.restype=C.c_void_p
x.XCloseDisplay.argtypes=[C.c_void_p]; x.XFree.argtypes=[C.c_void_p]
e.XShapeGetRectangles.argtypes=[C.c_void_p,C.c_ulong,C.c_int,C.POINTER(C.c_int),C.POINTER(C.c_int)]
e.XShapeGetRectangles.restype=C.c_void_p
d=x.XOpenDisplay(None)
assert d
ids=subprocess.run(['xdotool','search','--name','^TypeWhisper Overlay$'],capture_output=True,text=True).stdout.split()
values=[]
for wid in ids:
 n=C.c_int(); order=C.c_int()
 p=e.XShapeGetRectangles(d,int(wid),2,C.byref(n),C.byref(order))
 values.append({'window':wid,'input_rectangles':n.value})
 if p:x.XFree(p)
x.XCloseDisplay(d)
print(json.dumps(values))
'''


def shapes(env):
    return json.loads(subprocess.check_output([sys.executable, "-c", SHAPE], env=env, text=True))


def wait_shape(env, visible):
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        value = shapes(env)
        if len(value) == 1 and (value[0]["input_rectangles"] > 0) == visible:
            return value
        time.sleep(.1)
    raise AssertionError({"visible": visible, "shapes": value})


parser = argparse.ArgumentParser()
parser.add_argument("--out", type=Path, required=True)
out = parser.parse_args().out.resolve()
report = {"checks": {}}
with HiddenDesktop(out / "desktop") as desktop:
    with IsolatedApp(out / "app", configuration="Release", minimized=False,
                     virtual_mic=True, display="none",
                     settings={"liveTranscriptionEnabled": False},
                     extra_env={**private_x11_env(desktop.app_env()),
                                "TYPEWHISPER_DISABLE_OVERLAYS": "0"}) as app:
        report["idle"] = wait_shape(app.env, False)
        report["checks"]["idle_overlay_passes_input"] = True
        app.api("/v1/dictation/start", {})
        assert app.capture_sources() == [app.mic.source]
        report["checks"]["only_virtual_microphone"] = True
        report["recording"] = wait_shape(app.env, True)
        report["checks"]["recording_overlay_accepts_input"] = True
        checkpoint(desktop, out, "recording-overlay")
        with socket.socket(socket.AF_UNIX) as control:
            control.settimeout(5)
            control.connect(str(app.runtime / "typewhisper/control.sock"))
            control.sendall(b'{"v":1,"cmd":"record.cancel"}\n')
            assert json.loads(control.recv(16384))["ok"]
        report["canceled"] = wait_shape(app.env, False)
        report["checks"]["canceled_overlay_passes_input"] = True
        checkpoint(desktop, out, "idle-overlay")
write_json(out / "result.json", report)
write_json(out / "run.json", run_manifest({"kind": "real-overlay-input-shapes", "desktop": "private X11"}))
print(json.dumps(report, indent=2))
