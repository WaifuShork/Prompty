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
