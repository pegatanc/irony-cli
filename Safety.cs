using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IronyModManager.DI;
using IronyModManager.Models.Common;
using IronyModManager.Storage.Common;

namespace IronyCli;

internal sealed class DataSnapshot : IDisposable
{
    private readonly Mutex mutex;
    private readonly byte[] original;
    private readonly string databasePath;
    private readonly string workDirectory;
    private bool ownsMutex;
    public string DataDirectory { get; }
    public string StorageRoot { get; }
    private string StagedDatabase => Path.Combine(StorageRoot, "IronyModManager", Path.GetFileName(databasePath));

    public DataSnapshot(string directory)
    {
        DataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(DataDirectory)) throw new DirectoryNotFoundException("The explicit --data-dir does not exist.");
        Safety.RejectLinks(DataDirectory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())));
        mutex = new Mutex(false, @"Local\irony-cli-" + key);
        workDirectory = Path.Combine(Path.GetTempPath(), "irony-cli", Guid.NewGuid().ToString("N"));
        StorageRoot = Path.Combine(workDirectory, "storage");
        try
        {
            try { ownsMutex = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex) throw new IOException("Another CLI command is using this data directory.");
            var version = FileVersionInfo.GetVersionInfo(typeof(IronyModManager.Storage.Storage).Assembly.Location);
            databasePath = Path.Combine(DataDirectory, $"Database_{version.FileMajorPart}.{version.FileMinorPart}.json");
            var databases = Directory.GetFiles(DataDirectory, "Database_*.json");
            if (!File.Exists(databasePath))
                throw new FileNotFoundException($"Expected {Path.GetFileName(databasePath)}. This build will not migrate another Irony database version.");
            if (databases.Any(p => ParseVersion(p) > new Version(version.FileMajorPart, version.FileMinorPart)))
                throw new InvalidDataException("A newer Irony database is present. Refusing to use an older backend.");
            if (Directory.EnumerateFiles(DataDirectory, "Database_*.json.tmp").Any())
                throw new InvalidDataException("An Irony recovery .tmp database exists. Recover it in Irony before using this CLI.");
            Safety.RejectLinks(databasePath);
            original = File.ReadAllBytes(databasePath);
            Validate(original);
            Directory.CreateDirectory(Path.GetDirectoryName(StagedDatabase)!);
            File.WriteAllBytes(StagedDatabase, original);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static Version ParseVersion(string path) =>
        Version.TryParse(Path.GetFileNameWithoutExtension(path).Replace("Database_", ""), out var version) ? version : new Version(0, 0);

    public void CheckLoadedDatabase()
    {
        using var document = JsonDocument.Parse(original);
        var items = document.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("Name").GetString()!, e => e.GetProperty("Value"));
        var loaded = DIResolver.Get<IDatabase>();
        var expected = items["ModCollection"].EnumerateArray().Select(c => (c.GetProperty("Game").GetString()!, c.GetProperty("Name").GetString()!)).ToHashSet();
        var actual = loaded.ModCollection.Select(c => (c.Game, c.Name)).ToHashSet();
        if (!expected.SetEquals(actual) || items["ModCollection"].GetArrayLength() != loaded.ModCollection.Count())
            throw new InvalidDataException("Irony did not load the complete collection database. Refusing to continue.");
        if (items["Preferences"].TryGetProperty("Game", out var game) && game.GetString() != loaded.Preferences.Game)
            throw new InvalidDataException("Irony did not load the saved game preference. Refusing to continue.");
    }

    public void RequireUnchanged()
    {
        Safety.RequireClosedApps();
        if (!original.AsSpan().SequenceEqual(File.ReadAllBytes(databasePath)))
            throw new IOException("The database changed during this command; no CLI collection changes were committed.");
        if (File.Exists(databasePath + ".tmp")) throw new IOException("An Irony recovery file appeared during this command.");
    }

    public string Commit(string command, string? game = null, string? collection = null)
    {
        RequireUnchanged();
        var staged = File.ReadAllBytes(StagedDatabase);
        Validate(staged);
        var updated = ScopedUpdate(staged, command, game, collection);
        Validate(updated);
        var backupDirectory = Path.Combine(DataDirectory, "cli-backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDirectory);
        var backup = Path.Combine(backupDirectory, Path.GetFileName(databasePath));
        var pending = Path.Combine(DataDirectory, ".irony-cli-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(updated);
                stream.Flush(flushToDisk: true);
            }
            // Prevent normal concurrent writers between the final comparison and atomic replacement.
            using var current = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var currentBytes = new MemoryStream();
            current.CopyTo(currentBytes);
            if (!original.AsSpan().SequenceEqual(currentBytes.ToArray())) throw new IOException("Database changed before commit.");
            Safety.RequireClosedApps();
            File.Replace(pending, databasePath, backup);
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
        return backup;
    }

    private byte[] ScopedUpdate(byte[] staged, string command, string? game, string? collection)
    {
        var before = JsonNode.Parse(original)!.AsArray();
        var after = JsonNode.Parse(staged)!.AsArray();
        JsonNode Item(JsonArray database, string name) => database.Single(n => (string?)n!["Name"] == name)!;
        // Backend services decide the new state. Commit only requested fields, preserving unrelated/unknown data.
        if (command == "game select")
        {
            Item(before, "Preferences")["Value"]!["Game"] = Item(after, "Preferences")["Value"]!["Game"]!.DeepClone();
            var stagedSettings = Item(after, "GameSettings")["Value"]!.AsArray().Single(s => (string?)s!["Type"] == game)!;
            var settingsItem = before.FirstOrDefault(n => (string?)n!["Name"] == "GameSettings");
            if (settingsItem == null)
            {
                settingsItem = Item(after, "GameSettings").DeepClone();
                settingsItem["Value"] = new JsonArray();
                before.Add(settingsItem);
            }
            var settings = settingsItem["Value"]!.AsArray();
            var existing = settings.SingleOrDefault(s => (string?)s!["Type"] == game);
            if (existing == null) settings.Add(stagedSettings.DeepClone());
            else foreach (var field in new[] { "ExecutableLocation", "LaunchArguments", "CloseAppAfterGameLaunch", "RefreshDescriptors", "UserDirectory", "CustomModDirectory" })
                existing[field] = stagedSettings[field]?.DeepClone();
        }
        else
        {
            var oldCollections = Item(before, "ModCollection")["Value"]!.AsArray();
            var newCollections = Item(after, "ModCollection")["Value"]!.AsArray();
            if (oldCollections.Count != newCollections.Count) throw new InvalidDataException("Unexpected collection count change; nothing committed.");
            foreach (var oldCollection in oldCollections.Where(c => (string?)c!["Game"] == game))
            {
                var name = (string?)oldCollection!["Name"];
                var newCollection = newCollections.Single(c => (string?)c!["Game"] == game && (string?)c!["Name"] == name)!;
                if (command == "collection select") oldCollection["IsSelected"] = newCollection["IsSelected"]?.DeepClone();
                else if (name == collection)
                {
                    var oldMods = oldCollection["Mods"]?.AsArray().Select(m => (string?)m).ToArray() ?? [];
                    var oldIds = oldCollection["ModIds"] as JsonArray;
                    var newIds = newCollection["ModIds"]?.DeepClone();
                    if (oldIds != null && newIds is JsonArray ids)
                    {
                        var newMods = newCollection["Mods"]!.AsArray();
                        for (var i = 0; i < newMods.Count; i++)
                        {
                            var previous = Array.FindIndex(oldMods, d => Commands.Same(d, (string?)newMods[i]));
                            if (previous >= 0) ids[i] = oldIds[previous]?.DeepClone();
                        }
                    }
                    foreach (var field in new[] { "Mods", "ModPaths", "ModNames", "ModIds" })
                        oldCollection[field] = field == "ModIds" ? newIds : newCollection[field]?.DeepClone();
                }
            }
        }
        return JsonSerializer.SerializeToUtf8Bytes(before);
    }

    private static void Validate(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid Irony database: expected an array.");
        var names = new HashSet<string>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var properties = item.EnumerateObject().Select(p => p.Name).ToArray();
            if (!properties.SequenceEqual(new[] { "Type", "Name", "Value" }))
                throw new InvalidDataException("Unexpected Irony storage structure/property order. Nothing changed.");
            var name = item.GetProperty("Name").GetString() ?? "";
            var type = item.GetProperty("Type").GetString();
            if (!names.Add(name)) throw new InvalidDataException("Duplicate storage item.");
            var expectedType = name switch
            {
                "ModCollection" => "IEnumerable<IronyModManager.Models.ModCollection>",
                "GameSettings" => "IEnumerable<IronyModManager.Models.GameSettings>",
                "Preferences" => "IronyModManager.Models.Preferences",
                "AppState" => "IronyModManager.Models.AppState",
                "WindowState" => "IronyModManager.Models.WindowState",
                "InternalStorageDateId" => "InternalStorageDateId",
                _ => throw new InvalidDataException($"Unknown storage item: {name}")
            };
            var interfaceType = name is "ModCollection" or "GameSettings" ? $"IEnumerable<IronyModManager.Models.Common.I{name}>" : null;
            if (type == null || (type != expectedType && type != interfaceType)) throw new InvalidDataException($"Unexpected type for {name}. Nothing changed.");
            var value = item.GetProperty("Value");
            if (name is "ModCollection" or "GameSettings")
            {
                if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"Invalid {name} array.");
                if (name == "ModCollection")
                {
                    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var collection in value.EnumerateArray())
                    {
                        var game = collection.GetProperty("Game").GetString();
                        var collectionName = collection.GetProperty("Name").GetString();
                        if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(collectionName) || !keys.Add(game + "\0" + collectionName))
                            throw new InvalidDataException("Empty or duplicate collection identity.");
                        if (collection.TryGetProperty("Mods", out var mods) && mods.ValueKind != JsonValueKind.Null)
                        {
                            if (mods.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid mod list.");
                            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var mod in mods.EnumerateArray())
                            {
                                var descriptor = mod.GetString();
                                if (string.IsNullOrWhiteSpace(descriptor) || !seen.Add(descriptor.Replace('\\', '/')))
                                    throw new InvalidDataException("Empty or duplicate enabled mod descriptor.");
                            }
                        }
                    }
                }
            }
            else if (name != "InternalStorageDateId" && value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Invalid {name} object.");
        }
        if (!names.Contains("ModCollection") || !names.Contains("Preferences"))
            throw new InvalidDataException("Database is missing required collection/preference storage items.");
    }

    public void Dispose()
    {
        // This is only this command's UUID-named scratch folder, never a supplied data directory.
        if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, recursive: true);
        if (ownsMutex) { mutex.ReleaseMutex(); ownsMutex = false; }
        mutex.Dispose();
    }
}

