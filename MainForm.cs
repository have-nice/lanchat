using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LanChat;

public sealed class MainForm : Form
{
    private readonly LanChatApp _app;
    private readonly WebView2 _webView;
    private readonly NotifyIcon _notifyIcon;

    public MainForm(LanChatApp app)
    {
        _app = app;

        Text = "LAN Chat";
        Width = 1180;
        Height = 800;
        MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(13, 16, 24);
        Icon = LoadAppIcon();

        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open LAN Chat", null, (_, _) => RestoreWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());

        _notifyIcon = new NotifyIcon
        {
            Icon = Icon,
            Text = "LAN Chat",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => RestoreWindow();
        _notifyIcon.BalloonTipClicked += (_, _) => RestoreWindow();

        Load += OnLoadAsync;
        FormClosing += (_, _) =>
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        };
        HandleCreated += (_, _) => ApplyTheme(_app.ConfigService.Config.Theme);
    }

    private async void OnLoadAsync(object? sender, EventArgs e)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDir);
            await _webView.EnsureCoreWebView2Async(environment);
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessage;
            _webView.CoreWebView2.Navigate(_app.Url);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start the chat window.\n\n" + ex.Message,
                "LAN Chat", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return;
            var type = typeElement.GetString();

            switch (type)
            {
                case "notify":
                {
                    var title = root.TryGetProperty("title", out var t) ? t.GetString() : "LAN Chat";
                    var body = root.TryGetProperty("body", out var b) ? b.GetString() : "";
                    if (!ContainsFocus)
                    {
                        _notifyIcon.ShowBalloonTip(4000, title ?? "LAN Chat", body ?? "",
                            ToolTipIcon.Info);
                        FlashTaskbar();
                    }

                    break;
                }
                case "title":
                {
                    var text = root.TryGetProperty("text", out var t) ? t.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(text)) Text = text;
                    break;
                }
                case "theme":
                {
                    var theme = root.TryGetProperty("theme", out var t) ? t.GetString() : "dark";
                    ApplyTheme(theme ?? "dark");
                    break;
                }
                case "open-external":
                {
                    var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    }

                    break;
                }
            }
        }
        catch
        {
        }
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void ApplyTheme(string theme)
    {
        try
        {
            var dark = theme != "light";
            var value = dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref value, sizeof(int));
            DwmSetWindowAttribute(Handle, 19, ref value, sizeof(int));
            BackColor = dark ? Color.FromArgb(13, 16, 24) : Color.FromArgb(244, 246, 250);
        }
        catch
        {
        }
    }

    private void FlashTaskbar()
    {
        try
        {
            var info = new FLASHWINFO
            {
                hwnd = Handle,
                dwFlags = 0x00000003 | 0x0000000C,
                uCount = 3,
                dwTimeout = 0
            };
            FlashWindowEx(ref info);
        }
        catch
        {
        }
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var stream = typeof(MainForm).Assembly.GetManifestResourceStream("LanChat.icon.ico");
            if (stream != null) return new Icon(stream);
        }
        catch
        {
        }

        try
        {
            var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon != null) return icon;
        }
        catch
        {
        }

        return SystemIcons.Application;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
