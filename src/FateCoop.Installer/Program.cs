using System;
using System.Windows.Forms;

namespace FateCoop.Installer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.Run(new InstallerForm());
    }
}
