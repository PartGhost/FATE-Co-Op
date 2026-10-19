using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace FateCoop.Installer;

public static class ModFiles
{
    private const string AppId = "3030720", GameExe = "FateReawakened.exe", GameProcess = "FateReawakened";
    private const string LatestReleaseApi = "https://api.github.com/repos/PartGhost/FATE-Co-Op/releases/latest", ModAsset = "FateCoop-Mod.zip";
    private const string LoaderFolder = "BepInEx", StagingFolder = "FateCoop-staging", Manifest = "installed.txt", PluginConfig = "local.fatecoop.prototype.cfg";
    private const string SaveFile = "FateMainSave.save", OldSagestone = "\"baseName\":\"Item_\\w*Sagestone\"";
    private const int SaveLengthOffset = 8, SaveHeaderBytes = 12, OriginalGame = 0, GroveRealm = 0, QuestLogRows = 3;
    private static readonly string PluginFolder = Path.Combine(LoaderFolder, "plugins", "FateCoop");
    private static readonly string[] ModFolders = { "plugins", "patchers" };

    public static string SaveFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Tableflip", "FATE Reawakened");
    public static string BackupRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FATE Co-Op save backups");

    [DataContract] private sealed class Release
    {
        [DataMember] public string tag_name = "";
        [DataMember] public List<Asset> assets = new List<Asset>();
    }

    [DataContract] private sealed class Asset
    {
        [DataMember] public string name = "", browser_download_url = "";
    }

    [DataContract] private sealed class SaveFileData
    {
        [DataMember] public List<SaveSlot> saveSlots = new List<SaveSlot>();
    }

    [DataContract] private sealed class SaveSlot
    {
        [DataMember] public int targetGame { get; set; }
        [DataMember] public SlotData? saveData { get; set; }
    }

    [DataContract] private sealed class SlotData
    {
        [DataMember] public QuestData? questSaveData { get; set; }
    }

    [DataContract] private sealed class QuestData
    {
        [DataMember] public List<QuestRealm> questRealms = new List<QuestRealm>();
    }

    [DataContract] private sealed class QuestRealm
    {
        [DataMember] public int realmId { get; set; }
        [DataMember] public List<QuestSave> questSaves = new List<QuestSave>();
    }

    [DataContract] private sealed class QuestSave
    {
        [DataMember] public int status { get; set; }
    }

