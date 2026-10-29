# Irony CLI

An unofficial Windows x64 command-line companion for [**Irony Mod Manager 1.27.206**](https://github.com/bcssov/IronyModManager). Uses Irony's signed backend libraries; does not launch the desktop app, game, Steam, updater, or plugins. Requires the **.NET 8 x64 runtime**.

This repository contains the CLI source and tests, not the downloaded Irony binaries. Build instructions are below. This is a separate companion project, not an official Irony release.

## Use

Keep the entire distributed folder together. Run `irony-cli.exe --help` for commands.

`--data-dir` must explicitly name the folder containing **Database_1.27.json**. A typical installation uses `%APPDATA%\Mario\IronyModManager`; use a **copy** for initial testing. No profile directory is chosen automatically.

```powershell
# Replace this with a copied database folder first.
$data = 'C:\Irony-Test\IronyModManager'
.\irony-cli.exe --data-dir $data games list
.\irony-cli.exe --data-dir $data game select Stellaris
.\irony-cli.exe --data-dir $data collections list
.\irony-cli.exe --data-dir $data collection select 'My Collection'
.\irony-cli.exe --data-dir $data mods list
.\irony-cli.exe --data-dir $data mods enable 'mod/ugc_123456.mod'
.\irony-cli.exe --data-dir $data mods disable 'mod/ugc_123456.mod'
```

Mod selectors accept an exact descriptor, exact display name, or remote ID. Ambiguous matches are rejected; descriptors are the most reliable. Enabling appends to the load order. Enabling/disabling is idempotent. `--collection 'Name'` targets a specific collection without selecting it.

Collection edits **do not apply** to the game. Applying is a separate, explicitly confirmed command:

```powershell
.\irony-cli.exe --data-dir $data collection apply --collection 'My Collection' --yes
```

**A copied Irony database can still point to your real game directory.** Copying the database alone does not sandbox `apply`. The automated tests create both a synthetic database and synthetic game directories; the examples above do not do that for you. Inspect `games list`'s `userDirectory` before applying. `apply --collection` applies that collection without changing which collection Irony has selected.

Successful commands print JSON to stdout. Errors/logs go to stderr with exit code 1; success is exit code 0.

## Safety and recovery

- Reads and backend initialization operate on a scratch copy. Collection writes atomically replace the database only after confirming its original bytes have not changed, and preserve unrelated stored fields.
- Original database backups are saved under `<data-dir>\cli-backups\<timestamp>\Database_1.27.json` and their paths are returned in JSON.
- Apply backs up affected launcher JSON/SQLite files and patch descriptors first. `manifest.json` records original paths, backup files, and which targets originally did not exist. On a handled apply failure the CLI attempts rollback and reports any manual recovery needed. Backups remain available.
- Close Irony and the Paradox launcher before any mutation; close the selected game before apply. The CLI never closes processes for you. Do not start those apps during a mutation. Concurrent CLI operations are locked by database directory, and applies additionally by game directory.
- Symlink/junction data and apply-target paths, invalid storage formats, newer-version databases, and pending Irony recovery `.tmp` files are rejected. This build does not migrate databases or create collections.
- Process termination/power loss during apply is not an atomic transaction across all launcher files. Use the retained manifest/backups to recover with Irony, the launcher, and game closed. Do not use while Steam is updating mods.

## Build and test

Clone this repository, download [Irony's official v1.27.206 win-x64.zip](https://github.com/bcssov/IronyModManager/releases/download/v1.27.206/win-x64.zip), and extract its contents into a sibling folder named `irony-runtime` (so `../irony-runtime/IronyModManager.Services.dll` exists). Install/use a .NET 8 SDK and Python 3. Backend DLLs remain unmodified. The two explicit Microsoft package references supply v9 dependencies required by the release's ORM, rather than its bundled v8 framework DLLs. Run the following from the repository root:

```powershell
dotnet build .\irony-cli.csproj -c Release
python .\test_cli.py
dotnet publish .\irony-cli.csproj -c Release --no-restore -o ..\rebuilt
python .\test_cli.py ..\rebuilt\irony-cli.exe
```

Tests use Python's standard library and fresh synthetic data under the temporary directory. They never load or copy personal Irony collections. No upstream source fork/build or private CWTools package is required.

## Scope

Lists/selects games and collections, lists installed/enabled mods, enables/disables mods, and applies existing collections. No installation/download of mods, collection creation/deletion, merging, conflict solving, reordering command, or automatic game launch. The upstream runtime download includes its own `LICENSE` and `Credits.txt`; CLI publish bundles retain those files.
