namespace SCV.PrintAgent;

public sealed class PrintAgentApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly PdfPrintForm _printForm;
    private readonly ScvPrintAgentClient _client;

    public PrintAgentApplicationContext(AgentSettings settings)
    {
        _printForm = new PdfPrintForm();
        _printForm.Show();

        var menu = new ContextMenuStrip();
        var statusItem = new ToolStripMenuItem("Iniciando...") { Enabled = false };
        var refreshItem = new ToolStripMenuItem("Actualizar impresoras");
        var exitItem = new ToolStripMenuItem("Salir");
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(refreshItem);
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "SCV Print Agent",
            Visible = true,
            ContextMenuStrip = menu
        };

        _client = new ScvPrintAgentClient(settings, _printForm);
        _client.StatusChanged += status =>
        {
            if (_printForm.IsDisposed) return;
            _printForm.BeginInvoke(new Action(() => statusItem.Text = status));
        };

        refreshItem.Click += async (_, _) =>
        {
            try { await _client.RegisterAndDrainAsync(); }
            catch (Exception ex) { ShowError(ex.Message); }
        };
        exitItem.Click += async (_, _) => await ExitAsync();

        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        while (!_printForm.IsDisposed)
        {
            try
            {
                await _client.StartAsync(CancellationToken.None);
                return;
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }
    }

    private void ShowError(string message)
    {
        _trayIcon.BalloonTipTitle = "SCV Print Agent";
        _trayIcon.BalloonTipText = message.Length > 240 ? message[..240] : message;
        _trayIcon.ShowBalloonTip(5000);
    }

    private async Task ExitAsync()
    {
        _trayIcon.Visible = false;
        await _client.DisposeAsync();
        _printForm.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _printForm.Dispose();
        }
        base.Dispose(disposing);
    }
}
