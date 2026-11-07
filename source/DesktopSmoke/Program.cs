using System.Reflection;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PressReleaseDesktop;
using PressRelease;

static class Program
{
    static int passed;

    [STAThread]
    static int Main(string[] args)
    {
        var directory = Directory.CreateTempSubdirectory("press-release-desktop-smoke-").FullName;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var clean = Window(directory, _ => throw new Exception("Clean window should not confirm"));
            CloseAndWait(clean);
            Require(true, "clean close leaves Closing event before retry");

            var confirmations = new List<string>();
            var allow = false;
            var sourceOnly = Window(directory, message => { confirmations.Add(message); return allow; });
            Field<TextBox>(sourceOnly, "source").Text = "검증용 비식별 행사 자료";
            var closed = false;
            sourceOnly.Closed += (_, _) => closed = true;
            sourceOnly.Close();
            Drain();
            Require(!closed && confirmations.Count == 1 && confirmations[0].Contains("원자료"), "source-only close warns and can be refused");
            Require(Field<TextBox>(sourceOnly, "source").Text == "검증용 비식별 행사 자료", "refused close preserves source");
            allow = true;
            CloseAndWait(sourceOnly);
            Require(confirmations.Count == 2, "confirmed source-only close completes");

            var metadata = Window(directory, message => { Require(message.Contains("확인 정보"), "metadata-only close warns"); return true; });
            Field<TextBox>(metadata, "officialName").Text = "검증기관";
            CloseAndWait(metadata);

            var editing = Window(directory, message => { Require(message.Contains("초안"), "draft-only close warns"); return true; });
            Field<TextBox>(editing, "draft").Text = "## 보도자료 초안\n\n제목\n리드문입니다.\n\n본문입니다.\n\n## 검토 의견\n확인";
            Require(Field<TextBlock>(editing, "checks").Text.Contains("14자"), "WPF text edit updates corrected count");
            CloseAndWait(editing);

            var busy = Window(directory, _ => true);
            var started = false;
            var cancelled = false;
            Func<CancellationToken, Task> work = async token =>
            {
                started = true;
                try { await Task.Delay(TimeSpan.FromMinutes(1), token); }
                finally { cancelled = token.IsCancellationRequested; }
            };
            var method = typeof(MainWindow).GetMethod("Busy", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var operation = (Task)method.Invoke(busy, [work, false])!;
            Require(started && !Field<TextBox>(busy, "source").IsEnabled, "busy operation disables editors");
            var settingsButton = Field<Button>(busy, "settingsButton");
            var settingsItems = settingsButton.ContextMenu.Items.OfType<MenuItem>().ToArray();
            Require(settingsButton.IsEnabled && settingsItems.Select(item => item.Header).SequenceEqual(new[] { "AI 연결", "화면 설정" }) &&
                !settingsItems[0].IsEnabled && settingsItems[1].IsEnabled,
                "settings menu keeps display options available while AI connection is locked during work");
            Require(Field<Button>(busy, "cancel").IsEnabled && Field<Button>(busy, "cancel").Visibility == Visibility.Visible,
                "running work keeps the cancel button available");
            CloseAndWait(busy);
            Require(cancelled && operation.IsCompletedSuccessfully, "window close waits for cancelled operation cleanup");

            var fileFailure = Window(directory, _ => true);
            Field<TextBlock>(fileFailure, "connectionState").Text = "verified connection";
            Func<CancellationToken, Task> fail = _ => throw new InvalidDataException("synthetic bad input");
            var failedOperation = (Task)method.Invoke(fileFailure, [fail, false])!;
            PumpUntil(() => failedOperation.IsCompleted);
            Require(Field<TextBlock>(fileFailure, "connectionState").Text == "verified connection", "file failure preserves connection state");
            CloseAndWait(fileFailure);

            var fileImport = Window(directory, _ => true);
            var importer = typeof(MainWindow).GetMethod("ImportSourceFiles", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var first = Path.Combine(directory, "원문.txt");
            var second = Path.Combine(directory, "추가 원문.md");
            File.WriteAllText(first, "첫 행사 자료");
            File.WriteAllText(second, "둘째 행사 자료");
            var editor = Field<TextBox>(fileImport, "source");
            editor.Text = "기존 자료";
            var importFiles = (Task)importer.Invoke(fileImport, [new[] { first, second }])!;
            PumpUntil(() => importFiles.IsCompleted);
            var imported = editor.Text;
            Require(imported.Replace("\r\n", "\n") == "기존 자료\n\n첫 행사 자료\n\n둘째 행사 자료",
                "file import appends multiple documents in order");
            var tooLong = Path.Combine(directory, "긴 원문.txt");
            File.WriteAllText(tooLong, new string('x', 50_001));
            var rejected = (Task)importer.Invoke(fileImport, [new[] { first, tooLong }])!;
            PumpUntil(() => rejected.IsCompleted);
            Require(editor.Text == imported && Field<TextBlock>(fileImport, "status").Text.Contains("50,000"),
                "oversized batch leaves original text intact and reports the limit");
            CloseAndWait(fileImport);

            var preview = args is ["--render", var path] ? Path.GetFullPath(path) : null;
            CheckWorkspace(directory, preview);
            CheckAdaptiveDisplay(directory, preview);
            CheckLegacyConnectionSettings(directory);
            CheckConnectionPanel(directory, preview);
            CheckSaveButton(directory);
            CheckProofreading(directory, preview);
            Console.WriteLine($"RESULT: Desktop PASS {passed}, FAIL 0 (WPF events and offscreen layout)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            application.Shutdown();
            Directory.Delete(directory, recursive: true);
        }
    }

    static MainWindow Window(string path, Func<string, bool> confirmation) => new(path, confirmation)
        { ShowInTaskbar = false, Opacity = 0 };

    static void CheckSaveButton(string directory)
    {
        var profile = Path.Combine(directory, "저장 프로필");
        var output = Path.Combine(directory, "저장 결과");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(profile, "settings.json"), JsonSerializer.Serialize(new { Folder = output, Model = 0 }));
        var window = Window(profile, _ => true);
        var draft = Field<TextBox>(window, "draft");
        draft.Text = "## 보도자료 초안\n\n**행사 제목**\n\n중학생 75명이 참여했다.\n\n## 검토 의견\n\n날짜 확인";
        var root = (Grid)window.Content;
        var footer = (StackPanel)root.Children[3];
        var actions = (WrapPanel)footer.Children[0];
        var save = actions.Children.OfType<Button>().Single(button => Equals(button.Content, "초안 저장"));
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var status = Field<TextBlock>(window, "status").Text;
        var outputs = Directory.GetFiles(output);
        Require(outputs.Length == 2 && status.StartsWith("저장 완료") && status.Contains(output), "save button writes both files and reports the configured folder");
        var md = outputs.Single(path => Path.GetExtension(path) == ".md");
        var txt = Path.ChangeExtension(md, ".txt");
        var utf8 = new UTF8Encoding(false, true);
        var markdown = File.ReadAllText(md, utf8);
        var plain = File.ReadAllText(txt, utf8);
        Require(plain == Writing.PlainText(markdown) && plain.Contains("중학생 75명이 참여했다."), "saved TXT and MD contain identical text after removing formatting");
        Field<CheckBox>(window, "saveTxt").IsChecked = false;
        Field<CheckBox>(window, "saveMd").IsChecked = false;
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var failure = Field<TextBlock>(window, "status").Text;
        Require(failure.Contains("초안 저장 실패") && failure.Contains("하나 이상 선택") &&
            failure.Contains(output) && Directory.GetFiles(output).Length == 2 && draft.Text.Contains("75명"),
            "save with no format explains the failure, shows the folder, and preserves the draft");
        CloseAndWait(window);
    }

    static void CheckWorkspace(string directory, string? preview)
    {
        var window = Window(directory, _ => true);
        using (var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/PressReleaseWriter;component/Assets/press-release.ico")).Stream)
        {
            var icon = new IconBitmapDecoder(iconStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Require(window.Icon != null && icon.Frames.Any(frame => frame.PixelWidth == 16) &&
                icon.Frames.Any(frame => frame.PixelWidth == 256), "window icon loads with small and high-DPI resolutions");
        }
        var root = (Grid)window.Content;
        window.Content = null;
        var surface = new Border { Background = window.Background, Child = root };
        TextElement.SetFontFamily(surface, window.FontFamily);
        TextElement.SetFontSize(surface, window.FontSize);
        TextElement.SetForeground(surface, window.Foreground);
        var connection = Field<TextBlock>(window, "connectionState");
        var source = Field<TextBox>(window, "source");
        var draft = Field<TextBox>(window, "draft");
        var sourceCard = (Border)((Grid)root.Children[2]).Children[0];
        var tabs = ((Grid)sourceCard.Child).Children.OfType<TabControl>().Single(tab => tab.Name == "SourceTabs");
        Require(((Grid)((TabItem)tabs.Items[0]).Content).AllowDrop, "original tab accepts Explorer file drops");

        void Arrange(int width, int height, string? filename = null)
        {
            surface.Measure(new Size(width, height));
            surface.Arrange(new Rect(0, 0, width, height));
            surface.UpdateLayout();
            if (preview == null || filename == null) return;
            Directory.CreateDirectory(preview);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(preview, filename));
            png.Save(file);
        }

        Arrange(1264, 791, "workspace.png");
        Require(source.ActualHeight >= 180 && draft.ActualHeight >= 180, "both editors have room at default size");
        Require(source.FontWeight == FontWeights.Normal && Field<TextBlock>(window, "checks").FontWeight == FontWeights.Normal,
            "tab headings do not make source and review text bold");

        connection.Text = "설치: 실행 확인\n인증: ChatGPT 로그인됨\n사용 가능 여부: 미확인 · 실제 생성 시 권한·한도 확인";
        Arrange(824, 511, "compact.png");
        Require(source.ActualHeight >= 100 && draft.ActualHeight >= 100,
            $"connected compact editors remain usable ({source.ActualHeight:F0}, {draft.ActualHeight:F0} DIP)");
        source.Text = "검증용 원문";
        tabs.SelectedIndex = 1;
        Arrange(824, 511, "compact-context.png");
        Require(Field<ComboBox>(window, "institutions").ActualHeight > 0 && draft.ActualHeight >= 100,
            "writing options are scrollable without shrinking the draft");
        tabs.SelectedIndex = 0;
        Arrange(824, 511);
        Require(source.Text == "검증용 원문" && source.ActualHeight >= 100, "switching tabs retains original text and restores editing");

        if (preview != null)
        {
            source.Text = "[화면 검증용 가상 자료]\r\n\r\n11월 6일 중학생 75명을 대상으로 생활 안전 실습을 진행했다.\r\n\r\n학생들은 대피 경로를 확인하고, 모둠별로 안전 수칙을 점검했다.\r\n\r\n다음 운영 일정은 참가 학교에 별도로 안내할 예정이다.";
            draft.Text = "## 보도자료 초안\r\n\r\n인천광역시교육청, 중학생 생활 안전 실습 진행\r\n\r\n인천광역시교육청은 11월 6일 중학생 75명을 대상으로 생활 안전 실습을 진행했다.\r\n\r\n학생들은 대피 경로를 살펴보고 모둠별로 안전 수칙을 점검했다.\r\n\r\n다음 운영 일정은 참가 학교에 별도로 안내할 예정이다.\r\n\r\n## 검토 의견\r\n\r\n- 장소: 확인 필요\r\n- 실제 보도자료가 아닌 화면 검증용 가상 원고입니다.";
            Arrange(1264, 791, "workspace-filled.png");
            Arrange(824, 511, "compact-filled.png");
        }

        var model = Field<ComboBox>(window, "models");
        Require(model.Items.Count == 4 && model.Items.Cast<object>().Select(item => item.ToString()).SequenceEqual(
            new[] { ModelConnection.CodexCli, ModelConnection.ClaudeCli, ModelConnection.GeminiApi, ModelConnection.GeminiCli }.Select(Models.Label)),
            "workspace offers Codex, Claude Code, Gemini API, and institutional Gemini CLI");
        Require(Field<Button>(window, "cancel").Visibility == Visibility.Collapsed &&
            !Field<bool>(window, "externalAiConsent"), "idle workspace starts with transmission disabled");
        source.Text = "검증용 원문";
        draft.Text = "검증용 초안";
        for (int index = 0; index < model.Items.Count; index++)
        {
            model.SelectedIndex = index;
            foreach (var action in new[] { "Generate", "ProofreadDraft" })
            {
                typeof(MainWindow).GetMethod(action, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                Require(Field<TextBlock>(window, "status").Text.Contains("전송 동의") && draft.Text == "검증용 초안",
                    $"{action} requires consent before sending through {model.SelectedItem}");
            }
        }
        CloseAndWait(window);
    }

    static void CheckAdaptiveDisplay(string directory, string? preview)
    {
        var profile = Path.Combine(directory, "화면 설정");
        var window = Window(profile, _ => true);
        var appearance = Field<WorkspaceAppearance>(window, "appearance");
        var columns = Field<EditorColumns>(window, "columns");
        var source = Field<TextBox>(window, "source");
        var draft = Field<TextBox>(window, "draft");
        var draftCard = (Border)columns.Children[1];
        var splitter = columns.Children.OfType<GridSplitter>().Single(item => item.Name == "EditorDivider");
        var root = (Grid)window.Content;
        var surface = DetachSurface(window, appearance);
        source.Text = "[화면 검증용 가상 자료]\n중학생 75명이 안전 실습에 참여했다.";
        draft.Text = "## 보도자료 초안\n\n인천광역시교육청, 중학생 생활 안전 실습 진행\n\n학생들은 대피 경로를 확인했다.";

        static Border DetachSurface(MainWindow owner, WorkspaceAppearance theme)
        {
            var content = (FrameworkElement)owner.Content;
            owner.Content = null;
            var host = new Border { Child = content };
            host.Resources.MergedDictionaries.Add(theme.Resources);
            host.SetResourceReference(Border.BackgroundProperty, "Canvas");
            host.SetResourceReference(TextElement.FontFamilyProperty, "WorkspaceFont");
            host.SetResourceReference(TextElement.FontSizeProperty, "WorkspaceFontSize");
            host.SetResourceReference(TextElement.ForegroundProperty, "TextInk");
            return host;
        }

        void Arrange(int width, int height, string? filename = null, Border? target = null)
        {
            target ??= surface;
            target.Measure(new Size(width, height));
            target.Arrange(new Rect(0, 0, width, height));
            target.UpdateLayout();
            if (preview == null || filename == null) return;
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(target);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(preview, filename));
            png.Save(file);
        }

        void Drag(double delta, int width = 1264, int height = 791)
        {
            splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            splitter.RaiseEvent(new DragDeltaEventArgs(delta, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            splitter.RaiseEvent(new DragCompletedEventArgs(delta, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Arrange(width, height);
        }

        Arrange(1264, 791);
        Require(!appearance.IsDark && Near(appearance.FontPoints, 11) && Near(source.FontSize, 11 * 96.0 / 72) &&
            Near(draft.FontSize, source.FontSize), "light appearance starts with both editors at 11 points");
        var lightForeground = ((SolidColorBrush)source.Foreground).Color;
        var lightBackground = ((SolidColorBrush)source.Background).Color;
        Require(Contrast(lightForeground, lightBackground) >= 4.5 && IsTranslucent(draftCard.Background),
            "light editing text has at least 4.5:1 contrast and the draft uses a translucent surface");

        var settings = new DisplaySettingsDialog(appearance);
        var modes = DescendantsAndSelf((DependencyObject)settings.Content).OfType<RadioButton>().ToArray();
        var light = modes.Single(item => item.Name == "LightMode");
        var dark = modes.Single(item => item.Name == "DarkMode");
        var font = DescendantsAndSelf((DependencyObject)settings.Content).OfType<Slider>().Single(item => item.Name == "FontSizeSlider");
        Require(Near(font.Minimum, 11) && Near(font.Maximum, 20) && light.IsChecked == true && dark.IsChecked == false,
            "display settings exposes the light choice and the full 11 to 20 point range");
        dark.IsChecked = true;
        font.Value = 15.7;
        Arrange(1264, 791, "workspace-dark.png");
        var darkForeground = ((SolidColorBrush)source.Foreground).Color;
        var darkBackground = ((SolidColorBrush)source.Background).Color;
        Require(appearance.IsDark && darkForeground != lightForeground && darkBackground != lightBackground &&
            Contrast(darkForeground, darkBackground) >= 4.5 && IsTranslucent(draftCard.Background),
            "dark choice updates existing editor colors, retains contrast, and keeps the translucent draft surface");
        Require(((SolidColorBrush)light.Foreground).Color == darkForeground &&
            ((SolidColorBrush)dark.Foreground).Color == darkForeground, "mode options remain readable after switching to dark");
        Require(Near(appearance.FontPoints, 15.7) && Near(window.FontSize, 15.7 * 96 / 72) &&
            Near(source.FontSize, window.FontSize) && Near(draft.FontSize, window.FontSize),
            "fractional point size updates the window and both existing editors immediately");

        font.Value = 20;
        var update = Field<Button>(window, "updateButton");
        update.Visibility = Visibility.Visible;
        Arrange(824, 511, "compact-dark-20pt.png");
        Require(Near(source.FontSize, 20 * 96.0 / 72) && Near(draft.FontSize, source.FontSize) &&
            source.ActualWidth >= 110 && draft.ActualWidth >= 110 && source.ActualHeight >= 72 && draft.ActualHeight >= 72,
            $"compact window keeps both editors usable at 20 points ({source.ActualWidth:F0}x{source.ActualHeight:F0}, {draft.ActualWidth:F0}x{draft.ActualHeight:F0} DIP)");
        var compactSourceWidth = columns.ColumnDefinitions[0].ActualWidth;
        Drag(-1000, 824, 511);
        Require(source.ActualWidth >= 110 && source.ActualHeight >= 72 && draft.ActualHeight >= 72,
            $"compact 20-point layout at the narrowest source width ({source.ActualWidth:F0}x{source.ActualHeight:F0} DIP)");
        Drag(1000, 824, 511);
        Require(draft.ActualWidth >= 110 && source.ActualHeight >= 72 && draft.ActualHeight >= 72,
            $"compact 20-point layout at the narrowest draft width ({draft.ActualWidth:F0}x{draft.ActualHeight:F0} DIP)");
        Drag(compactSourceWidth - columns.ColumnDefinitions[0].ActualWidth, 824, 511);
        var header = DescendantsAndSelf(root.Children[0]).OfType<FrameworkElement>().ToArray();
        var logo = header.Single(item => item.Name == "IncheonEducationLogo");
        var title = header.OfType<TextBlock>().Single(item => item.Text == "보도자료 작성기");
        var slogan = header.Single(item => item.Name == "StudentSuccessSlogan");
        Rect Bounds(FrameworkElement item) => item.TransformToAncestor(root).TransformBounds(new Rect(item.RenderSize));
        Require(Bounds(logo).Right <= Bounds(title).Left && Bounds(title).Right <= Bounds(update).Left &&
            Bounds(update).Right <= Bounds(slogan).Left && Bounds(slogan).Width > 100 && Bounds(slogan).Right <= root.ActualWidth,
            "compact header keeps the logo, title, update button and one-line slogan visible without overlap at 20 points");
        light.IsChecked = true;
        Arrange(824, 511, "compact-light-20pt.png");
        Require(!appearance.IsDark && ((SolidColorBrush)source.Foreground).Color == lightForeground &&
            ((SolidColorBrush)source.Background).Color == lightBackground && Near(source.FontSize, 20 * 96.0 / 72),
            "returning to light restores editor colors while retaining the chosen font size");
        update.Visibility = Visibility.Collapsed;
        dark.IsChecked = true;
        font.Value = 15.7;
        settings.Close();

        Arrange(1264, 791);
        var sourceWidth = columns.ColumnDefinitions[0].ActualWidth;
        var draftWidth = columns.ColumnDefinitions[2].ActualWidth;
        var reviewWidth = columns.ColumnDefinitions[3].ActualWidth;
        Require(splitter.Focusable && splitter.ActualWidth > 0 && Grid.GetColumn(splitter) == 1 &&
            splitter.ResizeBehavior == GridResizeBehavior.PreviousAndNext, "focusable divider sits between source and draft");
        Drag(64);
        Require(columns.ColumnDefinitions[0].ActualWidth > sourceWidth + 30 && columns.ColumnDefinitions[2].ActualWidth < draftWidth - 30 &&
            Near(columns.ColumnDefinitions[3].ActualWidth, reviewWidth, 2), "native splitter drag widens source and narrows draft without shifting review");
        Drag(-32);
        Require(Near(columns.ColumnDefinitions[0].ActualWidth, sourceWidth + 32, 2) &&
            Near(columns.ColumnDefinitions[2].ActualWidth, draftWidth - 32, 2), "native splitter drag also widens the draft in the opposite direction");
        var chosenShare = columns.SourceShare;
        CloseAndWait(window);

        var restored = Window(profile, _ => true);
        var restoredAppearance = Field<WorkspaceAppearance>(restored, "appearance");
        var restoredColumns = Field<EditorColumns>(restored, "columns");
        Arrange(1264, 791, target: DetachSurface(restored, restoredAppearance));
        Require(restoredAppearance.IsDark && Near(restoredAppearance.FontPoints, 15.7) &&
            Near(restoredColumns.SourceShare, chosenShare) &&
            Near(restoredColumns.ColumnDefinitions[0].ActualWidth, columns.ColumnDefinitions[0].ActualWidth, 2) &&
            Near(restoredColumns.ColumnDefinitions[2].ActualWidth, columns.ColumnDefinitions[2].ActualWidth, 2),
            "reopening restores dark mode, fractional point size, and actual editor widths");
        CloseAndWait(restored);
    }

    static IEnumerable<DependencyObject> DescendantsAndSelf(DependencyObject node)
    {
        yield return node;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().SelectMany(DescendantsAndSelf))
            yield return child;
    }

    static bool IsTranslucent(Brush? brush) => brush != null && (brush.Opacity is > 0 and < 1 || (brush switch
    {
        SolidColorBrush solid => solid.Color.A is > 0 and < 255,
        GradientBrush gradient => gradient.GradientStops.Any(stop => stop.Color.A is > 0 and < 255),
        _ => false
    }));

    static double Contrast(Color first, Color second)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255.0;
            return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color value) => 0.2126 * Channel(value.R) + 0.7152 * Channel(value.G) + 0.0722 * Channel(value.B);
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    static bool Near(double actual, double expected, double tolerance = 0.01) => Math.Abs(actual - expected) <= tolerance;

    static void CheckLegacyConnectionSettings(string directory)
    {
        var profile = Path.Combine(directory, "기존 연결 설정");
        var savedFolder = Path.Combine(directory, "기존 작업 폴더");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"),
            JsonSerializer.Serialize(new { Folder = savedFolder, Model = 2, GoogleProject = "school-project-123", ExternalAiConsent = true }));
        var window = Window(profile, _ => true);
        Require(Field<ComboBox>(window, "models").SelectedItem?.ToString() == Models.Label(ModelConnection.GeminiCli) &&
            Field<string>(window, "folder") == savedFolder && Field<string>(window, "googleProject") == "school-project-123" &&
            !Field<bool>(window, "externalAiConsent"), "legacy Gemini settings preserve provider, project, folder, and start with transmission disabled");
        var appearance = Field<WorkspaceAppearance>(window, "appearance");
        Require(!appearance.IsDark && Near(appearance.FontPoints, 11) &&
            Near(Field<EditorColumns>(window, "columns").SourceShare, EditorColumns.DefaultSourceShare),
            "legacy settings receive the default light theme, 11-point text, and editor widths");
        CloseAndWait(window);
    }

    static void CheckConnectionPanel(string directory, string? preview)
    {
        var window = Window(directory, _ => true);
        var setConsent = typeof(MainWindow).GetMethod("SetConsent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var setApiKey = typeof(MainWindow).GetMethod("SetGeminiApiKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var appearance = Field<WorkspaceAppearance>(window, "appearance");
        var panel = new AiConnectionWindow(window, appearance,
            [ModelConnection.ClaudeCli, ModelConnection.CodexCli, ModelConnection.GeminiApi, ModelConnection.GeminiCli],
            provider => provider switch
            {
                ModelConnection.ClaudeCli => new("연결됨", Connected: true),
                ModelConnection.GeminiApi => new("API 키 입력 필요"),
                ModelConnection.GeminiCli => new("설치 확인 · 로그인 상태는 요청 시 확인"),
                _ => new("로그인 필요")
            },
            false, () => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
            value => setConsent.Invoke(window, [value]), () => { }, _ => { }, "", _ => { },
            value => setApiKey.Invoke(window, [value]), false);
        var consent = (CheckBox)typeof(AiConnectionWindow).GetField("consent", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var connectionState = Field<TextBlock>(window, "connectionState");
        Require(consent.IsChecked == false && connectionState.Text.StartsWith("전송 꺼짐"), "AI connection panel starts with consent off");
        consent.IsChecked = true;
        Require(Field<bool>(window, "externalAiConsent") && connectionState.Text.StartsWith("전송 켜짐"), "consent toggle enables transmission for the current session");
        consent.IsChecked = false;
        Require(!Field<bool>(window, "externalAiConsent") && connectionState.Text.StartsWith("전송 꺼짐"), "consent revocation disables transmission immediately");

        var providerPanel = (StackPanel)typeof(AiConnectionWindow).GetField("providerPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var apiCard = (StackPanel)((Border)providerPanel.Children[2]).Child;
        var keyRow = apiCard.Children.OfType<DockPanel>().Last();
        var password = keyRow.Children.OfType<PasswordBox>().Single();
        const string testKey = "mock-api-key-for-test-only";
        password.Password = testKey;
        typeof(MainWindow).GetMethod("Persist", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Require(Field<string>(window, "geminiApiKey") == testKey &&
            !File.ReadAllText(Path.Combine(directory, "settings.json")).Contains(testKey, StringComparison.Ordinal),
            "Gemini API key stays in the session and is not persisted");
        keyRow.Children.OfType<Button>().Single(item => Equals(item.Content, "키 지우기")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(Field<string>(window, "geminiApiKey") == "" && password.Password == "", "Gemini key can be cleared without leaving the session");
        if (preview != null)
        {
            var content = (FrameworkElement)panel.Content;
            panel.Content = null;
            var surface = new Border { Background = panel.Background, Child = content };
            TextElement.SetFontFamily(surface, panel.FontFamily);
            TextElement.SetFontSize(surface, panel.FontSize);
            TextElement.SetForeground(surface, panel.Foreground);
            surface.Measure(new Size(620, 620));
            int height = (int)Math.Ceiling(surface.DesiredSize.Height);
            surface.Arrange(new Rect(0, 0, 620, height));
            surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(620, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(preview, "ai-connection-panel.png"));
            png.Save(file);
        }
        panel.Close();
        CloseAndWait(window);
    }

    static void CheckProofreading(string directory, string? preview)
    {
        const string original = "학생 75명이 참여 하였다.\r\n모둠별로 안전 수칙을 확인했다.";
        var result = Proofreading.Parse(original, """{"edits":[{"before":"참여 하였다","after":"참여하였다","reason":"띄어쓰기"}]}""");
        var window = Window(directory, _ => true);
        window.Show();
        Drain();
        var editor = Field<TextBox>(window, "draft");
        editor.Text = original;
        var apply = typeof(MainWindow).GetMethod("ApplyProofreading", BindingFlags.Instance | BindingFlags.NonPublic)!;
        apply.Invoke(window, [result]);
        Require(editor.Text == result.Corrected && editor.CanUndo,
            $"proofreading applies as an undoable edit (text equal: {editor.Text == result.Corrected}, undo: {editor.CanUndo})");
        editor.Undo();
        Require(editor.Text == original, "one undo restores the draft before proofreading");
        editor.Text = "사용자가 새로 편집한 초안";
        var rejected = false;
        try { apply.Invoke(window, [result]); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { rejected = true; }
        Require(rejected && editor.Text == "사용자가 새로 편집한 초안", "stale proofreading cannot overwrite a newer draft");
        CloseAndWait(window);

        var dialog = new ProofreadingDialog(result);
        var root = (Grid)dialog.Content;
        dialog.Content = null;
        var surface = new Border { Background = dialog.Background, Child = root };
        TextElement.SetFontFamily(surface, dialog.FontFamily);
        TextElement.SetFontSize(surface, dialog.FontSize);
        TextElement.SetForeground(surface, dialog.Foreground);
        var columns = (Grid)root.Children[1];
        var originalEditor = (TextBox)((Grid)columns.Children[0]).Children[1];
        var correctedEditor = (TextBox)((Grid)columns.Children[1]).Children[1];
        foreach (var (width, height, name) in new[] { (1024, 701, "proofreading.png"), (624, 411, "proofreading-compact.png") })
        {
            surface.Measure(new Size(width, height));
            surface.Arrange(new Rect(0, 0, width, height));
            surface.UpdateLayout();
            Require(originalEditor.IsReadOnly && correctedEditor.IsReadOnly && originalEditor.ActualHeight >= 100 &&
                correctedEditor.ActualHeight >= 100, $"proofreading comparison remains readable at {width}x{height}");
            if (preview == null) continue;
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(preview, name));
            png.Save(file);
        }
        dialog.Close();
    }

    static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    static void CloseAndWait(MainWindow window)
    {
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        PumpUntil(() => closed);
    }

    static void Drain()
    {
        var finished = false;
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => finished = true));
        PumpUntil(() => finished);
    }

    static void PumpUntil(Func<bool> done)
    {
        if (done()) return;
        var frame = new DispatcherFrame();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || elapsed.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        if (!done()) throw new TimeoutException("WPF scenario did not complete");
    }

    static void Require(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + scenario);
        passed++;
        Console.WriteLine("PASS: " + scenario);
    }
}
