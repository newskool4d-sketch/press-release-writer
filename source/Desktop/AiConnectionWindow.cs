using System.Windows;
using System.Windows.Controls;
using PressRelease;

namespace PressReleaseDesktop;

sealed record AiConnectionStatus(string Text, bool Connected = false, bool NeedsInstall = false);

sealed class AiConnectionWindow : Window
{
    readonly Dictionary<ModelConnection, ProviderCard> cards = new();
    readonly StackPanel providerPanel = new();
    readonly CheckBox consent;
    readonly Button refresh;
    readonly Button close;
    readonly Button cancel;
    readonly TextBlock message = Text("");
    readonly TextBox deviceCode = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 0, 0, 6)
    };
    bool busy;

    public AiConnectionWindow(MainWindow owner, WorkspaceAppearance appearance, IEnumerable<ModelConnection> providers,
        Func<ModelConnection, AiConnectionStatus> status, bool consentEnabled,
        Func<Task> refreshConnections, Func<ModelConnection, bool, Task> login, Func<ModelConnection, Task> logout,
        Action<bool> setConsent, Action cancelWork, Action<ModelConnection> install,
        string googleProject, Action<string> setGoogleProject, Action<string> setGeminiApiKey,
        bool showInstitutionalGemini)
    {
        Title = "AI 연결";
        Icon = owner.Icon;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = Math.Min(620, SystemParameters.WorkArea.Width);
        MaxHeight = Math.Min(640, SystemParameters.WorkArea.Height);
        UseLayoutRounding = true;
        var content = new StackPanel { Margin = new Thickness(18) };
        var root = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Content = root;
        appearance.Attach(this, root);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        refresh = new Button { Content = "상태 새로고침" };
        refresh.Click += async (_, _) => await refreshConnections();
        close = new Button { Content = "닫기", IsCancel = true };
        cancel = new Button { Content = "작업 취소", Visibility = Visibility.Collapsed };
        cancel.Click += (_, _) => cancelWork();
        actions.Children.Add(refresh);
        actions.Children.Add(cancel);
        actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        var heading = Text("AI 연결");
        heading.SetResourceReference(TextBlock.FontSizeProperty, "WorkspaceHeadingSize");
        heading.FontWeight = FontWeights.SemiBold;
        heading.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(heading);
        content.Children.Add(header);

        var description = Text("이 PC에 설치된 공식 CLI를 사용합니다. Claude Code는 이 PC의 기존 로그인을 공유합니다. " +
            "Codex와 기관용 Gemini CLI는 앱 전용 프로필을 사용합니다. Gemini API는 Google AI Studio 키로 연결합니다.");
        description.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        description.Margin = new Thickness(0, 0, 0, 14);
        content.Children.Add(description);

        foreach (var provider in providers)
        {
            var detail = Text("");
            detail.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            var name = Text(provider == ModelConnection.CodexCli ? "ChatGPT · Codex" : Models.Label(provider));
            name.FontWeight = FontWeights.SemiBold;
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(name);
            labels.Children.Add(detail);
            var providerActions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            var action = new Button();
            action.Click += async (_, _) =>
            {
                var current = status(provider);
                if (provider == ModelConnection.GeminiApi) await login(provider, false);
                else if (current.NeedsInstall) install(provider);
                else if (current.Connected && provider != ModelConnection.GeminiCli) await logout(provider);
                else await login(provider, false);
            };
            providerActions.Children.Add(action);
            Button? device = null;
            if (provider == ModelConnection.CodexCli)
            {
                device = new Button { Content = "장치 코드 로그인" };
                device.Click += async (_, _) => await login(provider, true);
                providerActions.Children.Add(device);
            }
            var row = new DockPanel();
            DockPanel.SetDock(providerActions, Dock.Right);
            row.Children.Add(providerActions);
            row.Children.Add(labels);
            FrameworkElement body = row;
            if (provider == ModelConnection.GeminiApi)
            {
                var fields = new StackPanel();
                fields.Children.Add(row);
                var label = Text("Gemini API 키 · 앱을 닫을 때까지 사용");
                label.Margin = new Thickness(0, 8, 0, 3);
                fields.Children.Add(label);
                var input = new PasswordBox { MinHeight = 30, MaxLength = 512, ToolTip = "새 키를 입력하면 이번 실행에 사용한 키를 교체합니다." };
                System.Windows.Automation.AutomationProperties.SetName(input, "Gemini API 키");
                input.PasswordChanged += (_, _) => setGeminiApiKey(input.Password);
                var keyRow = new DockPanel();
                var clear = new Button { Content = "키 지우기", Margin = new Thickness(8, 0, 0, 0) };
                clear.Click += (_, _) => { input.Clear(); setGeminiApiKey(""); };
                var keyHelp = new Button { Content = "키 발급 안내", Margin = new Thickness(8, 0, 0, 0) };
                keyHelp.Click += (_, _) => install(provider);
                DockPanel.SetDock(keyHelp, Dock.Right);
                DockPanel.SetDock(clear, Dock.Right);
                keyRow.Children.Add(keyHelp);
                keyRow.Children.Add(clear);
                keyRow.Children.Add(input);
                fields.Children.Add(keyRow);
                body = fields;
            }
            else if (provider == ModelConnection.GeminiCli)
            {
                var fields = new StackPanel();
                fields.Children.Add(row);
                var label = Text("Google Cloud 프로젝트 ID · 학교·기관용 Gemini Code Assist");
                label.Margin = new Thickness(0, 8, 0, 3);
                fields.Children.Add(label);
                var input = new TextBox { Text = googleProject, ToolTip = "기관에서 승인한 프로젝트의 ID를 입력하세요." };
                System.Windows.Automation.AutomationProperties.SetName(input, "Google Cloud 프로젝트 ID");
                input.TextChanged += (_, _) => setGoogleProject(input.Text);
                fields.Children.Add(input);
                body = fields;
            }
            var surface = Surface(body);
            cards.Add(provider, new(surface, detail, action, device));
            if (provider == ModelConnection.GeminiCli)
                providerPanel.Children.Add(new Expander { Header = "기관용 Gemini CLI 설정", Content = surface,
                    IsExpanded = showInstitutionalGemini, Margin = new Thickness(0, 0, 0, 8) });
            else providerPanel.Children.Add(surface);
            SetProviderStatus(provider, status(provider));
        }
        content.Children.Add(providerPanel);

        System.Windows.Automation.AutomationProperties.SetName(deviceCode, "ChatGPT 장치 로그인 일회용 코드");
        content.Children.Add(deviceCode);
        message.Margin = new Thickness(0, 0, 0, 10);
        message.Visibility = Visibility.Collapsed;
        content.Children.Add(message);

        consent = new CheckBox { Content = "외부 AI 전송 동의", IsChecked = consentEnabled, FontWeight = FontWeights.SemiBold };
        consent.Checked += (_, _) => setConsent(true);
        consent.Unchecked += (_, _) => setConsent(false);
        var scope = Text("동의하면 ‘초안 생성’에 원자료 텍스트와 입력한 작성 조건 및 확인 정보가, ‘맞춤법 검사’에 현재 초안이 전송됩니다. " +
            "선택한 서비스에 전송합니다. Claude Code는 Anthropic, ChatGPT · Codex는 OpenAI, Gemini는 Google을 사용합니다. " +
            "개인정보나 비공개 내용은 전송 전에 지워 주세요. " +
            "동의는 이번 실행에만 적용됩니다.");
        scope.Margin = new Thickness(0, 6, 0, 0);
        var consentPanel = new StackPanel();
        consentPanel.Children.Add(consent);
        consentPanel.Children.Add(scope);
        content.Children.Add(Surface(consentPanel));

        Loaded += async (_, _) => await refreshConnections();
        Closing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            cancelWork();
        };
    }

    public void SetProviderStatus(ModelConnection provider, AiConnectionStatus status)
    {
        var card = cards[provider];
        card.Detail.Text = status.Text;
        card.Action.Content = provider == ModelConnection.GeminiApi ? "연결 확인"
            : status.NeedsInstall ? "설치 안내"
            : provider == ModelConnection.GeminiCli ? "계정 관리"
            : status.Connected ? "로그아웃" : "로그인";
        card.Surface.SetResourceReference(Border.BackgroundProperty, status.Connected ? "AccentWash" : "Glass");
        if (card.Device != null)
            card.Device.Visibility = status.Connected || status.NeedsInstall ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetBusy(bool value)
    {
        busy = value;
        providerPanel.IsEnabled = !value;
        consent.IsEnabled = !value;
        refresh.IsEnabled = !value;
        close.IsEnabled = !value;
        cancel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowDeviceCode(string? code)
    {
        deviceCode.Text = code ?? "";
        deviceCode.Visibility = string.IsNullOrWhiteSpace(code) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetMessage(string text, bool error)
    {
        message.Text = text;
        message.Visibility = Visibility.Visible;
        message.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorInk" : "TextMuted");
    }

    static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    static Border Surface(UIElement child)
    {
        var surface = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(11),
            Child = child
        };
        surface.SetResourceReference(Border.BackgroundProperty, "Glass");
        surface.SetResourceReference(Border.BorderBrushProperty, "GlassLine");
        return surface;
    }

    sealed record ProviderCard(Border Surface, TextBlock Detail, Button Action, Button? Device);
}
