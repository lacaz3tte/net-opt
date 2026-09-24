using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.App.Ui;

public enum UiScreen
{
    Idle,
    Recovery,
    Searching,
    Success,
    Failed,
    Stopped,
    Monitor
}

public sealed class ActivityEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Tag { get; init; } = "INFO";
    public string Message { get; init; } = "";
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;
    private CombinedProbeResult? _idleProbe;
    private PendingOperation? _pending;
    private OptimizationResult? _lastResult;
    private UiScreen _screen = UiScreen.Idle;
    private string _phase = "";
    private string _message = "Ready.";
    private int _current;
    private int _total;
    private Candidate? _candidate;
    private CombinedProbeResult? _live;
    private bool _busy;
    private SearchMode _mode;
    private DateTime _busyStarted;

    public MainViewModel(AppServices services)
    {
        _services = services;
        _mode = services.Configuration.Search.Mode;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Changed;
    public event EventHandler? StatusTick;

    public ObservableCollection<ActivityEntry> Activity { get; } = new();

    public UiScreen Screen { get => _screen; private set { _screen = value; } }
    public string Phase { get => _phase; private set => _phase = value; }
    public string Message { get => _message; private set => _message = value; }
    public int Current { get => _current; private set => _current = value; }
    public int Total { get => _total; private set => _total = value; }
    public Candidate? Candidate { get => _candidate; private set => _candidate = value; }
    public CombinedProbeResult? Live { get => _live; private set => _live = value; }
    public CombinedProbeResult? IdleProbe { get => _idleProbe; private set => _idleProbe = value; }
    public PendingOperation? Pending { get => _pending; private set => _pending = value; }
    public OptimizationResult? LastResult { get => _lastResult; private set => _lastResult = value; }
    public bool Busy { get => _busy; private set => _busy = value; }
    public DateTime BusyStarted => _busyStarted;
    public SearchMode Mode { get => _mode; set { _mode = value; _services.Configuration.Search.Mode = value; Raise(); } }
    public WorkingConfiguration? Working { get; private set; }
    public string CurrentStrategyLabel =>
        Working?.Candidate.DisplayName ?? LastResult?.WinningCandidate?.DisplayName ?? "—";
    public int CandidatesTested => LastResult?.Attempts.Count ?? Current;
    public string ElapsedLabel
    {
        get
        {
            if (!Busy) return "";
            var elapsed = DateTime.UtcNow - _busyStarted;
            return $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }
    }

    public async Task InitializeAsync()
    {
        Append("APP", "Network Optimizer started");
        _pending = await _services.CrashRecovery.DetectAsync(CancellationToken.None);
        if (_pending is not null)
        {
            Screen = UiScreen.Recovery;
            Message = "Previous network configuration was not restored.";
            Append("WARN", $"Interrupted operation: {_pending.StrategyId} / {_pending.CandidateName}");
            Raise();
            return;
        }

        Append("TEST", "Checking current YouTube and Discord path...");
        await RefreshIdleAsync();
        var yt = IdleProbe?.YouTubeOk == true ? "OK" : "down";
        var dc = IdleProbe?.DiscordOk == true ? "OK" : "down";
        Append("TEST", $"Current path — YouTube {yt} · Discord {dc}");

        var saved = await _services.Saved.CheckAsync(CancellationToken.None);
        if (saved.Present && saved.Working)
        {
            Working = saved.Configuration;
            IdleProbe = saved.Probe ?? IdleProbe;
            Message = "Saved configuration is working.";
            Append("OK", $"Saved configuration still works: {Working?.Candidate.DisplayName}");
        }
        else if (saved.Present && !saved.Working)
        {
            Message = "Saved configuration failed. Starting automatic rediscovery...";
            Append("FAIL", "Saved configuration is down. Starting AUTO DISCOVER.");
            Raise();
            await StartSearchAsync();
            return;
        }

        Raise();
    }

    public async Task StartSearchAsync()
    {
        if (Busy) return;
        Busy = true;
        _busyStarted = DateTime.UtcNow;
        Screen = UiScreen.Searching;
        Current = 0;
        Total = 0;
        Candidate = null;
        Live = null;
        Message = "Creating network snapshot...";
        Activity.Clear();
        Append("RUN", "AUTO DISCOVER & FIX started");
        Raise();
        _cts = new CancellationTokenSource();
        var progress = new Progress<OptimizationProgress>(p =>
        {
            Phase = p.Phase;
            if (!string.IsNullOrWhiteSpace(p.Message))
            {
                Message = p.Message;
            }

            var indexChanged = p.CurrentIndex > 0 && p.CurrentIndex != Current;
            if (p.CurrentIndex > 0) Current = p.CurrentIndex;
            if (p.Total > 0) Total = p.Total;
            var candidateChanged = p.Candidate is not null && p.Candidate.Id != Candidate?.Id;
            Candidate = p.Candidate ?? Candidate;
            Live = p.LiveProbe ?? Live;
            var tag = string.IsNullOrWhiteSpace(p.Tag) ? TagFromPhase(p.Phase) : p.Tag;
            if (!string.IsNullOrWhiteSpace(p.Message))
            {
                Append(tag, p.Message);
            }

            if (p.Phase == "probe-step" && !indexChanged && !candidateChanged)
            {
                StatusTick?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Raise();
            }
        });

        try
        {
            var result = await _services.Optimizer.RunAsync(progress, _cts.Token);
            LastResult = result;
            if (result.Success)
            {
                Working = new WorkingConfiguration
                {
                    StrategyId = result.WinningCandidate!.StrategyId,
                    StrategyName = result.WinningCandidate.StrategyName,
                    Candidate = result.WinningCandidate,
                    YouTubeLatencyMs = result.WinningProbe?.YouTube.TotalMs ?? 0,
                    DiscordLatencyMs = result.WinningProbe?.Discord.TotalMs ?? 0,
                    Score = result.WinningScore?.Total ?? 0
                };
                Live = result.WinningProbe;
                IdleProbe = result.WinningProbe;
                Screen = UiScreen.Success;
                Message = "WORKING CONFIGURATION FOUND";
                Append("OK", $"Kept {result.WinningCandidate.DisplayName} — YouTube {result.WinningProbe?.YouTube.TotalMs} ms, Discord {result.WinningProbe?.Discord.TotalMs} ms");
            }
            else if (result.Cancelled)
            {
                Screen = UiScreen.Stopped;
                Message = result.Message ?? "Stopped. Original configuration restored.";
                Append("STOP", Message);
            }
            else
            {
                Screen = UiScreen.Failed;
                Message = result.Message ?? "No working configuration found.";
                Append("FAIL", Message);
            }
        }
        catch (Exception ex)
        {
            _services.Log.Error("ui search failed", ex);
            Screen = UiScreen.Failed;
            Message = "Search failed. Original configuration restored.";
            Append("FAIL", Message);
        }
        finally
        {
            Busy = false;
            _cts.Dispose();
            _cts = null;
            Raise();
        }
    }

    public Task StopAsync()
    {
        if (_cts is null) return Task.CompletedTask;
        Message = "Stopping — rolling back to the original snapshot...";
        Append("STOP", Message);
        Raise();
        _cts.Cancel();
        return Task.CompletedTask;
    }

    public async Task RestoreOriginalAsync()
    {
        Busy = true;
        Message = "Restoring original configuration...";
        Append("ROLL", Message);
        Raise();
        try
        {
            await _services.Optimizer.RestoreOriginalAsync(CancellationToken.None);
            Working = null;
            await RefreshIdleAsync();
            Screen = UiScreen.Idle;
            Message = "Original configuration restored.";
            Append("OK", Message);
        }
        catch (Exception ex)
        {
            _services.Log.Error("restore failed", ex);
            Message = "Restore failed.";
            Append("FAIL", Message);
        }
        finally
        {
            Busy = false;
            Raise();
        }
    }

    public async Task RestoreCrashAsync()
    {
        await _services.CrashRecovery.RestoreAsync(CancellationToken.None);
        Pending = null;
        await RefreshIdleAsync();
        Screen = UiScreen.Idle;
        Message = "Previous state restored.";
        Append("OK", Message);
    }

    public async Task IgnoreCrashAsync()
    {
        await _services.CrashRecovery.IgnoreAsync(CancellationToken.None);
        Pending = null;
        await RefreshIdleAsync();
        Screen = UiScreen.Idle;
        Append("INFO", "Interrupted snapshot ignored.");
    }

    public void KeepConfiguration()
    {
        Screen = UiScreen.Idle;
        Message = "Working configuration kept.";
        IdleProbe = Live ?? IdleProbe;
        Append("OK", Message);
        Raise();
    }

    public async Task StartMonitorAsync()
    {
        if (Busy) return;
        Busy = true;
        _busyStarted = DateTime.UtcNow;
        Screen = UiScreen.Monitor;
        Message = "Monitor running.";
        Append("RUN", "Monitor started — rechecking YouTube and Discord on an interval.");
        Raise();
        _cts = new CancellationTokenSource();
        var progress = new Progress<OptimizationProgress>(p =>
        {
            Message = p.Message;
            Live = p.LiveProbe ?? Live;
            Candidate = p.Candidate ?? Candidate;
            var tag = string.IsNullOrWhiteSpace(p.Tag) ? TagFromPhase(p.Phase) : p.Tag;
            if (!string.IsNullOrWhiteSpace(p.Message)) Append(tag, p.Message);
            if (p.Phase == "rediscover")
            {
                Screen = UiScreen.Searching;
                Raise();
                return;
            }

            if (p.Phase == "success") Screen = UiScreen.Monitor;
            if (p.Phase is "monitor" or "probe-step")
            {
                StatusTick?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Raise();
            }
        });
        try
        {
            await _services.CreateMonitor().RunAsync(
                TimeSpan.FromSeconds(_services.Configuration.Monitor.IntervalSeconds),
                _services.Configuration.Monitor.AutoRediscover,
                progress,
                _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Screen = UiScreen.Idle;
            Message = "Monitor stopped.";
            Append("STOP", Message);
        }
        finally
        {
            Busy = false;
            _cts?.Dispose();
            _cts = null;
            await RefreshIdleAsync();
        }
    }

    public async Task TestCurrentAsync()
    {
        if (Busy) return;
        Append("TEST", "Manual test of the current path...");
        await RefreshIdleAsync();
        Append(IdleProbe?.IsFullSuccess == true ? "OK" : "FAIL",
            $"YouTube {(IdleProbe?.YouTubeOk == true ? "OK" : "down")} {IdleProbe?.YouTube.TotalMs ?? 0} ms · Discord {(IdleProbe?.DiscordOk == true ? "OK" : "down")} {IdleProbe?.Discord.TotalMs ?? 0} ms");
    }

    public void ClearActivity()
    {
        Activity.Clear();
        Append("INFO", "Console cleared.");
    }

    private async Task RefreshIdleAsync()
    {
        try
        {
            IdleProbe = await _services.Probe.ProbeAsync(
                ProbeTransport.SystemDefault.WithStep(msg => Append("TEST", msg)),
                CancellationToken.None);
            Live = IdleProbe;
        }
        catch
        {
            IdleProbe = null;
        }

        Raise();
    }

    private void Append(string tag, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Append(tag, message));
            return;
        }
        var last = Activity.Count > 0 ? Activity[^1] : null;
        if (last is not null && last.Tag == tag && last.Message == message) return;
        Activity.Add(new ActivityEntry { Tag = tag, Message = message, Time = DateTime.Now });
        while (Activity.Count > 400)
        {
            Activity.RemoveAt(0);
        }
    }

    private static string TagFromPhase(string phase) => phase switch
    {
        "snapshot" => "SNAP",
        "discover" or "generate" => "DISC",
        "apply" or "apply-winner" => "APPLY",
        "test" or "probe-current" or "probe-step" or "monitor" => "TEST",
        "rollback" or "rollback-original" => "ROLL",
        "stabilize" => "WAIT",
        "success" => "OK",
        "stopped" or "rediscover" => "RUN",
        _ => "INFO"
    };

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
