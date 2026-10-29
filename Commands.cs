using IronyModManager.DI;
using IronyModManager.IO.Common.Mods;
using IronyModManager.Models.Common;
using IronyModManager.Shared;

namespace IronyCli;

internal static class Commands
{
    public static async Task<object> RunAsync(BackendHost backend, DataSnapshot snapshot, Options options)
    {
        if (options.Command == "games list")
            return backend.Games.Get().Select(g => new { name = g.Type, abbreviation = g.Abrv, selected = g.IsSelected, userDirectory = g.UserDirectory }).ToArray();

        if (options.Command == "game select")
        {
            var games = backend.Games.Get().ToArray();
            var matches = games.Where(g => Same(g.Type, options.Argument) || Same(g.Abrv, options.Argument) || Same(g.Name, options.Argument)).ToArray();
            if (matches.Length != 1) throw new ArgumentException("Game not found or ambiguous; use games list.");
            if (matches[0].IsSelected) return new { changed = false, game = matches[0].Type };
            if (!backend.Games.SetSelected(games, matches[0])) throw new IOException("Could not select game.");
            backend.Flush();
            return new { changed = true, game = matches[0].Type, backup = snapshot.Commit(options.Command, matches[0].Type) };
        }

        var game = backend.Games.GetSelected() ?? throw new InvalidOperationException("No game selected. Use game select first.");
        if (options.Command == "collections list")
            return backend.Collections.GetAll().Select(c => new { name = c.Name, selected = c.IsSelected, enabledCount = c.Mods?.Count() ?? 0, game = c.Game }).ToArray();

        if (options.Command == "collection select")
        {
            var selected = backend.Collections.Get(options.Argument!) ?? throw new ArgumentException("Collection not found for the selected game.");
            if (selected.IsSelected) return new { changed = false, collection = selected.Name };
            selected.IsSelected = true;
            Save(backend, selected);
            return new { changed = true, collection = selected.Name, backup = snapshot.Commit(options.Command, selected.Game, selected.Name) };
        }

        var collection = options.Collection != null
            ? backend.Collections.Get(options.Collection)
            : backend.Collections.GetAll().SingleOrDefault(c => c.IsSelected);
        if (collection == null) throw new ArgumentException("Collection not found. Select one or specify --collection.");
        var installed = (await backend.Mods.GetInstalledModsAsync(game)).ToArray();
        var descriptors = collection.Mods?.ToList() ?? [];

        if (options.Command == "mods list")
        {
            var enabled = ResolveCollection(collection, installed, requireValid: false);
            var records = installed.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select(m => new
            {
                descriptor = m.DescriptorFile, name = m.Name, remoteId = m.RemoteId,
                source = m.Source.ToString(), valid = m.IsValid,
                enabled = enabled.Contains(m), position = enabled.Contains(m) ? enabled.IndexOf(m) + 1 : (int?)null
            });
            return new { game = game.Type, collection = collection.Name, mods = records,
                missing = descriptors.Where((_, i) => FindAt(collection, installed, i) == null).ToArray() };
        }

        if (options.Command is "mods enable" or "mods disable")
        {
            var enable = options.Command == "mods enable";
            var selector = options.Argument!;
            var matching = installed.Where(m => Same(m.DescriptorFile, selector) || Same(m.Name, selector) || m.RemoteId?.ToString() == selector).ToArray();
            var indices = descriptors.Select((d, i) => (d, i)).Where(p => Same(p.d, selector)).Select(p => p.i).ToArray();
            if (matching.Length > 1 || indices.Length > 1) throw new ArgumentException("Ambiguous mod; use its exact descriptor from mods list.");
            var mod = matching.SingleOrDefault();
            var index = indices.SingleOrDefault(-1);
            if (mod != null && index < 0)
                index = descriptors.FindIndex(d => Same(d, mod.DescriptorFile));
            if (mod != null && index < 0 && collection.ModPaths?.Count() == descriptors.Count)
                index = collection.ModPaths.ToList().FindIndex(p => Same(p, mod.FullPath));
            if (enable && mod == null) throw new ArgumentException("Installed mod not found; use mods list.");
            if (!enable && index < 0)
            {
                if (mod == null) throw new ArgumentException("Mod not found in the installed mods or collection.");
                return new { changed = false, collection = collection.Name };
            }
            if (enable && index >= 0) return new { changed = false, collection = collection.Name };
            if (enable && !mod!.IsValid) throw new ArgumentException("Cannot enable a mod whose content is missing.");

            var source = DIResolver.Get<IModCollectionSourceInfo>();
            if (mod?.Source == ModSource.Steam) source.SteamId = mod.RemoteId;
            if (mod?.Source == ModSource.Paradox) source.ParadoxId = mod.RemoteId;
            // Optional parallel metadata stays absent when absent; never discard unrelated missing mods.
            collection.ModPaths = EditMetadata(collection.ModPaths, descriptors.Count, index, enable, mod?.FullPath!);
            collection.ModNames = EditMetadata(collection.ModNames, descriptors.Count, index, enable, mod?.Name!);
            collection.ModIds = EditMetadata(collection.ModIds, descriptors.Count, index, enable, source);
            if (enable) descriptors.Add(mod!.DescriptorFile);
            else descriptors.RemoveAt(index);
            collection.Mods = descriptors;
            Save(backend, collection);
            return new { changed = true, collection = collection.Name, backup = snapshot.Commit(options.Command, collection.Game, collection.Name) };
        }

        if (options.Command == "collection apply")
        {
            Safety.RequireClosedApps(game);
            using var gameLock = new GameWriteLock(game.UserDirectory);
            snapshot.RequireUnchanged();
            var enabled = ResolveCollection(collection, installed, requireValid: true);
            var patchName = $"IronyModManager_{collection.Name}".GenerateValidFileName();
            var descriptorDirectory = game.ModDescriptorType == ModDescriptorType.DescriptorMod ? "mod" : "irony-mod";
            var extension = game.ModDescriptorType == ModDescriptorType.DescriptorMod ? ".mod" : ".json";
            var patchDirectory = Path.Combine(game.UserDirectory, "mod", patchName);
            if (!DIResolver.Get<IModWriter>().ModDirectoryExists(new ModWriterParameters { Path = patchDirectory }) && !string.IsNullOrWhiteSpace(game.CustomModDirectory))
                patchDirectory = Path.Combine(game.CustomModDirectory, patchName);
            var targets = new[] { "dlc_load.json", "game_data.json", "mods_registry.json", "content_load.json", "playsets.json",
                "launcher-v2.sqlite", "launcher-v2_openbeta.sqlite" }.Select(n => Path.Combine(game.UserDirectory, n)).ToList();
            foreach (var db in targets.Where(p => p.EndsWith(".sqlite")).ToArray())
                targets.AddRange(new[] { db + "-wal", db + "-shm", db + "-journal" });
            targets.Add(Path.Combine(game.UserDirectory, descriptorDirectory, patchName + extension));
            targets.Add(Path.Combine(patchDirectory, game.ModDescriptorType == ModDescriptorType.DescriptorMod ? "descriptor.mod" : Path.Combine(".metadata", "metadata.json")));
            using var backup = new ApplyBackup(snapshot.DataDirectory, game.UserDirectory, targets);
            try
            {
                // Keep acquisition/export/rollback on this thread: Windows mutexes are thread-owned.
                if (!backend.Mods.ExportModsAsync(enabled, installed, collection).GetAwaiter().GetResult())
                    throw new IOException("Irony reported that applying the collection failed.");
                backup.Complete();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Apply error: {ex}");
                backup.Restore();
                throw;
            }
            return new { applied = true, game = game.Type, collection = collection.Name, backup = backup.DirectoryPath };
        }

        throw new ArgumentException("Unknown command.");
    }

