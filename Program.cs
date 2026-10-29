using System.Text.Json;

namespace IronyCli;

internal static class Program
{
    internal const string Help = """
        Irony CLI (Windows, Irony backend 1.27.206)
        Usage: irony-cli --data-dir <IronyModManager database folder> <command>

          games list
          game select <game name or abbreviation>
          collections list
          collection select <collection name>
          mods list [--collection <name>]
          mods enable <descriptor, exact name or remote ID> [--collection <name>]
          mods disable <descriptor, exact name or remote ID> [--collection <name>]
          collection apply [--collection <name>] --yes

        Output is JSON. Collection edits do NOT apply to the game.
        Enabling a mod appends it to the collection's load order.
        Close Irony and the Paradox launcher before mutations; also close the game before apply.
        --data-dir is the folder containing Database_1.27.json, not its parent.
        """;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args is ["--help"] or ["-h"])
            {
                Console.WriteLine(Help);
                return 0;
            }

            var options = Options.Parse(args);
            options = options with { DataDirectory = Path.GetFullPath(options.DataDirectory) };
            // Upstream's launcher database template uses a path relative to the executable.
            Environment.CurrentDirectory = AppContext.BaseDirectory;
            // Run synchronously so the named mutex is released on its acquiring thread.
            using var snapshot = new DataSnapshot(options.DataDirectory);
            if (options.Mutates) Safety.RequireClosedApps();
            using var backend = new BackendHost(snapshot.StorageRoot);
            snapshot.CheckLoadedDatabase();
            var result = Commands.RunAsync(backend, snapshot, options).GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.GetBaseException().Message}");
            return 1;
        }
    }
}

internal sealed record Options(string DataDirectory, string Command, string? Argument, string? Collection, bool Yes)
{
    public bool Mutates => Command is not ("games list" or "collections list" or "mods list");

    public static Options Parse(string[] args)
    {
        var words = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var yes = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--yes")
            {
                if (yes) throw new ArgumentException("Duplicate --yes.");
                yes = true;
            }
            else if (arg is "--data-dir" or "--collection")
            {
                if (++i == args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--"))
                    throw new ArgumentException($"{arg} requires a value.");
                if (!values.TryAdd(arg, args[i])) throw new ArgumentException($"Duplicate {arg}.");
            }
            else if (arg.StartsWith('-')) throw new ArgumentException($"Unknown option: {arg}");
            else words.Add(arg);
        }

        if (words.Count < 2) throw new ArgumentException("Specify a command; see --help.");
        var command = $"{words[0]} {words[1]}";
        var expected = command switch
        {
            "games list" or "collections list" or "mods list" or "collection apply" => 2,
            "game select" or "collection select" or "mods enable" or "mods disable" => 3,
            _ => throw new ArgumentException($"Unknown command: {command}")
        };
        if (words.Count != expected) throw new ArgumentException($"Wrong arguments for {command}; quote names with spaces.");
        if (!values.TryGetValue("--data-dir", out var directory))
            throw new ArgumentException("--data-dir is required. No personal-data folder is chosen automatically.");
        values.TryGetValue("--collection", out var collection);
        if (collection != null && command is not ("mods list" or "mods enable" or "mods disable" or "collection apply"))
            throw new ArgumentException($"--collection is not valid for {command}.");
        if (yes && command != "collection apply") throw new ArgumentException("--yes is only valid for collection apply.");
        if (command == "collection apply" && !yes)
            throw new ArgumentException("Applying writes game/launcher files. Explicit --yes is required.");
        return new(directory, command, words.Count == 3 ? words[2] : null, collection, yes);
    }
}