    public static string? FindGame()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (!(key?.GetValue("SteamPath") is string steam)) return null;
        var libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraries)) return null;
        foreach (Match library in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s+\"([^\"]+)\""))
        {
            var steamapps = Path.Combine(library.Groups[1].Value.Replace(@"\\", @"\"), "steamapps");
            var manifest = Path.Combine(steamapps, "appmanifest_" + AppId + ".acf");
            if (!File.Exists(manifest)) continue;
            var game = Path.Combine(steamapps, "common", Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value);
            if (IsGame(game)) return game;
        }
        return null;
    }

    public static bool IsGame(string folder) => File.Exists(Path.Combine(folder, GameExe));

    public static string? InstalledVersion(string game)
    {
        var manifest = Path.Combine(game, PluginFolder, Manifest);
        return File.Exists(manifest) ? File.ReadLines(manifest).FirstOrDefault() : null;
    }

    private static HashSet<string> InstalledFiles(string game)
    {
        var manifest = Path.Combine(game, PluginFolder, Manifest);
        return new HashSet<string>(File.Exists(manifest) ? File.ReadLines(manifest).Skip(1) : Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<(string version, string url)> LatestRelease()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FateCoop-Installer");
        using var json = await client.GetStreamAsync(LatestReleaseApi);
        var release = (Release)new DataContractJsonSerializer(typeof(Release)).ReadObject(json);
        var asset = release.assets.FirstOrDefault(a => a.name == ModAsset) ?? throw new InvalidDataException("The latest release has no " + ModAsset + ".");
        return (release.tag_name.TrimStart('v'), asset.browser_download_url);
    }

    public static async Task<string> Download(string url)
    {
        var zip = Path.Combine(Path.GetTempPath(), ModAsset);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FateCoop-Installer");
        using var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var file = File.Create(zip);
        await response.Content.CopyToAsync(file);
        return zip;
    }

    public static string? Install(string game, string zip, string version)
    {
        EnsureGameClosed();
        var root = Path.GetFullPath(game) + Path.DirectorySeparatorChar;
        var staging = Path.Combine(game, StagingFolder);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        var owned = InstalledFiles(game);
        var files = new List<string>();
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
            {
                var target = Path.GetFullPath(Path.Combine(game, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The mod zip has a file outside the game folder: " + entry.FullName);
                var file = target.Substring(root.Length);
                if (File.Exists(target) && !owned.Contains(file) && !file.StartsWith(PluginFolder, StringComparison.OrdinalIgnoreCase))
                {
                    if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !SameFile(entry, target))
                        throw new InvalidOperationException("FATE already has a different BepInEx installed (" + file + "). FATE Co-Op needs its own BepInEx build: remove the other BepInEx and the mods that use it first, then install again.");
                    continue;
                }
                var staged = Path.Combine(staging, file);
                Directory.CreateDirectory(Path.GetDirectoryName(staged));
                entry.ExtractToFile(staged, true);
                files.Add(file);
            }
        }
        var backup = BackupSaves();
        var plugin = Path.Combine(game, PluginFolder);
        if (Directory.Exists(plugin)) Directory.Delete(plugin, true);
        Directory.CreateDirectory(plugin);
        File.WriteAllLines(Path.Combine(plugin, Manifest), new[] { version }.Concat(files));
        foreach (var file in files)
        {
            var target = Path.Combine(game, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(Path.Combine(staging, file), target, true);
        }
        Directory.Delete(staging, true);
        return backup;
    }

    private static bool SameFile(ZipArchiveEntry entry, string path)
    {
        using var packed = entry.Open();
        using var copy = new MemoryStream();
        packed.CopyTo(copy);
        return copy.ToArray().SequenceEqual(File.ReadAllBytes(path));
    }

    public static bool Remove(string game)
    {
        EnsureGameClosed();
        var owned = InstalledFiles(game);
        Directory.Delete(Path.Combine(game, PluginFolder), true);
        var loader = Path.Combine(game, LoaderFolder);
        var config = Path.Combine(loader, "config", PluginConfig);
        if (File.Exists(config)) File.Delete(config);
        var ownLoader = owned.Any(file => file.StartsWith(LoaderFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !file.StartsWith(PluginFolder, StringComparison.OrdinalIgnoreCase));
        var otherMods = ModFolders.Select(folder => Path.Combine(loader, folder)).Where(Directory.Exists).Any(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any());
        if (!ownLoader || otherMods) return false;
        foreach (var file in owned.Select(file => Path.Combine(game, file)).Where(File.Exists)) File.Delete(file);
        Directory.Delete(loader, true);
        return true;
    }

    public static IEnumerable<string> RemovalWarnings()
    {
        var path = Path.Combine(SaveFolder, SaveFile);
        if (!File.Exists(path)) return Enumerable.Empty<string>();
        var bytes = File.ReadAllBytes(path);
        var length = BitConverter.ToInt32(bytes, SaveLengthOffset);
        var save = (SaveFileData)new DataContractJsonSerializer(typeof(SaveFileData)).ReadObject(new MemoryStream(bytes, SaveHeaderBytes, length));
        var warnings = new List<string>();
        var quests = save.saveSlots.Where(slot => slot.targetGame == OriginalGame && slot.saveData?.questSaveData != null)
            .Select(slot => slot.saveData!.questSaveData!.questRealms.Where(realm => realm.realmId == GroveRealm).Sum(realm => realm.questSaves.Count(quest => quest.status != 0)))
            .DefaultIfEmpty(0).Max();
        if (quests > QuestLogRows) warnings.Add("Your save has " + quests + " side quests in the Grove. FATE without the mod only has room for " + QuestLogRows + ", so its quest log would stop opening. Finish or cancel some first.");
        if (Regex.IsMatch(Encoding.UTF8.GetString(bytes, SaveHeaderBytes, length), OldSagestone)) warnings.Add("Your save has Sagestones from an older version. Play once with this version and save, so they turn into Rubies instead of breaking the items they're in.");
        return warnings;
    }

    private static void EnsureGameClosed()
    {
        if (Process.GetProcessesByName(GameProcess).Length > 0) throw new InvalidOperationException("Close FATE first.");
    }

    private static string? BackupSaves()
    {
        var files = Directory.Exists(SaveFolder) ? Directory.GetFiles(SaveFolder, "*", SearchOption.AllDirectories) : Array.Empty<string>();
        if (files.Length == 0) return null;
        var backup = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"));
        foreach (var file in files)
        {
            var copy = Path.Combine(backup, file.Substring(SaveFolder.Length + 1));
            Directory.CreateDirectory(Path.GetDirectoryName(copy));
            File.Copy(file, copy, true);
        }
        return backup;
    }
}
