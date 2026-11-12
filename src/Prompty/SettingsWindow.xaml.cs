using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Prompty.Services;

namespace Prompty;

public partial class SettingsWindow : Window
{
    readonly AppSettings _settings;
    // Edited copy: provider switches happen here and are only applied on Save.
    readonly AppSettings _work;
    // Keys typed but not saved yet (null = forget).
    readonly Dictionary<string, string?> _pendingKeys = [];
    ProviderInfo _shown = null!;
    CancellationTokenSource? _signInCts;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _work = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

        ProviderBox.ItemsSource = Providers.All;
        CtxBox.Text = settings.ContextTokens.ToString();
        CharsBox.Text = settings.ContextMaxChars.ToString();
        DelayBox.Text = settings.AutoAnalyzeDelayMs.ToString();
        PortBox.Text = settings.McpPort.ToString();
        CommandBox.Text = settings.ClaudeAddCommand;
        VersionText.Text = $"Prompty {UpdateService.CurrentVersionText}";
        CheckOnLaunchBox.IsChecked = settings.CheckForUpdatesOnLaunch;
        if (settings.MuteUpdatesUntilMajor > UpdateService.CurrentVersion.Major || settings.SkippedUpdateVersion != null)
            SetStatus(UpdateStatus, settings.MuteUpdatesUntilMajor > UpdateService.CurrentVersion.Major
                ? $"Launch prompts are paused until version {settings.MuteUpdatesUntilMajor}.0. Checking here resets that."
                : $"You skipped version {settings.SkippedUpdateVersion}. Checking here resets that.", null);

