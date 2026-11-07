using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PressReleaseDesktop;

sealed class DisplaySettingsDialog : Window
{
    public DisplaySettingsDialog(WorkspaceAppearance appearance)
    {
        Title = "화면 설정";
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(480, workArea.Width);
        MaxHeight = Math.Min(540, workArea.Height);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true;

        var panel = new StackPanel { Margin = new Thickness(20) };
        var root = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Content = root;
        appearance.Attach(this, root);

        var modeLabel = new TextBlock { Text = "화면 모드", FontWeight = FontWeights.SemiBold };
        panel.Children.Add(modeLabel);
        var modes = new WrapPanel { Margin = new Thickness(0, 8, 0, 18) };
        var light = new RadioButton
        {
            Name = "LightMode", GroupName = "WorkspaceMode", Content = "라이트 모드",
            IsChecked = !appearance.IsDark, Margin = new Thickness(0, 0, 16, 6)
        };
        var dark = new RadioButton
        {
            Name = "DarkMode", GroupName = "WorkspaceMode", Content = "다크 모드",
            IsChecked = appearance.IsDark, Margin = new Thickness(0, 0, 0, 6)
        };
        foreach (var option in new[] { light, dark })
        {
            option.SetResourceReference(ForegroundProperty, "TextInk");
            option.SetResourceReference(FocusVisualStyleProperty, "WorkspaceFocus");
            modes.Children.Add(option);
        }
        panel.Children.Add(modes);

        var sizeLabel = new TextBlock { Text = "글자 크기", FontWeight = FontWeights.SemiBold };
        var valueText = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
        var sizeHeading = new DockPanel();
        DockPanel.SetDock(valueText, Dock.Right);
        sizeHeading.Children.Add(valueText);
        sizeHeading.Children.Add(sizeLabel);
        panel.Children.Add(sizeHeading);

        var slider = new Slider
        {
            Name = "FontSizeSlider",
            Minimum = 11, Maximum = 20, Value = appearance.FontPoints,
            SmallChange = 0.1, LargeChange = 1, TickFrequency = 0.1, IsSnapToTickEnabled = true,
            IsMoveToPointEnabled = true, MinHeight = 32, Margin = new Thickness(0, 6, 0, 4)
        };
        AutomationProperties.SetLabeledBy(slider, sizeLabel);
        AutomationProperties.SetHelpText(slider, "11포인트부터 20포인트까지 조절합니다.");
        panel.Children.Add(slider);
        var hint = new TextBlock { Text = "11–20 pt", Margin = new Thickness(0, 0, 0, 18) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        panel.Children.Add(hint);

        var close = new Button { Content = "닫기", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);

        valueText.Text = $"{slider.Value:0.#} pt";
        light.Checked += (_, _) => ApplyChanges(false);
        dark.Checked += (_, _) => ApplyChanges(true);
        slider.ValueChanged += (_, _) => ApplyChanges(dark.IsChecked == true);

        void ApplyChanges(bool useDark)
        {
            var points = Math.Round(slider.Value, 1);
            appearance.Apply(useDark, points);
            valueText.Text = $"{points:0.#} pt";
        }
    }
}
