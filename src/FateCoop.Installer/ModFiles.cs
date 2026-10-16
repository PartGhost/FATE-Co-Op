using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace FateCoop.Installer;

public static class ModFiles
{
    private const string AppId = "3030720", GameExe = "FateReawakened.exe", GameProcess = "FateReawakened";
    private const string LatestReleaseApi = "https://api.github.com/repos/PartGhost/FATE-Co-Op/releases/latest", ModAsset = "FateCoop-Mod.zip";
    private const string LoaderFolder = "BepInEx";
    private static readonly string PluginFolder = Path.Combine(LoaderFolder, "plugins", "FateCoop");
    private const string Manifest = "installed.txt";

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
        return File.Exists(manifest) ? File.ReadLines(manifest).First() : null;
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
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var file = File.Create(zip);
        await response.Content.CopyToAsync(file);
        return zip;
    }

    public static string? Install(string game, string zip, string version)
    {
        EnsureGameClosed();
        var backup = BackupSaves();
        var plugin = Path.Combine(game, PluginFolder);
        if (Directory.Exists(plugin)) Directory.Delete(plugin, true);
        var root = Path.GetFullPath(game) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
            {
                var target = Path.GetFullPath(Path.Combine(game, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The mod zip has a file outside the game folder: " + entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                entry.ExtractToFile(target, true);
                files.Add(target.Substring(root.Length));
            }
        }
        File.WriteAllLines(Path.Combine(plugin, Manifest), new[] { version }.Concat(files));
        return backup;
    }

    public static bool Remove(string game)
    {
        EnsureGameClosed();
        var plugin = Path.Combine(game, PluginFolder);
        var files = File.ReadLines(Path.Combine(plugin, Manifest)).Skip(1).ToList();
        Directory.Delete(plugin, true);
        var plugins = Path.GetDirectoryName(plugin);
        if (Directory.EnumerateFileSystemEntries(plugins).Any()) return false;
        foreach (var file in files.Where(f => Path.GetDirectoryName(f).Length == 0)) File.Delete(Path.Combine(game, file));
        Directory.Delete(Path.Combine(game, LoaderFolder), true);
        return true;
    }

    private static void EnsureGameClosed()
    {
        if (Process.GetProcessesByName(GameProcess).Length > 0) throw new InvalidOperationException("Close FATE first.");
    }

    private static string? BackupSaves()
    {
        if (!Directory.Exists(SaveFolder)) return null;
        var backup = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"));
        foreach (var file in Directory.GetFiles(SaveFolder, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(backup, file.Substring(SaveFolder.Length + 1));
            Directory.CreateDirectory(Path.GetDirectoryName(copy));
            File.Copy(file, copy, true);
        }
        return backup;
    }
}
