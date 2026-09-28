using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SCV.PrintAgent;

public sealed class PdfPrintForm : Form
{
    private readonly AgentSettings _settings;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PdfPrintForm(AgentSettings settings)
    {
        _settings = settings;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(10, 10);
        Controls.Add(_webView);
    }

    public Task PrintPdfAsync(
        byte[] content,
        string printerName,
        string format,
        int copies,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(format))
            throw new ArgumentException("El formato de impresión es obligatorio.", nameof(format));

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(new Action(async () =>
        {
            try
            {
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    if (string.Equals(format, "TICKET", StringComparison.OrdinalIgnoreCase))
                    {
                        // Para ticket térmico no usamos WebView2. Se renderiza el PDF
                        // al ancho físico del rollo y se imprime directo al spooler.
                        await TicketPdfPrinter.PrintAsync(
                            content,
                            printerName,
                            copies,
                            _settings,
                            cancellationToken);
                    }
                    else
                    {
                        await PrintStandardPdfAsync(
                            content,
                            printerName,
                            copies,
                            cancellationToken);
                    }
                }
                finally
                {
                    _gate.Release();
                }

                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }));

        return tcs.Task;
    }

    private async Task PrintStandardPdfAsync(
        byte[] content,
        string printerName,
        int copies,
        CancellationToken cancellationToken)
    {
        await EnsureWebViewAsync();
        var tempPath = Path.Combine(Path.GetTempPath(), $"scv-print-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(tempPath, content, cancellationToken);

        try
        {
            await NavigateAsync(new Uri(tempPath).AbsoluteUri, cancellationToken);
            var settings = _webView.CoreWebView2.Environment.CreatePrintSettings();
            settings.PrinterName = printerName;
            settings.Copies = Math.Clamp(copies, 1, 20);
            settings.MarginTop = 0;
            settings.MarginBottom = 0;
            settings.MarginLeft = 0;
            settings.MarginRight = 0;
            settings.ScaleFactor = 1.0;
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;

            var status = await _webView.CoreWebView2.PrintAsync(settings);
            if (status != CoreWebView2PrintStatus.Succeeded)
            {
                throw new InvalidOperationException(
                    $"WebView2 no pudo enviar el PDF a la impresora. Estado: {status}.");
            }
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webView.CoreWebView2 is not null)
            return;

        await _webView.EnsureCoreWebView2Async();
    }

    private Task NavigateAsync(string url, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _webView.NavigationCompleted -= Handler;
            if (e.IsSuccess)
                tcs.TrySetResult(true);
            else
                tcs.TrySetException(
                    new InvalidOperationException($"No se pudo abrir el PDF en WebView2: {e.WebErrorStatus}."));
        }

        _webView.NavigationCompleted += Handler;
        using var registration = cancellationToken.Register(
            () => tcs.TrySetCanceled(cancellationToken));
        _webView.CoreWebView2.Navigate(url);
        return tcs.Task;
    }
}
