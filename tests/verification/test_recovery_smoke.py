import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[2] / "scripts/verification"))
import recovery_smoke


class PrivateClipboardEnvironmentTests(unittest.TestCase):
    def test_private_display_disables_the_users_wayland_clipboard_and_evdev_session(self):
        env = recovery_smoke.private_x11_env({
            "DISPLAY": ":8", "XAUTHORITY": "/tmp/tw-verify-test/.mutter-Xwaylandauth-test",
            "DBUS_SESSION_BUS_ADDRESS": "unix:path=/tmp/private-bus", "WAYLAND_DISPLAY": "/run/user/1000/wayland-0",
        })
        self.assertEqual(":8", env["DISPLAY"])
        self.assertEqual("", env["WAYLAND_DISPLAY"])
        self.assertEqual("x11", env["XDG_SESSION_TYPE"])
        self.assertEqual("unix:path=/tmp/private-bus", env["DBUS_SESSION_BUS_ADDRESS"])

    def test_user_authority_file_cannot_authorize_a_verification_clipboard(self):
        with self.assertRaises(ValueError):
            recovery_smoke.private_x11_env({"DISPLAY": ":0", "XAUTHORITY": "/home/user/.Xauthority",
                                           "DBUS_SESSION_BUS_ADDRESS": "unix:path=/run/user/1000/bus"})

    def test_missing_private_display_is_an_error_instead_of_inheriting_the_user_display(self):
        with self.assertRaises(ValueError):
            recovery_smoke.private_x11_env({"XAUTHORITY": "/tmp/tw-verify-test/.mutter-Xwaylandauth-test"})


if __name__ == "__main__":
    unittest.main()
