using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Rhyolite.Core.Protocol;
using Rhyolite.Core.Storage;
using Rhyolite.ViewModels;
using Rhyolite.Views;

namespace Rhyolite.Desktop;

sealed class Program
{
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [STAThread]
    public static int Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Ensure Windows Explorer refreshes any cached .exe icon from prior versions
                SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                // Ignore if shell notification is unavailable
            }
        }

        if (args.Length >= 1 && string.Equals(args[0], "--mcp-stdio", StringComparison.OrdinalIgnoreCase))
        {
            string vaultDir = args.Length >= 2
                ? args[1]
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RhyoliteVault");
            return RunMcpStdioServer(vaultDir).GetAwaiter().GetResult();
        }

        if (args.Length >= 2 && args[0] == "--export-mockups")
        {
            return ExportAllScreenMockups(args[1]);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static async Task<int> RunMcpStdioServer(string vaultDir)
    {
        using var vault = new HybridVaultEngine(vaultDir);
        await vault.LoadOrSeedVaultAsync().ConfigureAwait(false);

        var authEngine = new RhyoliteAuthEngine();
        var ownerAccount = authEngine.RegisterAccount(
            email: "stdio-agent@rhyolite.local",
            displayName: "MCP Stdio Agent",
            password: "Rhyolite-Stdio-Session-Key-2026!",
            role: VaultRole.VaultOwner);
        var token = authEngine.IssueBearerToken(ownerAccount);

        var peerSync = new PeerSyncEngine("rhyolite-stdio-node");
        foreach (var note in vault.Notes)
        {
            peerSync.UpsertNote(note.RelativePath, note.RawMarkdown, note.Tags);
        }

        var mcp = new McpServerEngine(authEngine, peerSync, vault);

        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string responseJson = mcp.HandleJsonRpc(line, token.RawBearerToken);
            await Console.Out.WriteLineAsync(responseJson).ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
        }

        return 0;
    }

    private static int ExportAllScreenMockups(string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        BuildAvaloniaApp().SetupWithoutStarting();

        var vm = System.Threading.Tasks.Task.Run(async () =>
        {
            var m = new MainViewModel();
            await m.InitializationTask.ConfigureAwait(false);
            return m;
        }).GetAwaiter().GetResult();

        var view = new MainView
        {
            DataContext = vm,
            Width = 1440,
            Height = 900
        };

        var root = new ThemeVariantScope
        {
            RequestedThemeVariant = ThemeVariant.Light,
            Child = view,
            Width = 1440,
            Height = 900
        };

        var window = new Window
        {
            Width = 1440,
            Height = 900,
            Content = root,
            ShowInTaskbar = false
        };
        window.Show();

        var specs = new (string FileName, string Screen, string DesignId, bool DarkMode, bool OpenCommandPalette, string BasesMode)[]
        {
            ("01_note_studio_light.png", "Studio", "apple", false, false, ""),
            ("02_note_studio_dark.png", "Studio", "claude", true, false, ""),
            ("03_knowledge_graph_light.png", "Graph", "vercel", false, false, ""),
            ("04_spatial_canvas_dark.png", "Canvas", "raycast", true, false, ""),
            ("05_bases_database_light.png", "Bases", "notion", false, false, "Table"),
            ("06_study_timemachine_light.png", "Study", "stripe", false, false, ""),
            ("07_mcp_graphql_p2p_dark.png", "Protocol", "supabase", true, false, ""),
            ("08_mobile_ios_android_light.png", "Mobile", "linear-app", true, false, ""),
            ("09_enterprise_github_pr_devex_dark.png", "DevEx", "github", true, false, ""),
            ("10_obsidian_command_palette_dark.png", "Studio", "linear-app", true, true, ""),
            ("11_capacities_gallery_studio_dark.png", "Bases", "claude", true, false, "Gallery"),
            ("12_capacities_wall_calendar_light.png", "Bases", "apple", false, false, "Calendar")
        };

        var size = new Size(1440, 900);
        var pixelSize = new PixelSize(1440, 900);
        var dpi = new Vector(96, 96);

        foreach (var (fileName, screen, designId, darkMode, openCommandPalette, basesMode) in specs)
        {
            root.RequestedThemeVariant = darkMode ? ThemeVariant.Dark : ThemeVariant.Light;
            vm.SelectDesignSystem(designId, darkMode);
            vm.SwitchScreen(screen);
            if (basesMode == "Table")
            {
                vm.ShowBasesTable();
            }
            else if (basesMode == "Gallery")
            {
                vm.ShowBasesGallery();
            }
            else if (basesMode == "Calendar")
            {
                vm.ShowBasesCalendar();
            }
            else if (basesMode == "Kanban")
            {
                vm.ShowBasesKanban();
            }
            if (openCommandPalette)
            {
                vm.ToggleCommandPalette();
            }

            Dispatcher.UIThread.RunJobs();
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            using var bitmap = new RenderTargetBitmap(pixelSize, dpi);
            bitmap.Render(root);
            string fullPath = Path.Combine(outputDir, fileName);
            bitmap.Save(fullPath);
            if (openCommandPalette)
            {
                vm.CloseCommandPalette();
            }
            Console.WriteLine($"Rendered: {fullPath} ({designId}, dark={darkMode})");
        }

        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
