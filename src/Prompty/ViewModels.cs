using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ICSharpCode.AvalonEdit.Document;
using Prompty.Services;

namespace Prompty;

public sealed partial class MainViewModel : ObservableObject
{
    public ObservableCollection<SessionInfo> Sessions { get; } = [];
    public ObservableCollection<SectionVm> Sections { get; } = [];
    public ObservableCollection<SuggestionVm> Suggestions { get; } = [];
    public ObservableCollection<string> Models { get; } = [];
    /// <summary>Shells, monitors, agents and workflows Claude is running (or just finished) in the selected session.</summary>
    public ObservableCollection<TaskItemVm> Tasks { get; } = [];
    /// <summary>Questions Claude asked (AskUserQuestion) in the selected session and is waiting on; null when none.</summary>
    [ObservableProperty] private QuestionCardVm? _questionCard;
    [ObservableProperty] private bool _hasTasks;

    [ObservableProperty] private SessionInfo? _selectedSession;
    [ObservableProperty] private bool _followLatest = true;
    [ObservableProperty] private string _digest = "";
    [ObservableProperty] private string _turnInfo = "";
    [ObservableProperty] private string? _sharedContext;

    [ObservableProperty] private string _modelName = "";
    [ObservableProperty] private string _modelStatus = "Checking local model…";
    /// <summary>Shows the "Using ChatGPT plan · Manage usage" indicator next to the model picker.</summary>
    [ObservableProperty] private bool _usingChatGptPlan;
    [ObservableProperty] private bool _modelOk;
    [ObservableProperty] private bool _autoAnalyze = true;
    [ObservableProperty] private int _busyCount;
    [ObservableProperty] private string _activity = "";

    [ObservableProperty] private string _mcpStatus = "MCP starting…";
    [ObservableProperty] private bool _mcpOk;
    [ObservableProperty] private bool _claudeWaiting;
    [ObservableProperty] private string _waitingText = "";

    [ObservableProperty] private string _counts = "";
    [ObservableProperty] private string _fileTitle = "Untitled";
    [ObservableProperty] private string _askText = "";

    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private string _outputHeader = "";

    public string TargetLabel => SelectedSession switch
    {
        null => "no session selected (copies to clipboard)",
        { IsLive: true } s => $"{s.Title}  ({s.Project}, pid {s.Pid}{(s.IsBusy ? ", working — will queue" : "")})",
        { } s => $"{s.Title} has ended (copies to clipboard)",
    };

    partial void OnSelectedSessionChanged(SessionInfo? value) => OnPropertyChanged(nameof(TargetLabel));

    public bool IsBusy => BusyCount > 0;
    public bool HasSharedContext => !string.IsNullOrWhiteSpace(SharedContext);
    public bool NoSuggestions => Suggestions.Count == 0;

    public MainViewModel()
    {
        Suggestions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NoSuggestions));
    }

    partial void OnBusyCountChanged(int value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnSharedContextChanged(string? value) => OnPropertyChanged(nameof(HasSharedContext));
}

public sealed class SectionVm(PromptSection section)
{
    public PromptSection Section { get; } = section;
    public string Label => Section.Label;
    public string Detail => Section.IsEmpty ? "empty" : $"{Section.WordCount} words";
    public string ActionLabel => Section.IsEmpty ? "✨ Fill" : "Rewrite";
    public bool IsEmpty => Section.IsEmpty;
}

/// <summary>One row in the task list under the Claude output: a header that expands to the command and its output.</summary>
public sealed partial class TaskItemVm(string id) : ObservableObject
{
    static readonly Brush RunningBrush = Frozen(0xD9, 0xA2, 0x7A);
    static readonly Brush GoodBrush = Frozen(0x6C, 0xC5, 0x7C);
    static readonly Brush BadBrush = Frozen(0xE0, 0x6C, 0x75);
    static readonly Brush StoppedBrush = Frozen(0x9C, 0xA0, 0xAB);

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public string Id { get; } = id;
    ClaudeTaskView _view = null!;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private string _kindLabel = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private Brush _statusBrush = RunningBrush;
    [ObservableProperty] private string _outputText = "";
    [ObservableProperty] private bool _isExpanded;

