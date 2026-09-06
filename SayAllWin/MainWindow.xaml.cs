using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SayAll.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Application = System.Windows.Application;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using TabControl = System.Windows.Controls.TabControl;
using MessageBox = System.Windows.MessageBox;

namespace SayAll;

/// <summary>
/// 设置主窗口：连接与语音 / 按键映射 / 统计 / 回眸 / 关于。
/// 视觉对齐 macOS 原版（侧边栏导航 + 卡片式内容）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppState _state;
    private bool _updating;
    private string _statusText = "—";

    public MainWindow(AppState state)
    {
        // 必须先赋值 _state 再 InitializeComponent：
        // XAML 中 StatsDayRadio 的 IsChecked="True" 会在解析时触发 Checked 事件
        _state = state;
        InitializeComponent();
        _state.UiRefreshRequested += OnUiRefresh;
        SizeChanged += (_, _) => ScheduleWiringRedraw();
        Loaded += (_, _) => RefreshAll();
        RefreshAll();
    }

    private void OnUiRefresh()
    {
        Dispatcher.BeginInvoke(RefreshAll);
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // 页签切换时 TabControl 重新挂载内容，需在布局完成后重画引线/刷新数据
        if (e.Source is TabControl tab)
        {
            if (tab.SelectedItem == StatsTab) RefreshStats();
            else if (tab.SelectedItem == RecordingTab) RefreshTranscripts();
            else if (tab.SelectedItem == MappingTab) ScheduleWiringRedraw();
        }
    }

    public void RefreshAll()
    {
        _updating = true;
        try
        {
            RefreshLocalizedTexts();
            RefreshBridgeStatus();
            RefreshAudioDevicesPreservingSelection();
            RefreshVoiceSettings();
            RefreshMapping();
            RefreshStats();
            RefreshTranscripts();
        }
        finally
        {
            _updating = false;
        }
    }

    private void RefreshLocalizedTexts()
    {
        Title = L10n.T("window.title");
        ReconnectButton.Content = L10n.T("tray.reconnect");

        ConnectionTab.Header = L10n.T("page.connection");
        MappingTab.Header = L10n.T("page.mapping");
        StatsTab.Header = L10n.T("page.stats");
        RecordingTab.Header = L10n.T("page.recording");
        AboutTab.Header = L10n.T("page.about");

        ConnectionTitle.Text = L10n.T("page.connection");
        ConnectionGroup.Header = L10n.T("connection.group.status");
        VoiceGroup.Header = L10n.T("connection.audio.card");
        VoiceKeyGroup.Header = L10n.T("connection.voice_key.card");

        AudioDeviceLabel.Text = L10n.T("connection.audio.device");
        RefreshAudioButton.Content = L10n.T("connection.audio.refresh");
        AudioHintText.Text = L10n.T("connection.audio.virtual_hint");
        GainLabel.Text = L10n.T("connection.audio.gain");
        GainValueLabel.Text = $"{_state.Settings.GainDB:0.#} dB";
        VoiceKeyModeLabel.Text = L10n.T("connection.voice_key.label");
        TestToneButton.Content = L10n.T("connection.audio.test_tone");

        MappingTitle.Text = L10n.T("page.mapping");
        MappingEnabledCheck.Content = L10n.T("button_mapping.toggle");
        FnTapCheck.Content = L10n.T("connection.voice_key.fn_tap");
        ResetMappingButton.Content = L10n.T("mapping.reset");

        StatsTitle.Text = L10n.T("page.stats");
        StatsDayRadio.Content = L10n.T("stats.range.day");
        StatsWeekRadio.Content = L10n.T("stats.range.week");
        StatsAllRadio.Content = L10n.T("stats.range.all");
        StatsLocalTag.Text = L10n.T("stats.local_only");
        StatsPressesTitle.Text = L10n.T("stats.presses_card");
        StatsVoiceTitle.Text = L10n.T("stats.voice_card");
        StatsTopTitle.Text = L10n.T("stats.top_title");
        StatsTopHint.Text = L10n.T("stats.top_hint");
        StatsTopEmpty.Text = L10n.T("stats.empty_top");

        RecordingTitle.Text = L10n.T("page.recording");
        TranscriptEnabledCheck.Content = L10n.T("recording.transcript.enable");
        RecordingEnabledCheck.Content = L10n.T("recording.audio.enable");
        OpenRecordingsButton.Content = L10n.T("recording.open_folder");

        AboutTitle.Text = "无线麦 SayAll（Windows）";
        AboutGroup.Header = L10n.T("page.about");
        PrefsGroup.Header = L10n.T("about.prefs");
        LanguageLabel.Text = L10n.T("about.language");
        AutoStartCheck.Content = L10n.T("about.auto_start");
        McpCheck.Content = L10n.T("about.mcp_enable");
        McpHint.Text = "SayAll.exe --mcp-stdio（stdio JSON-RPC，只读访问回眸历史，不监听网络）";
        GithubButton.Content = L10n.T("about.github");
        ReleasesButton.Content = "GitHub Releases";
        LogButton.Content = L10n.T("about.log");
        VersionLabel.Text = $"{L10n.T("about.version")}: {VersionInfo.Version} ({VersionInfo.Build})";
        AboutDescriptionText.Text = L10n.IsZh
            ? "把小米蓝牙遥控器变成电脑语音输入与快捷键工具；蓝牙直连 + ATVV 语音通道。"
            : "Turn a Xiaomi Bluetooth remote into a voice input & shortcut tool; BLE + ATVV voice channel.";

        if (LanguageCombo.Items.Count == 0)
        {
            LanguageCombo.Items.Add(L10n.T("about.language.system"));
            LanguageCombo.Items.Add(L10n.T("about.language.zh"));
            LanguageCombo.Items.Add(L10n.T("about.language.en"));
        }
        LanguageCombo.SelectedIndex = L10n.Language switch
        {
            AppLanguage.System => 0,
            AppLanguage.zh_Hans => 1,
            _ => 2,
        };
    }

    private void RefreshBridgeStatus()
    {
        var bridge = _state.Bridge;
        var (key, args) = bridge.State switch
        {
            BluetoothBridgeState.Stopped => ("common.status.stopped", null),
            BluetoothBridgeState.BluetoothUnavailable => ("bluetooth.status.off", null),
            BluetoothBridgeState.Scanning => ("connection.status.searching", null),
            BluetoothBridgeState.Connecting => ("connection.status.connecting", null),
            BluetoothBridgeState.Discovering => ("connection.status.initializing_voice", null),
            BluetoothBridgeState.Ready => ("connection.status.connected_to_device", bridge.ReadyDeviceName ?? "MI RC"),
            BluetoothBridgeState.Reconnecting => ("connection.status.reconnecting", null),
            BluetoothBridgeState.Failed => ("connection.error.voice_service_missing", null),
            _ => ("common.status.unknown", null),
        };
        _statusText = args is null ? L10n.T(key) : L10n.T(key, args);
        BridgeStateLabel.Text = _statusText;
        StateDot.Fill = bridge.State switch
        {
            BluetoothBridgeState.Ready => FindBrush("GreenBrush"),
            BluetoothBridgeState.Scanning or BluetoothBridgeState.Connecting
                or BluetoothBridgeState.Discovering or BluetoothBridgeState.Reconnecting => FindBrush("OrangeBrush"),
            _ => FindBrush("RedBrush"),
        };

        var battery = bridge.BatteryLevel;
        var details = new List<string>();
        if (battery is not null) details.Add($"{L10n.T("connection.battery")}: {battery}%");
        if (bridge.RemoteModel is { } model) details.Add($"{L10n.T("connection.model")}: {model}");
        BatteryLabel.Text = details.Count > 0 ? string.Join(" · ", details) : "";

        ConnectionHintText.Text = bridge.State == BluetoothBridgeState.Ready
            ? (L10n.IsZh ? "按住语音键说话，松开停止；目标应用选择虚拟麦克风（CABLE Output）作为输入设备。"
                         : "Press and hold the voice key to talk; release to stop. Target apps select the virtual mic (CABLE Output) as input.")
            : bridge.State == BluetoothBridgeState.Failed
                ? (L10n.IsZh ? "连接失败：请确认遥控器已配对（长按主页+菜单进入配对模式）并在系统蓝牙中连接。"
                             : "Connection failed: make sure the remote is paired (hold Home+Menu to pair) and connected in Bluetooth settings.")
                : "";
    }

    private static Brush FindBrush(string key) => Application.Current.Resources[key] as Brush ?? Brushes.Gray;

    private void RefreshAudioDevicesPreservingSelection()
    {
        var devices = AudioDeviceCatalog.OutputDevices();
        var selected = _state.Settings.SelectedAudioDeviceId;

        _updating = true;
        try
        {
            // 设备数量很少，每次直接重建，避免类型/索引比较的边界问题
            AudioDeviceCombo.ItemsSource = null;
            AudioDeviceCombo.Items.Clear();
            foreach (var device in devices)
            {
                var label = device.IsVirtualCable ? $"★ {device.Name}" : device.Name;
                AudioDeviceCombo.Items.Add(new ComboItem(device.Id, label));
            }

            var index = devices.FindIndex(d => d.Id == selected);
            if (index < 0 && devices.Count > 0)
            {
                // 无选中记录时，默认选择虚拟声卡（VB-CABLE 等）
                index = devices.FindIndex(d => d.IsVirtualCable);
                if (index < 0) index = 0;
            }
            AudioDeviceCombo.SelectedIndex = index;

            GainSlider.Value = _state.Settings.GainDB;
            GainValueLabel.Text = $"{_state.Settings.GainDB:0.#} dB";
            MappingEnabledCheck.IsChecked = _state.Settings.CustomMappingEnabled;
            FnTapCheck.IsChecked = _state.Settings.VoiceFnTapModeEnabled;
            TranscriptEnabledCheck.IsChecked = _state.Settings.LocalTranscriptHistoryEnabled;
            RecordingEnabledCheck.IsChecked = _state.Settings.LocalOriginalAudioRecordingEnabled;
            AutoStartCheck.IsChecked = _state.Settings.LaunchAtLogin;
            McpCheck.IsChecked = _state.Settings.McpEnabled;

            if (VoiceKeyModeCombo.Items.Count == 0)
            {
                VoiceKeyModeCombo.Items.Add(L10n.T("connection.voice_key.mode.fn"));
                VoiceKeyModeCombo.Items.Add(L10n.T("connection.voice_key.mode.left_command"));
                VoiceKeyModeCombo.Items.Add(L10n.T("connection.voice_key.mode.right_command"));
            }
            VoiceKeyModeCombo.SelectedIndex = _state.Settings.VoiceKeyMode switch
            {
                VoiceKeyMode.Function => 0,
                VoiceKeyMode.LeftCommand => 1,
                _ => 2,
            };
        }
        finally
        {
            _updating = false;
        }
    }

    private void RefreshVoiceSettings() => RefreshLocalizedTexts();

    private static readonly RemoteButton[] LeftColumnButtons =
    [
        // 与 macOS 原版一致：电源、上、左、返回、主页、菜单
        RemoteButton.Power, RemoteButton.Up, RemoteButton.Left,
        RemoteButton.Back, RemoteButton.Home, RemoteButton.Menu,
    ];

    private static readonly RemoteButton[] RightColumnButtons =
    [
        // 与 macOS 原版一致：右、确定、下、音量+、音量−、TV（语音键说明卡由 RefreshMapping 插入在最前）
        RemoteButton.Right, RemoteButton.Ok, RemoteButton.Down,
        RemoteButton.VolumeUp, RemoteButton.VolumeDown, RemoteButton.Tv,
    ];

    private static readonly ButtonAction[] RepeatActions =
    [
        ButtonAction.ArrowUp, ButtonAction.ArrowDown, ButtonAction.ArrowLeft, ButtonAction.ArrowRight,
        ButtonAction.DeleteBackward, ButtonAction.ScrollUp, ButtonAction.ScrollDown,
        ButtonAction.VolumeUp, ButtonAction.VolumeDown, ButtonAction.VolumeMute, ButtonAction.PlayPause,
    ];

    private static readonly ButtonAction[] NotRepeatableActions =
    [
        ButtonAction.Disabled, ButtonAction.Escape, ButtonAction.ReturnKey, ButtonAction.CommandReturn,
        ButtonAction.ShiftReturn, ButtonAction.CommandCopy, ButtonAction.CommandPaste,
        ButtonAction.CommandClose, ButtonAction.CommandQuit, ButtonAction.CommandCut,
        ButtonAction.CommandSelectAll, ButtonAction.CommandUndo, ButtonAction.CommandRedo,
        ButtonAction.CommandFind, ButtonAction.CommandSave, ButtonAction.CommandDelete,
        ButtonAction.ShowDesktop, ButtonAction.ContextMenu, ButtonAction.AppSwitcher,
        ButtonAction.PreviousCommandLeft, ButtonAction.NextCommandRight, ButtonAction.CustomShortcut,
        ButtonAction.FocusInput, ButtonAction.ToggleLongRecording, ButtonAction.OpenCodex,
        ButtonAction.OpenClaude, ButtonAction.OpenCursor, ButtonAction.OpenWeChat,
        ButtonAction.OpenWeCom, ButtonAction.OpenNeteaseMusic, ButtonAction.OpenChrome,
        ButtonAction.OpenZed, ButtonAction.OpenSlack, ButtonAction.OpenTerminal, ButtonAction.OpenNotepad,
        ButtonAction.OpenCustomApplication,
    ];

    private void RefreshMapping()
    {
        MappingEnabledCheck.IsChecked = _state.Settings.CustomMappingEnabled;
        MappingStatusText.Text = _state.HidMonitor?.StatusKey switch
        {
            "button_mapping.status.connected" => L10n.T(_state.HidMonitor!.StatusKey),
            "button_mapping.status.connected_fallback" => L10n.T(_state.HidMonitor!.StatusKey),
            "button_mapping.status.disconnected" => L10n.T(_state.HidMonitor!.StatusKey),
            "button_mapping.status.waiting_for_device" => L10n.T(_state.HidMonitor!.StatusKey),
            "button_mapping.status.system_managed" => L10n.T(_state.HidMonitor!.StatusKey),
            _ => L10n.T("button_mapping.status.disabled"),
        };

        var actionList = NotRepeatableActions.Concat(RepeatActions).ToList();
        // 布局参照 macOS 原版：左列 电源/上/左/返回/主页/菜单，右列 语音键(说明)/右/确定/下/音量+/音量−/TV
        MappingLeftItems.ItemsSource = LeftColumnButtons
            .Select(b => new MappingRow(b, _state.Settings, actionList))
            .ToList();
        var rightCards = new List<IMappingCard> { new VoiceKeyInfoRow() };
        rightCards.AddRange(RightColumnButtons.Select(b => new MappingRow(b, _state.Settings, actionList)));
        MappingRightItems.ItemsSource = rightCards;
        ScheduleWiringRedraw();
    }

    // ---------------- 引线（按键卡 ↔ 遥控器照片） ----------------

    /// <summary>照片显示视口内（0-1 相对坐标）各按键的引导点。来源：对透明底照片做暗色连通域检测得到的按键圆心。</summary>
    internal static readonly IReadOnlyDictionary<RemoteButton, Point> RemoteKeyAnchors =
        new Dictionary<RemoteButton, Point>
        {
            [RemoteButton.Power] = new(0.295, 0.098),   // 顶部左键（座圈亮、图标暗，目测校准）
            [RemoteButton.Up] = new(0.498, 0.158),      // 方向环上缘
            [RemoteButton.Ok] = new(0.498, 0.243),      // 环中心
            [RemoteButton.Down] = new(0.498, 0.330),    // 环下缘
            [RemoteButton.Left] = new(0.325, 0.384),    // < 键
            [RemoteButton.Right] = new(0.828, 0.243),   // 环右缘
            [RemoteButton.Back] = new(0.300, 0.350),    // 无实体键：机身左缘引导位
            [RemoteButton.Home] = new(0.325, 0.470),
            [RemoteButton.VolumeUp] = new(0.672, 0.387),   // 胶囊上半圆心
            [RemoteButton.VolumeDown] = new(0.672, 0.467), // 胶囊下半圆心
            [RemoteButton.Menu] = new(0.325, 0.558),
            [RemoteButton.Tv] = new(0.667, 0.559),
        };

    /// <summary>照片显示视口内语音键位置（用于语音键说明卡的引线）。</summary>
    internal static readonly Point VoiceKeyAnchor = new(0.708, 0.098);

    private bool _wiringScheduled;

    private void ScheduleWiringRedraw()
    {
        if (_wiringScheduled) return;
        _wiringScheduled = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _wiringScheduled = false;
            RedrawWiring();
        }), DispatcherPriority.Loaded);
    }

    private void RedrawWiring()
    {
        if (WiringCanvas is null || RemotePhoto is null || RemotePhoto.ActualWidth <= 0) return;
        WiringCanvas.Children.Clear();
        DrawWiringSide(MappingLeftItems, isLeftCard: true);
        DrawWiringSide(MappingRightItems, isLeftCard: false);
    }

    private void DrawWiringSide(ItemsControl items, bool isLeftCard)
    {
        if (items.ItemsSource is not IEnumerable<IMappingCard> cards) return;
        foreach (var card in cards)
        {
            if (card.WireAnchor is not { } anchor) continue;
            if (items.ItemContainerGenerator.ContainerFromItem(card) is not FrameworkElement container) continue;
            if (container.ActualWidth <= 0) continue;

            // 照片上的按键点：相对坐标 × 显示尺寸
            var keyPt = RemotePhoto.TranslatePoint(
                new Point(anchor.X * RemotePhoto.ActualWidth, anchor.Y * RemotePhoto.ActualHeight),
                WiringCanvas);

            // 卡片朝向遥控器一侧的边缘点
            var cardPt = isLeftCard
                ? container.TranslatePoint(new Point(container.ActualWidth, container.ActualHeight * 0.42), WiringCanvas)
                : container.TranslatePoint(new Point(0, container.ActualHeight * 0.42), WiringCanvas);

            var highlighted = card.IsHighlighted;
            var stroke = highlighted
                ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0xC5, 0xC5, 0xCB));

            var mx = (cardPt.X + keyPt.X) / 2;
            var segment = new BezierSegment(
                new Point(mx, cardPt.Y),
                new Point(mx, keyPt.Y),
                keyPt, true);
            var geometry = new PathGeometry(new[]
            {
                new PathFigure(cardPt, new PathSegment[] { segment }, false),
            });
            WiringCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geometry,
                Stroke = stroke,
                StrokeThickness = highlighted ? 2.0 : 1.4,
            });
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = highlighted ? 6 : 4,
                Height = highlighted ? 6 : 4,
                Fill = stroke,
            };
            WiringCanvas.Children.Add(dot);
            System.Windows.Controls.Canvas.SetLeft(dot, keyPt.X - dot.Width / 2);
            System.Windows.Controls.Canvas.SetTop(dot, keyPt.Y - dot.Height / 2);
        }
    }

    // ---------------- 卡片高亮（选中设置框 → 卡片与引线变蓝） ----------------

    private IMappingCard? _highlightedCard;

    private void OnMappingComboGotFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MappingRow row)
        {
            SetHighlightedCard(row);
        }
    }

    private void OnMappingComboLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MappingRow row && ReferenceEquals(row, _highlightedCard))
        {
            SetHighlightedCard(null);
        }
    }

    private void SetHighlightedCard(IMappingCard? card)
    {
        if (ReferenceEquals(_highlightedCard, card)) return;
        if (_highlightedCard is MappingRow old) old.IsHighlighted = false;
        _highlightedCard = card;
        if (card is MappingRow current) current.IsHighlighted = true;
        RedrawWiring();
    }

    private void OnMappingScrollChanged(object sender, ScrollChangedEventArgs e) => ScheduleWiringRedraw();

    private void RefreshStats()
    {
        // 构造期 XAML 解析触发 Checked 事件时，统计控件可能尚未创建
        if (StatsDayRadio is null || PressesBars is null) return;
        var from = StatsDayRadio.IsChecked == true ? DateTime.Today
            : StatsWeekRadio.IsChecked == true ? DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek)
            : (DateTime?)null;
        var (presses, voice, longest) = _state.Settings.AggregateStats(from);
        StatsPressesLabel.Text = presses.ToString();
        StatsVoiceLabel.Text = FormatDuration(voice);

        // 最近 7 天柱状图（与 macOS 原版一致：日/周/全部切换影响汇总数字，柱状图固定展示最近 7 天）
        var days = Enumerable.Range(0, 7).Select(i => DateTime.Today.AddDays(-6 + i)).ToList();
        var pressesByDay = new List<long>();
        var voiceByDay = new List<double>();
        foreach (var day in days)
        {
            var stats = _state.Settings.Stats.TryGetValue(day.ToString("yyyy-MM-dd"), out var ds) ? ds : null;
            pressesByDay.Add(stats?.Presses.Values.Sum() ?? 0);
            voiceByDay.Add(stats?.VoiceSeconds ?? 0);
        }
        var maxPresses = Math.Max(1, pressesByDay.Max());
        var maxVoice = Math.Max(1.0, voiceByDay.Max());
        var barPresses = new List<BarRow>();
        var barVoice = new List<BarRow>();
        for (var i = 0; i < days.Count; i++)
        {
            var dayText = DayText(days[i]);
            barPresses.Add(new BarRow
            {
                ValueText = pressesByDay[i].ToString(),
                DayText = dayText,
                BarHeight = Math.Max(3, 150.0 * pressesByDay[i] / maxPresses),
                Brush = FindBrush("BlueBrush"),
            });
            barVoice.Add(new BarRow
            {
                ValueText = FormatDuration(voiceByDay[i]),
                DayText = dayText,
                BarHeight = Math.Max(3, 150.0 * voiceByDay[i] / maxVoice),
                Brush = FindBrush("OrangeBrush"),
            });
        }
        PressesBars.ItemsSource = barPresses;
        VoiceBars.ItemsSource = barVoice;

        // 单次语音时长排行（由回眸记录的起止时间推算）
        var top = _state.Settings.LoadTranscripts()
            .Where(t => t.EndedAt > t.StartedAt)
            .Select(t => new { t.StartedAt, Seconds = (t.EndedAt - t.StartedAt).TotalSeconds })
            .Where(x => x.Seconds >= 1)
            .OrderByDescending(x => x.Seconds)
            .Take(10)
            .ToList();
        VoiceTopList.ItemsSource = top.Select((x, i) => new TopRow
        {
            RankText = $"#{i + 1}",
            RankBrush = i < 3 ? FindBrush("OrangeBrush") : FindBrush("TextSecondaryBrush"),
            DurationText = FormatDuration(x.Seconds),
            DateText = x.StartedAt.ToString("yyyy年M月d日 HH:mm"),
        }).ToList();
        StatsTopEmpty.Visibility = top.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string DayText(DateTime day) => L10n.IsZh
        ? day.ToString("ddd").Replace("星期", "周")
        : day.ToString("ddd");

    internal static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    private void RefreshTranscripts()
    {
        if (TranscriptList is null) return;
        var records = _state.Settings.LoadTranscripts();
        TranscriptList.Items.Clear();
        foreach (var record in records.Take(200))
        {
            TranscriptList.Items.Add(new TranscriptRow(record));
        }
    }

    private sealed class TranscriptRow
    {
        public string Header { get; }
        public string Time { get; }
        public string Text { get; }

        public TranscriptRow(AppSettings.TranscriptRecord record)
        {
            Header = record.App ?? "SayAll";
            Time = record.StartedAt.ToString("yyyy-MM-dd HH:mm");
            Text = string.IsNullOrWhiteSpace(record.Text) ? "—" : record.Text;
        }
    }

    private sealed class ComboItem
    {
        public string Id { get; }
        public string Label { get; }
        public ComboItem(string id, string label) { Id = id; Label = label; }
        public override string ToString() => Label;
    }

    private sealed class BarRow
    {
        public string ValueText { get; set; } = "";
        public string DayText { get; set; } = "";
        public double BarHeight { get; set; }
        public Brush Brush { get; set; } = Brushes.Gray;
    }

    private sealed class TopRow
    {
        public string RankText { get; set; } = "";
        public Brush RankBrush { get; set; } = Brushes.Gray;
        public string DurationText { get; set; } = "";
        public string DateText { get; set; } = "";
    }

    // ---------------- 事件 ----------------

    private void OnReconnectClick(object sender, RoutedEventArgs e) => _state.Bridge.ReconnectNow();

    private void OnRefreshAudioClick(object sender, RoutedEventArgs e)
    {
        var devices = AudioDeviceCatalog.OutputDevices();
        AudioDeviceCombo.ItemsSource = null;
        AudioDeviceCombo.Items.Clear();
        foreach (var device in devices)
        {
            AudioDeviceCombo.Items.Add(new ComboItem(device.Id, device.IsVirtualCable ? $"★ {device.Name}" : device.Name));
        }
        if (AudioDeviceCombo.Items.Count > 0) AudioDeviceCombo.SelectedIndex = 0;
    }

    private void OnAudioDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (AudioDeviceCombo.SelectedItem is ComboItem item)
        {
            _state.Settings.SelectedAudioDeviceId = item.Id;
            _state.Settings.Save();
            _state.ConfigureAudioFromSettings();
        }
    }

    private void OnGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        _state.Settings.GainDB = Math.Round(GainSlider.Value, 1);
        GainValueLabel.Text = $"{_state.Settings.GainDB:0.#} dB";
        _state.Settings.Save();
    }

    private void OnTestToneClick(object sender, RoutedEventArgs e)
    {
        if (!_state.PlayTestTone())
        {
            MessageBox.Show(L10n.IsZh
                ? "无法发送测试音：请先选择语音输出设备，且当前没有正在进行的语音会话。"
                : "Cannot play test tone: select a voice output device first and ensure no voice session is active.",
                L10n.T("app.name"));
        }
    }

    private void OnVoiceKeyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        _state.Settings.VoiceKeyModeRaw = VoiceKeyModeCombo.SelectedIndex switch
        {
            1 => "left_command",
            2 => "right_command",
            _ => "fn",
        };
        _state.Settings.Save();
    }

    private void OnFnTapChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _state.Settings.VoiceFnTapModeEnabled = FnTapCheck.IsChecked == true;
        _state.Settings.Save();
    }

    private void OnMappingEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _state.Settings.CustomMappingEnabled = MappingEnabledCheck.IsChecked == true;
        _state.Settings.Save();
        if (_state.Settings.CustomMappingEnabled)
        {
            _state.HidMonitor?.Start();
        }
        RefreshMapping();
    }

    private void OnResetMappingClick(object sender, RoutedEventArgs e)
    {
        _state.Settings.ResetButtonBindings();
        RefreshMapping();
    }

    private void OnStatsRangeChanged(object sender, RoutedEventArgs e) => RefreshStats();

    private void OnTranscriptEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _state.Settings.LocalTranscriptHistoryEnabled = TranscriptEnabledCheck.IsChecked == true;
        _state.Settings.Save();
    }

    private void OnRecordingEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _state.Settings.LocalOriginalAudioRecordingEnabled = RecordingEnabledCheck.IsChecked == true;
        _state.Settings.Save();
    }

    private void OnOpenRecordingsClick(object sender, RoutedEventArgs e)
    {
        var dir = AppSettings.RecordingsDir;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var language = LanguageCombo.SelectedIndex switch
        {
            1 => AppLanguage.zh_Hans,
            2 => AppLanguage.en,
            _ => AppLanguage.System,
        };
        _state.Settings.LanguageRaw = language switch
        {
            AppLanguage.zh_Hans => "zh-Hans",
            AppLanguage.en => "en",
            _ => "system",
        };
        _state.Settings.Save();
        L10n.SetLanguage(language);
        RefreshAll();
        App.Tray?.UpdateStatus(L10n.T("app.name"), _statusText);
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _state.Settings.LaunchAtLogin = AutoStartCheck.IsChecked == true;
        _state.Settings.Save();
        _state.ApplyAutoStart();
    }

    private void OnGithubClick(object sender, RoutedEventArgs e) => OpenUrl(VersionInfo.GitHubUrl);

    private void OnReleasesClick(object sender, RoutedEventArgs e) => OpenUrl(VersionInfo.WindowsReleaseUrl);

    private void OnLogClick(object sender, RoutedEventArgs e)
    {
        var dir = AppLogger.LogDirectory;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private static void OpenUrl(string url)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}

