using System;
using System.Drawing;
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
        buttons.Controls.AddRange(new Control[] { installButton, updateButton, removeButton });
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.AddRange(new Control[] { gameLabel, browseButton, versionLabel, buttons, statusLabel });
        Controls.Add(layout);
        browseButton.Click += (_, _) => Browse();
        installButton.Click += async (_, _) => await Run(InstallLatest);
        updateButton.Click += async (_, _) => await Run(InstallLatest);
        removeButton.Click += async (_, _) => await Run(Remove);
        game = ModFiles.FindGame();
        Shown += async (_, _) => await Run(CheckLatest);
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
        catch (Exception e) { statusLabel.Text = e.Message; }
        busy = false;
        UpdateView();
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
        var loaderRemoved = await Task.Run(() => ModFiles.Remove(game!));
        return loaderRemoved ? "Removed. FATE is back to normal." : "Removed FATE Co-Op. BepInEx was left in place because other mods use it.";
    }
}
