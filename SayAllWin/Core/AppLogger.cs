using System.IO;
using System.Text;

namespace SayAll;

/// <summary>
/// 日志：写入 %LOCALAPPDATA%\SayAll\logs\runtime.log，10 MiB 滚动到 .1 ~ .3。
/// 不记录语音内容、蓝牙地址、前台应用等敏感信息（与 macOS 版一致的脱敏规则）。
/// </summary>
public static class AppLogger
{
    public static string LogDirectory { get; private set; } = "";

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static long _bytes;
    private static int _roll = 3;
    private const long MaxBytes = 10 * 1024 * 1024;

    public static void Initialize(string? directory = null)
    {
        lock (Gate)
        {
            try
            {
                LogDirectory = directory
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SayAll", "logs");
                Directory.CreateDirectory(LogDirectory);
                OpenWriter();
                Write("LOG START version=" + VersionInfo.Version);
            }
            catch
            {
                // 日志不可用时保持静默
            }
        }
    }

    private static void OpenWriter()
    {
        var path = Path.Combine(LogDirectory, "runtime.log");
        _writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = false };
        _bytes = new FileInfo(path).Length;
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            if (_writer is null) return;
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} v={VersionInfo.Version} {message}{Environment.NewLine}";
                var payload = Encoding.UTF8.GetByteCount(line);
                if (_bytes + payload > MaxBytes) Rotate();
                _writer.Write(line);
                _bytes += payload;
                _writer.Flush();
            }
            catch
            {
                // 忽略写入失败
            }
        }
    }

    private static void Rotate()
    {
        try
        {
            _writer?.Dispose();
            _writer = null;
            for (var i = _roll; i >= 1; i--)
            {
                var from = Path.Combine(LogDirectory, $"runtime.log.{i}");
                var to = Path.Combine(LogDirectory, $"runtime.log.{i + 1}");
                if (i == _roll && File.Exists(to)) File.Delete(to);
                if (File.Exists(from)) File.Move(from, to, overwrite: true);
            }
            var latest = Path.Combine(LogDirectory, "runtime.log");
            if (File.Exists(latest)) File.Move(latest, Path.Combine(LogDirectory, "runtime.log.1"), overwrite: true);
            OpenWriter();
        }
        catch
        {
            OpenWriter();
        }
    }

    public static string Sanitize(string value) =>
        string.IsNullOrEmpty(value) ? "unknown" : value.Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>设备名/路径等敏感标识的日志脱敏：不落明文，用 6 位短哈希保持可关联性。</summary>
    public static string NameHash(string value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 3));
    }
}

public static class VersionInfo
{
    public const string Version = "0.2.0-beta";
    public const string Build = "20260902.1";
    public const string Product = "SayAll (无线麦) Windows Port";
    public const string GitHubUrl = "https://github.com/HD838A/remote-mic-app";
    public const string WindowsReleaseUrl = "https://github.com/HD838A/remote-mic-app/releases";
}