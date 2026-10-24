"""Correct selected text in a private GTK window, change targets, and inspect recovery UI."""
import argparse
import json
import os
import socket
import secrets
import struct
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

sys.path.insert(0, str(Path.cwd() / "scripts/verification"))
from hidden_desktop import HiddenDesktop, all_names, find_node, compositor_env, private_accessibility_address, A11Y_LAUNCHER, A11Y_REGISTRY
from isolated_app import make_runtime_dir
from isolated_app import IsolatedApp, write_json, run_manifest
from recovery_smoke import X11Clipboard, checkpoint, click, private_x11_env, wait_for

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--out", type=Path, required=True)
parser.add_argument("--xvfb", default="Xvfb")
parser.add_argument("--wm", default="openbox")
args = parser.parse_args()
out = args.out.resolve()
if out.exists():
    raise RuntimeError("Choose a fresh evidence directory")
out.mkdir(parents=True)
corrected = "Corrected fixture text."
requests = []

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        self.reply({"data": [{"id": "fixture-cleanup", "object": "model"}]})

    def reply(self, value):
        body = json.dumps(value).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        payload = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        requests.append(payload)
        time.sleep(1.5)
        if payload.get("stream"):
            data = {"choices": [{"delta": {"content": corrected}}]}
            body = ("data: " + json.dumps(data) + "\n\ndata: [DONE]\n\n").encode()
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        else:
            self.reply({"choices": [{"message": {"role": "assistant", "content": corrected}}]})

gtk_script = r'''
import gi,json,sys
gi.require_version('Gtk','3.0')
from gi.repository import Gtk,GLib
windows=[]; entries=[]
for title,text in [('Correction target fixture','Original fixture selection.'),('Other target fixture','Other window text.')]:
    window=Gtk.Window(title=title); window.set_default_size(480,120)
    entry=Gtk.Entry();entry.set_text(text);window.add(entry);window.show_all()
    windows.append(window);entries.append(entry)
def save():
    open(sys.argv[1],'w').write(json.dumps([entry.get_text() for entry in entries]));return True
GLib.timeout_add(100,save)
Gtk.main()
'''

server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
checks = {}

class PrivateX11Desktop(HiddenDesktop):
    """Use native private X11 so the real global hook and XTEST share one server."""
    def start(self):
        self.workdir.mkdir(parents=True, exist_ok=True)
        self.runtime = make_runtime_dir()
        self.env = compositor_env(dict(os.environ), self.runtime, "")
        bus = subprocess.Popen(["dbus-daemon", "--session", "--nofork", "--print-address=1"],
                               env=self.env, stdout=subprocess.PIPE, text=True)
        self._processes.append(bus)
        self.bus_address = bus.stdout.readline().strip()
        self.env["DBUS_SESSION_BUS_ADDRESS"] = self.bus_address
        log = (self.workdir / "desktop.log").open("w")
        self._spawn([A11Y_LAUNCHER, "--launch-immediately"], log)
        address = subprocess.check_output(["gdbus", "call", "--session", "--dest", "org.a11y.Bus",
            "--object-path", "/org/a11y/bus", "--method", "org.a11y.Bus.GetAddress"], env=self.env, text=True, timeout=10)
        self.env["AT_SPI_BUS_ADDRESS"] = private_accessibility_address(address, self.runtime)
        self._spawn([A11Y_REGISTRY], log)
        self.auth = self.runtime / ".mutter-Xwaylandauth-Xvfb"
        fields = [b"", b"", b"MIT-MAGIC-COOKIE-1", secrets.token_bytes(16)]
        self.auth.write_bytes(struct.pack(">H", 65535) + b"".join(struct.pack(">H", len(v)) + v for v in fields))
        self.auth.chmod(0o600)
        read_fd, write_fd = os.pipe()
        try:
            xvfb = subprocess.Popen([args.xvfb, "-displayfd", str(write_fd), "-screen", "0", "1440x900x24",
                "-nolisten", "tcp", "-auth", str(self.auth)], env=self.env, pass_fds=(write_fd,), stdout=log, stderr=log)
            self._processes.append(xvfb)
        finally:
            os.close(write_fd)
        with os.fdopen(read_fd) as display:
            self.x_display = ":" + display.readline().strip()
        self.env.update(DISPLAY=self.x_display, XAUTHORITY=str(self.auth), XDG_SESSION_TYPE="x11")
        self.env["XDG_DATA_DIRS"] = str(Path(args.wm).resolve().parent.parent / "share") + ":/usr/local/share:/usr/share"
        self._spawn([args.wm, "--sm-disable"], log)
        time.sleep(2)

    def app_env(self):
        return {key: self.env[key] for key in ("DISPLAY", "XAUTHORITY", "DBUS_SESSION_BUS_ADDRESS", "AT_SPI_BUS_ADDRESS", "XDG_SESSION_TYPE")}

    def screenshot(self, path):
        subprocess.run(["import", "-window", "root", str(path)], env=self.env, check=True, timeout=10)
        return path