internal static class Safety
{
    public static void RequireClosedApps(IGame? game = null)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IronyModManager", "Paradox Launcher", "dowser", "bootstrapper-v2" };
        if (game != null)
        {
            if (string.IsNullOrWhiteSpace(game.UserDirectory) || !Directory.Exists(game.UserDirectory))
                throw new DirectoryNotFoundException("The selected game's user directory does not exist.");
            RejectLinks(game.UserDirectory);
            if (!string.IsNullOrWhiteSpace(game.ExecutableLocation) && !game.ExecutableLocation.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
                blocked.Add(Path.GetFileNameWithoutExtension(game.ExecutableLocation));
            else
            {
                // A Steam URL has no executable name; fall back to the selected game, not every game.
                var executable = game.Abrv switch { "EU4" => "eu4", "EU5" => "eu5", "HOI4" => "hoi4", "CK3" => "ck3", "IR" => "imperator", "Vic3" => "victoria3", "STInfinite" => "infinite", _ => game.Abrv };
                blocked.Add(executable);
            }
        }
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (blocked.Contains(process.ProcessName)) throw new IOException($"Close {process.ProcessName} before this operation.");
                }
                catch (InvalidOperationException) { /* The process exited during enumeration. */ }
            }
        }
    }

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Refusing a symlink/junction path: {current}");
    }
}

