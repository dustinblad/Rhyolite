using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Rhyolite.Core.Design;
using Rhyolite.ViewModels;
using Rhyolite.Views;

namespace Rhyolite;

public partial class App : Application
{
    private DesignSystemPreset _currentPreset = DesignSystemCatalog.GetOrDefault("apple");

    public DesignSystemPreset CurrentPreset => _currentPreset;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void SetAppleTheme(bool darkMode)
    {
        ApplyDesignSystem(_currentPreset ?? DesignSystemCatalog.GetOrDefault("apple"), darkMode);
    }

    public void ApplyDesignSystem(DesignSystemPreset preset, bool darkMode)
    {
        ArgumentNullException.ThrowIfNull(preset);
        _currentPreset = preset;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyDesignSystemOnUiThread(preset, darkMode));
            return;
        }

        ApplyDesignSystemOnUiThread(preset, darkMode);
    }

    private void ApplyDesignSystemOnUiThread(DesignSystemPreset preset, bool darkMode)
    {
        RequestedThemeVariant = darkMode ? ThemeVariant.Dark : ThemeVariant.Light;

        var p = darkMode ? preset.DarkPalette : preset.LightPalette;

        // 1. Semantic Palette Brushes
        Resources["AppleBgBrush"] = new SolidColorBrush(Color.Parse(p.BgHex));
        Resources["AppleSurfaceBrush"] = new SolidColorBrush(Color.Parse(p.SurfaceHex));
        Resources["AppleSurfaceWarmBrush"] = new SolidColorBrush(Color.Parse(p.SurfaceWarmHex));
        Resources["AppleSurfaceElevatedBrush"] = new SolidColorBrush(Color.Parse(p.SurfaceElevatedHex));
        Resources["AppleFgBrush"] = new SolidColorBrush(Color.Parse(p.FgHex));
        Resources["AppleFg2Brush"] = new SolidColorBrush(Color.Parse(p.Fg2Hex));
        Resources["AppleMutedBrush"] = new SolidColorBrush(Color.Parse(p.MutedHex));
        Resources["AppleMetaBrush"] = new SolidColorBrush(Color.Parse(p.MetaHex));
        Resources["AppleBorderBrush"] = new SolidColorBrush(Color.Parse(p.BorderHex));
        Resources["AppleBorderSoftBrush"] = new SolidColorBrush(Color.Parse(p.BorderSoftHex));
        Resources["AppleAccentBrush"] = new SolidColorBrush(Color.Parse(p.AccentHex));
        Resources["AppleAccentHoverBrush"] = new SolidColorBrush(Color.Parse(p.AccentHoverHex));
        Resources["AppleLinkBrush"] = new SolidColorBrush(Color.Parse(p.LinkHex));
        Resources["AppleCalloutBgBrush"] = new SolidColorBrush(Color.Parse(p.CalloutBgHex));
        Resources["AppleSuccessBrush"] = new SolidColorBrush(Color.Parse(p.SuccessHex));
        Resources["AppleWarnBrush"] = new SolidColorBrush(Color.Parse(p.WarnHex));
        Resources["AppleDangerBrush"] = new SolidColorBrush(Color.Parse(p.DangerHex));

        // 2. Typography Stacks & Weights
        Resources["AppleUiFont"] = new FontFamily(preset.UiFontFamily);
        Resources["AppleDisplayFont"] = new FontFamily(preset.DisplayFontFamily);
        Resources["AppleMonoFont"] = new FontFamily(preset.MonoFontFamily);
        Resources["AppleHeadingFontWeight"] = ParseFontWeight(preset.HeadingFontWeight);
        Resources["AppleBtnFontWeight"] = ParseFontWeight(preset.ButtonFontWeight);
        Resources["AppleBtnFontSize"] = preset.ButtonFontSize;

        // 3. Button Component Geometry, Contrast & Elevation
        Resources["ApplePrimaryBtnBgBrush"] = new SolidColorBrush(Color.Parse(preset.GetPrimaryButtonBgHex(darkMode)));
        Resources["ApplePrimaryBtnFgBrush"] = new SolidColorBrush(Color.Parse(preset.GetPrimaryButtonFgHex(darkMode)));
        Resources["ApplePrimaryBtnBorderBrush"] = new SolidColorBrush(Color.Parse(preset.GetPrimaryButtonBorderHex(darkMode)));
        Resources["ApplePrimaryBtnCornerRadius"] = new CornerRadius(preset.PrimaryButtonCornerRadius);
        Resources["AppleSecondaryBtnCornerRadius"] = new CornerRadius(preset.SecondaryButtonCornerRadius);
        Resources["AppleBtnCornerRadius"] = new CornerRadius(preset.SecondaryButtonCornerRadius);
        Resources["ApplePrimaryBtnBorderThickness"] = Thickness.Parse(preset.PrimaryButtonBorderThickness);
        Resources["AppleSecondaryBtnBorderThickness"] = Thickness.Parse(preset.SecondaryButtonBorderThickness);
        Resources["AppleBtnPadding"] = Thickness.Parse(preset.ButtonPadding);
        Resources["ApplePrimaryBtnShadow"] = BoxShadows.Parse(preset.GetPrimaryButtonShadow(darkMode));
        Resources["AppleSecondaryBtnShadow"] = BoxShadows.Parse(preset.GetSecondaryButtonShadow(darkMode));

        // 4. Card, Input & Pill Geometry
        Resources["AppleCardCornerRadius"] = new CornerRadius(preset.CardCornerRadius);
        Resources["AppleCardBorderThickness"] = Thickness.Parse(preset.CardBorderThickness);
        Resources["AppleCardPadding"] = Thickness.Parse(preset.CardPadding);
        Resources["AppleCardShadow"] = BoxShadows.Parse(preset.GetCardShadow(darkMode));
        Resources["AppleInputCornerRadius"] = new CornerRadius(preset.InputCornerRadius);
        Resources["AppleInputBorderThickness"] = Thickness.Parse(preset.InputBorderThickness);
        Resources["ApplePillCornerRadius"] = new CornerRadius(preset.PillCornerRadius);
        Resources["ApplePillBorderThickness"] = Thickness.Parse(preset.PillBorderThickness);
    }

    private static FontWeight ParseFontWeight(string? weight)
        => weight?.Trim().ToLowerInvariant() switch
        {
            "thin" => FontWeight.Thin,
            "light" => FontWeight.Light,
            "regular" or "normal" => FontWeight.Normal,
            "medium" => FontWeight.Medium,
            "semibold" => FontWeight.SemiBold,
            "bold" => FontWeight.Bold,
            "extrabold" or "heavy" => FontWeight.ExtraBold,
            "black" => FontWeight.Black,
            _ => FontWeight.SemiBold
        };

    public override void OnFrameworkInitializationCompleted()
    {
        var vm = new MainViewModel();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = vm
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
