using System.Windows;
using System.Windows.Controls;
using PressRelease;

namespace PressReleaseDesktop;

sealed class ProofreadingDialog : Window
{
    public ProofreadingDialog(ProofreadingResult result, WorkspaceAppearance? appearance = null)
    {
        Title = "맞춤법 수정안 확인";
        var work = SystemParameters.WorkArea;
        Width = Math.Min(1040, work.Width);
        Height = Math.Min(740, work.Height);
        MinWidth = Math.Min(640, work.Width);
        MinHeight = Math.Min(450, work.Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var root = new Grid { Margin = new Thickness(16) };
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(150), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        Content = root;
        (appearance ?? new WorkspaceAppearance()).Attach(this, root);
        var heading = new TextBlock
        {
            Text = $"수정 제안 {result.Edits.Count}건. 내용과 고유명사를 확인한 뒤 적용하세요.",
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        heading.SetResourceReference(TextBlock.FontSizeProperty, "WorkspaceHeadingSize");
        root.Children.Add(heading);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        AddEditor(columns, "수정 전", result.Original, 0);
        AddEditor(columns, "수정 후", result.Corrected, 2);
        Grid.SetRow(columns, 1);
        root.Children.Add(columns);
        var edits = new TextBox
        {
            Text = string.Join("\n\n", result.Edits.Select((edit, index) => $"{index + 1}. {edit.Before} → {edit.After}\n{edit.Reason}")),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 12, 0, 12)
        };
        System.Windows.Automation.AutomationProperties.SetName(edits, "맞춤법 수정 근거");
        Grid.SetRow(edits, 2);
        root.Children.Add(edits);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "적용하지 않기", IsCancel = true, MinWidth = 110 };
        close.Click += (_, _) => DialogResult = false;
        var apply = new Button { Content = "수정안 적용", MinWidth = 110, Style = (Style)root.FindResource("PrimaryAction") };
        apply.Click += (_, _) => DialogResult = true;
        actions.Children.Add(close);
        actions.Children.Add(apply);
        Grid.SetRow(actions, 3);
        root.Children.Add(actions);
    }

    static void AddEditor(Grid parent, string label, string text, int column)
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var editor = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12)
        };
        editor.SetResourceReference(Control.FontSizeProperty, "EditorFontSize");
        System.Windows.Automation.AutomationProperties.SetName(editor, label);
        Grid.SetRow(editor, 1);
        panel.Children.Add(editor);
        Grid.SetColumn(panel, column);
        parent.Children.Add(panel);
    }
}
