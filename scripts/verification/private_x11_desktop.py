"""Authenticated Xvfb/Openbox alternative for tests that need real XTEST delivery.

Mutter 51 can route XTEST through a consent portal. This private X11 server avoids
that portal without granting remote input on the user's desktop. The accessibility
bus, window manager, display cookie, and process lifetime all belong to this run.
"""
from __future__ import annotations

import os
from pathlib import Path
import secrets
import shutil
import struct
import subprocess

from hidden_desktop import (
    A11Y_LAUNCHER, A11Y_REGISTRY, HiddenDesktop,
    compositor_env, private_accessibility_address,
)
from isolated_app import make_runtime_dir


class PrivateX11Desktop(HiddenDesktop):
    def start(self):
        for tool in ("Xvfb", "openbox", "dbus-daemon", "gdbus"):
            if not shutil.which(tool):
                raise FileNotFoundError(f"{tool} is required for the private X11 editor check")
        self.workdir.mkdir(parents=True, exist_ok=True)
        self.runtime = make_runtime_dir()
        self.env = compositor_env(dict(os.environ), self.runtime, "")
        home = self.workdir / "home"
        home.mkdir()
        self.env.update(HOME=str(home), XDG_CONFIG_HOME=str(home / ".config"),
                        XDG_CACHE_HOME=str(home / ".cache"), XDG_DATA_HOME=str(home / ".local/share"),
                        XDG_CURRENT_DESKTOP="GNOME", XDG_SESSION_TYPE="x11", GDK_BACKEND="x11")
        log = (self.workdir / "desktop.log").open("w")
        bus = subprocess.Popen(["dbus-daemon", "--session", "--nofork", "--print-address=1"],
                               env=self.env, stdout=subprocess.PIPE, text=True)
        self._processes.append(bus)
        self.bus_address = bus.stdout.readline().strip()
        self.env["DBUS_SESSION_BUS_ADDRESS"] = self.bus_address
        self._spawn([A11Y_LAUNCHER, "--launch-immediately"], log)
        result = subprocess.run(["gdbus", "call", "--session", "--dest", "org.a11y.Bus",
                                 "--object-path", "/org/a11y/bus", "--method", "org.a11y.Bus.GetAddress"],
                                env=self.env, capture_output=True, text=True, timeout=10, check=True)
        self.env["AT_SPI_BUS_ADDRESS"] = private_accessibility_address(result.stdout, self.runtime)
        self._spawn([A11Y_REGISTRY], log)
        self.authority = self.runtime / "Xauthority"
        fields = (b"", b"", b"MIT-MAGIC-COOKIE-1", secrets.token_bytes(16))
        self.authority.write_bytes(struct.pack("!H", 65535) + b"".join(struct.pack("!H", len(v)) + v for v in fields))
        self.authority.chmod(0o600)
        read_fd, write_fd = os.pipe()
        try:
            server = subprocess.Popen(["Xvfb", "-displayfd", str(write_fd), "-auth", str(self.authority),
                                       "-screen", "0", self.size + "x24", "-nolisten", "tcp"],
                                      env=self.env, pass_fds=(write_fd,), stdout=log, stderr=subprocess.STDOUT)
            self._processes.append(server)
        finally:
            os.close(write_fd)
        with os.fdopen(read_fd) as pipe:
            number = pipe.readline().strip()
        if not number.isdigit():
            raise RuntimeError(f"Xvfb did not start; see {self.workdir / 'desktop.log'}")
        self.x_display = ":" + number
        self.env.update(DISPLAY=self.x_display, XAUTHORITY=str(self.authority))
        self._spawn(["openbox", "--sm-disable"], log)

    def app_env(self):
        return {key: self.env[key] for key in ("DISPLAY", "XAUTHORITY", "DBUS_SESSION_BUS_ADDRESS",
                "AT_SPI_BUS_ADDRESS", "XDG_CURRENT_DESKTOP", "XDG_SESSION_TYPE", "GDK_BACKEND")} | {"WAYLAND_DISPLAY": ""}

    def validate(self, env):
        if (self.runtime is None or self.x_display is None or env.get("DISPLAY") != self.x_display
                or env.get("XAUTHORITY") != str(self.authority)):
            raise ValueError("Refusing input outside this run's private X11 display")

    def screenshot(self, path: Path):
        path.parent.mkdir(parents=True, exist_ok=True)
        script = '''
import gi,sys
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk
root=Gdk.get_default_root_window()
pixbuf=Gdk.pixbuf_get_from_window(root,0,0,root.get_width(),root.get_height())
assert pixbuf
pixbuf.savev(sys.argv[1],"png",[],[])
'''
        subprocess.run([self._gi_python(), "-c", script, str(path)], env=self.env,
                       capture_output=True, check=True, timeout=15)
        return path
