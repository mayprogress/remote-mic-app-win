using System.Drawing;
using System.Windows.Forms;

namespace SayAll;

/// <summary>
/// 系统托盘图标（对应 macOS 菜单栏）。左键打开设置；右键菜单：
/// 状态、打开设置、重新连接、日志、关于、GitHub、退出。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _statusItem;

    public TrayIcon()
    {
        _statusItem = new System.Windows.Forms.ToolStripMenuItem(L10n.T("tray.status.disconnected")) { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(L10n.T("tray.show_settings"), null, (_, _) => App.ShowSettings());
        menu.Items.Add(L10n.T("tray.reconnect"), null, (_, _) => App.State?.Bridge.ReconnectNow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(L10n.T("about.github"), null, (_, _) => OpenUrl(VersionInfo.GitHubUrl));
        menu.Items.Add(L10n.T("about.log"), null, (_, _) => OpenFolder(AppLogger.LogDirectory));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(L10n.T("tray.quit"), null, (_, _) => App.Quit());

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = L10n.T("app.name"),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => App.ShowSettings();
    }

    public void Show() => _icon.Visible = true;

    public void UpdateStatus(string title, string statusText)
    {
        _icon.Text = title.Length > 63 ? title[..63] : title;
        _statusItem.Text = statusText;
    }

    private static Icon CreateIcon()
    {
        // 生成一个简单的麦克风图标（纯 GDI，不依赖资源文件）
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(88, 166, 255));
            // 麦克风主体
            g.FillRectangle(brush, 6, 2, 4, 8);
            // 底座
            g.FillRectangle(brush, 4, 10, 8, 2);
            g.FillRectangle(brush, 7, 12, 2, 2);
        }
        var handle = bmp.GetHicon();
        using var tmp = Icon.FromHandle(handle);
        var icon = (Icon)tmp.Clone();
        NativeMethods.DestroyIcon(handle);
        return icon;
    }

    private static void OpenUrl(string url) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    private static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}