    private static void Save(BackendHost backend, IModCollection collection)
    {
        if (!backend.Collections.Save(collection)) throw new IOException("Irony could not save the collection.");
        backend.Flush();
    }

    internal static bool Same(string? first, string? second) => string.Equals(first?.Replace('\\', '/'), second?.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<T>? EditMetadata<T>(IEnumerable<T>? values, int count, int index, bool add, T value)
    {
        if (values == null) return null;
        var result = values.ToList();
        if (result.Count != count) throw new InvalidDataException("Collection metadata lengths do not match. Repair the collection in Irony first.");
        if (add) result.Add(value);
        else result.RemoveAt(index);
        return result;
    }

    private static IMod? FindAt(IModCollection collection, IMod[] installed, int index)
    {
        var descriptor = collection.Mods.ElementAt(index);
        var matches = installed.Where(m => Same(m.DescriptorFile, descriptor)).ToArray();
        if (matches.Length > 1) throw new InvalidDataException($"Ambiguous descriptor: {descriptor}");
        if (matches.Length == 1) return matches[0];
        if (collection.ModPaths?.Count() == collection.Mods.Count())
        {
            var path = collection.ModPaths.ElementAt(index);
            if (!string.IsNullOrWhiteSpace(path))
            {
                matches = installed.Where(m => Same(m.FullPath, path)).ToArray();
                if (matches.Length > 1) throw new InvalidDataException($"Ambiguous mod path: {path}");
                return matches.SingleOrDefault();
            }
        }
        return null;
    }

    private static List<IMod> ResolveCollection(IModCollection collection, IMod[] installed, bool requireValid)
    {
        var result = new List<IMod>();
        for (var i = 0; i < (collection.Mods?.Count() ?? 0); i++)
        {
            var mod = FindAt(collection, installed, i);
            if (requireValid && (mod == null || !mod.IsValid))
                throw new InvalidDataException($"Enabled mod is missing or invalid: {collection.Mods!.ElementAt(i)}. Nothing applied.");
            if (mod == null) continue;
            if (result.Contains(mod)) throw new InvalidDataException("Collection contains duplicate references to the same installed mod.");
            result.Add(mod);
        }
        return result;
    }
}
