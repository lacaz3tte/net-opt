using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.App.Ui;

public sealed class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ContentControl _workspace = new();
    private readonly StackPanel _logPanel = new();
    private readonly ScrollViewer _logScroll = new();
    private readonly TextBlock _statusLine = new();
    private readonly TextBlock _elapsedLabel = new();
    private readonly ProgressBar _progress = new();
    private readonly StackPanel _servicePills = new();
    private readonly DispatcherTimer _clock;
    private bool _templateHooked;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        Title = "Network Optimizer";
        Width = 1080;
        Height = 720;
        MinWidth = 860;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = Theme.Brush(Theme.Chrome);
        FontFamily = Theme.UiFont;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 48,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            UseAeroCaptionButtons = false
        });

        Content = BuildShell();
        HookActivity();
        _vm.Changed += (_, _) => Dispatcher.Invoke(RefreshChrome);
        _vm.StatusTick += (_, _) => Dispatcher.Invoke(RefreshHeaderOnly);
        Loaded += async (_, _) =>
        {
            RefreshChrome();
            await _vm.InitializeAsync();
        };

        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clock.Tick += (_, _) =>
        {
            _elapsedLabel.Text = _vm.Busy ? $"running  {_vm.ElapsedLabel}" : "";
            _progress.IsIndeterminate = _vm.Busy && _vm.Screen is UiScreen.Searching or UiScreen.Monitor;
        };
        _clock.Start();
        RefreshChrome();
    }

    private UIElement BuildShell()
    {
        var root = new DockPanel { Background = Theme.Brush(Theme.Chrome) };
        var title = BuildTitleBar();
        DockPanel.SetDock(title, Dock.Top);
        WindowChrome.SetIsHitTestVisibleInChrome(title, true);
        // caption is still draggable except chrome buttons
        root.Children.Add(title);

        var console = BuildConsole();
        DockPanel.SetDock(console, Dock.Bottom);
        root.Children.Add(console);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sidebar = BuildSidebar();
        Grid.SetColumn(sidebar, 0);
        var mainDock = new DockPanel { LastChildFill = true };
        var header = BuildWorkspaceHeader();
        DockPanel.SetDock(header, Dock.Top);
        mainDock.Children.Add(header);
        mainDock.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(24, 8, 24, 16),
            Content = _workspace
        });
        var main = new Border
        {
            Background = Theme.Brush(Theme.Workspace),
            Child = mainDock
        };
        Grid.SetColumn(main, 1);
        body.Children.Add(sidebar);
        body.Children.Add(main);
        root.Children.Add(body);
        return root;
    }

    private Border BuildTitleBar()
    {
        var bar = new Grid { Height = 48, Background = Theme.Brush(Theme.Chrome) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) ToggleMax();
            else DragMove();
        };

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0)
        };
        left.Children.Add(new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(2),
            Background = Theme.Brush(Theme.Run),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        left.Children.Add(Theme.TextBlock("Network Optimizer", 13, FontWeights.SemiBold, Theme.Text));
        left.Children.Add(Theme.TextBlock("   local workspace", 12, FontWeights.Normal, Theme.Dim));
        Grid.SetColumn(left, 0);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var min = Theme.ChromeButton("—", (_, _) => WindowState = WindowState.Minimized);
        var max = Theme.ChromeButton("□", (_, _) => ToggleMax());
        var close = Theme.ChromeButton("✕", (_, _) => Close(), danger: true);
        WindowChrome.SetIsHitTestVisibleInChrome(min, true);
        WindowChrome.SetIsHitTestVisibleInChrome(max, true);
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        buttons.Children.Add(min);
        buttons.Children.Add(max);
        buttons.Children.Add(close);
        Grid.SetColumn(buttons, 1);

        bar.Children.Add(left);
        bar.Children.Add(buttons);
        return new Border { Child = bar, Background = Theme.Brush(Theme.Chrome) };
    }

    private UIElement BuildSidebar()
    {
        var stack = new StackPanel { Margin = new Thickness(12, 16, 12, 16) };
        stack.Children.Add(Theme.TextBlock("WORKSPACE", 11, FontWeights.Bold, Theme.Dim, new Thickness(8, 0, 0, 10)));
        stack.Children.Add(NavItem("Overview", true));
        stack.Children.Add(Theme.TextBlock("SEARCH", 11, FontWeights.Bold, Theme.Dim, new Thickness(8, 22, 0, 10)));
        stack.Children.Add(Theme.TextBlock("One button. The app starts bundled zapret/winws and tries known YouTube/Discord profiles until both work.", 11, FontWeights.Normal, Theme.Muted, new Thickness(8, 0, 8, 12)));
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 16) };
        modes.Children.Add(ModeChip("Fast", SearchMode.Fast));
        modes.Children.Add(ModeChip("Best", SearchMode.Best));
        stack.Children.Add(modes);
        stack.Children.Add(Theme.TextBlock("WinDivert needs Administrator. Fast keeps the first full success.", 11, FontWeights.Normal, Theme.Dim, new Thickness(8, 8, 8, 0)));

        return new Border
        {
            Background = Theme.Brush(Theme.Sidebar),
            BorderBrush = Theme.Brush(Theme.Chrome),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = stack
        };
    }

    private UIElement BuildWorkspaceHeader()
    {
        var header = new Border
        {
            Padding = new Thickness(24, 16, 24, 8),
            BorderBrush = Theme.Brush(Theme.Line),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        var col = new StackPanel();
        var top = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        _servicePills.Orientation = Orientation.Horizontal;
        DockPanel.SetDock(_servicePills, Dock.Left);
        _elapsedLabel.FontFamily = Theme.MonoFont;
        _elapsedLabel.FontSize = 12;
        _elapsedLabel.Foreground = Theme.Brush(Theme.Run);
        _elapsedLabel.VerticalAlignment = VerticalAlignment.Center;
        _elapsedLabel.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(_servicePills);
        top.Children.Add(_elapsedLabel);

        _progress.Height = 3;
        _progress.BorderThickness = new Thickness(0);
        _progress.Foreground = Theme.Brush(Theme.Run);
        _progress.Background = Theme.Brush(Theme.Input);
        _progress.IsIndeterminate = false;
        _progress.Margin = new Thickness(0, 0, 0, 10);

        _statusLine.FontSize = 13;
        _statusLine.Foreground = Theme.Brush(Theme.Muted);
        _statusLine.FontFamily = Theme.UiFont;
        _statusLine.TextWrapping = TextWrapping.Wrap;

        col.Children.Add(top);
        col.Children.Add(_progress);
        col.Children.Add(_statusLine);
        header.Child = col;
        return header;
    }

    private UIElement BuildConsole()
    {
        _logScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _logScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _logScroll.Height = 210;
        _logScroll.Content = _logPanel;
        _logScroll.Padding = new Thickness(16, 4, 16, 10);
        _logScroll.Background = Theme.Brush(Theme.ConsoleBg);

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(16, 8, 12, 4) };
        var title = Theme.TextBlock("CONSOLE", 11, FontWeights.Bold, Theme.Dim);
        var clear = Theme.GhostButton("Clear", (_, _) => _vm.ClearActivity(), 72);
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(clear);
        header.Children.Add(title);

        var box = new DockPanel { LastChildFill = true, Background = Theme.Brush(Theme.ConsoleBg) };
        var topLine = new Border
        {
            BorderBrush = Theme.Brush(Theme.Line),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = header
        };
        DockPanel.SetDock(topLine, Dock.Top);
        box.Children.Add(topLine);
        box.Children.Add(_logScroll);
        return box;
    }

    private void HookActivity()
    {
        if (_templateHooked) return;
        _templateHooked = true;
        _vm.Activity.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                _logPanel.Children.Clear();
                return;
            }

            if (e.NewItems is null) return;
            foreach (ActivityEntry entry in e.NewItems)
            {
                _logPanel.Children.Add(LogRow(entry));
            }

            _logScroll.ScrollToEnd();
        };
    }

    private static UIElement LogRow(ActivityEntry entry)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var time = new TextBlock
        {
            Text = entry.Time.ToString("HH:mm:ss"),
            FontFamily = Theme.MonoFont,
            FontSize = 11,
            Foreground = Theme.Brush(Theme.Dim)
        };
        var tag = new TextBlock
        {
            Text = entry.Tag.PadRight(5),
            FontFamily = Theme.MonoFont,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush(Theme.TagColor(entry.Tag))
        };
        var msg = new TextBlock
        {
            Text = entry.Message,
            FontFamily = Theme.MonoFont,
            FontSize = 11,
            Foreground = Theme.Brush(Theme.Text),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(time, 0);
        Grid.SetColumn(tag, 1);
        Grid.SetColumn(msg, 2);
        row.Children.Add(time);
        row.Children.Add(tag);
        row.Children.Add(msg);
        return row;
    }

    private void RefreshHeaderOnly()
    {
        var probe = _vm.Live ?? _vm.IdleProbe;
        _servicePills.Children.Clear();
        _servicePills.Children.Add(Theme.StatusPill("YouTube", probe?.YouTubeOk,
            probe is null ? null : $"{probe.YouTube.TotalMs} ms"));
        _servicePills.Children.Add(Theme.StatusPill("Discord", probe?.DiscordOk,
            probe is null ? null : $"{probe.Discord.TotalMs} ms"));
        var total = Math.Max(_vm.Total, _vm.Current);
        _statusLine.Text = _vm.Screen == UiScreen.Searching && total > 0
            ? $"{_vm.Message}    ·    candidate {_vm.Current}/{total}" +
              (_vm.Candidate is null ? "" : $"    ·    {_vm.Candidate.StrategyName}")
            : _vm.Message;
        _elapsedLabel.Text = _vm.Busy ? $"running  {_vm.ElapsedLabel}" : "";
        _progress.IsIndeterminate = _vm.Busy;
    }

    private void RefreshChrome()
    {
        RefreshHeaderOnly();
        _workspace.Content = _vm.Screen switch
        {
            UiScreen.Recovery => RecoveryView(),
            UiScreen.Searching => SearchingView(),
            UiScreen.Success => SuccessView(),
            UiScreen.Failed => FailedView(),
            UiScreen.Stopped => StoppedView(),
            UiScreen.Monitor => MonitorView(),
            _ => IdleView()
        };
    }

    private UIElement IdleView()
    {
        var stack = new StackPanel();
        stack.Children.Add(Theme.TextBlock("Overview", 22, FontWeights.SemiBold, Theme.Text, new Thickness(0, 4, 0, 6)));
        stack.Children.Add(Theme.TextBlock("Press one button. The app tries zapret desync profiles by itself until YouTube and Discord both work.", 13, FontWeights.Normal, Theme.Muted, new Thickness(0, 0, 0, 18)));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        actions.Children.Add(Theme.RoundButton("AUTO DISCOVER & FIX", Theme.Run, Colors.White, async (_, _) => await _vm.StartSearchAsync(), 220));
        actions.Children.Add(Theme.GhostButton("Test current path", async (_, _) => await _vm.TestCurrentAsync()));
        stack.Children.Add(actions);

        var meta = new StackPanel();
        meta.Children.Add(Kv("Current strategy", _vm.CurrentStrategyLabel));
        meta.Children.Add(Kv("Candidates tested", _vm.CandidatesTested.ToString()));
        meta.Children.Add(Kv("Search mode", _vm.Mode.ToString()));
        stack.Children.Add(Theme.Card(meta));
        return stack;
    }

    private UIElement RecoveryView()
    {
        var stack = new StackPanel();
        stack.Children.Add(Theme.TextBlock("Interrupted operation", 22, FontWeights.SemiBold, Theme.Warn, new Thickness(0, 4, 0, 8)));
        stack.Children.Add(Theme.TextBlock("Previous network configuration was not restored.", 14, FontWeights.Normal, Theme.Text, new Thickness(0, 0, 0, 8)));
        if (_vm.Pending is not null)
        {
            stack.Children.Add(Theme.TextBlock($"{_vm.Pending.StrategyId}  ·  {_vm.Pending.CandidateName}", 13, FontWeights.Normal, Theme.Muted, new Thickness(0, 0, 0, 16)));
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Theme.RoundButton("Restore snapshot", Theme.Blurple, Colors.White, async (_, _) => await _vm.RestoreCrashAsync()));
        row.Children.Add(Theme.GhostButton("Continue anyway", async (_, _) => await _vm.IgnoreCrashAsync()));
        stack.Children.Add(row);
        return stack;
    }

    private UIElement SearchingView()
    {
        var stack = new StackPanel();
        stack.Children.Add(Theme.TextBlock("Automatic discovery", 22, FontWeights.SemiBold, Theme.Text, new Thickness(0, 4, 0, 8)));
        var total = Math.Max(_vm.Total, _vm.Current);
        stack.Children.Add(Theme.TextBlock(
            total > 0 ? $"Candidate  {_vm.Current} / {total}" : "Preparing candidates…",
            16, FontWeights.SemiBold, Theme.Run, new Thickness(0, 0, 0, 12)));
        stack.Children.Add(Theme.TextBlock(_vm.Candidate?.StrategyName ?? "Working…", 18, FontWeights.SemiBold, Theme.Text));
        stack.Children.Add(Theme.TextBlock(_vm.Candidate?.DisplayName ?? "Watch the console — each zapret profile is tested against YouTube and Discord.", 13, FontWeights.Normal, Theme.Muted, new Thickness(0, 0, 0, 16)));

        var layers = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        layers.Children.Add(LayerLine("YouTube", _vm.Live?.YouTube));
        layers.Children.Add(LayerLine("Discord", _vm.Live?.Discord));
        stack.Children.Add(Theme.Card(layers));

        stack.Children.Add(Theme.RoundButton("Stop & restore", Theme.Danger, Colors.White, async (_, _) => await _vm.StopAsync(), 160));
        stack.Children.Add(Theme.TextBlock("Live output is in the console below. If a candidate hangs, it times out and the next one starts.", 12, FontWeights.Normal, Theme.Dim, new Thickness(0, 12, 0, 0)));
        return stack;
    }

    private UIElement SuccessView()
    {
        var stack = new StackPanel();
        stack.Children.Add(Theme.TextBlock("Working configuration found", 22, FontWeights.SemiBold, Theme.Success, new Thickness(0, 4, 0, 10)));
        stack.Children.Add(Theme.TextBlock(_vm.Working?.Candidate.DisplayName ?? _vm.Candidate?.DisplayName ?? "—", 16, FontWeights.SemiBold, Theme.Text, new Thickness(0, 0, 0, 16)));
        var lat = new StackPanel();
        lat.Children.Add(Kv("YouTube latency", $"{_vm.Live?.YouTube.TotalMs ?? _vm.Working?.YouTubeLatencyMs ?? 0} ms"));
        lat.Children.Add(Kv("Discord latency", $"{_vm.Live?.Discord.TotalMs ?? _vm.Working?.DiscordLatencyMs ?? 0} ms"));
        stack.Children.Add(Theme.Card(lat));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Theme.RoundButton("Keep configuration", Theme.Blurple, Colors.White, (_, _) => _vm.KeepConfiguration()));
        row.Children.Add(Theme.GhostButton("Restore original", async (_, _) => await _vm.RestoreOriginalAsync()));
        row.Children.Add(Theme.GhostButton("Monitor", async (_, _) => await _vm.StartMonitorAsync()));
        stack.Children.Add(row);
        return stack;
    }

    private UIElement FailedView() => ResultList("No working configuration", _vm.Message, "Try again");
    private UIElement StoppedView() => ResultList("Stopped", _vm.Message, "AUTO DISCOVER & FIX");

    private UIElement ResultList(string title, string message, string cta)
    {
        var stack = new StackPanel();
        stack.Children.Add(Theme.TextBlock(title, 22, FontWeights.SemiBold, Theme.Text, new Thickness(0, 4, 0, 8)));
        stack.Children.Add(Theme.TextBlock(message, 13, FontWeights.Normal, Theme.Muted, new Thickness(0, 0, 0, 12)));
        var panel = new StackPanel();
        if (_vm.LastResult is null || _vm.LastResult.Attempts.Count == 0)
        {
            panel.Children.Add(Theme.TextBlock("No candidates were fully tested.", 12, FontWeights.Normal, Theme.Dim));
        }
        else
        {
            foreach (var attempt in _vm.LastResult.Attempts.TakeLast(10).Reverse())
            {
                var line = $"{attempt.Index}. {attempt.Candidate.DisplayName}  —  {attempt.Result.ToLabel()}";
                if (attempt.Probe is not null)
                    line += $"   YT {(attempt.Probe.YouTubeOk ? "ok" : "fail")}  DC {(attempt.Probe.DiscordOk ? "ok" : "fail")}";
                panel.Children.Add(Theme.TextBlock(line, 12, FontWeights.Normal, Theme.Muted, new Thickness(0, 2, 0, 2)));
            }
        }

        stack.Children.Add(Theme.Card(panel));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Theme.RoundButton(cta, Theme.Run, Colors.White, async (_, _) => await _vm.StartSearchAsync()));
        row.Children.Add(Theme.GhostButton("Back", (_, _) => _vm.KeepConfiguration()));
        stack.Children.Add(row);
        return stack;
    }

    private UIElement MonitorView()
    {
        var stack = new StackPanel();
        stack.Children.Add(Theme.TextBlock("Monitor", 22, FontWeights.SemiBold, Theme.Text, new Thickness(0, 4, 0, 8)));
        stack.Children.Add(Theme.TextBlock("Rechecking the live path. Console shows each poll.", 13, FontWeights.Normal, Theme.Muted, new Thickness(0, 0, 0, 16)));
        stack.Children.Add(Theme.RoundButton("Stop monitor", Theme.Danger, Colors.White, async (_, _) => await _vm.StopAsync(), 160));
        return stack;
    }

    private static UIElement Kv(string key, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var k = Theme.TextBlock(key, 12, FontWeights.Normal, Theme.Dim);
        var v = Theme.TextBlock(value, 13, FontWeights.SemiBold, Theme.Text);
        Grid.SetColumn(v, 1);
        row.Children.Add(k);
        row.Children.Add(v);
        return row;
    }

    private static UIElement LayerLine(string name, ServiceProbeResult? s)
    {
        string Mark(LayerResult layer) =>
            layer.Status == OperationStatus.Unavailable && layer.Reason == "Not tested" ? "…" :
            layer.Ok ? "ok" : "fail";

        var text = s is null
            ? $"{name,-9}  DNS …   TCP …   TLS …   HTTP …"
            : $"{name,-9}  DNS {Mark(s.Dns),-4}  TCP {Mark(s.Tcp),-4}  TLS {Mark(s.Tls),-4}  HTTP {Mark(s.Http),-4}";
        return new TextBlock
        {
            Text = text,
            FontFamily = Theme.MonoFont,
            FontSize = 12,
            Foreground = Theme.Brush(Theme.Text),
            Margin = new Thickness(0, 4, 0, 4)
        };
    }

    private Border NavItem(string label, bool active)
    {
        return new Border
        {
            Background = Theme.Brush(active ? Theme.Input : Theme.Sidebar),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 4),
            Child = Theme.TextBlock(label, 13, active ? FontWeights.SemiBold : FontWeights.Normal, active ? Theme.Text : Theme.Muted)
        };
    }

    private Border ModeChip(string label, SearchMode mode)
    {
        var selected = _vm.Mode == mode;
        var border = new Border
        {
            Background = Theme.Brush(selected ? Theme.Blurple : Theme.Input),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 5, 12, 5),
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock
            {
                Text = label,
                Foreground = Theme.Brush(Theme.Text),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = Theme.UiFont
            }
        };
        WindowChrome.SetIsHitTestVisibleInChrome(border, true);
        border.MouseLeftButtonUp += (_, _) => { _vm.Mode = mode; RefreshChrome(); };
        return border;
    }

    private void ToggleMax() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