    public bool IsRunning => _view.Status == ClaudeTaskStatus.Running;
    public bool OutputIsLive => _view.OutputIsLive;
    public string Chevron => IsExpanded ? "▾" : "▸";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(Chevron));
        RenderOutput();
    }

    public void Update(ClaudeTaskView view)
    {
        var changed = _view == null || !Equals(_view, view);
        _view = view;
        if (!changed) { Tick(); return; }
        Title = view.Title;
        Command = view.Command;
        KindLabel = view.Kind switch
        {
            ClaudeTaskKind.Shell => "Shell",
            ClaudeTaskKind.BackgroundShell => "Background shell",
            ClaudeTaskKind.Monitor => "Monitor",
            ClaudeTaskKind.Agent => "Agent",
            _ => "Workflow",
        };
        StatusBrush = view.Status switch
        {
            ClaudeTaskStatus.Running => RunningBrush,
            ClaudeTaskStatus.Completed => GoodBrush,
            ClaudeTaskStatus.Failed => BadBrush,
            _ => StoppedBrush,
        };
        Tick();
        RenderOutput();
    }

    /// <summary>Refreshes the elapsed time; called every second while something runs.</summary>
    public void Tick()
    {
        var v = _view;
        var elapsed = Format((v.EndedAt ?? DateTime.Now) - v.StartedAt);
        var exit = v.ExitCode is { } code ? $"exit {code} · " : "";
        StatusText = v.Status switch
        {
            ClaudeTaskStatus.Running => $"running · {elapsed}",
            ClaudeTaskStatus.Completed => $"done · {exit}{elapsed}",
            ClaudeTaskStatus.Failed => $"failed · {exit}{elapsed}",
            _ => $"stopped · {elapsed}",
        };
    }

    /// <summary>Only an expanded row builds its (possibly large) output text.</summary>
    void RenderOutput()
    {
        if (!IsExpanded) { OutputText = ""; return; }
        var v = _view;
        var prompt = v.Kind == ClaudeTaskKind.Agent ? "agent> " : "$ ";
        var body = v.Output.Length > 0 ? v.Output
            : v.Status == ClaudeTaskStatus.Running ? "(waiting for output…)" : "(no output)";
        OutputText = $"{prompt}{v.Command.TrimEnd()}\n\n{(v.OutputTruncated ? "… earlier output trimmed …\n" : "")}{body.TrimEnd()}";
    }

    static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return t.TotalSeconds < 10 ? $"{t.TotalSeconds:0.0}s" : $"{(int)t.TotalSeconds}s";
    }
}

/// <summary>
/// The "Claude is asking" card. Mirrors the step Claude Code's question widget is on, read from its screen:
/// one question at a time, then the review step.
/// </summary>
public sealed partial class QuestionCardVm : ObservableObject
{
    public QuestionCardVm(QuestionScreen screen, string sessionTitle)
    {
        Screen = screen;
        SessionTitle = sessionTitle;
        if (!screen.IsReview) Question = new QuestionVm(screen, Recompute);
        Recompute();
    }

    public QuestionScreen Screen { get; }
    public string Key => Screen.Key;
    public string SessionTitle { get; }
    public bool IsReview => Screen.IsReview;
    public bool IsQuestion => !Screen.IsReview;
    public QuestionVm? Question { get; }
    public string Heading => IsReview ? "Review your answers" : "Claude is asking";
    public string SendLabel => IsReview ? "Submit answers ➤" : "Answer ➤";

    /// <summary>"Color ✓ · Toppings": the widget's tabs, ticked once answered.</summary>
    public string Progress => Screen.Tabs.Count <= 1 ? "" :
        string.Join("  ·  ", Screen.Tabs.Select(t => t.Answered ? $"{t.Title} ✓" : t.Title));
    public bool HasProgress => Progress.Length > 0;
    public IReadOnlyList<string> ReviewLines => Screen.Review.Select(r => $"{r.Question}  →  {r.Answer}").ToList();

    [ObservableProperty] private bool _canSend;
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;

    partial void OnIsSendingChanged(bool value) => Recompute();

    public void Recompute() => CanSend = !IsSending && (IsReview || Question?.IsAnswered == true);
}

public sealed partial class QuestionVm : ObservableObject
{
    readonly Action _changed;
    bool _syncing;

