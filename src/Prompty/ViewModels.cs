using System.Collections.ObjectModel;
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
