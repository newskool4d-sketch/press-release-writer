using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace PressReleaseDesktop;

sealed class EditorColumns : Grid
{
    const double EditorWeight = 2.6;
    public const double DefaultSourceShare = 1.1 / EditorWeight;

    readonly ColumnDefinition sourceColumn;
    readonly ColumnDefinition draftColumn;

    public EditorColumns(FrameworkElement source, FrameworkElement draft, FrameworkElement review,
        double sourceShare, Action<double> onShareChanged)
    {
        if (!double.IsFinite(sourceShare) || sourceShare <= 0 || sourceShare >= 1)
            sourceShare = DefaultSourceShare;

        sourceColumn = new ColumnDefinition { Width = Star(EditorWeight * sourceShare), MinWidth = 156 };
        draftColumn = new ColumnDefinition { Width = Star(EditorWeight * (1 - sourceShare)), MinWidth = 190 };
        ColumnDefinitions.Add(sourceColumn);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(draftColumn);
        ColumnDefinitions.Add(new ColumnDefinition { Width = Star(.8), MinWidth = 144 });

        Add(source, 0);
        Add(draft, 2);
        Add(review, 3);

        var divider = new GridSplitter
        {
            Name = "EditorDivider",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 10, 0, 10),
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Cursor = Cursors.SizeWE,
            KeyboardIncrement = 16,
            DragIncrement = 1,
            Focusable = true,
            IsTabStop = true,
            ToolTip = "끌거나 포커스를 둔 뒤 좌우 방향키로 편집칸 너비를 조절하세요."
        };
        divider.SetResourceReference(StyleProperty, "EditorDividerStyle");
        AutomationProperties.SetName(divider, "원자료와 초안 편집칸 너비 조절");
        AutomationProperties.SetHelpText(divider, "포커스를 둔 뒤 좌우 방향키로 조절할 수 있습니다.");
        Add(divider, 1);

        var savedShare = SourceShare;
        divider.DragCompleted += (_, e) =>
        {
            if (!e.Canceled) PublishShare();
        };
        divider.KeyUp += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right) PublishShare();
        };
        divider.LostKeyboardFocus += (_, _) =>
        {
            if (!divider.IsDragging) PublishShare();
        };

        void PublishShare()
        {
            var share = SourceShare;
            if (Math.Abs(share - savedShare) < 0.00001) return;
            savedShare = share;
            onShareChanged(share);
        }
    }

    public double SourceShare => sourceColumn.Width.Value / (sourceColumn.Width.Value + draftColumn.Width.Value);

    static GridLength Star(double value) => new(value, GridUnitType.Star);

    void Add(UIElement element, int column)
    {
        SetColumn(element, column);
        Children.Add(element);
    }
}