try:
    with PrivateX11Desktop(out / "desktop") as desktop:
        app = IsolatedApp(out / "app", configuration="Release", minimized=False, display="none",
                          settings={"defaultLlmProvider": "plugin:com.typewhisper.openai-compatible:fixture-cleanup",
                                    "cleanupLevel": 2,
                                    "pluginEnabledState": {"com.typewhisper.openai-compatible": True}},
                          plugin_settings={"com.typewhisper.openai-compatible": {
                              "baseUrl": f"http://127.0.0.1:{server.server_address[1]}", "selectedLlmModel": "fixture-cleanup"}},
                          extra_env=private_x11_env(desktop.app_env()))
        write_json(app.instance / "data/TypeWhisper/Data/prompt-actions.json", [{
            "Id": "fixture-correction", "Name": "Fixture correction", "SystemPrompt": "Correct the selected fixture text.",
            "IsEnabled": True, "IsManualOnly": True, "HotkeyKey": "Ctrl+Shift+F8"}])
        with app:
            with socket.socket(socket.AF_UNIX) as control:
                control.settimeout(5)
                control.connect(str(app.runtime / "typewhisper/control.sock"))
                control.sendall(b'{"v":1,"cmd":"status"}\n')
                write_json(out / "control-status.json", json.loads(control.recv(16384)))
            deadline = time.monotonic() + 15
            while True:
                value = app.api("/v1/text/process", {"text": "provider canary"})
                if requests:
                    break
                if time.monotonic() > deadline:
                    raise AssertionError("Local correction provider did not activate")
                time.sleep(.2)
            entries_file = out / "target-text.json"
            fixture = subprocess.Popen(["/usr/bin/python3", "-c", gtk_script, str(entries_file)],
                                       env={**app.env, "GDK_BACKEND": "x11", "GTK_USE_PORTAL": "0"},
                                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            try:
                def xdo(*arguments):
                    return subprocess.check_output(["xdotool", *arguments], env=app.env, text=True, timeout=5).strip()
                deadline = time.monotonic() + 10
                while True:
                    try:
                        target = xdo("search", "--onlyvisible", "--name", "^Correction target fixture$").splitlines()[0]
                        other = xdo("search", "--onlyvisible", "--name", "^Other target fixture$").splitlines()[0]
                        break
                    except (subprocess.CalledProcessError, IndexError):
                        if time.monotonic() > deadline:
                            raise
                        time.sleep(.1)
                xdo("windowactivate", target)
                xdo("windowfocus", "--sync", target)
                xdo("key", "--clearmodifiers", "ctrl+a")
                time.sleep(.2)
                write_json(out / "target-before.json", {"active": xdo("getactivewindow", "getwindowname"),
                    "selection": subprocess.check_output(["xclip", "-o", "-selection", "primary"], env=app.env, text=True, timeout=5)})
                before = len(requests)
                xdo("key", "--clearmodifiers", "Ctrl+Shift+F8")
                deadline = time.monotonic() + 10
                while len(requests) == before:
                    if time.monotonic() > deadline:
                        checkpoint(desktop, out, "failure")
                        raise AssertionError("The private action shortcut did not request a correction")
                    time.sleep(.05)
                xdo("windowactivate", other)
                xdo("windowfocus", "--sync", other)
                tree = wait_for(desktop, lambda names: any("selection was left unchanged" in n for n in names), "blocked correction result")
                names = all_names(tree)
                checks["warning_reports_copy_success"] = any("correction was copied to the clipboard" in name for name in names)
                clipboard = X11Clipboard(app.env)
                checks["clipboard_read_back_matches"] = clipboard.read().decode() == corrected
                message = find_node(tree, next(n for n in names if "selection was left unchanged" in n))
                x, y, width, height = message["extents"]
                # Read-only Avalonia text peers raise TypeError on this AT-SPI version.
                # Select and copy the displayed field, then verify its actual text.
                clipboard.write("private correction selection canary")
                xdo("mousemove", str(x + width // 2), str(y + height + 26), "click", "1")
                xdo("key", "--clearmodifiers", "ctrl+a", "ctrl+c")
                time.sleep(.2)
                checks["result_visible_and_selectable"] = clipboard.read().decode() == corrected
                checks["both_target_windows_unchanged"] = json.loads(entries_file.read_text()) == [
                    "Original fixture selection.", "Other window text."]
                checkpoint(desktop, out, "correction-result")
                click(desktop, "OK")
            finally:
                fixture.terminate()
                fixture.wait(timeout=5)
            click(desktop, "About")
            wait_for(desktop, lambda names: "Delete All Data…" in names, "reset controls")
            click(desktop, "Delete All Data…")
            tree = wait_for(desktop, lambda names: "I understand that this cannot be undone" in names, "reset acknowledgement")
            checks["upstream_reset_acknowledgement_retained"] = "Cancel" in all_names(tree)
            checkpoint(desktop, out, "reset-confirmation")
            click(desktop, "Cancel")
            checks["cancel_keeps_isolated_data"] = (app.instance / "data/TypeWhisper/Data/prompt-actions.json").is_file()
            write_json(out / "result.json", {"checks": checks, "requests": len(requests)})
            assert all(checks.values()), checks
finally:
    server.shutdown()
    server.server_close()
write_json(out / "run.json", run_manifest({"kind": "correction-delivery", "desktop": "private X11", "provider": "local fixture"}))
print(json.dumps(checks, indent=2))
