using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PressReleaseDesktop;

sealed class WorkspaceAppearance
{
    public const double MinimumFontPoints = 11;
    public const double MaximumFontPoints = 20;
    public const double DefaultFontPoints = MinimumFontPoints;

    ResourceDictionary? darkPalette;

    public ResourceDictionary Resources { get; } = new();
    public bool IsDark { get; private set; }
    public double FontPoints { get; private set; } = double.NaN;

    public WorkspaceAppearance(bool dark = false, double fontPoints = DefaultFontPoints)
    {
        Resources.MergedDictionaries.Add(Load("WorkspaceTheme.xaml"));
        Apply(dark, fontPoints);
    }

    public void Apply(bool dark, double fontPoints)
    {
        if (dark != IsDark)
        {
            if (dark) Resources.MergedDictionaries.Add(darkPalette ??= Load("WorkspaceDark.xaml"));
            else Resources.MergedDictionaries.Remove(darkPalette!);
            IsDark = dark;
        }

        var points = double.IsFinite(fontPoints)
            ? Math.Clamp(fontPoints, MinimumFontPoints, MaximumFontPoints)
            : DefaultFontPoints;
        if (points == FontPoints) return;
        FontPoints = points;

        var dips = points * 96 / 72;
        Resources["WorkspaceFontSize"] = dips;
        Resources["EditorFontSize"] = dips;
        Resources["WorkspaceSmallSize"] = dips - 1.5;
        Resources["WorkspaceHeadingSize"] = dips + 2;
        Resources["WorkspaceTitleSize"] = dips + 7;
        Resources["WorkspaceStatusLineHeight"] = dips + 2;
        var compact = points >= 17;
        Resources["CompactTools"] = compact ? Visibility.Visible : Visibility.Collapsed;
        Resources["FullTools"] = compact ? Visibility.Collapsed : Visibility.Visible;
        Resources["SourceOptionsLabel"] = compact ? "조건" : "작성 조건";
        Resources["WorkspaceTabPadding"] = compact ? new Thickness(5, 5, 5, 5) : new Thickness(10, 6, 10, 6);
    }

    public void Attach(Window window, FrameworkElement root)
    {
        window.Resources.MergedDictionaries.Add(Resources);
        root.Resources.MergedDictionaries.Add(Resources);

        window.SetResourceReference(Control.FontFamilyProperty, "WorkspaceFont");
        window.SetResourceReference(Control.FontSizeProperty, "WorkspaceFontSize");
        window.SetResourceReference(Control.ForegroundProperty, "TextInk");
        window.SetResourceReference(Control.BackgroundProperty, "Canvas");
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
    }

    static ResourceDictionary Load(string name) => new()
    {
        Source = new Uri($"/PressReleaseWriter;component/{name}", UriKind.Relative)
    };
}
