"""Pure-logic tests for the Python-side platform handling (no Rhino needed; run on any OS)."""
import os
import unittest

import mcp_client as c


class DataDirTests(unittest.TestCase):
    def test_mac(self):
        d = c.data_dir("darwin", {}, "/Users/me")
        self.assertEqual(d, os.path.join("/Users/me", "Library", "Application Support", "RhinoMCP"))

    def test_windows_uses_appdata(self):
        d = c.data_dir("win32", {"APPDATA": "C:\\Users\\me\\AppData\\Roaming"}, "C:\\Users\\me")
        self.assertEqual(d, os.path.join("C:\\Users\\me\\AppData\\Roaming", "RhinoMCP"))

    def test_windows_without_appdata_falls_back(self):
        d = c.data_dir("win32", {}, "/home/me")
        self.assertEqual(d, os.path.join("/home/me", "AppData", "Roaming", "RhinoMCP"))

    def test_env_override_wins(self):
        self.assertEqual(c.data_dir("win32", {"RHINOMCP_DATA_DIR": "/x/y", "APPDATA": "/a"}, "/h"), "/x/y")

    def test_pid_alive(self):
        self.assertTrue(c.pid_alive(os.getpid()))
        self.assertFalse(c.pid_alive(2 ** 22 - 3))


if __name__ == "__main__":
    unittest.main()
