"""Isolated end-to-end checks. Never reads or copies personal Irony data."""
import json
import ctypes
import hashlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


EXE = Path(sys.argv.pop(1)).resolve() if len(sys.argv) > 1 else Path(__file__).parent / "bin/Release/net8.0/irony-cli.exe"
TEMP = Path(os.environ.get("LOCALAPPDATA", tempfile.gettempdir())) / "Temp/opencode"


class CliTests(unittest.TestCase):
    def setUp(self):
        TEMP.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="irony-cli-test-", dir=TEMP)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.data = self.root / "IronyModManager"
        self.data.mkdir()
        self.game = self.root / "fake-game-user"
        (self.game / "mod").mkdir(parents=True)
        self.database = self.data / "Database_1.27.json"
        self.collections = [self.collection("First", True, ["a"]), self.collection("Second", False, ["b"])]
        for key in ("a", "b"):
            content = self.game / (key + "-content")
            content.mkdir()
            (self.game / "mod" / (key + ".mod")).write_text(
                f'name="Test {key.upper()}"\npath="{content.as_posix()}"\nsupported_version="4.0.*"\n', encoding="utf-8")
        self.values = [
            self.item("Preferences", {"Game": "Stellaris"}),
            self.item("AppState", {}),
            self.item("WindowState", {}),
            self.item("GameSettings", [{"Type": "Stellaris", "UserDirectory": str(self.game), "CustomModDirectory": "",
                                         "ExecutableLocation": str(self.root / "fixture-game.exe"), "LaunchArguments": "", "RefreshDescriptors": False}]),
            self.item("ModCollection", self.collections),
        ]
        self.write()
        self.env = dict(os.environ)
        # Contain incidental profile/temp access by upstream; no personal database is copied.
        for name in ("APPDATA", "LOCALAPPDATA", "TEMP", "TMP"):
            folder = self.root / name
            folder.mkdir()
            self.env[name] = str(folder)

    def collection(self, name, selected, mods):
        return {"Game": "Stellaris", "Name": name, "IsSelected": selected,
                "Mods": [f"mod/{m}.mod" for m in mods], "ModNames": [f"Test {m.upper()}" for m in mods],
                "ModPaths": [str(self.game / (m + "-content")) for m in mods], "ModIds": [{} for _ in mods],
                "PatchModEnabled": True, "MergedFolderName": "keep-this-metadata"}

    @staticmethod
    def item(name, value):
        model = f"IronyModManager.Models.{name}"
        if name in ("ModCollection", "GameSettings"):
            model = f"IEnumerable<IronyModManager.Models.Common.I{name}>"
        return {"Type": model, "Name": name, "Value": value}

    def write(self):
        self.database.write_text(json.dumps(self.values), encoding="utf-8")

    def read(self, name="ModCollection"):
        return next(i["Value"] for i in json.loads(self.database.read_text(encoding="utf-8-sig")) if i["Name"] == name)

    def call(self, *args, ok=True, data=True):
        command = [str(EXE)] + (["--data-dir", str(self.data)] if data else []) + list(args)
        result = subprocess.run(command, env=self.env, text=True, capture_output=True, timeout=45, cwd=self.root)
        if ok:
            self.assertEqual(result.returncode, 0, result.stderr)
            return json.loads(result.stdout)
        self.assertNotEqual(result.returncode, 0, result.stdout)
        return result

    def test_lists_are_read_only_and_independent_of_cwd(self):
        original = self.database.read_bytes()
        self.assertTrue(any(g["selected"] and g["abbreviation"] == "Stellaris" for g in self.call("games", "list")))
        self.assertEqual(len(self.call("collections", "list")), 2)
        mods = self.call("mods", "list")
        self.assertEqual([m["descriptor"] for m in mods["mods"] if m["enabled"]], ["mod/a.mod"])
        self.assertEqual(self.database.read_bytes(), original)
        self.assertEqual(sorted(p.name for p in self.data.iterdir()), [self.database.name])

    def test_select_persists_and_backs_up(self):
        original = self.database.read_bytes()
        result = self.call("collection", "select", "Second")
        self.assertTrue(result["changed"])
        self.assertEqual(Path(result["backup"]).read_bytes(), original)
        self.assertEqual([c["Name"] for c in self.read() if c["IsSelected"]], ["Second"])
        self.assertTrue(next(c for c in self.call("collections", "list") if c["name"] == "Second")["selected"])

    def test_mod_edit_preserves_unknown_fields_and_unrelated_storage(self):
        self.collections[0]["ExtraMetadata"] = {"preserve": [1, 2, 3]}
        self.collections[0]["ModIds"][0] = {"SteamId": 123, "ExtraMetadata": {"keep": True}}
        self.collections[1]["ExtraMetadata"] = "also keep"
        next(i["Value"] for i in self.values if i["Name"] == "Preferences")["FutureSetting"] = "untouched"
        self.write()
        original_preferences = self.read("Preferences")
        original_second = self.collections[1].copy()
        self.call("mods", "enable", "mod/b.mod")
        changed = {c["Name"]: c for c in self.read()}
        self.assertEqual(changed["First"]["ExtraMetadata"], {"preserve": [1, 2, 3]})
        self.assertEqual(changed["Second"], original_second)
        self.assertEqual(self.read("Preferences"), original_preferences)
        self.assertEqual(changed["First"]["ModIds"][0], {"SteamId": 123, "ExtraMetadata": {"keep": True}})
        self.call("mods", "disable", "mod/b.mod")
        retained = next(c for c in self.read() if c["Name"] == "First")
        self.assertEqual(retained["ModIds"], [{"SteamId": 123, "ExtraMetadata": {"keep": True}}])

    def test_apply_lock_is_keyed_by_game_not_database(self):
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.CreateMutexW.argtypes = (ctypes.c_void_p, ctypes.c_bool, ctypes.c_wchar_p)
        kernel32.CreateMutexW.restype = ctypes.c_void_p
        kernel32.ReleaseMutex.argtypes = (ctypes.c_void_p,)
        kernel32.CloseHandle.argtypes = (ctypes.c_void_p,)
        key = hashlib.sha256(str(self.game.resolve()).rstrip("\\/").upper().encode()).hexdigest().upper()
        handle = kernel32.CreateMutexW(None, True, "Local\\irony-cli-game-" + key)
        self.assertTrue(handle)
        try:
            result = self.call("collection", "apply", "--yes", ok=False)
            self.assertIn("applying to this game directory", result.stderr)
            self.assertFalse((self.game / "dlc_load.json").exists())
        finally:
            kernel32.ReleaseMutex(handle)
            kernel32.CloseHandle(handle)

    def test_remote_id_and_ambiguous_name(self):
        content = self.game / "remote-content"
        content.mkdir()
        (self.game / "mod" / "ugc_123.mod").write_text(
            f'name="Test A"\npath="{content.as_posix()}"\nremote_file_id="123"\n', encoding="utf-8")
        self.assertIn("Ambiguous", self.call("mods", "enable", "Test A", ok=False).stderr)
        self.call("mods", "enable", "123")
        first = next(c for c in self.read() if c["Name"] == "First")
        self.assertEqual(first["Mods"], ["mod/a.mod", "mod/ugc_123.mod"])
        self.assertEqual(first["ModIds"][-1]["SteamId"], 123)

    def test_game_selection_preserves_collections_and_other_preferences(self):
        preferences = next(i["Value"] for i in self.values if i["Name"] == "Preferences")
        preferences["FutureSetting"] = {"keep": True}
        self.write()
        collections = self.read()
        self.call("game", "select", "HOI4")
        self.assertTrue(next(g for g in self.call("games", "list") if g["abbreviation"] == "HOI4")["selected"])
        self.assertEqual(self.read(), collections)
        self.assertEqual(self.read("Preferences")["FutureSetting"], {"keep": True})
        self.call("game", "select", "Stellaris")
        self.assertEqual(self.read("Preferences")["Game"], "Stellaris")
        self.assertEqual(next(g for g in self.call("games", "list") if g["abbreviation"] == "Stellaris")["userDirectory"].replace("\\", "/"), self.game.as_posix())

    def test_apply_nonselected_and_empty_collection(self):
        original = self.database.read_bytes()
        self.call("collection", "apply", "--collection", "Second", "--yes")
        self.assertEqual(json.loads((self.game / "dlc_load.json").read_text())["enabled_mods"], ["mod/b.mod"])
        self.assertEqual(self.database.read_bytes(), original)
        self.call("mods", "disable", "mod/b.mod", "--collection", "Second")
        self.call("collection", "apply", "--collection", "Second", "--yes")
        self.assertEqual(json.loads((self.game / "dlc_load.json").read_text())["enabled_mods"], [])
        self.assertEqual([c["Name"] for c in self.read() if c["IsSelected"]], ["First"])

    def test_enable_disable_preserve_order_metadata_and_selection(self):
        self.assertTrue(self.call("mods", "enable", "mod/b.mod")["changed"])
        first = next(c for c in self.read() if c["Name"] == "First")
        self.assertEqual(first["Mods"], ["mod/a.mod", "mod/b.mod"])
        self.assertEqual(first["ModNames"], ["Test A", "Test B"])
        self.assertEqual(first["MergedFolderName"], "keep-this-metadata")
        self.assertTrue(first["PatchModEnabled"])
        unchanged = self.database.read_bytes()
        self.assertFalse(self.call("mods", "enable", "mod/b.mod")["changed"])
        self.assertEqual(self.database.read_bytes(), unchanged)
        self.call("mods", "disable", "mod/a.mod")
        first = next(c for c in self.read() if c["Name"] == "First")
        self.assertEqual(first["Mods"], ["mod/b.mod"])
        self.assertEqual(first["ModNames"], ["Test B"])
        self.call("mods", "disable", "mod/b.mod", "--collection", "Second")
        self.assertEqual([c["Name"] for c in self.read() if c["IsSelected"]], ["First"])
        self.assertFalse((self.game / "dlc_load.json").exists())

    def test_missing_mod_can_be_removed_without_dropping_other_missing_entries(self):
        first = self.collections[0]
        first["Mods"] += ["mod/missing.mod", "mod/missing2.mod"]
        first["ModNames"] += ["Missing", "Missing 2"]
        first["ModPaths"] += ["", ""]
        first["ModIds"] += [{}, {}]
        self.write()
        self.call("mods", "disable", "mod/missing.mod")
        first = next(c for c in self.read() if c["Name"] == "First")
        self.assertEqual(first["Mods"], ["mod/a.mod", "mod/missing2.mod"])

    def test_rejections_do_not_mutate_database(self):
        original = self.database.read_bytes()
        for args in [("mods", "enable", "not-a-mod"), ("collection", "select", "missing"),
                     ("collection", "apply"), ("mods", "list", "--nonsense")]:
            self.call(*args, ok=False)
            self.assertEqual(self.database.read_bytes(), original)
        self.assertFalse((self.game / "dlc_load.json").exists())
        self.call("collections", "list", ok=False, data=False)

    def test_recovery_and_newer_database_are_rejected(self):
        original = self.database.read_bytes()
        recovery = Path(str(self.database) + ".tmp")
        recovery.write_bytes(original)
        self.assertIn("recovery", self.call("collections", "list", ok=False).stderr)
        recovery.unlink()
        (self.data / "Database_1.28.json").write_bytes(original)
        self.assertIn("newer", self.call("collections", "list", ok=False).stderr)
        self.assertEqual(self.database.read_bytes(), original)

    def test_corrupt_database_is_not_overwritten(self):
        self.database.write_text('[{"Name":"bad"}]', encoding="utf-8")
        original = self.database.read_bytes()
        self.call("collection", "select", "Second", ok=False)
        self.assertEqual(self.database.read_bytes(), original)

    def test_apply_writes_only_isolated_game_directory_in_collection_order(self):
        self.call("mods", "enable", "mod/b.mod")
        original = self.database.read_bytes()
        result = self.call("collection", "apply", "--yes")
        self.assertTrue(result["applied"])
        self.assertTrue((Path(result["backup"]) / "manifest.json").exists())
        self.assertEqual(json.loads((self.game / "dlc_load.json").read_text())["enabled_mods"], ["mod/a.mod", "mod/b.mod"])
        self.assertTrue((self.game / "launcher-v2.sqlite").is_file())
        self.assertEqual(self.database.read_bytes(), original)

    def test_apply_rejects_missing_enabled_mod_before_writing(self):
        self.collections[0]["Mods"] = ["mod/missing.mod"]
        self.collections[0]["ModPaths"] = [str(self.root / "missing-content")]
        self.write()
        original = self.database.read_bytes()
        result = self.call("collection", "apply", "--yes", ok=False)
        self.assertIn("missing", result.stderr)
        self.assertFalse((self.game / "dlc_load.json").exists())
        self.assertEqual(self.database.read_bytes(), original)

    def test_failed_apply_rolls_back_launcher_and_custom_patch_descriptors(self):
        custom = self.root / "custom-mods"
        patch = custom / "IronyModManager_First"
        patch.mkdir(parents=True)
        # Upstream treats an empty default directory as absent and falls back to custom mods.
        (self.game / "mod" / "IronyModManager_First").mkdir()
        descriptor = patch / "descriptor.mod"
        descriptor.write_text('name="original patch"\n', encoding="utf-8")
        settings = next(i["Value"] for i in self.values if i["Name"] == "GameSettings")
        settings[0]["CustomModDirectory"] = str(custom)
        self.write()
        (self.game / "launcher-v2.sqlite").write_bytes(b"not-a-valid-sqlite-database")
        (self.game / "dlc_load.json").write_text('{"enabled_mods":[],"disabled_dlcs":["preserve"]}', encoding="utf-8")
        before = {p: p.read_bytes() for root in (self.game, custom) for p in root.rglob("*") if p.is_file()}
        result = self.call("collection", "apply", "--yes", ok=False)
        self.assertIn("restored", result.stderr)
        after = {p: p.read_bytes() for root in (self.game, custom) for p in root.rglob("*") if p.is_file()}
        self.assertEqual(after, before)


if __name__ == "__main__":
    unittest.main(verbosity=2)