    public QuestionVm(QuestionScreen screen, Action changed)
    {
        _changed = changed;
        Text = screen.Question;
        MultiSelect = screen.MultiSelect;
        foreach (var o in screen.Options)
            Options.Add(new QuestionOptionVm(o.Number, o.Label, o.Description, OnOptionChanged) { IsSelected = screen.MultiSelect && o.Checked });
        // Text already typed into Claude Code's free-text row shows there instead of "Type something".
        if (screen.TypeRow is { } t && t.Label.TrimEnd('.') is var typed && typed is not ("Type something" or ""))
        {
            _customText = typed;
            _useCustom = !screen.MultiSelect || t.Checked;
        }
    }

    public string Text { get; }
    public bool MultiSelect { get; }
    public bool SingleSelect => !MultiSelect;
    public string Hint => MultiSelect ? "Choose any that apply" : "Choose one";
    public string GroupName { get; } = "q" + Guid.NewGuid().ToString("N");
    public ObservableCollection<QuestionOptionVm> Options { get; } = [];
    // Only one kind of control is created per question: hidden radio buttons would still enforce their group's
    // one-checked rule through the shared bindings and untick multi-select answers.
    public IEnumerable<QuestionOptionVm>? SingleOptions => MultiSelect ? null : Options;
    public IEnumerable<QuestionOptionVm>? MultiOptions => MultiSelect ? Options : null;

    [ObservableProperty] private bool _useCustom;
    [ObservableProperty] private string _customText = "";

    public bool IsAnswered => Options.Any(o => o.IsSelected) || (UseCustom && CustomText.Trim().Length > 0);

    void OnOptionChanged(QuestionOptionVm option)
    {
        if (_syncing) return;
        _syncing = true;
        if (!MultiSelect && option.IsSelected)
        {
            foreach (var o in Options) if (o != option) o.IsSelected = false;
            UseCustom = false;
        }
        _syncing = false;
        _changed?.Invoke();
    }

    partial void OnUseCustomChanged(bool value)
    {
        if (!_syncing && value && !MultiSelect)
        {
            _syncing = true;
            foreach (var o in Options) o.IsSelected = false;
            _syncing = false;
        }
        _changed?.Invoke();
    }

    partial void OnCustomTextChanged(string value)
    {
        if (value.Trim().Length > 0 && !UseCustom) UseCustom = true; // typing an answer picks "Other"
        _changed?.Invoke();
    }

    public QuestionAnswer ToAnswer() => new(
        UseCustom && !MultiSelect ? [] : Options.Where(o => o.IsSelected).Select(o => o.Number).ToList(),
        UseCustom && CustomText.Trim().Length > 0 ? CustomText.Trim() : null);
}

public sealed partial class QuestionOptionVm(int number, string label, string description, Action<QuestionOptionVm> changed) : ObservableObject
{
    public int Number { get; } = number;
    public string Label { get; } = label;
    public string Description { get; } = description;
    public bool HasDescription => Description.Length > 0 && Description != Label;
    [ObservableProperty] private bool _isSelected;
    partial void OnIsSelectedChanged(bool value) => changed(this);
}

public enum SuggestionKind { Section, Selection, Insert, Placeholders, ReplaceAll, Info }

public sealed partial class SuggestionVm : ObservableObject
{
    public required SuggestionKind Kind { get; init; }
    public required string Badge { get; init; }
    public required string Title { get; init; }
    public string Reason { get; init; } = "";

    /// <summary>Target section title for <see cref="SuggestionKind.Section"/>.</summary>
    public string SectionTitle { get; init; } = "";
    public bool IsAddition { get; init; }
    public bool FromAutoAnalysis { get; init; }

    /// <summary>Anchors survive edits made while the model is still streaming.</summary>
    public TextAnchor? Start { get; init; }
    public TextAnchor? End { get; init; }

    public CancellationTokenSource? Cts { get; set; }

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string? _error;

    public bool HasReason => Reason.Length > 0;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool CanApply => Kind != SuggestionKind.Info && !IsStreaming && Text.Trim().Length > 0;
    public bool ShowText => Kind != SuggestionKind.Info || Text.Length > 0;

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(CanApply));
    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(CanApply));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
}
