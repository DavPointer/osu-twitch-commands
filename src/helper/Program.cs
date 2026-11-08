using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Realms;

namespace OsuSkinExport;

/// <summary>
/// Exports an osu!lazer skin into a .osk archive (zip with the skin files at the archive root).
/// Prints a single JSON object on stdout: exit code 0 on success, 1 on error.
///   {"ok":true,"skin":"name","path":"...","entries":337,"bytes":123,"skipped":0}
///   {"ok":false,"error":"reason"}
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            // Keep non-ASCII skin names readable in the JSON reply sent to Streamer.bot.
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            string root = null;
            string skin = null;
            string output = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--root": root = Next(args, ref i); break;
                    case "--skin": skin = Next(args, ref i); break;
                    case "--out": output = Next(args, ref i); break;
                }
            }

            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(output))
            {
                return Error("Usage: osu-skin-export --root <lazer data folder> [--skin <name|guid>] --out <file.osk>");
            }
            if (!Directory.Exists(root))
            {
                return Error($"Data folder not found: {root}");
            }

            string currentId = ReadCurrentSkinId(root);
            if (string.IsNullOrEmpty(skin))
            {
                // No explicit skin: fall back to the one selected in game.ini.
                skin = currentId;
            }

            // Legacy folder skins (osu!lazer also loads <data>\Skins\<name>).
            if (!LooksLikeGuid(skin))
            {
                string folder = string.IsNullOrEmpty(skin) ? null : Path.Combine(root, "Skins", skin);
                if (folder != null && Directory.Exists(folder))
                {
                    return ZipFolder(folder, skin, output);
                }
            }

            string dbPath = Path.Combine(root, "client.realm");
            if (!File.Exists(dbPath))
            {
                return Error($"client.realm not found in {root}");
            }

            return ExportFromRealm(dbPath, root, skin, currentId, output);
        }
        catch (Exception ex)
        {
            return Error(ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static int ExportFromRealm(string dbPath, string root, string wanted, string currentId, string output)
    {
        Guid wantedId;
        bool byId = Guid.TryParse(wanted, out wantedId);

        var config = new RealmConfiguration(dbPath) { IsReadOnly = true, IsDynamic = true };
        using var realm = Realm.GetInstance(config);

        IRealmObjectBase chosen = null;
        string chosenName = null;

        foreach (var candidate in realm.DynamicApi.All("Skin"))
        {
            Guid id = candidate.DynamicApi.Get<Guid>("ID");
            string name = candidate.DynamicApi.Get<string>("Name");

            if (byId)
            {
                if (id == wantedId)
                {
                    chosen = candidate;
                    chosenName = name;
                    break;
                }
                continue;
            }

            if (!string.Equals(Normalise(name), Normalise(wanted), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Prefer the skin currently selected in game.ini when names collide.
            if (chosen == null || (currentId != null && id.ToString() == currentId))
            {
                chosen = candidate;
                chosenName = name;
            }
        }

        if (chosen == null)
        {
            return Error($"Skin not found in client.realm: {wanted}");
        }

        var usages = chosen.DynamicApi.GetList<IRealmObjectBase>("Files");
        if (usages.Count == 0)
        {
            return Error($"Skin has no files: {chosenName}");
        }

        string filesRoot = Path.Combine(root, "files");
        int entries = 0, skipped = 0;
        long bytes = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var fs = File.Create(output))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var usage in usages)
            {
                string name = usage.DynamicApi.Get<string>("Filename");
                var file = usage.DynamicApi.Get<IRealmObjectBase>("File");
                string hash = file?.DynamicApi.Get<string>("Hash");

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(hash) || hash.Length < 3)
                {
                    skipped++;
                    continue;
                }

                string entryName = name.Replace('\\', '/').TrimStart('/');
                if (!seen.Add(entryName))
                {
                    skipped++;
                    continue;
                }

                string source = Path.Combine(filesRoot, hash.Substring(0, 1), hash.Substring(0, 2), hash);
                if (!File.Exists(source))
                {
                    skipped++;
                    continue;
                }

                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using (var target = entry.Open())
                using (var input = File.OpenRead(source))
                {
                    input.CopyTo(target);
                    bytes += input.Length;
                }
                entries++;
            }
        }

        if (entries == 0)
        {
            TryDelete(output);
            return Error($"No files could be exported for skin: {chosenName}");
        }

        return Ok(chosenName, output, entries, bytes, skipped);
    }

    private static int ZipFolder(string folder, string name, string output)
    {
        string temp = output + ".tmp";
        TryDelete(temp);
        ZipFile.CreateFromDirectory(folder, temp, CompressionLevel.Optimal, false);
        File.Move(temp, output, true);

        int entries;
        using (var zip = ZipFile.OpenRead(output))
        {
            entries = zip.Entries.Count;
        }
        return Ok(name, output, entries, new FileInfo(output).Length, 0);
    }

    /// <summary>Reads the "Skin = " value from game.ini (a GUID for realm skins, a name for folder skins).</summary>
    private static string ReadCurrentSkinId(string root)
    {
        string ini = Path.Combine(root, "game.ini");
        if (!File.Exists(ini))
        {
            return null;
        }

        foreach (string line in File.ReadAllLines(ini))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            if (line.Substring(0, eq).Trim() == "Skin")
            {
                string value = line.Substring(eq + 1).Trim();
                return value.Length == 0 ? null : value;
            }
        }
        return null;
    }

    private static string Normalise(string value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static bool LooksLikeGuid(string value)
    {
        return !string.IsNullOrEmpty(value) && Guid.TryParse(value, out _);
    }

    private static string Next(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {args[index]}");
        }
        return args[++index];
    }

    private static int Ok(string skin, string path, int entries, long bytes, int skipped)
    {
        var payload = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["skin"] = skin,
            ["path"] = path,
            ["entries"] = entries,
            ["bytes"] = bytes,
            ["skipped"] = skipped
        };
        Console.Out.Write(JsonSerializer.Serialize(payload));
        return 0;
    }

    private static int Error(string message)
    {
        var payload = new Dictionary<string, object>
        {
            ["ok"] = false,
            ["error"] = message
        };
        Console.Out.Write(JsonSerializer.Serialize(payload));
        return 1;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
    }
}
