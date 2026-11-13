using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PressRelease;

namespace PressReleaseDesktop;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (ReleaseUpdater.TryRunApplyMode(args)) return;
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        // An unexpected error in a UI handler must not close the window and discard the unsaved source and draft.
        application.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            MessageBox.Show("예기치 않은 오류가 발생했습니다. 초안을 저장한 뒤 앱을 다시 열어 주세요.\n" + e.Exception.Message,
                "보도자료 작성기", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        try
        {
            string? dataDirectory = null;
            if (args.Length != 0)
            {
                if (args.Length != 2 || args[0] != "--data-dir" || !Path.IsPathFullyQualified(args[1]))
                    throw new ArgumentException("사용법: PressReleaseWriter.exe [--data-dir <절대 경로>]");
                dataDirectory = args[1];
            }
            application.Run(new MainWindow(dataDirectory));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "보도자료 작성기 시작 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

sealed class MainWindow : Window
{
    const string StatusNeutral = "StatusNeutral", StatusSuccess = "StatusSuccess", StatusError = "StatusError";
    const string Ink = "TextInk", Muted = "TextMuted", Line = "Line";
    static readonly ModelConnection[] Providers =
        [ModelConnection.CodexCli, ModelConnection.ClaudeCli, ModelConnection.GeminiApi, ModelConnection.GeminiCli];

    readonly string appFolder;
    readonly Func<string, bool> confirmClose;
    readonly WorkspaceAppearance appearance;
    readonly EditorColumns columns;
    readonly ComboBox models = new() { MinWidth = 155 };
    readonly ComboBox institutions = new() { ItemsSource = new[] { "본청", "교육지원청", "직속기관", "확인 필요" }, SelectedIndex = 3 };
    readonly ComboBox tenses = new() { ItemsSource = new[] { "행사 예정", "행사 진행 중", "행사 완료", "확인 필요" }, SelectedIndex = 3 };
    readonly ComboBox modes = new() { ItemsSource = new[] { "새 초안 작성", "기존 초안 검토" }, SelectedIndex = 0 };
    readonly TextBox source = Editor();
    readonly TextBox draft = Editor();
    readonly TextBlock checks = Caption("초안을 입력하거나 생성하면 점검 항목이 표시됩니다.");
    readonly TextBlock folderLabel = Caption("");
    readonly TextBlock connectionState = Caption("");
    readonly Dictionary<ModelConnection, AiConnectionStatus> connectionStates = new();
    readonly TextBox officialName = new(), eventStart = new(), eventEnd = new(), publicationDate = new(), uploadAt = new(), targetCount = new(), priorCoverage = new();
    readonly ComboBox photo = new() { ItemsSource = new[] { "있음", "없음", "확인 필요" }, SelectedIndex = 2 };
    readonly TextBox department = new();
    // One selector per guideline rejection category; the officer's judgement is sent as the baseline instead of a model guess.
    readonly ComboBox[] rejections = Writing.RejectionReasons
        .Select(_ => new ComboBox { ItemsSource = Writing.RejectionStatuses, SelectedIndex = 2 }).ToArray();
    readonly TextBlock conditionChecks = Caption("");
    Button reviewHide = null!, reviewShow = null!;
    readonly TextBlock status = Caption("AI 연결을 선택하고 원자료를 입력해 주세요.");
    readonly Border statusBar = new() { CornerRadius = new(4), Padding = new(10, 5, 10, 5) };
    readonly CheckBox saveTxt = new() { Content = "TXT", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox saveMd = new() { Content = "MD", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox saveHwpx = new() { Content = "HWPX", IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
    readonly List<Control> whileBusy = [];
    readonly CancellationTokenSource updateCancellation = new();
    readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    Button settingsButton = null!;
    Button updateButton = null!;
    Button cancel = null!;
    AiConnectionWindow? connections;
    CancellationTokenSource? running;
    TaskCompletionSource? runningCompletion;
    CodexAppServer? codex;
    Task? updateCheck;
    AvailableUpdate? availableUpdate;
    StagedUpdate? stagedUpdate;
    string folder = "";
    string googleProject = "";
    string geminiApiKey = "";
    bool externalAiConsent;
    bool draftDirty;
    bool sourceDirty;
    bool ready;
    bool closingApproved;
    bool closingInProgress;
    ModelConnection Selected => models.SelectedItem is ModelItem entry ? entry.Connection : ModelConnection.CodexCli;

    public MainWindow(string? dataDirectory = null, Func<string, bool>? closeConfirmation = null)
    {
        appFolder = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PressReleaseWriter");
        confirmClose = closeConfirmation ?? (message => MessageBox.Show(this, message, Title, MessageBoxButton.YesNo) == MessageBoxResult.Yes);
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        Title = $"인천교육 보도자료 작성기 {version}";
        Icon = new BitmapImage(new Uri("pack://application:,,,/PressReleaseWriter;component/Assets/press-release.ico"));
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1280, workArea.Width);
        Height = Math.Min(830, workArea.Height);
        MinWidth = Math.Min(840, workArea.Width);
        MinHeight = Math.Min(550, workArea.Height);
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Directory.CreateDirectory(appFolder);
        var settings = LoadSettings();
        appearance = new WorkspaceAppearance(settings.DarkMode, settings.FontPoints);
        folder = settings.Folder ?? "";
        googleProject = settings.GoogleProject ?? "";
        department.Text = settings.Department ?? "";
        var modelItems = Providers.Select(provider => new ModelItem(provider)).ToArray();
        models.ItemsSource = modelItems;
        var savedModel = settings.Model switch
        {
            ModelConnection.ClaudeWeb => ModelConnection.ClaudeCli,
            ModelConnection.GeminiWeb => ModelConnection.GeminiApi,
            _ => settings.Model
        };
        models.SelectedItem = modelItems.FirstOrDefault(item => item.Connection == savedModel) ?? modelItems[0];
        System.Windows.Automation.AutomationProperties.SetName(models, "사용할 AI");
        whileBusy.AddRange([models, institutions, tenses, modes, source, draft, saveTxt, saveMd, saveHwpx,
            officialName, eventStart, eventEnd, publicationDate, uploadAt, targetCount, priorCoverage, photo, department, .. rejections]);

        var root = GridRows("auto", "auto", "*", "auto");
        root.Margin = new(12);
        appearance.Attach(this, root);
        Content = root;

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center
        };
        var logo = BrandImage(BrandBitmap("incheon-education-ci.jpg"), 34, "인천광역시교육청");
        logo.Name = "IncheonEducationLogo";
        logo.Margin = new(0, 0, 10, 0);
        header.Children.Add(logo);
        var heading = new TextBlock
        {
            Text = "보도자료 작성기", FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        heading.SetResourceReference(TextBlock.FontSizeProperty, "WorkspaceTitleSize");
        heading.SetResourceReference(TextBlock.ForegroundProperty, "BrandInk");
        header.Children.Add(heading);
        updateButton = Action("업데이트 확인", () => StartUpdateCheck(automatic: false), lockWhileBusy: false);
        updateButton.Visibility = Visibility.Collapsed;
        updateButton.Margin = new(0, 0, 12, 0);
        updateButton.VerticalAlignment = VerticalAlignment.Center;
        var rightHeader = new Grid { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(12, 0, 0, 0) };
        rightHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rightHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Place(rightHeader, updateButton, 0, 0);
        Place(rightHeader, SuccessSlogan(), 0, 1);
        var headerBar = new Grid();
        headerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Place(headerBar, header, 0, 0);
        Place(headerBar, rightHeader, 0, 1);
        var masthead = new Border
        {
            BorderThickness = new(0, 0, 0, 1),
            Padding = new(10, 6, 10, 6), Margin = new(0, 0, 0, 8), Child = headerBar
        };
        masthead.SetResourceReference(Border.BackgroundProperty, "BrandPaper");
        masthead.SetResourceReference(Border.BorderBrushProperty, Line);
        Place(root, masthead, 0);

        var connectionPanel = new Grid();
        connectionPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        connectionPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var config = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 4, 7, 0) };
        models.Margin = new(0, 0, 8, 4);
        config.Children.Add(models);
        settingsButton = Dropdown("설정", null, false, MenuAction("AI 연결", OpenConnections),
            MenuAction("화면 설정", OpenDisplaySettings, lockWhileBusy: false));
        var settingsContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var gear = new TextBlock { Text = "\uE713", Margin = new(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false, Focusable = false };
        gear.SetResourceReference(TextBlock.FontFamilyProperty, "SymbolFont");
        settingsContent.Children.Add(gear);
        settingsContent.Children.Add(new TextBlock { Text = "설정", VerticalAlignment = VerticalAlignment.Center });
        settingsButton.Content = settingsContent;
        System.Windows.Automation.AutomationProperties.SetName(settingsButton, "설정");
        System.Windows.Automation.AutomationProperties.SetHelpText(settingsButton, "AI 연결과 화면 설정을 선택합니다.");
        config.Children.Add(settingsButton);
        var help = Dropdown("도움말", null, false, MenuAction("사용 안내", () => RunUi(() =>
        {
            var path = UserGuide.Extract(appFolder);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }), lockWhileBusy: false));
        System.Windows.Automation.AutomationProperties.SetName(help, "도움말");
        System.Windows.Automation.AutomationProperties.SetHelpText(help, "HTML 사용 안내를 기본 브라우저에서 엽니다.");
        config.Children.Add(help);
        Place(connectionPanel, config, 0, 0);
        connectionState.SetResourceReference(TextBlock.ForegroundProperty, Muted);
        connectionState.SetResourceReference(TextBlock.FontSizeProperty, "WorkspaceSmallSize");
        connectionState.SetResourceReference(TextBlock.LineHeightProperty, "WorkspaceStatusLineHeight");
        connectionState.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        var accountStatus = new Border
        {
            Padding = new(10, 3, 0, 3), BorderThickness = new(1, 0, 0, 0), MaxWidth = 285,
            VerticalAlignment = VerticalAlignment.Center, Child = Scroll(connectionState, 60)
        };
        accountStatus.SetResourceReference(Border.BorderBrushProperty, Line);
        Place(connectionPanel, accountStatus, 0, 1);
        var toolbar = new Border
        {
            Padding = new(8, 0, 8, 3), Margin = new(0, 0, 0, 10), CornerRadius = new(6),
            BorderThickness = new(1), Child = connectionPanel
        };
        toolbar.SetResourceReference(Border.BackgroundProperty, "Glass");
        toolbar.SetResourceReference(Border.BorderBrushProperty, "GlassLine");
        Place(root, toolbar, 1);

        columns = new EditorColumns(SourcePanel(), DraftPanel(), ReviewPanel(), ReviewStrip(), settings.SourceShare, settings.ReviewVisible, _ => Persist());
        Place(root, columns, 2);

        var bottom = new StackPanel { Margin = new(0, 10, 0, 0) };
        var save = new WrapPanel { Margin = new(0, 0, 0, 4) };
        saveTxt.Margin = new(2, 0, 0, 4);
        save.Children.Add(saveTxt);
        saveMd.Margin = new(12, 0, 12, 4);
        save.Children.Add(saveMd);
        saveHwpx.Margin = new(0, 0, 14, 4);
        saveHwpx.ToolTip = "공식 양식 저장 · 인천광역시교육청 보도자료 양식에 제목·본문·보도 희망일·사진 여부·담당 부서를 채워 저장합니다. 자료문의 이름과 연락처는 한글에서 직접 기재합니다.";
        save.Children.Add(saveHwpx);
        save.Children.Add(Action("초안 저장", SaveDraft));
        save.Children.Add(Dropdown("작업 폴더", null, true, MenuAction("폴더 지정", () => RunUi(() => TryPickFolder())),
            MenuAction("폴더 열기", () => RunUi(() =>
        {
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("작업 폴더를 먼저 지정해 주세요.");
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }))));
        folderLabel.Margin = new(4, 0, 0, 4);
        folderLabel.VerticalAlignment = VerticalAlignment.Center;
        folderLabel.SetResourceReference(TextBlock.ForegroundProperty, Muted);
        folderLabel.MaxWidth = 240;
        folderLabel.TextWrapping = TextWrapping.NoWrap;
        folderLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        save.Children.Add(folderLabel);
        bottom.Children.Add(save);
        statusBar.Child = Scroll(status, 42);
        bottom.Children.Add(statusBar);
        Place(root, bottom, 3);

        draft.TextChanged += (_, _) =>
        {
            draftDirty = true;
            checks.Text = string.IsNullOrWhiteSpace(draft.Text) ? "초안을 입력하거나 생성하면 점검 항목이 표시됩니다." : Writing.Check(draft.Text);
        };
        source.TextChanged += (_, _) => sourceDirty = true;
        foreach (var field in new[] { officialName, eventStart, eventEnd, publicationDate, uploadAt, targetCount, priorCoverage, department })
            field.TextChanged += (_, _) => { sourceDirty = true; UpdateConditionChecks(); };
        foreach (var selector in rejections.Append(photo))
            selector.SelectionChanged += (_, _) => { sourceDirty = true; UpdateConditionChecks(); };
        tenses.SelectionChanged += (_, _) => UpdateConditionChecks();
        UpdateConditionChecks();
        models.SelectionChanged += (_, _) =>
        {
            UpdateConnectionButtons();
            Persist();
        };
        Closing += OnClosing;
        // Windows shutdown and logoff close the window without honoring Cancel, so the async close path cannot finish there.
        var application = Application.Current;
        if (application != null) application.SessionEnding += OnSessionEnding;
        Closed += (_, _) =>
        {
            if (application != null) application.SessionEnding -= OnSessionEnding;
            updateCancellation.Dispose();
        };
        if (string.Equals(Path.GetFileName(Environment.ProcessPath), "PressReleaseWriter.exe", StringComparison.OrdinalIgnoreCase))
        {
            updateButton.Visibility = Visibility.Visible;
            updateTimer.Tick += (_, _) => StartUpdateCheck(automatic: true);
            Loaded += (_, _) => { updateTimer.Start(); StartUpdateCheck(automatic: true); };
        }
        ready = true;
        UpdateConnectionButtons();
        UpdateFolderLabel();
        SetStatus("원자료를 붙여넣거나 파일을 열어 주세요.", StatusNeutral);
    }

    CodexAppServer Codex
    {
        get
        {
            if (codex != null) return codex;
            var server = new CodexAppServer(() => Models.CreateAppServerStart(appFolder));
            codex = server;
            server.AccountChanged += account =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(codex, server) && !closingInProgress)
                        SetConnectionStatus(ModelConnection.CodexCli, AccountStatus(account.IsChatGpt));
                });
            };
            return server;
        }
    }

    async Task ResetCodex()
    {
        var previous = codex;
        codex = null;
        if (previous != null) await previous.DisposeAsync();
    }

    void OpenConnections()
    {
        if (running != null || connections != null) return;
        var dialog = new AiConnectionWindow(this, appearance, Providers, ConnectionStatus, externalAiConsent,
            RefreshConnections, Connect, Disconnect, SetConsent,
            () => running?.Cancel(), provider => RunUi(() => Models.OpenOfficialPage(provider)),
            googleProject, SetGoogleProject, SetGeminiApiKey, Selected == ModelConnection.GeminiCli) { Owner = this };
        connections = dialog;
        try { dialog.ShowDialog(); }
        finally
        {
            connections = null;
            Persist();
        }
    }

    void OpenDisplaySettings()
    {
        var dialog = new DisplaySettingsDialog(appearance) { Owner = this };
        dialog.ShowDialog();
        Persist();
    }

    Task RefreshConnections() => Busy(async token =>
    {
        SetStatus("설치 및 계정 상태를 확인하고 있습니다…", StatusNeutral);
        foreach (var provider in Providers)
        {
            token.ThrowIfCancellationRequested();
            try { await ReadConnectionStatus(provider, token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetConnectionStatus(provider, ConnectionFailure(provider, ex));
                if (provider == ModelConnection.CodexCli) await ResetCodex();
            }
        }
        SetStatus("AI 연결 상태를 갱신했습니다.", StatusNeutral);
    });

    async Task<bool> ReadConnectionStatus(ModelConnection provider, CancellationToken token)
    {
        var previouslyConnected = ConnectionStatus(provider).Connected;
        if (provider == ModelConnection.GeminiApi && !previouslyConnected) return false;
        SetConnectionStatus(provider, new("상태 확인 중…"));
        try
        {
            if (provider == ModelConnection.GeminiApi)
            {
                await CheckGeminiApi(token);
                return true;
            }
            if (provider == ModelConnection.GeminiCli)
            {
                await Models.CheckGeminiInstallationAsync(appFolder, token);
                SetConnectionStatus(provider, previouslyConnected
                    ? new("설치 확인 · 이번 실행에서 응답 확인됨", Connected: true)
                    : new("설치 확인 · 로그인 상태는 요청 시 확인"));
                return previouslyConnected;
            }
            var connected = await Models.IsConnectedAsync(provider, appFolder, token,
                provider == ModelConnection.CodexCli ? Codex : null);
            SetConnectionStatus(provider, AccountStatus(connected));
            return connected;
        }
        catch (OperationCanceledException)
        {
            SetConnectionStatus(provider, new("상태 새로고침 필요"));
            throw;
        }
    }

    Task Connect(ModelConnection provider, bool deviceCode) => RunAccountAction(provider,
        provider == ModelConnection.GeminiApi ? "연결 확인" : "로그인", async token =>
    {
        if (provider == ModelConnection.GeminiApi)
        {
            await CheckGeminiApi(token);
            SetStatus("Gemini API 키와 사용할 모델을 확인했습니다.", StatusSuccess);
            return;
        }
        if (provider == ModelConnection.GeminiCli)
        {
            var project = Models.ValidateGeminiProject(googleProject);
            SetConnectionStatus(provider, new("Gemini CLI 계정 창 열림"));
            SetStatus("Gemini CLI에서 학교·기관 Google 계정으로 로그인하세요. 계정을 바꾸려면 /auth를 사용하고, 마치면 /quit를 입력하세요.", StatusNeutral);
            await Models.LoginGeminiAsync(appFolder, project, token);
            SetConnectionStatus(provider, new("설치 확인 · 로그인 상태는 요청 시 확인"));
            SetStatus("Gemini CLI 창이 닫혔습니다. 초안 생성 또는 맞춤법 검사에서 연결을 확인합니다.", StatusNeutral);
            return;
        }
        var connected = false;
        try { connected = await ReadConnectionStatus(provider, token); }
        catch (CliExecutionException ex) when (ex.Kind == CliFailureKind.Authentication) { }
        if (!connected)
        {
            SetConnectionStatus(provider, new("로그인 진행 중…"));
            if (provider == ModelConnection.CodexCli)
            {
                SetStatus("브라우저에서 ChatGPT 공식 로그인을 완료해 주세요.", StatusNeutral);
                await Codex.LoginAsync(deviceCode, async challenge =>
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (challenge.DeviceCode)
                        {
                            connections?.ShowDeviceCode(challenge.UserCode);
                            SetStatus("일회용 코드를 브라우저에 입력해 주세요.", StatusNeutral);
                        }
                        Process.Start(new ProcessStartInfo(challenge.VerificationUri.AbsoluteUri) { UseShellExecute = true });
                    });
                }, token);
            }
            else
            {
                SetStatus("열린 Claude Code 창의 안내에 따라 로그인해 주세요.", StatusNeutral);
                await Models.LoginClaudeAsync(appFolder, token);
            }
            if (!await ReadConnectionStatus(provider, token))
            {
                SetStatus("구독 계정 로그인이 확인되지 않았습니다. 연결 창에서 다시 로그인해 주세요.", StatusError);
                return;
            }
        }
        SetStatus($"{Models.Label(provider)} 연결을 확인했습니다.", StatusSuccess);
    });

    Task Disconnect(ModelConnection provider)
    {
        if (provider == ModelConnection.GeminiApi)
        {
            SetGeminiApiKey("");
            return Task.CompletedTask;
        }
        if (provider == ModelConnection.GeminiCli) return Connect(provider, false);
        var prompt = provider == ModelConnection.CodexCli
            ? "보도자료 작성봇 전용 Codex 계정에서 로그아웃할까요?"
            : "이 PC의 Claude Code 로그인을 공유합니다. 로그아웃하면 다른 Claude Code 작업에서도 다시 로그인해야 합니다. 계속할까요?";
        if (running != null || MessageBox.Show((Window?)connections ?? this, prompt, "AI 로그아웃", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return Task.CompletedTask;
        return RunAccountAction(provider, "로그아웃", async token =>
        {
            SetConnectionStatus(provider, new("로그아웃 진행 중…"));
            if (provider == ModelConnection.CodexCli) await Codex.LogoutAsync(token);
            else await Models.LogoutClaudeAsync(appFolder, token);
            if (await ReadConnectionStatus(provider, token))
                SetStatus("계정이 여전히 연결되어 있습니다. 다시 확인해 주세요.", StatusError);
            else
                SetStatus($"{Models.Label(provider)}에서 로그아웃했습니다.", StatusSuccess);
        });
    }

    Task RunAccountAction(ModelConnection provider, string actionName, Func<CancellationToken, Task> action) => Busy(async token =>
    {
        try { await action(token); }
        catch (OperationCanceledException)
        {
            SetConnectionStatus(provider, new("상태 새로고침 필요"));
            if (provider == ModelConnection.CodexCli) await ResetCodex();
            throw;
        }
        catch (Exception ex)
        {
            SetConnectionStatus(provider, ConnectionFailure(provider, ex));
            SetStatus($"{actionName} 실패 · {UserMessage(ex)}", StatusError);
            if (provider == ModelConnection.CodexCli) await ResetCodex();
        }
    }, affectsConnection: false);

    static AiConnectionStatus AccountStatus(bool connected) => connected ? new("구독 계정 연결됨", Connected: true) : new("구독 계정 로그인 필요");

    static AiConnectionStatus ConnectionFailure(ModelConnection provider, Exception ex) => ex switch
    {
        GeminiApiException { KeyRejected: true } => new("API 키 또는 프로젝트 권한 확인 필요"),
        FileNotFoundException or CliExecutionException { Kind: CliFailureKind.Installation } => new("CLI 설치 또는 업데이트 필요", NeedsInstall: true),
        CliExecutionException { Kind: CliFailureKind.Authentication } when provider == ModelConnection.GeminiCli => new("학교·기관 Google 계정 로그인 확인 필요"),
        CliExecutionException { Kind: CliFailureKind.Authentication } => AccountStatus(false),
        _ => new($"상태 확인 실패 · {UserMessage(ex)}")
    };

    AiConnectionStatus ConnectionStatus(ModelConnection provider) =>
        connectionStates.GetValueOrDefault(provider) ?? new(provider == ModelConnection.GeminiApi ? "API 키 입력 필요" : "상태 확인 전");

    void SetConnectionStatus(ModelConnection provider, AiConnectionStatus status)
    {
        connectionStates[provider] = status;
        connections?.SetProviderStatus(provider, status);
        UpdateConnectionButtons();
    }

    void SetConsent(bool consent)
    {
        externalAiConsent = consent;
        UpdateConnectionButtons();
    }

    void SetGoogleProject(string project)
    {
        project = project.Trim();
        if (googleProject == project) return;
        googleProject = project;
        var state = ConnectionStatus(ModelConnection.GeminiCli);
        SetConnectionStatus(ModelConnection.GeminiCli, state.NeedsInstall ? state : new("프로젝트 변경 · 연결 확인 필요"));
    }

    void SetGeminiApiKey(string key)
    {
        key = key.Trim();
        if (geminiApiKey == key) return;
        geminiApiKey = key;
        SetConnectionStatus(ModelConnection.GeminiApi,
            new(key.Length == 0 ? "API 키 입력 필요" : "키 입력됨 · 연결 확인 필요"));
    }

    async Task CheckGeminiApi(CancellationToken token)
    {
        SetConnectionStatus(ModelConnection.GeminiApi, new("API 키 확인 중…"));
        await Models.CheckGeminiApiAsync(geminiApiKey, token);
        SetConnectionStatus(ModelConnection.GeminiApi, new("API 키와 모델 확인됨", Connected: true));
    }

    void MarkAiResponded(ModelConnection provider) => SetConnectionStatus(provider,
        provider is ModelConnection.GeminiCli or ModelConnection.GeminiApi
            ? new("이번 실행에서 응답 확인됨", Connected: true) : AccountStatus(true));

    bool RequireAiConsent()
    {
        if (externalAiConsent) return true;
        SetStatus("AI 연결 창에서 ‘외부 AI 전송 동의’를 켠 뒤 다시 시도해 주세요.", StatusError);
        return false;
    }

    // Automatic runs only read release metadata. The executable is downloaded when the user clicks the offered version.
    void StartUpdateCheck(bool automatic)
    {
        if (closingInProgress || updateCheck is { IsCompleted: false } || stagedUpdate != null) return;
        if (availableUpdate is { } available && !automatic) updateCheck = DownloadUpdate(available);
        else if (availableUpdate == null) updateCheck = CheckForUpdate();
    }

    async Task CheckForUpdate()
    {
        updateButton.Content = "업데이트 확인 중…";
        updateButton.IsEnabled = false;
        try
        {
            var installed = typeof(MainWindow).Assembly.GetName().Version!;
            var current = new Version(installed.Major, installed.Minor, installed.Build);
            availableUpdate = await ReleaseUpdater.CheckLatestAsync(current, appFolder, updateCancellation.Token);
            if (availableUpdate != null)
            {
                updateButton.Content = $"v{availableUpdate.Version} 내려받기";
                updateButton.ToolTip = $"새 버전 v{availableUpdate.Version}이 공개됐습니다. 누르면 실행 파일({availableUpdate.Size / (1024 * 1024)}MB)을 내려받아 검증하고, 정상 종료할 때 적용합니다.";
                return;
            }
            updateButton.ToolTip = "설치된 버전보다 새로운 공개 릴리스가 없습니다.";
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            updateButton.ToolTip = "업데이트를 확인하지 못했습니다. 다시 확인할 수 있습니다.";
        }
        finally
        {
            if (availableUpdate == null && !closingInProgress) updateButton.Content = "업데이트 확인";
            if (!closingInProgress) updateButton.IsEnabled = true;
        }
    }

    async Task DownloadUpdate(AvailableUpdate available)
    {
        updateButton.Content = $"v{available.Version} 내려받는 중…";
        updateButton.IsEnabled = false;
        try
        {
            stagedUpdate = await ReleaseUpdater.StageAsync(available, appFolder, updateCancellation.Token);
            updateButton.Content = $"v{stagedUpdate.Version} · 종료 시 적용";
            updateButton.ToolTip = "앱을 정상 종료하면 새 실행 파일을 적용합니다. 다음 실행부터 새 버전을 사용합니다.";
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            updateButton.ToolTip = $"업데이트 파일을 내려받지 못했습니다. 다시 시도할 수 있습니다.\n{UserMessage(ex)}";
        }
        finally
        {
            if (stagedUpdate == null && !closingInProgress)
            {
                updateButton.Content = $"v{available.Version} 내려받기";
                updateButton.IsEnabled = true;
            }
        }
    }

    void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        if (closingApproved) return;
        // Windows closes the window regardless of Cancel; do the synchronous part of the close path now.
        // A staged update is not applied here: the helper would be terminated with the session, so it is discarded instead.
        closingApproved = true;
        closingInProgress = true;
        Persist();
        running?.Cancel();
        updateCancellation.Cancel();
        if (stagedUpdate is { } pending)
        {
            stagedUpdate = null;
            try { ReleaseUpdater.Discard(pending); } catch (Exception) { }
        }
        // DisposeAsync kills the child process before its first await; the remaining wait is best effort.
        _ = ResetCodex();
    }

    async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (closingApproved) return;
        e.Cancel = true;
        if (closingInProgress) return;
        var unsavedDraft = draftDirty && !string.IsNullOrWhiteSpace(draft.Text);
        if ((sourceDirty || unsavedDraft) && !confirmClose(
            (sourceDirty ? "원자료와 확인 정보는 자동 저장되지 않으며 창을 닫으면 사라집니다.\n" : "") +
            (unsavedDraft ? "저장하지 않은 초안이 있습니다.\n" : "") + "닫을까요?")) return;
        closingInProgress = true;
        IsEnabled = false;
        try
        {
            Persist();
            running?.Cancel();
            if (runningCompletion != null) await runningCompletion.Task;
            await ResetCodex();
            updateTimer.Stop();
            updateCancellation.Cancel();
            if (updateCheck != null) await updateCheck;
            if (closingApproved) return; // The session ended while waiting; OnSessionEnding already cleaned up.
            if (stagedUpdate is { } pending)
            {
                var applying = false;
                try
                {
                    applying = await ReleaseUpdater.LaunchApplyAsync(pending);
                }
                catch (Exception) { }
                stagedUpdate = null;
                if (!applying)
                {
                    ReleaseUpdater.Discard(pending);
                    if (!closingApproved)
                        MessageBox.Show(this, "업데이트를 시작하지 못했습니다. 앱을 다시 열고 업데이트를 내려받아 주세요.",
                            Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                if (closingApproved) return;
            }
            closingApproved = true;
            // ResetCodex may complete synchronously; leave this Closing event before closing again.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception ex)
        {
            closingInProgress = false;
            IsEnabled = true;
            SetStatus("종료할 작업을 정리하지 못했습니다. " + UserMessage(ex), StatusError);
        }
    }

    FrameworkElement SourcePanel()
    {
        var panel = GridRows("auto", "*");
        var heading = Heading("원자료");
        heading.SetResourceReference(VisibilityProperty, "FullTools");
        Place(panel, heading, 0);
        var inputMenu = Dropdown("원자료", null, true, MenuAction("파일 열기", LoadSource), MenuAction("붙여넣기", PasteSource));
        inputMenu.HorizontalAlignment = HorizontalAlignment.Left;
        inputMenu.SetResourceReference(VisibilityProperty, "CompactTools");
        System.Windows.Automation.AutomationProperties.SetName(inputMenu, "원자료 입력 메뉴");
        Place(panel, inputMenu, 0);
        var original = GridRows("auto", "*");
        original.AllowDrop = true;
        original.PreviewDragOver += SourceDragOver;
        original.PreviewDrop += SourceDrop;
        var file = new WrapPanel { Margin = new(0, 0, 0, 4) };
        file.SetResourceReference(VisibilityProperty, "FullTools");
        file.Children.Add(Action("파일 열기", LoadSource));
        file.Children.Add(Action("붙여넣기", PasteSource));
        Place(original, file, 0);
        source.ToolTip = "TXT·MD·HWPX·PDF 파일을 놓을 수 있습니다. 비공개 정보나 개인정보는 전송 전에 제거해 주세요.";
        Place(original, EditorSurface(source, "원자료", "파일을 끌어다 놓거나 행사 자료를 붙여넣으세요."), 1);

        var facts = new StackPanel();
        facts.Children.Add(Field("기관 구분", institutions));
        facts.Children.Add(Field("보도 시점", tenses));
        facts.Children.Add(Field("작성 유형", modes));
        facts.Children.Add(Subheading("날짜·인원 등 확인 정보"));
        var hint = Caption("확인된 값만 입력하세요. 빈 항목은 ‘확인 필요’로 처리합니다.");
        hint.SetResourceReference(TextBlock.ForegroundProperty, Muted);
        hint.Margin = new(0, 0, 0, 6);
        facts.Children.Add(hint);
        facts.Children.Add(Field("공식 기관명", officialName));
        facts.Children.Add(Field("행사 시작일", eventStart));
        facts.Children.Add(Field("행사 종료일", eventEnd));
        facts.Children.Add(Field("보도 희망일", publicationDate));
        facts.Children.Add(Field("탑재 예정 시각", uploadAt));
        facts.Children.Add(Field("대상·인원", targetCount));
        facts.Children.Add(Field("기보도 자료", priorCoverage));
        facts.Children.Add(Field("사진 여부", photo));
        facts.Children.Add(Field("담당 부서·팀", department));
        eventStart.ToolTip = eventEnd.ToolTip = publicationDate.ToolTip = "예: 2026-11-06";
        uploadAt.ToolTip = "예: 2026-11-05 14:30";
        photo.ToolTip = "양식 상단의 필수 표기입니다. 사진이 있으면 초상권·보호자 동의를 확인하세요.";
        department.ToolTip = "예: 교학과 교육과정운영팀 · 양식 머리말에 들어가며 다음 실행에 복원됩니다.";
        facts.Children.Add(Subheading("반려 사유 사전 점검"));
        var rejectionHint = Caption("담당자가 판단한 값을 선택하세요. 모르면 ‘미확인’으로 두고, ‘해당’이 있으면 작성 보류를 검토합니다.");
        rejectionHint.SetResourceReference(TextBlock.ForegroundProperty, Muted);
        rejectionHint.Margin = new(0, 0, 0, 6);
        facts.Children.Add(rejectionHint);
        for (var index = 0; index < rejections.Length; index++)
            facts.Children.Add(Field(Writing.RejectionReasons[index], rejections[index]));
        facts.Children.Add(Subheading("작성 조건 점검"));
        conditionChecks.SetResourceReference(TextBlock.ForegroundProperty, Muted);
        System.Windows.Automation.AutomationProperties.SetName(conditionChecks, "작성 조건 점검");
        facts.Children.Add(conditionChecks);
        var tabs = new TabControl { Name = "SourceTabs" };
        tabs.Items.Add(new TabItem { Header = "원문", Content = original, ToolTip = "행사 개요, 계획서, 현장 결과 등 초안의 근거" });
        var optionsTab = new TabItem { Content = Scroll(facts), ToolTip = "보도 주체, 시점과 작성 유형을 선택" };
        optionsTab.SetResourceReference(HeaderedContentControl.HeaderProperty, "SourceOptionsLabel");
        System.Windows.Automation.AutomationProperties.SetName(optionsTab, "작성 조건");
        tabs.Items.Add(optionsTab);
        Place(panel, tabs, 1);
        return Card(panel, new Thickness(0), raised: true);
    }

    void PasteSource() => RunUi(() =>
    {
        var text = Clipboard.GetText();
        if (!string.IsNullOrWhiteSpace(text)) source.SelectedText = text;
    });

    FrameworkElement DraftPanel()
    {
        var panel = GridRows("auto", "auto", "*", "auto");
        var heading = Heading("초안 작성·편집");
        heading.SetResourceReference(VisibilityProperty, "FullTools");
        Place(panel, heading, 0);
        var draftMenu = Dropdown("초안", "PrimaryAction", true, MenuAction("초안 생성", Generate), MenuAction("맞춤법 검사", ProofreadDraft));
        draftMenu.HorizontalAlignment = HorizontalAlignment.Left;
        draftMenu.SetResourceReference(VisibilityProperty, "CompactTools");
        System.Windows.Automation.AutomationProperties.SetName(draftMenu, "초안 작성과 맞춤법 메뉴");
        Place(panel, draftMenu, 0);
        var actions = new WrapPanel { Margin = new(0, 0, 0, 5) };
        cancel = Action("취소", () => running?.Cancel(), "CancelAction", lockWhileBusy: false);
        cancel.IsEnabled = false;
        var generate = Action("초안 생성", Generate, "PrimaryAction");
        generate.SetResourceReference(VisibilityProperty, "FullTools");
        actions.Children.Add(generate);
        var proofread = Action("맞춤법 검사", ProofreadDraft);
        proofread.SetResourceReference(VisibilityProperty, "FullTools");
        actions.Children.Add(proofread);
        actions.Children.Add(cancel);
        Place(panel, actions, 1);
        draft.BorderThickness = new(0);
        draft.SetResourceReference(Control.BackgroundProperty, "Clear");
        Place(panel, EditorSurface(draft, "초안", "생성된 초안을 여기에서 편집할 수 있습니다."), 2);
        var notice = Caption(Writing.ReviewNotice);
        notice.SetResourceReference(TextBlock.ForegroundProperty, Muted);
        notice.SetResourceReference(TextBlock.FontSizeProperty, "WorkspaceSmallSize");
        notice.ToolTip = Writing.ReviewNotice;
        var noticeScroll = Scroll(notice, 38);
        noticeScroll.Margin = new(0, 6, 0, 0);
        Place(panel, noticeScroll, 3);
        return Card(panel, new Thickness(0, 0, 14, 0), raised: true);
    }

    FrameworkElement ReviewPanel()
    {
        var panel = GridRows("auto", "*");
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Heading("검토"));
        reviewHide = Action("숨기기", () => SetReviewVisible(false), lockWhileBusy: false);
        reviewHide.ToolTip = "검토 칸을 접어 편집 공간을 넓힙니다. 오른쪽 가장자리의 ‘검토 보기’로 다시 폅니다.";
        reviewHide.Padding = new(8, 2, 8, 2);
        reviewHide.Margin = new(8, 0, 0, 8);
        reviewHide.VerticalAlignment = VerticalAlignment.Top;
        System.Windows.Automation.AutomationProperties.SetName(reviewHide, "검토 칸 숨기기");
        Grid.SetColumn(reviewHide, 1);
        header.Children.Add(reviewHide);
        Place(panel, header, 0);
        var content = new StackPanel();
        content.SetResourceReference(System.Windows.Documents.TextElement.FontSizeProperty, "WorkspaceSmallSize");
        var checksBlock = new Border
        {
            BorderThickness = new(0, 0, 0, 1), Padding = new(0, 0, 0, 12), Child = checks
        };
        checksBlock.SetResourceReference(Border.BorderBrushProperty, Line);
        content.Children.Add(checksBlock);
        content.Children.Add(Subheading("보도 일정"));
        content.Children.Add(Caption("보도 희망일 전날 15시까지 탑재\n행사 당일 또는 익일 보도\n행사 후 3일 이상 경과 시 반려"));
        content.Children.Add(Subheading("적용 지침"));
        var guidance = Caption(Writing.GuidelineReference);
        guidance.SetResourceReference(TextBlock.ForegroundProperty, Muted);
        content.Children.Add(guidance);

        Place(panel, Scroll(content), 1);
        var surface = Card(panel, new Thickness(0), padding: 0);
        surface.Padding = new(0, 12, 0, 0);
        return surface;
    }

    FrameworkElement ReviewStrip()
    {
        reviewShow = Action("검토 보기", () => SetReviewVisible(true), lockWhileBusy: false);
        // Upright characters stacked in a column read naturally on a vertical strip; rotated text does not.
        reviewShow.Content = new TextBlock { Text = "검\n토\n보\n기", TextAlignment = TextAlignment.Center, LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        reviewShow.ToolTip = "접어 둔 검토 칸을 다시 폅니다.";
        reviewShow.Padding = new(5, 9, 5, 9);
        reviewShow.Margin = new(6, 10, 0, 0);
        reviewShow.VerticalAlignment = VerticalAlignment.Top;
        System.Windows.Automation.AutomationProperties.SetName(reviewShow, "검토 칸 보이기");
        return reviewShow;
    }

    void SetReviewVisible(bool visible)
    {
        columns.SetReviewVisible(visible);
        Persist();
    }

    async void LoadSource()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "원자료 (*.txt;*.md;*.hwpx;*.pdf)|*.txt;*.md;*.hwpx;*.pdf",
            Title = "원자료 열기", CheckFileExists = true, Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        await ImportSourceFiles(dialog.FileNames);
    }

    void SourceDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)) return;
        e.Effects = running == null ? e.AllowedEffects & DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void SourceDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)) return;
        e.Handled = true;
        if (running != null || (e.AllowedEffects & DragDropEffects.Copy) == 0) return;
        if (e.Data.GetData(DataFormats.FileDrop, autoConvert: false) is string[] { Length: > 0 } files)
        {
            e.Effects = DragDropEffects.Copy;
            await ImportSourceFiles(files);
        }
    }

    Task ImportSourceFiles(string[] files) => Busy(async token =>
    {
        var existing = source.Text;
        var (combined, notes) = await Task.Run(() =>
        {
            var text = new StringBuilder(string.IsNullOrWhiteSpace(existing) ? "" : existing);
            var messages = new List<string>();
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var result = SourceFiles.Read(file);
                if (text.Length > 0) text.Append("\n\n");
                if (text.Length + result.Text.Length > SourceFiles.MaxInput)
                    throw new InvalidDataException("원자료가 50,000자를 넘습니다. 필요한 부분을 별도로 붙여넣어 주세요.");
                text.Append(result.Text);
                messages.Add(result.Note);
            }
            return (text.ToString(), string.Join(" · ", messages.Distinct()));
        }, token);
        token.ThrowIfCancellationRequested();
        source.Text = combined;
        var label = files.Length == 1 ? Path.GetFileName(files[0]) : $"파일 {files.Length}개를 불러왔습니다.";
        SetStatus($"{label} · {notes}", StatusNeutral);
    }, affectsConnection: false);

    async void Generate()
    {
        if (running != null || !RequireAiConsent()) return;
        string prompt;
        try { prompt = Writing.Prompt(source.Text.Trim(), CurrentContext()); }
        catch (Exception ex) { SetStatus(ex.Message, StatusError); return; }
        var model = Selected;
        if (!CanReplaceDraft()) return;
        await Busy(async token =>
        {
            SetStatus($"{Models.Label(model)}에서 초안 작성 중…", StatusNeutral);
            var response = await Models.GenerateAsync(model, appFolder, prompt, token,
                model == ModelConnection.CodexCli ? Codex : null, googleProject, geminiApiKey);
            token.ThrowIfCancellationRequested();
            draft.Text = response;
            MarkAiResponded(model);
            SetStatus("초안을 가져왔습니다. 원자료와 대조한 뒤 저장해 주세요.", StatusSuccess);
        });
    }

    async void ProofreadDraft()
    {
        if (running != null || !RequireAiConsent()) return;
        var original = draft.Text;
        string prompt;
        try { prompt = Proofreading.Prompt(original); }
        catch (Exception ex) { SetStatus(UserMessage(ex), StatusError); return; }
        var model = Selected;
        string? response = null;
        await Busy(async token =>
        {
            SetStatus("맞춤법과 띄어쓰기를 검사하고 있습니다…", StatusNeutral);
            var completed = await Models.CompleteAsync(model, appFolder, prompt, token,
                model == ModelConnection.CodexCli ? Codex : null, googleProject, geminiApiKey);
            token.ThrowIfCancellationRequested();
            MarkAiResponded(model);
            response = completed;
        });
        if (response != null && !closingInProgress) RunUi(() => ReviewProofreading(original, response));
    }

    void ReviewProofreading(string original, string response)
    {
        if (draft.Text != original) throw new InvalidOperationException("검사 후 초안이 변경됐습니다. 현재 초안으로 맞춤법 검사를 다시 실행해 주세요.");
        var result = Proofreading.Parse(original, response);
        var skippedNote = result.Skipped.Count == 0 ? "" : $" 규칙에 맞지 않는 제안 {result.Skipped.Count}건은 적용하지 않았습니다.";
        if (result.Edits.Count == 0)
        {
            SetStatus($"맞춤법 검사 완료 · 적용할 수정 제안이 없습니다.{skippedNote} 고유명사와 인용문은 원자료와 대조해 주세요.", StatusSuccess);
            return;
        }
        var dialog = new ProofreadingDialog(result, appearance) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ApplyProofreading(result);
            SetStatus($"맞춤법 수정 {result.Edits.Count}건을 적용했습니다.{skippedNote} Ctrl+Z로 되돌릴 수 있습니다. 확인 후 초안을 저장하세요.", StatusSuccess);
        }
        else SetStatus("맞춤법 수정안을 적용하지 않았습니다. 기존 초안은 유지됩니다.", StatusNeutral);
    }

    void ApplyProofreading(ProofreadingResult result)
    {
        if (draft.Text != result.Original) throw new InvalidOperationException("초안이 변경됐습니다. 맞춤법 검사를 다시 실행해 주세요.");
        draft.BeginChange();
        try
        {
            draft.SelectAll();
            draft.SelectedText = result.Corrected;
            draft.Select(0, 0);
        }
        finally { draft.EndChange(); }
    }

    WritingContext CurrentContext() => new(institutions.Text, tenses.Text, modes.SelectedIndex == 1,
        officialName.Text, eventStart.Text, eventEnd.Text, publicationDate.Text, uploadAt.Text, targetCount.Text, priorCoverage.Text,
        photo.SelectedItem as string, department.Text,
        Writing.RejectionReasons.Select((reason, index) => new RejectionCheck(reason, rejections[index].SelectedItem as string ?? Writing.Unverified)).ToArray());

    void UpdateConditionChecks() => conditionChecks.Text = Writing.ContextCheck(CurrentContext());

    bool CanReplaceDraft() => !draftDirty || string.IsNullOrWhiteSpace(draft.Text) ||
        MessageBox.Show(this, "편집 중인 초안을 새 초안으로 바꿀까요?", Title, MessageBoxButton.YesNo) == MessageBoxResult.Yes;

    bool TryPickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "작업 폴더를 선택해 주세요." };
        if (Directory.Exists(folder)) dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) != true) return false;
        folder = Path.GetFullPath(dialog.FolderName);
        UpdateFolderLabel();
        Persist();
        return true;
    }

    void SaveDraft()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(draft.Text)) throw new InvalidDataException("저장할 초안을 입력해 주세요.");
            var txt = saveTxt.IsChecked == true;
            var md = saveMd.IsChecked == true;
            var hwpx = saveHwpx.IsChecked == true;
            if (!txt && !md && !hwpx) throw new InvalidOperationException("TXT, MD 또는 HWPX 양식을 하나 이상 선택해 주세요.");
            if (!Directory.Exists(folder) && !TryPickFolder())
            {
                SetStatus("저장이 취소됐습니다. 작업 폴더를 선택한 뒤 다시 저장해 주세요.", StatusNeutral);
                return;
            }
            var destination = Path.GetFullPath(folder);
            var files = Writing.Save(destination, draft.Text, txt, md, hwpx ? Writing.FormFields(draft.Text, CurrentContext()) : null);
            draftDirty = false;
            var formNote = hwpx ? "\n양식의 자료문의 이름·연락처는 한글에서 직접 기재해 주세요." : "";
            SetStatus($"저장 완료 · {files.Replace("\n", " · ")}\n저장 위치: {destination}{formNote}", StatusSuccess);
        }
        catch (Exception ex)
        {
            var details = $"초안 저장 실패 · {UserMessage(ex)}";
            if (!string.IsNullOrWhiteSpace(folder)) details += $"\n작업 폴더: {folder}";
            SetStatus(details, StatusError);
            if (IsVisible)
                MessageBox.Show(this, details, "초안 저장 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    async Task Busy(Func<CancellationToken, Task> action, bool affectsConnection = true)
    {
        if (running != null) return;
        using var tokenSource = new CancellationTokenSource();
        running = tokenSource;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runningCompletion = completion;
        foreach (var control in whileBusy) control.IsEnabled = false;
        cancel.IsEnabled = true;
        connections?.SetBusy(true);
        // The model selector is locked while busy, so Selected is the provider this work used.
        // Only a Codex failure or cancellation needs the app-server restarted; other providers leave it untouched.
        var usedCodex = affectsConnection && Selected == ModelConnection.CodexCli;
        try { await action(tokenSource.Token); }
        catch (OperationCanceledException)
        {
            SetStatus("작업을 취소했습니다.", StatusNeutral);
            if (usedCodex) await ResetCodex();
        }
        catch (Exception ex)
        {
            SetStatus(UserMessage(ex), StatusError);
            if (affectsConnection)
            {
                if (ex is FileNotFoundException or GeminiApiException { KeyRejected: true }
                    or CliExecutionException { Kind: CliFailureKind.Authentication or CliFailureKind.Installation })
                    SetConnectionStatus(Selected, ConnectionFailure(Selected, ex));
                if (usedCodex) await ResetCodex();
            }
        }
        finally
        {
            running = null;
            foreach (var control in whileBusy) control.IsEnabled = true;
            cancel.IsEnabled = false;
            connections?.ShowDeviceCode(null);
            connections?.SetBusy(false);
            completion.TrySetResult();
        }
    }

    static string UserMessage(Exception ex) => ex is JsonException ? "CLI 응답 형식을 읽지 못했습니다. CLI 업데이트 후 다시 시도해 주세요." : ex.Message;

    void RunUi(Action action)
    {
        try { action(); }
        catch (Exception ex) { SetStatus(UserMessage(ex), StatusError); }
    }

    void UpdateConnectionButtons()
    {
        var transmission = externalAiConsent ? "전송 켜짐" : "전송 꺼짐";
        settingsButton.ToolTip = $"AI 연결과 화면 설정\n{transmission}";
        connectionState.Text = $"{transmission}\n{ConnectionStatus(Selected).Text}";
        connectionState.ToolTip = connectionState.Text;
    }

    void UpdateFolderLabel()
    {
        folderLabel.Text = string.IsNullOrEmpty(folder) ? "작업 폴더 미지정" : folder;
        folderLabel.ToolTip = folderLabel.Text;
    }

    void SetStatus(string text, string tone)
    {
        status.Text = text;
        status.ToolTip = text;
        statusBar.SetResourceReference(Border.BackgroundProperty, tone);
        status.SetResourceReference(TextBlock.ForegroundProperty, tone == StatusError ? "ErrorInk" : Ink);
        connections?.SetMessage(text, tone == StatusError);
    }

    Options LoadSettings()
    {
        try
        {
            var path = Path.Combine(appFolder, "settings.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<Options>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            // Settings are cosmetic. An unreadable file must never prevent the app from opening.
            return new();
        }
    }

    void Persist()
    {
        if (!ready) return;
        RunUi(() => File.WriteAllText(Path.Combine(appFolder, "settings.json"), JsonSerializer.Serialize(
            new Options(folder, Selected, googleProject, appearance.IsDark, appearance.FontPoints, columns.SourceShare,
                string.IsNullOrWhiteSpace(department.Text) ? null : department.Text.Trim(), columns.ReviewVisible))));
    }

    Button Action(string title, Action action, string? style = null, bool lockWhileBusy = true)
    {
        var button = new Button { Content = title };
        if (style != null) button.SetResourceReference(StyleProperty, style);
        button.Click += (_, _) => action();
        if (lockWhileBusy) whileBusy.Add(button);
        return button;
    }

    MenuItem MenuAction(string title, Action action, bool lockWhileBusy = true)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => action();
        if (lockWhileBusy) whileBusy.Add(item);
        return item;
    }

    Button Dropdown(string title, string? style, bool lockWhileBusy, params Control[] items)
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Resources.MergedDictionaries.Add(appearance.Resources);
        foreach (var item in items) menu.Items.Add(item);
        var button = Action(title + "  ▾", () => menu.IsOpen = true, style, lockWhileBusy);
        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        return button;
    }

    static TextBox Editor()
    {
        var editor = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new(13, 11, 13, 11), UndoLimit = 50
        };
        editor.SetResourceReference(Control.FontSizeProperty, "EditorFontSize");
        return editor;
    }

    static FrameworkElement EditorSurface(TextBox editor, string name, string placeholder)
    {
        System.Windows.Automation.AutomationProperties.SetName(editor, name);
        var surface = new Grid();
        surface.Children.Add(editor);
        var hint = Caption(placeholder);
        hint.Margin = new(13, 12, 13, 0);
        hint.VerticalAlignment = VerticalAlignment.Top;
        hint.SetResourceReference(TextBlock.ForegroundProperty, Muted);
        hint.SetResourceReference(TextBlock.FontSizeProperty, "EditorFontSize");
        hint.IsHitTestVisible = false;
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        var empty = new DataTrigger
        {
            Binding = new System.Windows.Data.Binding(nameof(TextBox.Text)) { Source = editor },
            Value = ""
        };
        empty.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible));
        style.Triggers.Add(empty);
        hint.Style = style;
        surface.Children.Add(hint);
        return surface;
    }

    static FrameworkElement Field(string label, Control editor)
    {
        System.Windows.Automation.AutomationProperties.SetName(editor, label);
        var row = new StackPanel { Margin = new(0, 0, 0, 8) };
        var caption = Caption(label);
        caption.Margin = new(0, 0, 0, 3);
        caption.SetResourceReference(TextBlock.ForegroundProperty, Muted);
        row.Children.Add(caption);
        row.Children.Add(editor);
        return row;
    }

    static Viewbox SuccessSlogan()
    {
        var slogan = BrandBitmap("student-success-slogan.jpg");
        // The supplied artwork has two rows. Display those source regions side by side.
        var lead = new CroppedBitmap(slogan, new Int32Rect(450, 736, 1183, 299));
        var tail = new CroppedBitmap(slogan, new Int32Rect(114, 1068, 1857, 281));
        var line = new StackPanel { Name = "StudentSuccessSlogan", Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(BrandImage(lead, 21, "읽걷쓰 AI로"));
        var ending = BrandImage(tail, 21, "학생성공시대 완성");
        ending.Margin = new(5.25, 0, 0, 0);
        line.Children.Add(ending);

        return new Viewbox
        {
            Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Child = line
        };
    }

    static BitmapImage BrandBitmap(string file) => new(new Uri($"pack://application:,,,/PressReleaseWriter;component/Assets/{file}"));

    static Image BrandImage(BitmapSource bitmap, double height, string label)
    {
        var image = new Image { Source = bitmap, Height = height, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(image, label);
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    static TextBlock Caption(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    static TextBlock Subheading(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 5)
    };

    static FrameworkElement Heading(string title)
    {
        var heading = new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center
        };
        heading.SetResourceReference(TextBlock.FontSizeProperty, "WorkspaceHeadingSize");
        return heading;
    }

    static ScrollViewer Scroll(UIElement content, double maxHeight = double.PositiveInfinity) => new()
    {
        Content = content, MaxHeight = maxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly
    };

    static Border Card(UIElement child, Thickness margin, double padding = 12, bool raised = false)
    {
        var border = new Border
        {
            BorderThickness = raised ? new(1) : new(0), CornerRadius = raised ? new(6) : new(0),
            Padding = new(padding), Margin = margin, Child = child
        };
        if (raised)
        {
            border.SetResourceReference(Border.BackgroundProperty, "Glass");
            border.SetResourceReference(Border.BorderBrushProperty, "GlassLine");
        }
        return border;
    }

    static Grid GridRows(params string[] rows)
    {
        var grid = new Grid();
        foreach (var row in rows)
            grid.RowDefinitions.Add(new RowDefinition { Height = row == "*" ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        return grid;
    }

    static void Place(Grid grid, UIElement element, int row, int column = 0)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    sealed record Options(string? Folder = null, ModelConnection Model = ModelConnection.CodexCli, string? GoogleProject = null,
        bool DarkMode = false, double FontPoints = WorkspaceAppearance.DefaultFontPoints, double SourceShare = EditorColumns.DefaultSourceShare,
        string? Department = null, bool ReviewVisible = true);
    sealed record ModelItem(ModelConnection Connection)
    {
        public override string ToString() => Models.Label(Connection);
    }
}
