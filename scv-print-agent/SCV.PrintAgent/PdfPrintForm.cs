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
        if (content is null || content.Length == 0)
            throw new ArgumentException("El PDF está vacío.", nameof(content));

        if (string.IsNullOrWhiteSpace(printerName))
            throw new ArgumentException("La impresora es obligatoria.", nameof(printerName));

        if (string.IsNullOrWhiteSpace(format))
            throw new ArgumentException("El formato de impresión es obligatorio.", nameof(format));

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        BeginInvoke(new Action(async () =>
        {
            try
            {
                await _gate.WaitAsync(cancellationToken);

                try
                {
                    if (string.Equals(
                            format,
                            "TICKET",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        await PrintTicketPdfAsync(
                            content,
                            printerName,
                            copies,
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

                tcs.TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));

        return tcs.Task;
    }

    private Task PrintTicketPdfAsync(
        byte[] content,
        string printerName,
        int copies,
        CancellationToken cancellationToken)
    {
        // Replica la prueba manual que funcionó:
        // - PDF original
        // - WebView2
        // - papel configurado en el driver (58(48) x 297 mm)
        // - escala personalizada 82 %
        //
        // NO se configura MediaSize/PageWidth/PageHeight aquí.
        // De esa manera WebView2 usa el formulario/tamaño configurado
        // realmente para DIR-E58/POS-58 en Windows.
        return PrintWithWebViewAsync(
            content,
            printerName,
            copies,
            cancellationToken,
            configure: settings =>
            {
                settings.MarginTop =
                    MmToInches(_settings.TicketMarginTopMm);

                settings.MarginBottom =
                    MmToInches(_settings.TicketMarginBottomMm);

                settings.MarginLeft =
                    MmToInches(_settings.TicketMarginLeftMm);

                settings.MarginRight =
                    MmToInches(_settings.TicketMarginRightMm);

                settings.ScaleFactor =
                    _settings.TicketScaleFactor;

                settings.Orientation =
                    CoreWebView2PrintOrientation.Portrait;
            },
            isTicket: true);
    }

    private Task PrintStandardPdfAsync(
        byte[] content,
        string printerName,
        int copies,
        CancellationToken cancellationToken)
    {
        return PrintWithWebViewAsync(
            content,
            printerName,
            copies,
            cancellationToken,
            configure: settings =>
            {
                settings.MarginTop = 0;
                settings.MarginBottom = 0;
                settings.MarginLeft = 0;
                settings.MarginRight = 0;
                settings.ScaleFactor = 1.0;
            },
            isTicket: false);
    }

    private async Task PrintWithWebViewAsync(
        byte[] content,
        string printerName,
        int copies,
        CancellationToken cancellationToken,
        Action<CoreWebView2PrintSettings> configure,
        bool isTicket)
    {
        await EnsureWebViewAsync();

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"scv-print-{Guid.NewGuid():N}.pdf");

        await File.WriteAllBytesAsync(
            tempPath,
            content,
            cancellationToken);

        try
        {
            await NavigateAsync(
                new Uri(tempPath).AbsoluteUri,
                cancellationToken);

            // El visor PDF de Chromium necesita un pequeño margen después
            // de NavigationCompleted para terminar de construir la vista.
            await Task.Delay(
                TimeSpan.FromMilliseconds(250),
                cancellationToken);

            var settings =
                _webView.CoreWebView2.Environment
                    .CreatePrintSettings();

            settings.PrinterName =
                printerName;

            settings.Copies =
                Math.Clamp(copies, 1, 20);

            settings.ShouldPrintBackgrounds =
                true;

            settings.ShouldPrintHeaderAndFooter =
                false;

            settings.PagesPerSide = 1;

            configure(settings);

            var status =
                await _webView.CoreWebView2
                    .PrintAsync(settings);

            if (status != CoreWebView2PrintStatus.Succeeded)
            {
                throw new InvalidOperationException(
                    $"WebView2 no pudo enviar el documento a la impresora. Estado: {status}.");
            }

            if (isTicket)
            {
                AppendTicketDiagnostic(
                    printerName,
                    copies);
            }
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // No bloquear una impresión exitosa por limpieza temporal.
            }
        }
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webView.CoreWebView2 is not null)
            return;

        await _webView.EnsureCoreWebView2Async();
    }

    private Task NavigateAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var tcs =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(
            object? sender,
            CoreWebView2NavigationCompletedEventArgs e)
        {
            _webView.NavigationCompleted -= Handler;

            if (e.IsSuccess)
            {
                tcs.TrySetResult(true);
            }
            else
            {
                tcs.TrySetException(
                    new InvalidOperationException(
                        $"No se pudo abrir el PDF en WebView2: {e.WebErrorStatus}."));
            }
        }

        _webView.NavigationCompleted += Handler;

        using var registration =
            cancellationToken.Register(
                () => tcs.TrySetCanceled(cancellationToken));

        _webView.CoreWebView2.Navigate(url);

        return tcs.Task;
    }

    private void AppendTicketDiagnostic(
        string printerName,
        int copies)
    {
        if (!_settings.TicketSaveDiagnostics)
            return;

        try
        {
            var logsDirectory = Path.Combine(
                AppContext.BaseDirectory,
                "logs");

            Directory.CreateDirectory(logsDirectory);

            var logPath = Path.Combine(
                logsDirectory,
                "print-agent.log");

            var line = string.Join(
                " | ",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "renderer=WEBVIEW2_DIRECT_PDF",
                "format=TICKET",
                $"printer={printerName}",
                $"copies={copies}",
                $"scale={_settings.TicketScaleFactor:0.###}",
                "media=PRINTER_DEFAULT",
                $"margins-mm={_settings.TicketMarginLeftMm:0.##},{_settings.TicketMarginTopMm:0.##},{_settings.TicketMarginRightMm:0.##},{_settings.TicketMarginBottomMm:0.##}");

            File.AppendAllText(
                logPath,
                line + Environment.NewLine);
        }
        catch
        {
            // El diagnóstico nunca debe bloquear la impresión.
        }
    }

    private static double MmToInches(
        double millimeters)
        => millimeters / 25.4;
}