        _shown = _work.ProviderInfo;
        ProviderBox.SelectedItem = _shown; // fires SelectionChanged → ShowProvider
        ShowProvider();
        Closed += (_, _) => _signInCts?.Cancel();
    }

    // ───────────────────────────── provider ─────────────────────────────

    void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedItem is not ProviderInfo p || _shown == null || p == _shown) return;
        CaptureFields();
        _work.SwitchProvider(p.Id);
        _shown = p;
        ShowProvider();
    }

    /// <summary>Copies the visible fields into the working settings for the shown provider.</summary>
    void CaptureFields()
    {
        _work.BaseUrl = UrlBox.Text.Trim();
        _work.Model = ModelBox.Text.Trim();
        _work.GeneratorModel = GeneratorModelBox.Text.Trim();
        if (KeyBox.Password.Length > 0) _pendingKeys[_shown.Id] = KeyBox.Password.Trim();
    }

    void ShowProvider()
    {
        var p = _shown;
        UrlBox.Text = _work.BaseUrl;
        ModelBox.ItemsSource = null;
        GeneratorModelBox.ItemsSource = null;
        ModelBox.Text = _work.Model;
        GeneratorModelBox.Text = _work.GeneratorModel;
        KeyBox.Password = "";
        TestResult.Text = "";

        ProviderNote.Text = p.Note ?? (p.IsCloud ? "Cloud provider: needs an API key." : "Runs on this PC; nothing leaves your machine.");
        var cloudVis = p.IsCloud ? Visibility.Visible : Visibility.Collapsed;
        KeyLabel.Visibility = KeyPanel.Visibility = KeyStatus.Visibility = cloudVis;
        // Local OpenAI-compatible servers sometimes want a key too, so keep the field available there.
        if (p.Id == "openai-local") KeyLabel.Visibility = KeyPanel.Visibility = KeyStatus.Visibility = Visibility.Visible;
        SignInPanel.Visibility = p.SupportsSignIn ? Visibility.Visible : Visibility.Collapsed;
        // Account sign-in (ChatGPT plan) has no API key at all.
        if (p.UsesAccountSignIn) { KeyLabel.Text = "Account"; KeyPanel.Visibility = Visibility.Collapsed; }
        else KeyLabel.Text = "API key";
        SignInHint.Visibility = p.UsesAccountSignIn ? Visibility.Collapsed : Visibility.Visible;
        ManageUsageButton.Visibility = p.UsesAccountSignIn ? Visibility.Visible : Visibility.Collapsed;
        SignInButton.Content = SignInLabel(p);
        GetKeyButton.Visibility = p.KeyUrl != null ? Visibility.Visible : Visibility.Collapsed;
        CtxLabel.Visibility = CtxBox.Visibility = p.Api == ApiKind.Ollama ? Visibility.Visible : Visibility.Collapsed;
        PrivacyNote.Text = p.UsesAccountSignIn
            ? "With your ChatGPT plan, the Claude conversation digest, your draft and (for GENERATE) the project overview " +
              "(README, CLAUDE.md and file list) are sent to OpenAI, and the calls count against your plan's usage limits " +
              "(on Plus, the 5-hour limit is shared with ChatGPT and your other apps). It isn't billed as API usage."
            : p.IsCloud
            ? $"With {p.Name}, the Claude conversation digest, your draft and (for GENERATE) the project overview " +
              "(README, CLAUDE.md and file list) are sent to that service, and usage is billed to your account."
            : "";
        PrivacyNote.Visibility = p.IsCloud ? Visibility.Visible : Visibility.Collapsed;
        UpdateKeyStatus();
    }

    string? EffectiveKey(ProviderInfo p) =>
        _pendingKeys.TryGetValue(p.Id, out var pending) ? pending : CredentialStore.Get(p);

    static string SignInLabel(ProviderInfo p) => p.UsesAccountSignIn ? "Continue with ChatGPT" : $"Sign in with {p.Name}";

    void UpdateKeyStatus()
    {
        var p = _shown;
        ForgetKeyButton.IsEnabled = false;
        if (p.UsesAccountSignIn)
        {
            var account = ChatGptAuth.Account;
            SignOutButton.Visibility = account != null ? Visibility.Visible : Visibility.Collapsed;
            SignInButton.Visibility = account == null ? Visibility.Visible : Visibility.Collapsed;
            if (account == null) SetStatus(KeyStatus, "Not signed in. Uses OpenAI's official Sign in with ChatGPT; no API key needed.", false);
            else if (!account.HasPlanUsage)
                SetStatus(KeyStatus, $"Signed in as {account.Email}, but this account's sign-in doesn't allow using its plan in other apps.", false);
            else SetStatus(KeyStatus, $"✓ Signed in as {account.Email} · Using ChatGPT plan", true);
            return;
        }
        SignOutButton.Visibility = Visibility.Collapsed;
        SignInButton.Visibility = Visibility.Visible;
        if (_pendingKeys.TryGetValue(p.Id, out var pending))
        {
            SetStatus(KeyStatus, pending == null ? "Key will be removed when you save." : $"New key {CredentialStore.Mask(pending)}: saved when you click Save.",
                pending == null ? null : true);
            ForgetKeyButton.IsEnabled = pending != null;
            return;
        }
        var (key, source, env) = CredentialStore.Resolve(p);
        switch (source)
        {
            case CredentialStore.KeySource.Saved:
                SetStatus(KeyStatus, $"✓ Signed in: key {CredentialStore.Mask(key!)} saved (encrypted for your Windows account).", true);
                ForgetKeyButton.IsEnabled = true;
                break;
            case CredentialStore.KeySource.Environment:
                SetStatus(KeyStatus, $"✓ Using {env} from your environment. Paste a key to override it.", true);
                break;
            default:
                SetStatus(KeyStatus, p.IsCloud ? "Not signed in." : "No key (most local servers don't need one).", p.IsCloud ? false : null);
                break;
        }
    }

    static void SetStatus(TextBlock block, string text, bool? ok)
    {
        block.Text = text;
        block.Foreground = ok switch
        {
            true => new SolidColorBrush(Color.FromRgb(0x7F, 0xD1, 0x8B)),
            false => new SolidColorBrush(Color.FromRgb(0xF0, 0x8A, 0x80)),
            null => (Brush)Application.Current.FindResource("MutedText"),
        };
    }

    // ───────────────────────────── sign-in & keys ─────────────────────────────

    void GetKey_Click(object sender, RoutedEventArgs e)
    {
        if (_shown.KeyUrl is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    void ForgetKey_Click(object sender, RoutedEventArgs e)
    {
        KeyBox.Password = "";
        _pendingKeys[_shown.Id] = null;
        UpdateKeyStatus();
    }

    void ManageUsage_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(ChatGptAuth.ManageUsageUrl) { UseShellExecute = true });

    async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        SignOutButton.IsEnabled = false;
        var revoked = await ChatGptAuth.SignOutAsync();
        SignOutButton.IsEnabled = true;
        UpdateKeyStatus();
        if (!revoked)
            SetStatus(KeyStatus, "Signed out on this PC, but OpenAI couldn't be reached to revoke access. " +
                                 "You can also disconnect Prompty in ChatGPT settings.", false);
    }

    async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_signInCts != null) { _signInCts.Cancel(); return; }
        var provider = _shown;
        if (provider.UsesAccountSignIn)
        {
            await SignInChatGptAsync();
            return;
        }
        using var cts = _signInCts = new CancellationTokenSource();
        SignInButton.Content = "Cancel sign-in";
        SetStatus(KeyStatus, "Waiting for you to approve Prompty in the browser…", null);
        try
        {
            var key = await OpenRouterSignIn.SignInAsync(cts.Token);
            CredentialStore.Set(provider.Id, key); // saved right away: the key now exists on the account
            _pendingKeys.Remove(provider.Id);
            if (_shown == provider) UpdateKeyStatus();
        }
        catch (OperationCanceledException) { if (_shown == provider) SetStatus(KeyStatus, "Sign-in cancelled.", false); }
        catch (Exception ex) { if (_shown == provider) SetStatus(KeyStatus, "Sign-in failed: " + ex.Message, false); }
        finally
        {
            _signInCts = null;
            SignInButton.Content = SignInLabel(provider);
        }
    }

    /// <summary>One-time confirmation, then OpenAI's browser sign-in. Loads the plan's models on success.</summary>
    async Task SignInChatGptAsync()
    {
        var first = MessageBox.Show(this,
            "Prompty will use your ChatGPT plan for its model calls: analysis, suggestions, rewrites and the prompt generator.\n\n" +
            "• These calls count against your plan's usage limits. On Plus, the 5-hour limit is shared with ChatGPT and your other apps.\n" +
            "• They aren't billed as API usage. When you reach a limit, Prompty pauses; it never switches to paid billing on its own.\n" +
            "• You can see or cap Prompty's share any time in ChatGPT settings → Usage, and sign out here.\n\n" +
            "Continue to sign in with ChatGPT in your browser?",
            "Use your ChatGPT plan in Prompty", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (first != MessageBoxResult.OK) return;

        using var cts = _signInCts = new CancellationTokenSource();
        SignInButton.Content = "Cancel sign-in";
        SetStatus(KeyStatus, "Waiting for you to sign in with ChatGPT in the browser…", null);
        try
        {
            var account = await ChatGptAuth.SignInAsync(cts.Token);
            LocalModelClient.ResumePlanUsage();
            UpdateKeyStatus();
            if (account.HasPlanUsage) Test_Click(this, new RoutedEventArgs()); // load the plan's models
        }
        catch (OperationCanceledException ex) { SetStatus(KeyStatus, ex.Message.Contains("cancelled") ? ex.Message : "Sign-in cancelled.", false); }
        catch (Exception ex) { SetStatus(KeyStatus, ex.Message, false); }
        finally
        {
            _signInCts = null;
            SignInButton.Content = SignInLabel(_shown);
        }
    }

    // ───────────────────────────── test ─────────────────────────────

    async void Test_Click(object sender, RoutedEventArgs e)
    {
        CaptureFields();
        var provider = _shown;
        var probe = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_work))!;
        var key = EffectiveKey(provider);
        if (provider.UsesAccountSignIn ? !ChatGptAuth.IsSignedIn : provider.IsCloud && string.IsNullOrEmpty(key))
        {
            SetStatus(TestResult, provider.UsesAccountSignIn ? "Click Continue with ChatGPT first." : "Sign in or paste an API key first.", false);
            return;
        }
        TestButton.IsEnabled = false;
        SetStatus(TestResult, $"Contacting {provider.Name}…", null);
        try
        {
            var models = await new LocalModelClient(probe, key ?? "").ListModelsAsync();
            if (provider.UsesAccountSignIn) LocalModelClient.ResumePlanUsage(); // an explicit test resumes after a usage-limit pause
            if (_shown != provider) return;
            ModelBox.ItemsSource = models;
            GeneratorModelBox.ItemsSource = models;
            ModelBox.Text = _work.Model;
            GeneratorModelBox.Text = _work.GeneratorModel;
            var missing = new[] { _work.Model, _work.GeneratorModel }
                .Where(m => m.Length > 0 && models.Count > 0 && !models.Contains(m)).Distinct().ToList();
            if (models.Count == 0)
                SetStatus(TestResult, "✓ Connected, but the provider listed no models. Type a model name.", true);
            else if (missing.Count > 0)
                SetStatus(TestResult, $"✓ Connected · {models.Count} models. Not in the list: {string.Join(", ", missing)}. Pick from the dropdowns.", false);
            else
                SetStatus(TestResult, $"✓ Connected · {models.Count} models available (open the dropdowns to pick).", true);
            if (models.Count > 0 && _work.Model.Length == 0) ModelBox.Text = models[0];
            if (models.Count > 0 && _work.GeneratorModel.Length == 0) GeneratorModelBox.Text = ModelBox.Text;
        }
        catch (Exception ex)
        {
            if (_shown == provider) SetStatus(TestResult, ex is TaskCanceledException ? $"No response from {probe.BaseUrl}." : ex.Message, false);
        }
        finally { TestButton.IsEnabled = true; }
    }

    // ───────────────────────────── save ─────────────────────────────

    void Save_Click(object sender, RoutedEventArgs e)
    {
        CaptureFields();
        foreach (var (id, key) in _pendingKeys) CredentialStore.Set(id, key);

        _settings.Provider = _work.Provider;
        _settings.BaseUrl = _work.BaseUrl;
        _settings.Model = _work.Model;
        // No generator model picked: use the main model rather than keeping another provider's choice.
        _settings.GeneratorModel = _work.GeneratorModel.Length > 0 ? _work.GeneratorModel : _work.Model;
        _settings.Profiles = _work.Profiles;
        _settings.Profiles[_work.Provider] = new ProviderProfile(_work.BaseUrl, _work.Model, _settings.GeneratorModel);
        if (int.TryParse(CtxBox.Text, out var ctx) && ctx >= 2048) _settings.ContextTokens = ctx;
        if (int.TryParse(CharsBox.Text, out var chars) && chars >= 1000) _settings.ContextMaxChars = chars;
        if (int.TryParse(DelayBox.Text, out var delay) && delay >= 500) _settings.AutoAnalyzeDelayMs = delay;
        _settings.CheckForUpdatesOnLaunch = CheckOnLaunchBox.IsChecked == true;
        if (int.TryParse(PortBox.Text, out var port) && port is > 1024 and < 65536) _settings.McpPort = port;
        DialogResult = true;
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        SetStatus(UpdateStatus, "Checking GitHub…", null);
        try
        {
            var update = await UpdateService.CheckAsync();
            if (update == null)
            {
                SetStatus(UpdateStatus, $"✓ You're on the latest version ({UpdateService.CurrentVersionText}).", true);
                return;
            }
            // A manual check always offers the update and clears earlier "skip" / "don't ask" choices.
            _settings.SkippedUpdateVersion = null;
            _settings.MuteUpdatesUntilMajor = 0;
            _settings.Save();
            SetStatus(UpdateStatus, $"Prompty {update.Version} is available.", true);
            new UpdateWindow(update, _settings) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            SetStatus(UpdateStatus, "Couldn't check for updates: " + ex.Message, false);
        }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }

    void CopyCommand_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(CommandBox.Text);

    async void Register_Click(object sender, RoutedEventArgs e)
    {
        RegisterResult.Text = "Running…";
        try
        {
            var psi = new ProcessStartInfo("claude", $"mcp add --transport http --scope user prompty {_settings.McpUrl}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            RegisterResult.Text = p.ExitCode == 0
                ? "Registered ✓ Restart any running Claude Code sessions to pick it up."
                : output.Contains("already exists") ? "Already registered ✓" : output.Trim();
        }
        catch (Exception ex)
        {
            RegisterResult.Text = $"Couldn't run 'claude' ({ex.Message}). Copy the command and run it in a terminal.";
        }
    }
}
