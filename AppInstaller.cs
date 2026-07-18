using System.Diagnostics;

namespace ProcLens;

internal static class AppInstaller
{
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ProcLens");

    public static string InstalledExecutable => Path.Combine(InstallDirectory, "ProcLens.exe");

    public static void Install()
    {
        var source = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var destination = InstallDirectory.TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(destination);

        if (!source.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var file in Directory.EnumerateFiles(source))
            {
                var extension = Path.GetExtension(file);
                if (extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            }

            var assets = Path.Combine(source, "wwwroot");
            if (Directory.Exists(assets)) CopyDirectory(assets, Path.Combine(destination, "wwwroot"));
        }

        if (!File.Exists(InstalledExecutable))
            throw new FileNotFoundException("The published ProcLens executable was not found. Run install from a published release folder.", InstalledExecutable);

        TrayApplicationContext.SetStartup(true, InstalledExecutable);
        if (!source.Equals(destination, StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(InstalledExecutable, "tray") { UseShellExecute = true });
    }

    public static void DisableAndRemoveStartup() => TrayApplicationContext.SetStartup(false);

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
