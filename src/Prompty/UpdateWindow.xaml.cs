using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Prompty.Services;

namespace Prompty;

/// <summary>
/// Offers an update: Update now (download, verify, swap, restart), Skip this version, or
/// Don't ask again until the next major version.
/// </summary>
public partial class UpdateWindow : Window
{
    readonly UpdateInfo _update;
    readonly AppSettings _settings;
    CancellationTokenSource? _cts;
    bool _restarting;

    public UpdateWindow(UpdateInfo update, AppSettings settings)
    {
        InitializeComponent();
        _update = update;
        _settings = settings;

        var v = $"{update.Version.Major}.{update.Version.Minor}.{update.Version.Build}";
        Heading.Text = $"Prompty {v} is available";
        SubHeading.Text = $"You have {UpdateService.CurrentVersionText}." +
                          (update.Version.Major > UpdateService.CurrentVersion.Major ? " This is a new major version." : "");
        Notes.Text = string.IsNullOrWhiteSpace(update.Notes) ? "(No release notes.)" : update.Notes.Trim();
        MuteButton.Content = $"Don't ask again until {update.Version.Major + 1}.0";

        if (!UpdateService.CanSelfUpdate)
        {
            UpdateButton.Content = "Open download page";
            SetStatus("This is a development build, so it can't replace itself. Download the release instead.", null);
        }
        Closing += (_, e) =>
        {
            if (_restarting) return;
            if (_cts != null) { _cts.Cancel(); e.Cancel = true; } // let the download stop cleanly first
        };
    }

    /// <summary>Whether a launch-time prompt should be shown for this release.</summary>
    public static bool ShouldPrompt(UpdateInfo update, AppSettings settings) =>
        update.Version.ToString() != settings.SkippedUpdateVersion
        && update.Version.Major >= settings.MuteUpdatesUntilMajor;

    void Skip_Click(object sender, RoutedEventArgs e)
    {
        _settings.SkippedUpdateVersion = _update.Version.ToString();
        _settings.Save();
        Close();
    }

    void Mute_Click(object sender, RoutedEventArgs e)
    {
        _settings.MuteUpdatesUntilMajor = _update.Version.Major + 1;
        _settings.Save();
        Close();
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (!UpdateService.CanSelfUpdate)
        {
            Process.Start(new ProcessStartInfo(_update.PageUrl) { UseShellExecute = true });
            Close();
            return;
        }
        if (_cts != null) { _cts.Cancel(); return; }

        using var cts = _cts = new CancellationTokenSource();
        UpdateButton.Content = "Cancel";
        SkipButton.IsEnabled = MuteButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        SetStatus($"Downloading Prompty {_update.Version}…", null);
        try
        {
            var staged = await UpdateService.DownloadAsync(_update,
                new Progress<double>(p =>
                {
                    Progress.Value = p;
                    Status.Text = $"Downloading… {p:P0}";
                }), cts.Token);

            SetStatus("Verified. Restarting Prompty…", true);
            _restarting = true;
            UpdateService.ApplyAndRestart(staged);
            // Close the main window normally so drafts and settings are saved; the new instance waits for us.
            Application.Current.MainWindow?.Close();
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Update cancelled. Nothing was changed.", null);
            ResetButtons();
        }
        catch (Exception ex)
        {
            _restarting = false;
            SetStatus(ex.Message + " You can also download it from the releases page.", false);
            UpdateButton.Content = "Retry";
            SkipButton.IsEnabled = MuteButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
        finally { _cts = null; }
    }

    void ResetButtons()
    {
        UpdateButton.Content = "Update now";
        SkipButton.IsEnabled = MuteButton.IsEnabled = true;
        Progress.Visibility = Visibility.Collapsed;
    }

    void SetStatus(string text, bool? ok)
    {
        Status.Text = text;
        Status.Foreground = ok switch
        {
            true => (Brush)FindResource("Good"),
            false => (Brush)FindResource("Bad"),
            null => (Brush)FindResource("MutedText"),
        };
    }
}
