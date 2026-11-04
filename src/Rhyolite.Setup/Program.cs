using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Rhyolite.Setup;

internal static class Program
{
    private const uint MB_OK = 0x00000000;
    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONINFORMATION = 0x00000040;
    private const uint MB_ICONQUESTION = 0x00000020;
    private const uint MB_ICONERROR = 0x00000010;
    private const int IDYES = 6;
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    public static int Main(string[] args)
    {
        bool silent = args.Any(a =>
            string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/quiet", StringComparison.OrdinalIgnoreCase));

        bool uninstall = args.Any(a =>
            string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/UNINSTALL", StringComparison.OrdinalIgnoreCase)) ||
            Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty)
                .StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase);

        bool noLaunch = args.Any(a =>
            string.Equals(a, "--no-launch", StringComparison.OrdinalIgnoreCase));

        string version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "0.1.1";

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        }

        string installDir = Path.Combine(localAppData, "Programs", "Rhyolite");
        string targetExe = Path.Combine(installDir, "Rhyolite.exe");
        string targetIco = Path.Combine(installDir, "Rhyolite.ico");
        string uninstallerExe = Path.Combine(installDir, "Uninstall-Rhyolite.exe");
        string startMenuLnk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "Rhyolite.lnk");
        string desktopLnk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Rhyolite.lnk");

        try
        {
            if (uninstall)
            {
                return RunUninstall(installDir, startMenuLnk, desktopLnk, silent, version);
            }

            return RunInstall(installDir, targetExe, targetIco, uninstallerExe, startMenuLnk, desktopLnk, silent, noLaunch, version);
        }
        catch (Exception ex)
        {
            if (!silent && OperatingSystem.IsWindows())
            {
                MessageBoxW(
                    IntPtr.Zero,
                    $"Rhyolite Setup encountered an error:\n\n{ex.Message}",
                    $"Rhyolite v{version} User Setup",
                    MB_OK | MB_ICONERROR);
            }
            else
            {
                Console.Error.WriteLine($"Rhyolite Setup error: {ex}");
            }
            return 1;
        }
    }

    private static int RunInstall(
        string installDir,
        string targetExe,
        string targetIco,
        string uninstallerExe,
        string startMenuLnk,
        string desktopLnk,
        bool silent,
        bool noLaunch,
        string version)
    {
        if (!silent && OperatingSystem.IsWindows())
        {
            int choice = MessageBoxW(
                IntPtr.Zero,
                $"Install Rhyolite v{version} for the current user ({Environment.UserName})?\n\n" +
                $"• Install Location: {installDir}\n" +
                "• Architecture: Single-File Self-Contained Executable (All .dlls Combined)\n" +
                "• Shortcuts: Start Menu & Desktop (with embedded Rhyolite Icon)\n" +
                "• Scope: Current User (No Administrator / UAC Required)",
                $"Rhyolite v{version} — User Setup",
                MB_YESNO | MB_ICONQUESTION);

            if (choice != IDYES)
            {
                return 0;
            }
        }

        // Stop any running Rhyolite instance in the target directory before replacing Rhyolite.exe
        foreach (var proc in Process.GetProcessesByName("Rhyolite"))
        {
            try
            {
                proc.Kill();
                proc.WaitForExit(3000);
            }
            catch
            {
                // Ignore if already exited or inaccessible
            }
        }

        Directory.CreateDirectory(installDir);

        var asm = typeof(Program).Assembly;

        // 1. Extract embedded Rhyolite.ico
        using (var icoStream = asm.GetManifestResourceStream("Rhyolite.ico"))
        {
            if (icoStream is not null)
            {
                using var outIco = File.Create(targetIco);
                icoStream.CopyTo(outIco);
            }
        }

        // 2. Extract embedded compressed single-file Rhyolite.exe.gz
        using (var gzStream = asm.GetManifestResourceStream("Rhyolite.exe.gz"))
        {
            if (gzStream is null)
            {
                throw new InvalidOperationException("Embedded single-file Rhyolite.exe.gz payload was not found in the installer package.");
            }

            using var gzip = new GZipStream(gzStream, CompressionMode.Decompress);
            using var outExe = File.Create(targetExe);
            gzip.CopyTo(outExe);
        }

        // 3. Copy this installer executable as Uninstall-Rhyolite.exe
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
        {
            string currentFull = Path.GetFullPath(Environment.ProcessPath);
            string uninstallerFull = Path.GetFullPath(uninstallerExe);
            if (!string.Equals(currentFull, uninstallerFull, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(currentFull, uninstallerFull, overwrite: true);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            // 4. Create Start Menu & Desktop Shortcuts + Register HKCU Uninstall & File Associations
            RegisterWindowsUserInstall(installDir, targetExe, targetIco, uninstallerExe, startMenuLnk, desktopLnk, version);

            // 5. Flush Windows Explorer Icon Cache so the new Rhyolite.exe icon appears immediately
            try
            {
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                // Ignore if shell notification is unavailable
            }

            if (!silent)
            {
                int launchChoice = MessageBoxW(
                    IntPtr.Zero,
                    $"Rhyolite v{version} has been installed to:\n{targetExe}\n\n" +
                    "Start Menu and Desktop shortcuts have been created.\n\n" +
                    "Would you like to launch Rhyolite now?",
                    $"Rhyolite v{version} — Installation Complete",
                    MB_YESNO | MB_ICONINFORMATION);

                if (launchChoice == IDYES && !noLaunch)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = targetExe,
                        WorkingDirectory = installDir,
                        UseShellExecute = true
                    });
                }
            }
            else if (!noLaunch)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetExe,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });
            }
        }

        return 0;
    }

    private static void RegisterWindowsUserInstall(
        string installDir,
        string targetExe,
        string targetIco,
        string uninstallerExe,
        string startMenuLnk,
        string desktopLnk,
        string version)
    {
        long sizeKb = new FileInfo(targetExe).Length / 1024;
        string escapedInstallDir = installDir.Replace("'", "''");
        string escapedTargetExe = targetExe.Replace("'", "''");
        string escapedTargetIco = targetIco.Replace("'", "''");
        string escapedUninstaller = uninstallerExe.Replace("'", "''");
        string escapedStartMenu = startMenuLnk.Replace("'", "''");
        string escapedDesktop = desktopLnk.Replace("'", "''");

        string psScript = $@"
$ErrorActionPreference = 'SilentlyContinue'
$wsh = New-Object -ComObject WScript.Shell
foreach ($lnkPath in @('{escapedStartMenu}', '{escapedDesktop}')) {{
    $sc = $wsh.CreateShortcut($lnkPath)
    $sc.TargetPath = '{escapedTargetExe}'
    $sc.WorkingDirectory = '{escapedInstallDir}'
    $sc.IconLocation = '{escapedTargetExe},0'
    $sc.Description = 'Rhyolite — Local-First Markdown Knowledge Graph, Spatial Canvas & Enterprise DevEx Workbench'
    $sc.Save()
}}

$uninstKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Rhyolite'
New-Item -Path $uninstKey -Force | Out-Null
New-ItemProperty -Path $uninstKey -Name 'DisplayName' -Value 'Rhyolite' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstKey -Name 'DisplayVersion' -Value '{version}' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstKey -Name 'Publisher' -Value 'Dustin Blad' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstKey -Name 'DisplayIcon' -Value '{escapedTargetExe},0' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstKey -Name 'InstallLocation' -Value '{escapedInstallDir}' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstKey -Name 'UninstallString' -Value '""{escapedUninstaller}"" --uninstall' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstKey -Name 'QuietUninstallString' -Value '""{escapedUninstaller}"" --uninstall --silent' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstKey -Name 'NoModify' -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstKey -Name 'NoRepair' -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstKey -Name 'EstimatedSize' -Value {sizeKb} -PropertyType DWord -Force | Out-Null

# Per-user file association for .canvas and .md
$progId = 'HKCU:\Software\Classes\Rhyolite.Document'
New-Item -Path ""$progId\DefaultIcon"" -Force | Out-Null
Set-Item -Path ""$progId\DefaultIcon"" -Value '""{escapedTargetExe}"",0'
New-Item -Path ""$progId\shell\open\command"" -Force | Out-Null
Set-Item -Path ""$progId\shell\open\command"" -Value '""{escapedTargetExe}"" ""%1""'
New-Item -Path 'HKCU:\Software\Classes\.canvas\OpenWithProgids' -Force | Out-Null
New-ItemProperty -Path 'HKCU:\Software\Classes\.canvas\OpenWithProgids' -Name 'Rhyolite.Document' -Value '' -PropertyType String -Force | Out-Null
New-Item -Path 'HKCU:\Software\Classes\.md\OpenWithProgids' -Force | Out-Null
New-ItemProperty -Path 'HKCU:\Software\Classes\.md\OpenWithProgids' -Name 'Rhyolite.Document' -Value '' -PropertyType String -Force | Out-Null

# Add %LocalAppData%\Programs\Rhyolite to User PATH if not already present
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath -notlike '*{escapedInstallDir}*') {{
    $newPath = if ([string]::IsNullOrWhiteSpace($userPath)) {{ '{escapedInstallDir}' }} else {{ ""$userPath;{escapedInstallDir}"" }}
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
}}
";

        RunPowerShell(psScript);
    }

    private static int RunUninstall(
        string installDir,
        string startMenuLnk,
        string desktopLnk,
        bool silent,
        string version)
    {
        if (!silent && OperatingSystem.IsWindows())
        {
            int choice = MessageBoxW(
                IntPtr.Zero,
                $"Uninstall Rhyolite from {installDir}?\n\nYour local Markdown vaults and notes will NOT be deleted.",
                $"Rhyolite v{version} — Uninstall",
                MB_YESNO | MB_ICONQUESTION);

            if (choice != IDYES)
            {
                return 0;
            }
        }

        foreach (var proc in Process.GetProcessesByName("Rhyolite"))
        {
            try
            {
                proc.Kill();
                proc.WaitForExit(3000);
            }
            catch
            {
                // Ignore
            }
        }

        if (File.Exists(startMenuLnk)) File.Delete(startMenuLnk);
        if (File.Exists(desktopLnk)) File.Delete(desktopLnk);

        string targetExe = Path.Combine(installDir, "Rhyolite.exe");
        string targetIco = Path.Combine(installDir, "Rhyolite.ico");
        if (File.Exists(targetExe)) File.Delete(targetExe);
        if (File.Exists(targetIco)) File.Delete(targetIco);

        if (OperatingSystem.IsWindows())
        {
            string psCleanup = @"
$ErrorActionPreference = 'SilentlyContinue'
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Rhyolite' -Recurse -Force
Remove-Item -Path 'HKCU:\Software\Classes\Rhyolite.Document' -Recurse -Force
";
            RunPowerShell(psCleanup);

            try
            {
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                // Ignore
            }

            if (!silent)
            {
                MessageBoxW(
                    IntPtr.Zero,
                    "Rhyolite has been uninstalled from your user profile.",
                    "Rhyolite — Uninstalled",
                    MB_OK | MB_ICONINFORMATION);
            }
        }

        return 0;
    }

    private static void RunPowerShell(string script)
    {
        try
        {
            string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(15000);
        }
        catch
        {
            // Ignore on non-Windows environments
        }
    }
}
