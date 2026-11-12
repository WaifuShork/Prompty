using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.AspNetCore.Builder;
using Microsoft.Win32;
using Prompty.Services;

namespace Prompty;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm = new();
    readonly AppSettings _settings = AppSettings.Load();
    readonly PromptBridge _bridge = new();
    readonly LocalModelClient _model;
    readonly PromptAssistant _assistant;

    readonly DispatcherTimer _analyzeTimer = new();
    readonly DispatcherTimer _contextTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    FileSystemWatcher? _watcher;
    FileSystemWatcher? _registryWatcher;
    string _renderedOutput = "";
    string? _renderedSessionId;
    FoldingManager _outputFolds = null!;
    readonly DiffHighlighter _diff = new();
    // Fold state survives re-renders, keyed by tool_use_id. In "verbose" mode (Ctrl+O) folds start open.
    readonly HashSet<string> _openFolds = [];
    readonly HashSet<string> _closedFolds = [];
    bool _verbose;
    WebApplication? _mcp;
    CancellationTokenSource? _analyzeCts;

    // One draft per Claude session: its own document (and undo history), generator request and cards.
    readonly Dictionary<string, DraftState> _drafts = [];
    DraftState _draft = null!;
    bool _switchingDraft;
    DateTime _lastUserEdit = DateTime.MinValue;
    string? _legacyDraft;
    bool _suppressSessionChange;
    string? _projectDir;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _model = new LocalModelClient(_settings);
        _assistant = new PromptAssistant(_model);

        _vm.AutoAnalyze = _settings.AutoAnalyze;
        _vm.ModelName = _settings.Model;

        _analyzeTimer.Interval = TimeSpan.FromMilliseconds(_settings.AutoAnalyzeDelayMs);
        _analyzeTimer.Tick += (_, _) => { _analyzeTimer.Stop(); if (_vm.AutoAnalyze) _ = AnalyzeAsync(auto: true); };
        _contextTimer.Tick += async (_, _) => { _contextTimer.Stop(); await RefreshContextAsync(); };
        _autosaveTimer.Tick += (_, _) => { _autosaveTimer.Stop(); SaveDraft(); };

        SetupEditor();
        Loaded += OnLoaded;
        Closing += (_, _) => { SaveDraft(); _settings.Save(); _ = _mcp?.DisposeAsync(); };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ───────────────────────────── setup ─────────────────────────────

    void SetupEditor()
    {
        Editor.SyntaxHighlighting = LoadHighlighting("Markdown");
        Output.SyntaxHighlighting = LoadHighlighting("Output");
        Output.Options.EnableHyperlinks = true;
        Output.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xD9, 0xA2, 0x7A));
        Output.TextArea.SelectionForeground = null;
        Output.TextArea.TextView.LinkTextForegroundBrush = Brushes.LightSkyBlue;

        // Collapsible tool output and diffs (click a summary line, the margin, or Ctrl+O).
        _outputFolds = FoldingManager.Install(Output.TextArea);
        Output.SetValue(FoldingMargin.FoldingMarkerBrushProperty, new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x80)));
        Output.SetValue(FoldingMargin.FoldingMarkerBackgroundBrushProperty, new SolidColorBrush(Color.FromRgb(0x16, 0x17, 0x1B)));
        Output.SetValue(FoldingMargin.SelectedFoldingMarkerBrushProperty, new SolidColorBrush(Color.FromRgb(0xD9, 0xA2, 0x7A)));
        Output.SetValue(FoldingMargin.SelectedFoldingMarkerBackgroundBrushProperty, new SolidColorBrush(Color.FromRgb(0x25, 0x27, 0x2E)));
        Output.TextArea.TextView.BackgroundRenderers.Add(_diff);
        Output.TextArea.TextView.LineTransformers.Add(_diff);
        // AvalonEdit's selection handler marks mouse-up as handled, so listen to handled events too.
        // Only presses that start on the text count: the fold margin toggles on its own mouse-down,
        // and treating its mouse-up as a line click would immediately toggle the block back.
        Output.TextArea.AddHandler(PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((_, e) => _pressStartedOnText = IsWithinTextView(e.OriginalSource)), handledEventsToo: true);
        Output.TextArea.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(Output_MouseLeftButtonUp), handledEventsToo: true);

        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xD9, 0xA2, 0x7A));
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.SelectionCornerRadius = 2;
        Editor.TextArea.Caret.CaretBrush = Brushes.White;
        Editor.TextArea.TextView.LinkTextForegroundBrush = Brushes.LightSkyBlue;

        // Drafts load per session once the session list is read. Before per-session drafts existed there
        // was a single draft.md; it moves to the first session opened.
        if (!Directory.Exists(DraftState.Dir) && File.Exists(AppSettings.DraftPath))
            _legacyDraft = File.ReadAllText(AppSettings.DraftPath);
        _draft = new DraftState { Key = DraftState.NoKey, Doc = Editor.Document };
        Editor.TextChanged += OnEditorTextChanged;
        GeneratorInput.TextChanged += (_, _) =>
        {
            if (_switchingDraft) return;
            _draft.GeneratorText = GeneratorInput.Text;
            _draft.Dirty = true;
            _lastUserEdit = DateTime.Now;
            _autosaveTimer.Stop(); _autosaveTimer.Start();
        };
        OnEditorTextChanged(null, EventArgs.Empty);
        _analyzeTimer.Stop();
    }

    static IHighlightingDefinition LoadHighlighting(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Prompty.Resources.{name}.xshd")!;
        using var reader = XmlReader.Create(s);
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Editor.Focus();

        _bridge.ContextShared += text => Dispatcher.Invoke(() =>
        {
            _vm.SharedContext = text;
            _vm.Activity = "Claude shared conversation context";
            if (_vm.AutoAnalyze) _analyzeTimer.Start();
        });
        _bridge.DraftSeeded += text => Dispatcher.Invoke(() => OnDraftSeeded(text));
        _bridge.WaitersChanged += () => Dispatcher.Invoke(UpdateWaitingBanner);

        try
        {
            _mcp = await McpHost.StartAsync(_bridge, _settings.McpPort);
            _vm.McpOk = true;
            _vm.McpStatus = $"MCP ● {_settings.McpUrl}";
        }
        catch (Exception ex)
        {
            _vm.McpStatus = $"MCP offline ({ex.GetBaseException().Message})";
        }

        StartWatchingTranscripts();
        _ = CheckForUpdatesOnLaunchAsync();
        await RefreshContextAsync();
        await CheckModelAsync();

        // Keep retrying until the model server is up (e.g. Ollama installed or started later).
        var recheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        recheck.Tick += async (_, _) => { if (!_vm.ModelOk && _vm.BusyCount == 0) await CheckModelAsync(); };
        recheck.Start();
    }

    void StartWatchingTranscripts()
    {
        FileSystemEventHandler restart = (_, _) => Dispatcher.BeginInvoke(() => { _contextTimer.Stop(); _contextTimer.Start(); });
        FileSystemWatcher? Watch(string dir, string filter, bool recursive)
        {
            if (!Directory.Exists(dir)) return null;
            var w = new FileSystemWatcher(dir, filter)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            w.Changed += restart;
            w.Created += restart;
            w.Deleted += restart;
            w.EnableRaisingEvents = true;
            return w;
        }
        _watcher = Watch(ClaudeSessions.ProjectsDir, "*.jsonl", true);
        // Running sessions (and their busy/idle status) live in a separate registry.
        _registryWatcher = Watch(ClaudeSessions.RegistryDir, "*.json", false);
    }

    // ───────────────────────────── conversation context ─────────────────────────────

    async Task RefreshContextAsync()
    {
        var sessions = await Task.Run(() => ClaudeSessions.ListRecent());
        var keepId = _vm.SelectedSession?.SessionId;

        _suppressSessionChange = true;
        _vm.Sessions.Clear();
        foreach (var s in sessions) _vm.Sessions.Add(s);
        // "Follow latest" holds still while you're typing in (or generating) the current session's draft,
        // so the editor isn't swapped out from under you.
        var holdDraft = _draft.GenerateCts != null || DateTime.Now - _lastUserEdit < TimeSpan.FromSeconds(30);
        _vm.SelectedSession = _vm.FollowLatest && !(holdDraft && sessions.Any(x => x.SessionId == keepId))
            ? sessions.FirstOrDefault()
            : sessions.FirstOrDefault(s => s.SessionId == keepId) ?? sessions.FirstOrDefault();
        _suppressSessionChange = false;

        await LoadSelectedSessionAsync();
    }

    async Task LoadSelectedSessionAsync()
    {
        var session = _vm.SelectedSession;
        SwitchDraft(session?.SessionId ?? "");
        if (session == null)
        {
            _vm.Digest = $"No Claude Code sessions found in {ClaudeSessions.ProjectsDir}.\n\n" +
                         "Prompty reads Claude Code transcripts automatically. Claude can also push context with the share_context MCP tool.";
            _vm.TurnInfo = "";
            _vm.OutputHeader = "No session selected";
            ShowOutput(null, RenderedOutput.FromText(""));
            UpdateActivity(null, null);
            return;
        }
        var (snapshot, rendered, activity) = await Task.Run(() => (ClaudeSessions.Load(session),
            ClaudeSessions.RenderOutput(session.FilePath), ClaudeSessions.GetTurnActivity(session.FilePath)));
        if (_vm.SelectedSession?.SessionId != session.SessionId) return; // selection moved on meanwhile
        UpdateActivity(session, activity);

        _projectDir = snapshot.Cwd;
        _vm.Digest = snapshot.ToDigest(_settings.ContextMaxChars);
        _vm.TurnInfo = $"{snapshot.Turns.Count} turns";
        _vm.OutputHeader = $"{session.Title}  ·  {session.Detail}";
        ShowOutput(session.SessionId, rendered.Text.Length > 0 ? rendered : RenderedOutput.FromText("(No messages in this session yet.)\n"));
    }

    // ───────────────────────────── activity status line ─────────────────────────────

    static readonly string[] SpinFrames = ["·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢"];
    static readonly string[] Verbs =
    [
        "Sketching", "Pondering", "Thinking", "Musing", "Brewing", "Crafting", "Noodling", "Percolating",
        "Cogitating", "Conjuring", "Mulling", "Tinkering", "Wrangling", "Forging", "Simmering", "Deliberating",
        "Synthesizing", "Puzzling", "Whirring", "Scheming",
    ];
    static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xA2, 0x7A));

    readonly DispatcherTimer _spinTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    TurnActivity? _activity;
    bool _activityBusy;
    int _spinFrame;

    /// <summary>
    /// While the selected session is working: a spinner, a verb picked per turn, elapsed time and output
    /// tokens. Once it's idle: how long the last turn took.
    /// </summary>
    void UpdateActivity(SessionInfo? session, TurnActivity? activity)
    {
        _activity = activity;
        _activityBusy = activity != null && session is { IsLive: true, IsBusy: true };
        if (_spinTimer.IsEnabled != _activityBusy)
        {
            if (_activityBusy)
            {
                _spinTimer.Tick -= SpinTick;
                _spinTimer.Tick += SpinTick;
                _spinTimer.Start();
            }
            else _spinTimer.Stop();
        }
        RenderActivity();
    }

    void SpinTick(object? sender, EventArgs e) { _spinFrame++; RenderActivity(); }

    void RenderActivity()
    {
        var a = _activity;
        if (a == null || (!_activityBusy && a.LastActivity <= a.Start))
        {
            ActivityBar.Visibility = Visibility.Collapsed;
            return;
        }
        ActivityBar.Visibility = Visibility.Visible;
        var tokens = a.OutputTokens > 0 ? $" · ↓ {FormatTokens(a.OutputTokens)} tokens" : "";
        if (_activityBusy)
        {
            ActivityGlyph.Text = SpinFrames[_spinFrame % SpinFrames.Length] + " ";
            ActivityGlyph.Foreground = ActivityVerb.Foreground = AccentBrush;
            ActivityVerb.Text = Verbs[(int)(a.Start.Ticks / TimeSpan.TicksPerSecond % Verbs.Length)] + "…";
            ActivityStats.Text = $"  ({FormatDuration(DateTime.Now - a.Start)}{tokens})";
        }
        else
        {
            ActivityGlyph.Text = "✻ ";
            ActivityGlyph.Foreground = ActivityVerb.Foreground = (Brush)FindResource("MutedText");
            ActivityVerb.Text = $"Worked for {FormatDuration(a.LastActivity - a.Start)}";
            ActivityStats.Text = tokens;
        }
    }

    static string FormatDuration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{(int)t.TotalSeconds}s";
    }

    static string FormatTokens(int n) => n < 1000 ? n.ToString() : $"{n / 1000.0:0.#}k";

    /// <summary>
    /// Updates the output pane. Only the changed tail of the document is replaced, so the scroll position
    /// holds; folds are rebuilt with each tool block's open/closed state carried over.
    /// </summary>
    void ShowOutput(string? sessionId, RenderedOutput rendered)
    {
        var view = Output.TextArea.TextView;
        var atBottom = view.VerticalOffset + view.ActualHeight >= view.DocumentHeight - 40;
        var sameSession = sessionId == _renderedSessionId;
        var text = rendered.Text;

        if (sameSession) CaptureFoldState();
        else { _openFolds.Clear(); _closedFolds.Clear(); }
        if (sameSession && text == _renderedOutput) return;

        var scroll = view.VerticalOffset;
        if (sameSession)
        {
            var p = CommonPrefixLength(_renderedOutput, text);
            // A caret inside the rewritten tail would be pushed to the end of the document, and the
            // editor then keeps scrolling to it. Park it before the change instead.
            if (Output.CaretOffset >= p) Output.CaretOffset = 0;
            Output.Document.Replace(p, Output.Document.TextLength - p, text[p..]);
        }
        else
        {
            Output.Document.Text = text;
            Output.CaretOffset = 0;
        }

        _diff.Lines = rendered.DiffLines;
        _outputFolds.Clear();
        foreach (var f in rendered.Folds)
        {
            if (f.End > Output.Document.TextLength) continue;
            var section = _outputFolds.CreateFolding(f.Start, f.End);
            section.Title = $" … +{f.LineCount} lines ";
            section.Tag = f.Id;
            section.IsFolded = _verbose ? _closedFolds.Contains(f.Id) : !_openFolds.Contains(f.Id);
        }

        _renderedOutput = text;
        _renderedSessionId = sessionId;
        if (!sameSession || (_vm.AutoScroll && atBottom)) Output.ScrollToEnd();
        else Output.ScrollToVerticalOffset(scroll);
        view.InvalidateLayer(KnownLayer.Background);
    }

    void CaptureFoldState()
    {
        foreach (var f in _outputFolds.AllFoldings)
        {
            if (f.Tag is not string id) continue;
            if (f.IsFolded) { _openFolds.Remove(id); _closedFolds.Add(id); }
            else { _openFolds.Add(id); _closedFolds.Remove(id); }
        }
    }

    static int CommonPrefixLength(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    /// <summary>Clicking a tool's header or summary line toggles its collapsed output, like Ctrl+O per block.</summary>
    void Output_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressStartedOnText) return;
        _pressStartedOnText = false;
        // Mouse-down already moved the caret to the clicked line; a drag-selection isn't a toggle.
        if (Output.SelectionLength == 0) ToggleFoldAtCaret();
    }

    bool _pressStartedOnText;

    bool IsWithinTextView(object source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d == Output.TextArea.TextView) return true;
            if (d is AbstractMargin || d == Output.TextArea) return false;
        }
        return false;
    }

    bool ToggleFoldAtCaret()
    {
        var line = Output.Document.GetLineByOffset(Math.Min(Output.CaretOffset, Output.Document.TextLength));
        var fold = FoldEndingLine(line) ?? (line.NextLine is { } next ? FoldEndingLine(next) : null);
        if (fold == null) return false;
        fold.IsFolded = !fold.IsFolded;
        return true;
    }

    FoldingSection? FoldEndingLine(DocumentLine line) =>
        _outputFolds.GetFoldingsAt(line.EndOffset).FirstOrDefault();

    void SetAllFolds(bool expanded)
    {
        _verbose = expanded;
        _openFolds.Clear();
        _closedFolds.Clear();
        foreach (var f in _outputFolds.AllFoldings) f.IsFolded = !expanded;
        _vm.Activity = expanded ? "Showing all tool output (Ctrl+O to collapse)" : "Tool output collapsed (Ctrl+O to expand)";
    }

    void ExpandAll_Click(object sender, RoutedEventArgs e) => SetAllFolds(true);
    void CollapseAll_Click(object sender, RoutedEventArgs e) => SetAllFolds(false);

    string BuildContext()
    {
        var sb = new StringBuilder();
        if (_vm.HasSharedContext)
            sb.Append("## Summary shared by Claude\n").Append(_vm.SharedContext).Append("\n\n## Recent transcript\n");
        sb.Append(_vm.Digest);
        var s = sb.ToString();
        return s.Length > _settings.ContextMaxChars + 4000 ? s[^(_settings.ContextMaxChars + 4000)..] : s;
    }

    async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSessionChange || e.AddedItems.Count == 0) return;
        if (_vm.SelectedSession != _vm.Sessions.FirstOrDefault()) _vm.FollowLatest = false;
        await LoadSelectedSessionAsync();
    }

    async void RefreshContext_Click(object sender, RoutedEventArgs e) => await RefreshContextAsync();
    async void FollowLatest_Click(object sender, RoutedEventArgs e) { if (_vm.FollowLatest) await RefreshContextAsync(); }
    void ClearShared_Click(object sender, RoutedEventArgs e) => _vm.SharedContext = null;

    // ───────────────────────────── model ─────────────────────────────

    async Task CheckModelAsync()
    {
        var provider = _settings.ProviderInfo;
        _vm.UsingChatGptPlan = provider.UsesAccountSignIn;
        if (provider.IsCloud)
        {
            await CheckCloudAsync(provider);
            return;
        }
        try
        {
            var models = await _model.ListModelsAsync();
            var current = _vm.ModelName;
            _vm.Models.Clear();
            foreach (var m in models) _vm.Models.Add(m);
            _vm.ModelName = current;

            bool Installed(string name) => models.Any(m => m == name || m == name + ":latest" || m.Split(':')[0] == name);
            if (models.Count == 0)
            {
                _vm.ModelOk = false;
                _vm.ModelStatus = $"{_settings.ProviderInfo.Name}: no models installed";
                ShowSetupCard($"{_settings.ProviderInfo.Name} is running but has no models. Pull a small, fast instruct model:",
                    $"ollama pull {_settings.Model}");
            }
            else if (!Installed(_settings.Model))
            {
                _vm.ModelOk = false;
                _vm.ModelStatus = $"Model '{_settings.Model}' not installed";
                ShowSetupCard($"'{_settings.Model}' isn't installed. Pull it, or pick one of your installed models in the toolbar:",
                    $"ollama pull {_settings.Model}");
            }
            else
            {
                _vm.ModelOk = true;
                _vm.ModelStatus = $"{_settings.ProviderInfo.Name} ● {_settings.Model}";
                _setupShownFor = null;
                RemoveSetupCards();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or PlanLimitException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _vm.ModelOk = false;
            _vm.ModelStatus = $"{_settings.ProviderInfo.Name} not reachable at {_settings.BaseUrl}";
            ShowSetupCard(
                $"Prompty uses a local model for analysis. Couldn't reach {_settings.ProviderInfo.Name} at {_settings.BaseUrl}. " +
                "Install Ollama and pull a small instruct model (qwen2.5:3b is fast and handles JSON well). Prompty reconnects automatically:",
                $"winget install Ollama.Ollama\nollama pull {_settings.Model}");
        }
    }

    /// <summary>Cloud providers: a key must be present and the API must accept it.</summary>
    async Task CheckCloudAsync(ProviderInfo provider)
    {
        var name = provider.Name;
        if (provider.UsesAccountSignIn)
        {
            var account = ChatGptAuth.Account;
            if (account == null)
            {
                _vm.ModelOk = false;
                _vm.ModelStatus = "☁ ChatGPT plan: not signed in";
                ShowSetupCard("Prompty is set to use your ChatGPT plan but isn't signed in. Open ⚙ Settings and click Continue with ChatGPT.",
                    "", "Sign in needed");
                return;
            }
            if (!account.HasPlanUsage)
            {
                _vm.ModelOk = false;
                _vm.ModelStatus = "☁ ChatGPT plan: plan usage not available";
                ShowSetupCard($"You're signed in as {account.Email}, but this sign-in doesn't allow using your ChatGPT plan in other apps. " +
                              "Pick an API key provider in ⚙ Settings instead.", "", "Plan usage unavailable");
                return;
            }
            if (LocalModelClient.PlanLimitPaused)
            {
                _vm.ModelOk = false;
                _vm.ModelStatus = "☁ ChatGPT plan: usage limit reached";
                ShowSetupCard("You've reached your ChatGPT plan's usage limit, so Prompty paused its model requests. " +
                              "Check your usage below; when it has reset, open ⚙ Settings and click Test & load models to resume.",
                    ChatGptAuth.ManageUsageUrl, "Usage limit reached");
                return;
            }
        }
        else if (CredentialStore.Get(provider) == null)
        {
            _vm.ModelOk = false;
            _vm.ModelStatus = $"☁ {name}: not signed in";
            ShowSetupCard($"Prompty is set to use {name}, but has no API key for it. Open ⚙ Settings to " +
                          (provider.SupportsSignIn ? "sign in, or paste a key." : "paste an API key.") +
                          (provider.KeyUrl != null ? " You can create one here:" : ""),
                provider.KeyUrl ?? "", "Sign in needed");
            return;
        }
        try
        {
            var models = await _model.ListModelsAsync();
            var current = _vm.ModelName;
            _vm.Models.Clear();
            foreach (var m in models) _vm.Models.Add(m);
            _vm.ModelName = current;
            if (string.IsNullOrWhiteSpace(_settings.Model))
            {
                _vm.ModelOk = false;
                _vm.ModelStatus = $"☁ {name}: pick a model";
                ShowSetupCard($"Connected to {name}. Pick a model in the toolbar or in ⚙ Settings.", "", "Pick a model");
                return;
            }
            _vm.ModelOk = true;
            // Some APIs (Azure deployments, OpenRouter variants) accept names their list doesn't show.
            var listed = models.Count == 0 || models.Contains(_settings.Model);
            _vm.ModelStatus = provider.UsesAccountSignIn
                ? $"☁ ChatGPT ({ChatGptAuth.Account?.Email}) ● {_settings.Model}{(listed ? "" : " (not in your plan's list)")}"
                : $"☁ {name} ● {_settings.Model}{(listed ? "" : " (not in the provider's list)")}";
            _setupShownFor = null;
            RemoveSetupCards();
        }
        catch (Exception ex)
        {
            _vm.ModelOk = false;
            _vm.ModelStatus = $"☁ {name}: {(ex is TaskCanceledException ? "no response" : "connection failed")}";
            ShowSetupCard($"Couldn't use {name}: {(ex is TaskCanceledException ? $"no response from {_settings.BaseUrl}." : ex.Message)} " +
                          "Check the key and URL in ⚙ Settings. Prompty retries automatically.", "", "Connection problem");
        }
    }

    void ManageUsage_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(ChatGptAuth.ManageUsageUrl) { UseShellExecute = true });

    string? _setupShownFor;

    void ShowSetupCard(string reason, string commands, string title = "Local model needed")
    {
        if (_setupShownFor == reason) return; // don't resurrect a card the user dismissed
        _setupShownFor = reason;
        RemoveSetupCards();
        _vm.Suggestions.Insert(0, new SuggestionVm
        {
            Kind = SuggestionKind.Info, Badge = "SETUP", Title = title, Reason = reason, Text = commands,
        });
    }

    void RemoveSetupCards()
    {
        foreach (var s in _vm.Suggestions.Where(s => s.Kind == SuggestionKind.Info).ToList()) _vm.Suggestions.Remove(s);
    }

    async void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelBox.SelectedItem is string m && m != _settings.Model) await SetModelAsync(m);
    }

    async void ModelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var m = ModelBox.Text.Trim();
        if (m.Length > 0 && m != _settings.Model) await SetModelAsync(m);
    }

    async Task SetModelAsync(string model)
    {
        _settings.Model = model;
        _vm.ModelName = model;
        _settings.Save();
        await CheckModelAsync();
    }

    // ───────────────────────────── editor ─────────────────────────────

    void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_switchingDraft) return;
        if (sender != null && _draft.GenerateCts == null) _lastUserEdit = DateTime.Now;
        var text = Editor.Text;
        _bridge.CurrentDraft = text;

        _vm.Sections.Clear();
        foreach (var s in PromptDocument.Parse(text)) _vm.Sections.Add(new SectionVm(s));

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        _vm.Counts = $"{words} words · ~{(text.Length + 3) / 4} tokens";
        _vm.FileTitle = (_draft.FilePath != null ? Path.GetFileName(_draft.FilePath) : "Untitled") + (sender != null ? " •" : "");

        _autosaveTimer.Stop(); _autosaveTimer.Start();
        if (_vm.AutoAnalyze) { _analyzeTimer.Stop(); _analyzeTimer.Start(); }
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.F5: _ = AnalyzeAsync(auto: false); break;
            case Key.Enter when ctrl: _ = SendAsync(); break;
            case Key.Space when ctrl: _ = ContinueAsync(); break;
            case Key.R when ctrl: _ = RewriteAsync(_vm.AskText); break;
            case Key.S when ctrl && shift: SaveAs(); break;
            case Key.S when ctrl: Save(); break;
            case Key.O when ctrl && Output.IsKeyboardFocusWithin: SetAllFolds(!_verbose); break; // Claude Code's Ctrl+O
            case Key.Enter when !ctrl && Output.IsKeyboardFocusWithin:
                if (!ToggleFoldAtCaret()) return;
                break;
            case Key.O when ctrl: Open(); break;
            case Key.N when ctrl: ShowTemplateMenu(); break;
            default: return;
        }
        e.Handled = true;
    }

    void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is not SectionVm s) return;
        var offset = Math.Min(s.Section.BodyStart, Editor.Document.TextLength);
        Editor.CaretOffset = offset;
        Editor.ScrollToLine(Editor.Document.GetLineByOffset(offset).LineNumber);
        Editor.Focus();
        SectionList.SelectedItem = null;
    }

    // ───────────────────────────── analysis & generation ─────────────────────────────

    async Task AnalyzeAsync(bool auto)
    {
        var draft = Editor.Text;
        if (auto && (draft == _draft.LastAnalyzed || draft.Trim().Length < 15)) return;
        if (auto && !_vm.ModelOk) return;
        _draft.LastAnalyzed = draft;

        _analyzeCts?.Cancel();
        var cts = _analyzeCts = new CancellationTokenSource();
        _vm.BusyCount++;
        _vm.Activity = "Analyzing draft…";
        var sw = Stopwatch.StartNew();
        try
        {
            var state = _draft;
            var results = await _assistant.AnalyzeAsync(BuildContext(), draft, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (state != _draft) { state.LastAnalyzed = ""; return; } // switched sessions meanwhile

            // Replace stale analysis cards the user hasn't touched.
            foreach (var old in _vm.Suggestions.Where(s => s.FromAutoAnalysis && !s.IsStreaming).ToList())
                _vm.Suggestions.Remove(old);

            foreach (var r in results)
            {
                _vm.Suggestions.Add(new SuggestionVm
                {
                    Kind = SuggestionKind.Section,
                    Badge = $"{r.Action.ToUpperInvariant()} · {r.Section}",
                    Title = r.Title,
                    Reason = r.Reason,
                    Text = r.Text,
                    SectionTitle = r.Section,
                    IsAddition = r.Action == "add",
                    FromAutoAnalysis = true,
                });
            }
            _vm.Activity = results.Count == 0
                ? $"No suggestions — draft looks good ({sw.Elapsed.TotalSeconds:0.0}s)"
                : $"{results.Count} suggestion(s) in {sw.Elapsed.TotalSeconds:0.0}s";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _vm.Activity = "Analysis failed: " + ex.Message;
            if (!auto || ex is HttpRequestException or PlanLimitException) await CheckModelAsync();
        }
        finally
        {
            _vm.BusyCount--;
        }
    }

    /// <summary>Creates a card and streams model output into it.</summary>
    async Task StreamIntoCardAsync(SuggestionVm card, Func<CancellationToken, IAsyncEnumerable<string>> run)
    {
        card.Cts = new CancellationTokenSource();
        card.IsStreaming = true;
        _vm.Suggestions.Insert(FirstNonInfoIndex(), card);
        _vm.BusyCount++;
        try
        {
            var sb = new StringBuilder();
            await foreach (var chunk in run(card.Cts.Token))
            {
                sb.Append(chunk);
                card.Text = sb.ToString().TrimStart();
            }
            card.Text = CleanGenerated(sb.ToString());
            if (card.Text.Length == 0) card.Error = "The model returned nothing.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            card.Error = ex.Message;
            if (ex is HttpRequestException or PlanLimitException) _ = CheckModelAsync();
        }
        finally
        {
            card.IsStreaming = false;
            _vm.BusyCount--;
        }
    }

    int FirstNonInfoIndex() => _vm.Suggestions.TakeWhile(s => s.Kind == SuggestionKind.Info).Count();

    /// <summary>Small models often wrap output in code fences or quotes; strip that.</summary>
    static string CleanGenerated(string s)
    {
        s = LocalModelClient.StripThinking(s).Trim();
        if (s.StartsWith("```"))
        {
            var firstNl = s.IndexOf('\n');
            s = firstNl > 0 ? s[(firstNl + 1)..] : "";
            if (s.TrimEnd().EndsWith("```")) s = s.TrimEnd()[..^3];
        }
        if (s.Length > 1 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return s.Trim();
    }

    Task SectionAsync(PromptSection section, string? instruction)
    {
        var card = new SuggestionVm
        {
            Kind = SuggestionKind.Section,
            Badge = $"{(section.IsEmpty ? "FILL" : "REWRITE")} · {section.Title}",
            Title = section.IsEmpty ? $"Autofill “{section.Title}”" : $"Rewrite “{section.Title}”",
            Reason = instruction ?? "",
            SectionTitle = section.Title,
        };
        var ctx = BuildContext();
        var draft = Editor.Text;
        return StreamIntoCardAsync(card, ct => _assistant.StreamSectionAsync(ctx, draft, section, instruction, ct));
    }

    async void SectionAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SectionVm s) await SectionAsync(s.Section, NullIfBlank(_vm.AskText));
    }

    async void FillEmpty_Click(object sender, RoutedEventArgs e)
    {
        var empty = PromptDocument.Parse(Editor.Text).Where(s => s.IsEmpty).ToList();
        if (empty.Count == 0) { _vm.Activity = "No empty sections to fill"; return; }
        foreach (var s in empty) await SectionAsync(s, null); // sequential: local models serve one request at a time
    }

    // ───────────────────────────── prompt generator ─────────────────────────────

    void GeneratorInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = GenerateAsync();
        }
    }

    void Generate_Click(object sender, RoutedEventArgs e) => _ = GenerateAsync();

    /// <summary>
    /// Expands the generator box's request into a complete prompt, streamed into the editor with the larger
    /// generator model. The whole replacement is one undo step; pressing the button again stops it.
    /// </summary>
    async Task GenerateAsync()
    {
        var state = _draft;
        if (state.GenerateCts != null) { state.GenerateCts.Cancel(); return; }
        var request = GeneratorInput.Text.Trim();
        if (request.Length == 0)
        {
            _vm.Activity = "Type what you want in the GENERATE box first";
            GeneratorInput.Focus();
            return;
        }

        var ctx = BuildContext();
        var dir = _projectDir;
        // Pass the current draft along unless it's just an untouched template.
        var draft = Editor.Text;
        var sections = PromptDocument.Parse(draft);
        var draftHasContent = sections.Count > 0 ? sections.Any(sec => !sec.IsEmpty) : !PromptDocument.LooksEmpty(draft);
        var existing = draftHasContent && !Templates.All.Any(t => t.Body.Trim() == draft.Trim()) ? draft : "";

        using var cts = state.GenerateCts = new CancellationTokenSource();
        UpdateGenerateUi();
        _vm.BusyCount++;
        _vm.Activity = $"Generating prompt with {_settings.GeneratorModel}…";

        var doc = state.Doc; // keeps writing into this session's draft even if you switch away
        var sb = new StringBuilder();
        var wrote = false;
        doc.UndoStack.StartUndoGroup();
        try
        {
            var project = await Task.Run(() => ProjectContext.Build(dir));
            await foreach (var chunk in _assistant.StreamGenerateAsync(ctx, project, request, existing,
                               _settings.GeneratorModel, _settings.GeneratorContextTokens, cts.Token))
            {
                sb.Append(chunk);
                if (!wrote)
                {
                    var start = sb.ToString().TrimStart();
                    if (start.Length == 0) continue;
                    doc.Replace(0, doc.TextLength, start); // first real text replaces the draft
                    wrote = true;
                }
                else doc.Insert(doc.TextLength, chunk);
                if (state == _draft) Editor.ScrollToEnd();
            }

            var final = CleanGenerated(sb.ToString());
            if (final.Length == 0) { _vm.Activity = "The generator returned nothing"; return; }
            if (doc.Text != final) doc.Replace(0, doc.TextLength, final);
            if (state == _draft)
            {
                Editor.ScrollToHome();
                Editor.CaretOffset = 0;
            }
            _vm.Activity = "Prompt generated (Ctrl+Z restores your previous draft)";
        }
        catch (OperationCanceledException)
        {
            _vm.Activity = wrote ? "Generation stopped (Ctrl+Z restores your previous draft)" : "Generation stopped";
        }
        catch (Exception ex)
        {
            _vm.Activity = "Generation failed: " + ex.Message;
            if (ex is HttpRequestException or PlanLimitException) _ = CheckModelAsync();
        }
        finally
        {
            doc.UndoStack.EndUndoGroup();
            _vm.BusyCount--;
            state.GenerateCts = null;
            UpdateGenerateUi();
        }
    }

    void UpdateGenerateUi()
    {
        var busy = _draft.GenerateCts != null;
        Editor.IsReadOnly = busy;
        GenerateButton.Content = busy ? "■" : "➜";
        GenerateButton.ToolTip = busy ? "Stop generating"
            : "Generate a complete prompt from this request into the editor below (Enter)";
    }

    // ───────────────────────────── per-session drafts ─────────────────────────────

    /// <summary>Shows the draft that belongs to a session, creating or loading it on first use.</summary>
    void SwitchDraft(string key)
    {
        if (_draft.Key == key) return;
        _analyzeCts?.Cancel();
        _analyzeTimer.Stop();
        _draft.LastAnalyzed = "";
        _draft.Suggestions = _vm.Suggestions.Where(x => x.Kind != SuggestionKind.Info).ToList();

        _draft = GetDraft(key);
        _switchingDraft = true;
        try
        {
            Editor.Document = _draft.Doc;
            GeneratorInput.Text = _draft.GeneratorText;
            foreach (var x in _vm.Suggestions.Where(x => x.Kind != SuggestionKind.Info).ToList()) _vm.Suggestions.Remove(x);
            foreach (var x in _draft.Suggestions) _vm.Suggestions.Add(x);
        }
        finally { _switchingDraft = false; }

        UpdateGenerateUi();
        OnEditorTextChanged(null, EventArgs.Empty);
        _autosaveTimer.Stop();
    }

    DraftState GetDraft(string key)
    {
        if (_drafts.TryGetValue(key, out var existing)) return existing;
        string text, request = "";
        var migrated = false;
        try
        {
            if (File.Exists(DraftState.PathFor(key))) text = File.ReadAllText(DraftState.PathFor(key));
            else if (_legacyDraft != null) { text = _legacyDraft; _legacyDraft = null; migrated = true; }
            else text = Templates.All[0].Body;
            if (File.Exists(DraftState.RequestPathFor(key))) request = File.ReadAllText(DraftState.RequestPathFor(key));
        }
        catch (IOException) { text = Templates.All[0].Body; }

        var doc = new TextDocument(text);
        doc.UndoStack.ClearAll();
        var state = new DraftState { Key = key, Doc = doc, GeneratorText = request, Dirty = migrated };
        // Edits can land while the draft isn't shown (a generation still streaming into it).
        doc.TextChanged += (_, _) =>
        {
            if (_switchingDraft) return;
            state.Dirty = true;
            _autosaveTimer.Stop(); _autosaveTimer.Start();
        };
        _drafts[key] = state;
        return state;
    }

    async Task RewriteAsync(string? instruction)
    {
        instruction = NullIfBlank(instruction);
        var doc = Editor.Document;
        int start, length;
        if (Editor.SelectionLength > 0)
        {
            start = Editor.SelectionStart; length = Editor.SelectionLength;
        }
        else if (PromptDocument.At(Editor.Text, Editor.CaretOffset) is { } section)
        {
            await SectionAsync(section, instruction);
            return;
        }
        else
        {
            start = 0; length = doc.TextLength;
        }
        if (length == 0) { _vm.Activity = "Nothing to rewrite"; return; }

        var selection = doc.GetText(start, length);
        var card = new SuggestionVm
        {
            Kind = SuggestionKind.Selection,
            Badge = length == doc.TextLength ? "REWRITE · whole prompt" : "REWRITE · selection",
            Title = instruction ?? "Clearer, more specific wording",
            Start = doc.CreateAnchor(start),
            End = doc.CreateAnchor(start + length),
        };
        var ctx = BuildContext();
        var draft = Editor.Text;
        await StreamIntoCardAsync(card, ct => _assistant.StreamRewriteAsync(ctx, draft, selection, instruction, ct));
    }

    async Task ContinueAsync()
    {
        var doc = Editor.Document;
        var caret = Editor.CaretOffset;
        var before = doc.GetText(Math.Max(0, caret - 6000), Math.Min(caret, 6000));
        var after = doc.GetText(caret, Math.Min(1500, doc.TextLength - caret));
        var card = new SuggestionVm
        {
            Kind = SuggestionKind.Insert,
            Badge = $"CONTINUE · line {doc.GetLineByOffset(caret).LineNumber}",
            Title = "Continue at cursor",
            Start = doc.CreateAnchor(caret),
        };
        var ctx = BuildContext();
        await StreamIntoCardAsync(card, ct => _assistant.StreamContinueAsync(ctx, before, after, ct));
    }

    async void Placeholders_Click(object sender, RoutedEventArgs e)
    {
        var names = PromptDocument.Placeholders(Editor.Text);
        if (names.Count == 0) { _vm.Activity = "No {{placeholders}} in the draft"; return; }
        _vm.BusyCount++;
        _vm.Activity = "Filling placeholders…";
        var state = _draft;
        try
        {
            var values = await _assistant.FillPlaceholdersAsync(BuildContext(), Editor.Text, names, CancellationToken.None);
            if (state != _draft) return; // switched sessions meanwhile
            var lines = names.Select(n => $"{n} = {(values.TryGetValue(n, out var v) ? v.ReplaceLineEndings(" ") : "")}");
            _vm.Suggestions.Insert(FirstNonInfoIndex(), new SuggestionVm
            {
                Kind = SuggestionKind.Placeholders,
                Badge = $"PLACEHOLDERS · {names.Count}",
                Title = "Fill {{placeholders}}",
                Reason = "Edit values as needed; blank values are left untouched.",
                Text = string.Join("\n", lines),
            });
            _vm.Activity = "";
        }
        catch (Exception ex) { _vm.Activity = "Placeholder fill failed: " + ex.Message; }
        finally { _vm.BusyCount--; }
    }

    // ───────────────────────────── applying suggestions ─────────────────────────────

    void Apply(SuggestionVm s)
    {
        var doc = Editor.Document;
        var text = s.Text.Trim();
        int changeStart, changeLength;

        doc.BeginUpdate();
        try
        {
            switch (s.Kind)
            {
                case SuggestionKind.Section:
                    (changeStart, changeLength) = ApplySection(s.SectionTitle, text);
                    break;

                case SuggestionKind.Selection when s.Start is { IsDeleted: false } a && s.End is { IsDeleted: false } b:
                    changeStart = a.Offset;
                    doc.Replace(changeStart, b.Offset - changeStart, text);
                    changeLength = text.Length;
                    break;

                case SuggestionKind.Insert when s.Start is { IsDeleted: false } a:
                    changeStart = a.Offset;
                    var prev = changeStart > 0 ? doc.GetCharAt(changeStart - 1) : '\n';
                    var insert = char.IsWhiteSpace(prev) ? text : " " + text;
                    doc.Insert(changeStart, insert);
                    changeLength = insert.Length;
                    break;

                case SuggestionKind.Placeholders:
                    var values = s.Text.Split('\n')
                        .Select(l => l.Split(" = ", 2))
                        .Where(p => p.Length == 2)
                        .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
                    var filled = PromptDocument.FillPlaceholders(doc.Text, values);
                    doc.Replace(0, doc.TextLength, filled);
                    (changeStart, changeLength) = (0, 0);
                    break;

                case SuggestionKind.ReplaceAll:
                    doc.Replace(0, doc.TextLength, text + "\n");
                    (changeStart, changeLength) = (0, 0);
                    break;

                default:
                    _vm.Activity = "The text this suggestion targeted was deleted";
                    return;
            }
        }
        finally
        {
            doc.EndUpdate();
        }

        _vm.Suggestions.Remove(s);
        _draft.LastAnalyzed = ""; // allow re-analysis of the improved draft
        if (changeLength > 0 && changeStart >= 0 && changeStart + changeLength <= doc.TextLength)
        {
            Editor.Select(changeStart, changeLength);
            Editor.ScrollToLine(doc.GetLineByOffset(changeStart).LineNumber);
        }
        Editor.Focus();
    }

    (int Start, int Length) ApplySection(string title, string body)
    {
        var doc = Editor.Document;
        var sections = PromptDocument.Parse(doc.Text);

        if (sections.Count == 0 && title.Equals("Prompt", StringComparison.OrdinalIgnoreCase))
        {
            doc.Replace(0, doc.TextLength, body + "\n");
            return (0, body.Length);
        }

        var target = PromptDocument.Find(doc.Text, title);
        if (target != null)
        {
            var formatted = PromptDocument.FormatBody(target, body, target.BodyEnd >= doc.TextLength);
            doc.Replace(target.BodyStart, target.BodyEnd - target.BodyStart, formatted);
            return (target.BodyStart, formatted.Length);
        }

        // New section: match the document's style (XML tags vs markdown headings).
        var useXml = sections.Count > 0 && sections.Count(x => x.IsXml) * 2 > sections.Count;
        var tag = string.Join("_", title.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var block = useXml ? $"<{tag}>\n{body}\n</{tag}>\n" : $"## {title}\n\n{body}\n";
        var prefix = doc.Text.EndsWith("\n\n") || doc.TextLength == 0 ? "" : doc.Text.EndsWith('\n') ? "\n" : "\n\n";
        var at = doc.TextLength;
        doc.Insert(at, prefix + block);
        return (at + prefix.Length, block.Length);
    }

    void Apply_Click(object sender, RoutedEventArgs e) { if (Card(sender) is { } s) Apply(s); }
    void Stop_Click(object sender, RoutedEventArgs e) => Card(sender)?.Cts?.Cancel();
    void Copy_Click(object sender, RoutedEventArgs e) { if (Card(sender) is { } s) Clipboard.SetText(s.Text); }

    void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        if (Card(sender) is not { } s) return;
        s.Cts?.Cancel();
        _vm.Suggestions.Remove(s);
    }

    /// <summary>Hovering a card highlights the text it would change.</summary>
    void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (Card(sender) is not { } s || Editor.IsKeyboardFocusWithin) return;
        var doc = Editor.Document;
        if (s.Kind == SuggestionKind.Section && PromptDocument.Find(doc.Text, s.SectionTitle) is { } sec)
            Editor.ScrollToLine(doc.GetLineByOffset(Math.Min(sec.HeadingStart, doc.TextLength)).LineNumber);
        else if (s.Start is { IsDeleted: false } a)
            Editor.ScrollToLine(doc.GetLineByOffset(a.Offset).LineNumber);
    }

    void ClearSuggestions_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in _vm.Suggestions.Where(s => s.Kind != SuggestionKind.Info).ToList())
        {
            s.Cts?.Cancel();
            _vm.Suggestions.Remove(s);
        }
    }

    static SuggestionVm? Card(object sender) => (sender as FrameworkElement)?.DataContext as SuggestionVm;

    // ───────────────────────────── Claude bridge ─────────────────────────────

    bool _sending;

    async Task SendAsync()
    {
        var text = Editor.Text.Trim();
        if (text.Length == 0) { _vm.Activity = "Nothing to send"; return; }
        if (_sending) return;
        _sending = true;
        SendButton.IsEnabled = false;
        try
        {
            SaveHistory(text);

            // 1. Claude is blocked in wait_for_prompt: hand it over via MCP (typing too would deliver it twice).
            if (_bridge.WaitingCount > 0)
            {
                _bridge.Submit(text);
                _vm.Activity = "Delivered to Claude via wait_for_prompt ✓";
                ClearAfterSend();
                return;
            }

            // 2. Type it into the selected running Claude Code session.
            var target = _vm.SelectedSession;
            if (target is { IsLive: true, Pid: int pid })
            {
                // Re-check right before typing: the PID must still be that exact Claude Code process.
                if (!ClaudeSessions.IsSameProcess(pid, target.ProcStart))
                {
                    _vm.Activity = $"“{target.Title}” is no longer running — pick another session";
                    await RefreshContextAsync();
                    return;
                }
                await Task.Run(() => ConsoleInjector.SendPrompt(pid, text));
                _bridge.Submit(text); // keeps /mcp__prompty__send and get_draft consistent
                _vm.Activity = $"Sent to “{target.Title}” (pid {pid}) ✓";
                CenterTabs.SelectedItem = OutputTab;
                ClearAfterSend();
                return;
            }

            // 3. No live target: stage for /mcp__prompty__send and copy.
            _bridge.Submit(text);
            try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
            _vm.Activity = "No running session selected — staged for /mcp__prompty__send and copied to clipboard";
        }
        catch (Exception ex)
        {
            try { Clipboard.SetText(text); } catch { }
            _vm.Activity = $"Send failed ({ex.Message}) — prompt copied to clipboard";
        }
        finally
        {
            _sending = false;
            SendButton.IsEnabled = true;
            UpdateWaitingBanner();
        }
    }

    /// <summary>Like a chat box: clear after sending (Ctrl+Z restores; every send is kept in history).</summary>
    void ClearAfterSend()
    {
        Editor.Document.Replace(0, Editor.Document.TextLength, "");
        _draft.LastAnalyzed = "";
        foreach (var s in _vm.Suggestions.Where(s => s.Kind != SuggestionKind.Info).ToList())
        {
            s.Cts?.Cancel();
            _vm.Suggestions.Remove(s);
        }
        Editor.Focus();
    }

    async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    void UpdateWaitingBanner()
    {
        var waiting = _bridge.WaitingCount > 0;
        if (waiting && !_vm.ClaudeWaiting)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
        _vm.ClaudeWaiting = waiting;
        _vm.WaitingText = _bridge.PendingRequest ?? "Write your next instruction, then press Send (Ctrl+Enter).";
    }

    void OnDraftSeeded(string text)
    {
        var secs = PromptDocument.Parse(Editor.Text);
        var blank = Editor.Text.Trim().Length == 0 || (secs.Count > 0 && secs.All(s => s.IsEmpty));
        if (blank)
        {
            Editor.Document.Replace(0, Editor.Document.TextLength, text.Trim() + "\n");
            _vm.Activity = "Claude proposed a draft";
            return;
        }
        _vm.Suggestions.Insert(FirstNonInfoIndex(), new SuggestionVm
        {
            Kind = SuggestionKind.ReplaceAll,
            Badge = "FROM CLAUDE",
            Title = "Claude proposed a draft",
            Reason = "Apply replaces the editor contents (Ctrl+Z to undo).",
            Text = text.Trim(),
        });
    }

    // ───────────────────────────── toolbar ─────────────────────────────

    async void Analyze_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync(auto: false);
    async void RewriteSelection_Click(object sender, RoutedEventArgs e) => await RewriteAsync(_vm.AskText);
    async void Continue_Click(object sender, RoutedEventArgs e) => await ContinueAsync();
    async void Ask_Click(object sender, RoutedEventArgs e) => await RewriteAsync(_vm.AskText);

    async void AskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RewriteAsync(_vm.AskText);
    }

    void AutoAnalyze_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoAnalyze = _vm.AutoAnalyze;
        _settings.Save();
        if (_vm.AutoAnalyze) _analyzeTimer.Start();
    }

    void New_Click(object sender, RoutedEventArgs e) => ShowTemplateMenu();

    void ShowTemplateMenu()
    {
        var menu = new ContextMenu { PlacementTarget = NewButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (name, body) in Templates.All)
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) =>
            {
                Editor.Document.Replace(0, Editor.Document.TextLength, body);
                _draft.FilePath = null;
                _draft.LastAnalyzed = "";
                OnEditorTextChanged(null, EventArgs.Empty);
                Editor.CaretOffset = PromptDocument.Parse(body).FirstOrDefault()?.BodyStart ?? 0;
                Editor.Focus();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    void Open_Click(object sender, RoutedEventArgs e) => Open();
    void Save_Click(object sender, RoutedEventArgs e) => Save();

    void Open()
    {
        var dlg = new OpenFileDialog { Filter = "Prompts (*.md;*.txt;*.prompt)|*.md;*.txt;*.prompt|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        Editor.Document.Replace(0, Editor.Document.TextLength, File.ReadAllText(dlg.FileName));
        _draft.FilePath = dlg.FileName;
        OnEditorTextChanged(null, EventArgs.Empty);
    }

    void Save()
    {
        if (_draft.FilePath == null) { SaveAs(); return; }
        File.WriteAllText(_draft.FilePath, Editor.Text);
        OnEditorTextChanged(null, EventArgs.Empty);
        _analyzeTimer.Stop();
    }

    void SaveAs()
    {
        var dlg = new SaveFileDialog { Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt", FileName = "prompt.md" };
        if (dlg.ShowDialog(this) != true) return;
        _draft.FilePath = dlg.FileName;
        Save();
    }

    void SaveDraft()
    {
        foreach (var d in _drafts.Values.Where(d => d.Dirty)) d.Save();
    }

    static void SaveHistory(string text)
    {
        try
        {
            var dir = Path.Combine(AppSettings.Dir, "history");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.md"), text);
        }
        catch (IOException) { }
    }

    // ───────────────────────────── updates ─────────────────────────────

    async Task CheckForUpdatesOnLaunchAsync()
    {
        if (UpdateService.JustUpdated) _vm.Activity = $"Updated to Prompty {UpdateService.CurrentVersionText}";
        if (!_settings.CheckForUpdatesOnLaunch) return;
        await Task.Delay(TimeSpan.FromSeconds(3)); // let the window settle first
        try
        {
            var update = await UpdateService.CheckAsync();
            if (update == null || !UpdateWindow.ShouldPrompt(update, _settings)) return;
            if (OwnedWindows.Count > 0) return; // don't stack on top of an open dialog
            new UpdateWindow(update, _settings) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            // Offline, rate-limited or anything else: never let the update check disturb startup. Retried next launch.
            Debug.WriteLine($"Update check failed: {ex}");
        }
    }

    async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var oldPort = _settings.McpPort;
        var win = new SettingsWindow(_settings) { Owner = this };
        if (win.ShowDialog() != true) return;

        _settings.Save();
        _vm.ModelName = _settings.Model;
        _analyzeTimer.Interval = TimeSpan.FromMilliseconds(_settings.AutoAnalyzeDelayMs);
        if (_settings.McpPort != oldPort)
            _vm.McpStatus = "MCP port changed — restart Prompty to apply";
        await CheckModelAsync();
        await LoadSelectedSessionAsync();
    }

    static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
