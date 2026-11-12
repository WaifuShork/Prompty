namespace Prompty.Services;

/// <summary>
/// Shared state between the editor UI and the MCP server. MCP calls arrive on thread-pool
/// threads; events are raised there and the UI marshals them to the dispatcher.
/// </summary>
public sealed class PromptBridge
{
    readonly object _gate = new();
    readonly List<TaskCompletionSource<string>> _waiters = [];

    public volatile string CurrentDraft = "";

    public string? SharedContext { get; private set; }
    public string? ReadyPrompt { get; private set; }
    public string? PendingRequest { get; private set; }

    public event Action<string>? ContextShared;
    public event Action<string>? DraftSeeded;
    public event Action? WaitersChanged;

    public int WaitingCount { get { lock (_gate) return _waiters.Count; } }

    public void ShareContext(string context)
    {
        SharedContext = context;
        ContextShared?.Invoke(context);
    }

    public void SeedDraft(string text) => DraftSeeded?.Invoke(text);

    /// <summary>Marks the prompt as finished. Returns how many blocked Claude calls received it.</summary>
    public int Submit(string text)
    {
        List<TaskCompletionSource<string>> waiters;
        lock (_gate)
        {
            ReadyPrompt = text;
            PendingRequest = null;
            waiters = [.. _waiters];
            _waiters.Clear();
        }
        foreach (var w in waiters) w.TrySetResult(text);
        if (waiters.Count > 0) WaitersChanged?.Invoke();
        return waiters.Count;
    }

    public async Task<string?> WaitForPromptAsync(string? request, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiters.Add(tcs);
            if (!string.IsNullOrWhiteSpace(request)) PendingRequest = request;
        }
        WaitersChanged?.Invoke();

        try
        {
            return await tcs.Task.WaitAsync(timeout, ct);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            bool removed;
            lock (_gate) removed = _waiters.Remove(tcs);
            if (removed) WaitersChanged?.Invoke();
        }
    }

    /// <summary>Returns the finished prompt once; subsequent calls fall back to the live draft.</summary>
    public string TakeReadyPrompt()
    {
        lock (_gate)
        {
            var p = ReadyPrompt;
            ReadyPrompt = null;
            return p ?? CurrentDraft;
        }
    }
}
