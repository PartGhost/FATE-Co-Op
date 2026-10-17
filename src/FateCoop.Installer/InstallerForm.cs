using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FateCoop.Installer;

internal sealed class InstallerForm : Form
{
    private readonly Label gameLabel = new Label { AutoSize = true, MaximumSize = new Size(460, 0) };
    private readonly Label versionLabel = new Label { AutoSize = true };
    private readonly Label statusLabel = new Label { AutoSize = true, MaximumSize = new Size(460, 0) };
    private readonly Button browseButton = new Button { Text = "Change folder...", AutoSize = true };
    private readonly Button installButton = new Button { Text = "Install", AutoSize = true };
    private readonly Button updateButton = new Button { Text = "Update", AutoSize = true };
    private readonly Button removeButton = new Button { Text = "Remove", AutoSize = true };
    private readonly Button retryButton = new Button { Text = "Try again", AutoSize = true };
    private string? game;
    private string? latestVersion, latestUrl;
    private bool busy;

    internal InstallerForm()
    {
        Text = "FATE Co-Op";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = new Font("Segoe UI", 10f);
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 12) };
        buttons.Controls.AddRange(new Control[] { installButton, updateButton, removeButton, retryButton });
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.AddRange(new Control[] { gameLabel, browseButton, versionLabel, buttons, statusLabel });
        Controls.Add(layout);
        browseButton.Click += (_, _) => Browse();
        installButton.Click += async (_, _) => await Run(InstallLatest);
        updateButton.Click += async (_, _) => await Run(InstallLatest);
        removeButton.Click += async (_, _) => await Run(Remove);
        retryButton.Click += async (_, _) => await Run(CheckLatest);
        FormClosing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            statusLabel.Text = "Wait until this finishes, so the game isn't left half changed.";
        };
        Shown += async (_, _) => await Run(FindAndCheck);
    }

    private void UpdateView()
    {
        var installed = game != null ? ModFiles.InstalledVersion(game) : null;
        gameLabel.Text = game != null ? "FATE Reawakened: " + game : "FATE Reawakened wasn't found. Pick its folder.";
        versionLabel.Text = "Installed: " + (installed ?? "not installed") + "     Latest: " + (latestVersion ?? "unknown");
        browseButton.Enabled = !busy;
        installButton.Enabled = !busy && game != null && installed == null && latestUrl != null;
        updateButton.Enabled = !busy && installed != null && latestUrl != null;
        removeButton.Enabled = !busy && installed != null;
        retryButton.Visible = !busy && latestUrl == null;
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { Description = "Pick the FATE Reawakened folder (the one with FateReawakened.exe)." };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!ModFiles.IsGame(dialog.SelectedPath)) { statusLabel.Text = "That folder doesn't have FateReawakened.exe."; return; }
        game = dialog.SelectedPath;
        statusLabel.Text = "";
        UpdateView();
    }

    private async Task Run(Func<Task<string>> action)
    {
        busy = true;
        UpdateView();
        try { statusLabel.Text = await action(); }
        catch (UnauthorizedAccessException) { statusLabel.Text = "Windows blocked changes to the game folder. Right-click the installer and choose Run as administrator."; }
        catch (HttpRequestException) { statusLabel.Text = "Couldn't reach GitHub. Check your internet connection and try again."; }
        catch (TaskCanceledException) { statusLabel.Text = "GitHub took too long to answer. Try again."; }
        catch (InvalidDataException) { statusLabel.Text = "The download was damaged. Try again."; }
        catch (Exception e) { statusLabel.Text = e.Message; }
        busy = false;
        UpdateView();
    }

    private async Task<string> FindAndCheck()
    {
        game = ModFiles.FindGame();
        return await CheckLatest();
    }

    private async Task<string> CheckLatest()
    {
        statusLabel.Text = "Checking GitHub for the latest version...";
        (latestVersion, latestUrl) = await ModFiles.LatestRelease();
        var installed = game != null ? ModFiles.InstalledVersion(game) : null;
        return installed == null ? "" : installed == latestVersion ? "You have the latest version." : "Version " + latestVersion + " is available. Your friend needs the same version.";
    }

    private async Task<string> InstallLatest()
    {
        statusLabel.Text = "Downloading " + latestVersion + "...";
        var zip = await ModFiles.Download(latestUrl!);
        var backup = await Task.Run(() => ModFiles.Install(game!, zip, latestVersion!));
        return "Installed " + latestVersion + ". Start FATE from Steam as usual." + (backup != null ? " Your saves were backed up to " + backup + "." : "");
    }

    private async Task<string> Remove()
    {
        string[] warnings;
        try { warnings = ModFiles.RemovalWarnings().ToArray(); }
        catch (Exception e) when (e is SerializationException || e is ArgumentException || e is IOException) { warnings = Array.Empty<string>(); }
        if (warnings.Length > 0 && MessageBox.Show(this, string.Join("\n\n", warnings) + "\n\nRemove anyway?", "FATE Co-Op", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return "Nothing was removed.";
        var loaderRemoved = await Task.Run(() => ModFiles.Remove(game!));
        return loaderRemoved ? "Removed. FATE is back to normal." : "Removed FATE Co-Op. BepInEx stays because other mods use it or it was there before.";
    }
}