internal sealed class ApplyBackup : IDisposable
{
    private readonly List<(string Target, string Backup, FileAttributes? Attributes)> entries = [];
    public string DirectoryPath { get; }
    public ApplyBackup(string dataDirectory, string gameDirectory, IEnumerable<string> targets)
    {
        DirectoryPath = Path.Combine(dataDirectory, "cli-backups", "apply-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N"));
        foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Safety.RejectLinks(target);
            var backup = Path.Combine(DirectoryPath, entries.Count.ToString("D3") + "-" + Path.GetFileName(target));
            var attributes = File.Exists(target) ? File.GetAttributes(target) : (FileAttributes?)null;
            entries.Add((target, backup, attributes));
        }
        Directory.CreateDirectory(DirectoryPath);
        foreach (var entry in entries.Where(e => e.Attributes.HasValue)) File.Copy(entry.Target, entry.Backup);
        File.WriteAllText(Path.Combine(DirectoryPath, "manifest.json"), JsonSerializer.Serialize(new
        {
            gameDirectory, files = entries.Select(e => new { path = e.Target, backup = e.Backup, existed = e.Attributes.HasValue, attributes = e.Attributes?.ToString() })
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.Error.WriteLine($"Apply backup: {DirectoryPath}");
    }

    public void Complete() => File.WriteAllText(Path.Combine(DirectoryPath, "SUCCESS"), DateTime.UtcNow.ToString("O"));

    public void Restore()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var errors = new List<string>();
        foreach (var entry in entries)
        {
            try
            {
                Safety.RejectLinks(entry.Target);
                if (File.Exists(entry.Target)) File.SetAttributes(entry.Target, FileAttributes.Normal);
                if (entry.Attributes.HasValue)
                {
                    File.Copy(entry.Backup, entry.Target, overwrite: true);
                    File.SetAttributes(entry.Target, entry.Attributes.Value);
                }
                else if (File.Exists(entry.Target)) File.Delete(entry.Target);
            }
            catch (Exception ex) { errors.Add($"{entry.Target}: {ex.Message}"); }
        }
        if (errors.Count > 0) throw new IOException($"Apply failed and rollback needs manual recovery from {DirectoryPath}: {string.Join("; ", errors)}");
        Console.Error.WriteLine("Apply failed; backed-up launcher/descriptor files restored.");
    }

    public void Dispose() { /* Backups intentionally survive success and failure. */ }
}

internal sealed class GameWriteLock : IDisposable
{
    private readonly Mutex mutex;
    public GameWriteLock(string directory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        mutex = new Mutex(false, @"Local\irony-cli-game-" + key);
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            mutex.Dispose();
            throw new IOException("Another CLI command is applying to this game directory.");
        }
    }

    public void Dispose()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