/// <summary>按键卡统一视图接口：按键卡与语音键说明卡共用，供模板绑定与引线绘制。</summary>
internal interface IMappingCard
{
    string Title { get; }
    string IconGlyph { get; }
    bool IsHighlighted { get; }

    /// <summary>引线在照片上的锚点（视口相对坐标）；null 表示不画引线。</summary>
    Point? WireAnchor { get; }
}

/// <summary>按键映射卡：按键图标 + 名称 + 单击/双击/长按三个动作选择。</summary>
internal sealed class MappingRow : INotifyPropertyChanged, IMappingCard
{
    private readonly RemoteButton _button;
    private readonly AppSettings _settings;
    private readonly List<ButtonAction> _actions;
    private bool _suppress;

    public string Title { get; }
    public string IconGlyph { get; }
    public RemoteButton Button { get; }
    public string TriggerSingle { get; }
    public string TriggerDouble { get; }
    public string TriggerLong { get; }
    public IReadOnlyList<string> Items { get; }

    public Point? WireAnchor =>
        MainWindow.RemoteKeyAnchors.TryGetValue(Button, out var anchor) ? anchor : null;

    private bool _isHighlighted;
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set
        {
            if (_isHighlighted == value) return;
            _isHighlighted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private int _singleIndex;
    private int _doubleIndex;
    private int _longIndex;

    public int SingleIndex { get => _singleIndex; set => Set(ref _singleIndex, value, ButtonTrigger.SingleClick); }
    public int DoubleIndex { get => _doubleIndex; set => Set(ref _doubleIndex, value, ButtonTrigger.DoubleClick); }
    public int LongIndex { get => _longIndex; set => Set(ref _longIndex, value, ButtonTrigger.LongPress); }

    public MappingRow(RemoteButton button, AppSettings settings, List<ButtonAction> actions)
    {
        _button = button;
        _settings = settings;
        _actions = actions;
        Button = button;
        Title = ButtonShortName(button);
        IconGlyph = ButtonGlyph(button);
        TriggerSingle = L10n.T("mapping.trigger.single");
        TriggerDouble = L10n.T("mapping.trigger.double");
        TriggerLong = L10n.T("mapping.trigger.long");
        Items = actions.Select(ActionLabel).ToList();
        _suppress = true;
        _singleIndex = IndexOfAction(_settings.GetConfiguredAction(button, ButtonTrigger.SingleClick).Action);
        _doubleIndex = IndexOfAction(_settings.GetConfiguredAction(button, ButtonTrigger.DoubleClick).Action);
        _longIndex = IndexOfAction(_settings.GetConfiguredAction(button, ButtonTrigger.LongPress).Action);
        _suppress = false;
    }

    private int IndexOfAction(ButtonAction action) => Math.Max(0, _actions.IndexOf(action));

    private void Set(ref int field, int value, ButtonTrigger trigger)
    {
        if (_suppress || field == value || value < 0) return;
        field = value;
        var action = _actions[value];
        var configured = new ConfiguredButtonAction { Action = action };
        if (action == ButtonAction.CustomShortcut)
        {
            // 基本快捷键选择：用默认操作演示（真实录制在后续版本扩展）
            configured.Shortcut = new CustomKeyboardShortcut { Vk = 0x56, Modifiers = 0x01, Label = "Ctrl+V" };
        }
        _settings.SetConfiguredAction(_button, trigger, configured);
        _settings.Save();
    }

    private static string ButtonGlyph(RemoteButton button) => button switch
    {
        RemoteButton.Power => "\uE7E8",
        RemoteButton.Up => "\uE70E",
        RemoteButton.Left => "\uE76B",
        RemoteButton.Ok => "\uE73E",
        RemoteButton.Right => "\uE76C",
        RemoteButton.Down => "\uE70D",
        RemoteButton.Back => "\uE72B",
        RemoteButton.VolumeUp => "\uE767",
        RemoteButton.Home => "\uE80F",
        RemoteButton.VolumeDown => "\uE767",
        RemoteButton.Menu => "\uE700",
        RemoteButton.Tv => "\uE7F4",
        _ => "\uE7C9",
    };

    private static string ButtonShortName(RemoteButton button) => button switch
    {
        RemoteButton.Power => "电源 / Power",
        RemoteButton.Up => "上 / Up",
        RemoteButton.Left => "左 / Left",
        RemoteButton.Ok => "确定 / OK",
        RemoteButton.Right => "右 / Right",
        RemoteButton.Down => "下 / Down",
        RemoteButton.Back => "返回 / Back",
        RemoteButton.VolumeUp => "音量+ / Vol+",
        RemoteButton.Home => "主页 / Home",
        RemoteButton.VolumeDown => "音量− / Vol−",
        RemoteButton.Menu => "菜单 / Menu",
        RemoteButton.Tv => "TV",
        _ => button.ToString(),
    };

    private static string ActionLabel(ButtonAction action) => action switch
    {
        ButtonAction.Disabled => "未设置 / Not set",
        ButtonAction.Escape => "Esc",
        ButtonAction.ReturnKey => "回车 / Enter",
        ButtonAction.CommandReturn => "Ctrl+Enter",
        ButtonAction.ShiftReturn => "Shift+Enter",
        ButtonAction.CommandCopy => "Ctrl+C",
        ButtonAction.CommandPaste => "Ctrl+V",
        ButtonAction.CommandClose => "Ctrl+W",
        ButtonAction.CommandQuit => "Ctrl+Q",
        ButtonAction.CommandCut => "Ctrl+X",
        ButtonAction.CommandSelectAll => "Ctrl+A",
        ButtonAction.CommandUndo => "Ctrl+Z",
        ButtonAction.CommandRedo => "Ctrl+Y",
        ButtonAction.CommandFind => "Ctrl+F",
        ButtonAction.CommandSave => "Ctrl+S",
        ButtonAction.CommandDelete => "退格 / Backspace",
        ButtonAction.ArrowUp => "上方向键 / ▲",
        ButtonAction.ArrowDown => "下方向键 / ▼",
        ButtonAction.ArrowLeft => "左方向键 / ◀",
        ButtonAction.ArrowRight => "右方向键 / ▶",
        ButtonAction.ScrollUp => "向上滚动 / ↑",
        ButtonAction.ScrollDown => "向下滚动 / ↓",
        ButtonAction.DeleteBackward => "退格 / Backspace",
        ButtonAction.ShowDesktop => "显示桌面 / Win+D",
        ButtonAction.ContextMenu => "右键菜单键 / Menu",
        ButtonAction.AppSwitcher => "Alt+Tab 应用切换",
        ButtonAction.VolumeUp => "系统音量+",
        ButtonAction.VolumeDown => "系统音量−",
        ButtonAction.VolumeMute => "静音",
        ButtonAction.PlayPause => "播放/暂停",
        ButtonAction.PreviousCommandLeft => "Ctrl+←",
        ButtonAction.NextCommandRight => "Ctrl+→",
        ButtonAction.CustomShortcut => "自定义快捷键…",
        ButtonAction.FocusInput => "聚焦输入框",
        ButtonAction.OpenCustomApplication => "打开自定义应用…",
        ButtonAction.ToggleLongRecording => "切换长录音",
        ButtonAction.OpenCodex => "打开 Codex",
        ButtonAction.OpenClaude => "打开 Claude",
        ButtonAction.OpenCursor => "打开 Cursor",
        ButtonAction.OpenWeChat => "打开微信",
        ButtonAction.OpenWeCom => "打开企业微信",
        ButtonAction.OpenNeteaseMusic => "打开网易云音乐",
        ButtonAction.OpenChrome => "打开 Chrome",
        ButtonAction.OpenZed => "打开 Zed",
        ButtonAction.OpenSlack => "打开 Slack",
        ButtonAction.OpenTerminal => "打开终端",
        ButtonAction.OpenNotepad => "打开记事本",
        _ => action.ToString(),
    };
}

/// <summary>语音键说明卡（右列首卡，对齐 macOS 原版）：语音路径独立于按键映射，不可配置。</summary>
internal sealed class VoiceKeyInfoRow : IMappingCard
{
    public string Title { get; } = L10n.T("connection.voice_key.card");
    public string FixedLabel { get; } = L10n.T("mapping.voice_key_fixed");
    public string Description { get; } = L10n.T("mapping.voice_key_desc");
    public string IconGlyph { get; } = "\uE720";
    public bool IsHighlighted => false;
    public Point? WireAnchor { get; } = MainWindow.VoiceKeyAnchor;
}