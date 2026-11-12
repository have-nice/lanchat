using System.Threading;

namespace LanChat;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "LanChat.SingleInstance.v1", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("LAN Chat is already running.", "LAN Chat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        var startHidden = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

        var app = new LanChatApp();
        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start LAN Chat.\n\n" + ex.Message, "LAN Chat",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (app.ConfigService.Config.StartWithWindows != StartupService.IsEnabled())
        {
            StartupService.Set(app.ConfigService.Config.StartWithWindows);
        }

        using (var form = new MainForm(app, startHidden))
        {
            Application.Run(form);
        }

        try
        {
            app.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }
    }
}
