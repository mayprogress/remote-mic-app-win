using System.IO;
using System.Text.Json;

namespace SayAll.Core;

/// <summary>
/// 设置与统计数据持久化（自 AppSettings / 统计 / 回眸存档移植）。
/// 存储在 %APPDATA%\SayAll\settings.json；统计、回眸、录音分开保存。
/// 仅保存在本机，不上传。
/// </summary>
public sealed class AppSettings
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SayAll");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");
    private static readonly string StatsPath = Path.Combine(SettingsDir, "stats.json");
    private static readonly string TranscriptsPath = Path.Combine(SettingsDir, "transcripts.json");
    public static readonly string RecordingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SayAll Recordings");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public double GainDB { get; set; }
    public string? SelectedAudioDeviceId { get; set; }
    public bool CustomMappingEnabled { get; set; }
    public string VoiceKeyModeRaw { get; set; } = "fn";
    public bool VoiceFnTapModeEnabled { get; set; }
    public string LanguageRaw { get; set; } = "system";
    public bool LaunchAtLogin { get; set; }
    public bool LocalTranscriptHistoryEnabled { get; set; }
    public bool LocalOriginalAudioRecordingEnabled { get; set; }
    public bool McpEnabled { get; set; }
    public bool ExperimentalContinuousRecordingEnabled { get; set; }
    public string? CachedDeviceAddress { get; set; }

    public VoiceKeyMode VoiceKeyMode => VoiceKeyModeHelper.Parse(VoiceKeyModeRaw);

    /// <summary>按键绑定：button -> trigger -> 动作。</summary>
    public Dictionary<string, Dictionary<string, ConfiguredButtonActionDto>> ButtonBindings { get; set; } = new();

    public Dictionary<string, CustomAppDto> CustomApps { get; set; } = new();

    public sealed class ConfiguredButtonActionDto
    {
        public string Action { get; set; } = "Disabled";
        public CustomKeyboardShortcutDto? Shortcut { get; set; }
        public string? CustomAppId { get; set; }
    }

    public sealed class CustomKeyboardShortcutDto
    {
        public ushort Vk { get; set; }
        public byte Modifiers { get; set; }
        public string Label { get; set; } = "";
    }

    public sealed class CustomAppDto
    {
        public string Name { get; set; } = "";
        public string? Path { get; set; }
    }

    public ConfiguredButtonAction GetConfiguredAction(RemoteButton button, ButtonTrigger trigger)
    {
        var key = button.ToString();
        var triggerKey = trigger.ToString();
        if (ButtonBindings.TryGetValue(key, out var triggers)
            && triggers.TryGetValue(triggerKey, out var dto))
        {
            return ToAction(dto);
        }
        return new ConfiguredButtonAction { Action = DefaultAction(button) };
    }

    public bool HasSecondaryAction(RemoteButton button) =>
        GetConfiguredAction(button, ButtonTrigger.DoubleClick).Action != ButtonAction.Disabled
        || GetConfiguredAction(button, ButtonTrigger.LongPress).Action != ButtonAction.Disabled;

    public bool AllowsRapidPress(RemoteButton button) => !HasSecondaryAction(button);

    /// <summary>清除全部自定义按键覆盖，回到内置默认映射。</summary>
    public void ResetButtonBindings()
    {
        ButtonBindings.Clear();
        Save();
        AppLogger.Write("MAPPING reset_all");
    }

    public void SetConfiguredAction(RemoteButton button, ButtonTrigger trigger, ConfiguredButtonAction action)    {
        var key = button.ToString();
        if (!ButtonBindings.TryGetValue(key, out var triggers))
        {
            triggers = new Dictionary<string, ConfiguredButtonActionDto>();
            ButtonBindings[key] = triggers;
        }
        triggers[trigger.ToString()] = FromAction(action);
    }

    private ConfiguredButtonAction ToAction(ConfiguredButtonActionDto dto)
    {
        var action = Enum.TryParse<ButtonAction>(dto.Action, out var parsed) ? parsed : ButtonAction.Disabled;
        CustomKeyboardShortcut? shortcut = null;
        if (dto.Shortcut is not null && dto.Shortcut.Vk != 0)
        {
            shortcut = new CustomKeyboardShortcut
            {
                Vk = dto.Shortcut.Vk,
                Modifiers = dto.Shortcut.Modifiers,
                Label = dto.Shortcut.Label,
            };
        }
        return new ConfiguredButtonAction
        {
            Action = action,
            Shortcut = shortcut,
            CustomAppId = dto.CustomAppId,
        };
    }

    private static ConfiguredButtonActionDto FromAction(ConfiguredButtonAction action) => new()
    {
        Action = action.Action.ToString(),
        Shortcut = action.Shortcut is null ? null : new CustomKeyboardShortcutDto
        {
            Vk = action.Shortcut.Vk,
            Modifiers = action.Shortcut.Modifiers,
            Label = action.Shortcut.Label,
        },
        CustomAppId = action.CustomAppId,
    };

    /// <summary>默认映射（与 macOS 版默认一致，showDesktop 用 Win+D，appSwitcher 用 Alt+Tab）。</summary>
    public static ButtonAction DefaultAction(RemoteButton button) => button switch
    {
        RemoteButton.Power => ButtonAction.Escape,
        RemoteButton.Up => ButtonAction.ArrowUp,
        RemoteButton.Down => ButtonAction.ArrowDown,
        RemoteButton.Left => ButtonAction.ArrowLeft,
        RemoteButton.Right => ButtonAction.ArrowRight,
        RemoteButton.Ok => ButtonAction.ReturnKey,
        RemoteButton.Back => ButtonAction.DeleteBackward,
        RemoteButton.Home => ButtonAction.ShowDesktop,
        RemoteButton.Menu => ButtonAction.ContextMenu,
        RemoteButton.Tv => ButtonAction.AppSwitcher,
        RemoteButton.VolumeUp => ButtonAction.VolumeUp,
        RemoteButton.VolumeDown => ButtonAction.VolumeDown,
        _ => ButtonAction.Disabled,
    };

    public static AppSettings Load()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Write("SETTINGS load_failed error=" + ex.Message);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            AppLogger.Write("SETTINGS save_failed error=" + ex.Message);
        }
    }

    public static string SettingsFolder => SettingsDir;
    public static string TranscriptsFile => TranscriptsPath;

    // ---------------- 统计 ----------------

    public sealed class DailyStats
    {
        public Dictionary<string, long> Presses { get; set; } = new();
        public double VoiceSeconds { get; set; }
        public double LongestVoiceSeconds { get; set; }
    }

    public Dictionary<string, DailyStats> Stats { get; set; } = new();

    private static string DayKey(DateTime date) => date.ToString("yyyy-MM-dd");

    public void RecordButtonPress(string control, DateTime at)
    {
        var day = DayKey(at);
        if (!Stats.TryGetValue(day, out var stats))
        {
            stats = new DailyStats();
            Stats[day] = stats;
        }
        stats.Presses.TryGetValue(control, out var count);
        stats.Presses[control] = count + 1;
    }

    public void RecordVoiceDuration(double seconds, DateTime startedAt, DateTime endedAt)
    {
        var day = DayKey(startedAt);
        if (!Stats.TryGetValue(day, out var stats))
        {
            stats = new DailyStats();
            Stats[day] = stats;
        }
        stats.VoiceSeconds += seconds;
        stats.LongestVoiceSeconds = Math.Max(stats.LongestVoiceSeconds, seconds);
    }

    public (long Presses, double VoiceSeconds, double LongestVoice) AggregateStats(DateTime? from)
    {
        long presses = 0;
        double voice = 0;
        double longest = 0;
        foreach (var (day, stats) in Stats)
        {
            if (from.HasValue)
            {
                if (!DateTime.TryParse(day, out var date) || date < from.Value.Date) continue;
            }
            presses += stats.Presses.Values.Sum();
            voice += stats.VoiceSeconds;
            longest = Math.Max(longest, stats.LongestVoiceSeconds);
        }
        return (presses, voice, longest);
    }

    // ---------------- 回眸 ----------------

    public sealed class TranscriptRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime StartedAt { get; set; }
        public DateTime EndedAt { get; set; }
        public string? App { get; set; }
        public string? Text { get; set; }
        public string Source { get; set; } = "bluetooth_remote";
    }

    public List<TranscriptRecord> LoadTranscripts()
    {
        try
        {
            if (!File.Exists(TranscriptsPath)) return [];
            return JsonSerializer.Deserialize<List<TranscriptRecord>>(File.ReadAllText(TranscriptsPath), JsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void AppendTranscript(TranscriptRecord record)
    {
        try
        {
            var records = LoadTranscripts();
            records.Add(record);
            records.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
            records = records.Take(500).ToList();
            File.WriteAllText(TranscriptsPath, JsonSerializer.Serialize(records, JsonOpts));
        }
        catch (Exception ex)
        {
            AppLogger.Write("TRANSCRIPT append_failed error=" + ex.Message);
        }
    }
}

/// <summary>本地录音：每个语音会话一个 16 kHz 单声道 PCM16 WAV（对应 RecordingAssetStore）。</summary>
public sealed class VoiceRecordingSession : IDisposable
{
    private FileStream? _stream;
    private BinaryWriter? _writer;
    private long _dataBytes;

    public DateTime StartedAt { get; }
    public string FilePath { get; }

    public VoiceRecordingSession(string? appName)
    {
        StartedAt = DateTime.Now;
        try
        {
            Directory.CreateDirectory(AppSettings.RecordingsDir);
            var safe = SanitizeFileName(appName ?? "sayall");
            FilePath = Path.Combine(AppSettings.RecordingsDir,
                $"{StartedAt:yyyyMMdd-HHmmss}-{safe}.wav");
            _stream = new FileStream(FilePath, FileMode.Create);
            _writer = new BinaryWriter(_stream);
            WriteHeader(0);
        }
        catch (Exception ex)
        {
            AppLogger.Write("RECORDING open_failed error=" + ex.Message);
            _writer = null;
            _stream = null;
            FilePath = "";
        }
    }

    private void WriteHeader(long dataBytes)
    {
        var w = _writer!;
        w.Write("RIFF"u8);
        w.Write((int)(36 + dataBytes));
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write((int)16);
        w.Write((short)1); // PCM
        w.Write((short)1); // mono
        w.Write((int)16000);
        w.Write((int)(16000 * 2));
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write((int)dataBytes);
    }

    public void Append(short[] samples)
    {
        if (_writer is null || samples.Length == 0) return;
        try
        {
            foreach (var s in samples) _writer.Write(s);
            _dataBytes += samples.Length * 2;
        }
        catch (Exception ex)
        {
            AppLogger.Write("RECORDING append_failed error=" + ex.Message);
        }
    }

    public void Finish()
    {
        if (_writer is null) return;
        try
        {
            _stream!.SetLength(0);
            _stream.Seek(0, SeekOrigin.Begin);
            WriteHeader(_dataBytes);
            _writer.Flush();
        }
        catch (Exception ex)
        {
            AppLogger.Write("RECORDING finalize_failed error=" + ex.Message);
        }
        Dispose();
    }

    public void Dispose()
    {
        try { _writer?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
        _writer = null;
        _stream = null;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 40 ? name[..40] : name;
    }
}