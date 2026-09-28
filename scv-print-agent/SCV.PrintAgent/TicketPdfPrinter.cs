using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace SCV.PrintAgent;

/// <summary>
/// Impresor específico para tickets térmicos.
///
/// No usa el motor de impresión de Chromium/WebView2. El PDF se rasteriza al
/// ancho físico del rollo, se elimina el espacio blanco vertical sobrante y se
/// envía directamente al spooler de Windows mediante PrintDocument.
///
/// Esto evita el "fit to page" de WebView2, que en impresoras térmicas puede
/// reducir todo el ticket para ajustarlo a una altura de papel configurada en
/// el driver, provocando texto diminuto y un tramo blanco grande al inicio.
/// </summary>
public static class TicketPdfPrinter
{
    public static async Task PrintAsync(
        byte[] pdfBytes,
        string printerName,
        int copies,
        AgentSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        if (pdfBytes.Length == 0)
            throw new ArgumentException("El PDF del ticket está vacío.", nameof(pdfBytes));

        if (string.IsNullOrWhiteSpace(printerName))
            throw new ArgumentException("La impresora del ticket es obligatoria.", nameof(printerName));

        using var bitmap = await RenderTicketAsync(pdfBytes, settings, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        PrintBitmap(
            bitmap,
            printerName,
            Math.Clamp(copies, 1, 20),
            settings.TicketPaperWidthMm,
            settings.TicketRenderDpi);
    }

    private static async Task<Bitmap> RenderTicketAsync(
        byte[] pdfBytes,
        AgentSettings settings,
        CancellationToken cancellationToken)
    {
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input))
        {
            writer.WriteBytes(pdfBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        input.Seek(0);

        cancellationToken.ThrowIfCancellationRequested();
        var pdf = await PdfDocument.LoadFromStreamAsync(input);
        if (pdf.PageCount == 0)
            throw new InvalidOperationException("El PDF del ticket no contiene páginas.");

        var renderWidth = Math.Max(
            1,
            (int)Math.Round(settings.TicketPaperWidthMm / 25.4 * settings.TicketRenderDpi));

        var pages = new List<Bitmap>((int)pdf.PageCount);
        try
        {
            for (uint pageIndex = 0; pageIndex < pdf.PageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var page = pdf.GetPage(pageIndex);
                var aspectRatio = page.Size.Width <= 0
                    ? 1d
                    : page.Size.Height / page.Size.Width;

                var renderHeight = Math.Max(1, (int)Math.Ceiling(renderWidth * aspectRatio));
                using var output = new InMemoryRandomAccessStream();
                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)renderWidth,
                    DestinationHeight = (uint)renderHeight
                };

                await page.RenderToStreamAsync(output, options);
                output.Seek(0);

                await using var imageStream = output.AsStreamForRead();
                using var rendered = Image.FromStream(imageStream, useEmbeddedColorManagement: false, validateImageData: true);
                using var normalized = new Bitmap(rendered.Width, rendered.Height, PixelFormat.Format24bppRgb);
                normalized.SetResolution(settings.TicketRenderDpi, settings.TicketRenderDpi);

                using (var graphics = Graphics.FromImage(normalized))
                {
                    graphics.Clear(Color.White);
                    graphics.DrawImageUnscaled(rendered, 0, 0);
                }

                var cropped = CropVerticalWhitespace(
                    normalized,
                    settings.TicketWhiteThreshold,
                    settings.TicketTopPaddingMm,
                    settings.TicketBottomPaddingMm,
                    settings.TicketRenderDpi);

                ApplyMonochromeThreshold(cropped, settings.TicketBlackThreshold);
                pages.Add(cropped);
            }

            return CombineVertically(pages, settings.TicketRenderDpi);
        }
        finally
        {
            foreach (var page in pages)
                page.Dispose();
        }
    }

    private static Bitmap CropVerticalWhitespace(
        Bitmap source,
        int whiteThreshold,
        double topPaddingMm,
        double bottomPaddingMm,
        int dpi)
    {
        var bounds = FindContentBounds(source, whiteThreshold);
        if (bounds is null)
            return (Bitmap)source.Clone();

        var topPadding = Math.Max(0, (int)Math.Round(topPaddingMm / 25.4 * dpi));
        var bottomPadding = Math.Max(0, (int)Math.Round(bottomPaddingMm / 25.4 * dpi));

        var top = Math.Max(0, bounds.Value.Top - topPadding);
        var bottom = Math.Min(source.Height - 1, bounds.Value.Bottom + bottomPadding);
        var height = Math.Max(1, bottom - top + 1);

        var result = source.Clone(
            new Rectangle(0, top, source.Width, height),
            PixelFormat.Format24bppRgb);
        result.SetResolution(dpi, dpi);
        return result;
    }

    private static Rectangle? FindContentBounds(Bitmap bitmap, int whiteThreshold)
    {
        using var rgb = Ensure24Bpp(bitmap);
        var rect = new Rectangle(0, 0, rgb.Width, rgb.Height);
        var data = rgb.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            var stride = Math.Abs(data.Stride);
            var bytes = new byte[stride * rgb.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

            var first = -1;
            var last = -1;

            for (var y = 0; y < rgb.Height; y++)
            {
                var rowOffset = data.Stride >= 0
                    ? y * stride
                    : (rgb.Height - 1 - y) * stride;

                var rowHasInk = false;
                for (var x = 0; x < rgb.Width; x++)
                {
                    var index = rowOffset + x * 3;
                    var b = bytes[index];
                    var g = bytes[index + 1];
                    var r = bytes[index + 2];

                    if (r < whiteThreshold || g < whiteThreshold || b < whiteThreshold)
                    {
                        rowHasInk = true;
                        break;
                    }
                }

                if (!rowHasInk)
                    continue;

                if (first < 0)
                    first = y;
                last = y;
            }

            return first < 0
                ? null
                : Rectangle.FromLTRB(0, first, rgb.Width, last + 1);
        }
        finally
        {
            rgb.UnlockBits(data);
        }
    }

    private static void ApplyMonochromeThreshold(Bitmap bitmap, int threshold)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);

        try
        {
            var stride = Math.Abs(data.Stride);
            var bytes = new byte[stride * bitmap.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

            for (var y = 0; y < bitmap.Height; y++)
            {
                var rowOffset = data.Stride >= 0
                    ? y * stride
                    : (bitmap.Height - 1 - y) * stride;

                for (var x = 0; x < bitmap.Width; x++)
                {
                    var index = rowOffset + x * 3;
                    var b = bytes[index];
                    var g = bytes[index + 1];
                    var r = bytes[index + 2];

                    // Luminancia perceptual entera aproximada.
                    var luminance = (r * 299 + g * 587 + b * 114) / 1000;
                    var value = luminance < threshold ? (byte)0 : (byte)255;

                    bytes[index] = value;
                    bytes[index + 1] = value;
                    bytes[index + 2] = value;
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Bitmap CombineVertically(IReadOnlyList<Bitmap> pages, int dpi)
    {
        if (pages.Count == 0)
            throw new InvalidOperationException("No se pudo rasterizar el ticket.");

        if (pages.Count == 1)
            return (Bitmap)pages[0].Clone();

        var width = pages.Max(x => x.Width);
        var height = pages.Sum(x => x.Height);
        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        result.SetResolution(dpi, dpi);

        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.White);

        var top = 0;
        foreach (var page in pages)
        {
            graphics.DrawImageUnscaled(page, 0, top);
            top += page.Height;
        }

        return result;
    }

    private static Bitmap Ensure24Bpp(Bitmap source)
    {
        if (source.PixelFormat == PixelFormat.Format24bppRgb)
            return (Bitmap)source.Clone();

        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        copy.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        using var graphics = Graphics.FromImage(copy);
        graphics.Clear(Color.White);
        graphics.DrawImageUnscaled(source, 0, 0);
        return copy;
    }

    private static void PrintBitmap(
        Bitmap bitmap,
        string printerName,
        int copies,
        double paperWidthMm,
        int dpi)
    {
        using var document = new PrintDocument
        {
            PrintController = new StandardPrintController(),
            OriginAtMargins = false,
            DocumentName = "SCV Ticket"
        };

        document.PrinterSettings.PrinterName = printerName;
        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException($"La impresora '{printerName}' no está disponible en Windows.");

        var widthHundredths = Math.Max(1, MmToHundredthsOfInch(paperWidthMm));
        var heightHundredths = Math.Max(
            1,
            (int)Math.Ceiling(bitmap.Height * 100d / dpi));

        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        document.DefaultPageSettings.PaperSize = new PaperSize(
            "SCV Ticket",
            widthHundredths,
            heightHundredths);

        document.PrinterSettings.Copies = (short)Math.Clamp(copies, 1, short.MaxValue);

        document.PrintPage += (_, e) =>
        {
            var graphics = e.Graphics
                ?? throw new InvalidOperationException(
                    "Windows no proporcionó un contexto gráfico para la impresión.");

            graphics.PageUnit = GraphicsUnit.Display; // 1/100 de pulgada.
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.CompositingQuality = CompositingQuality.HighSpeed;

            // Compensa el margen físico informado por el driver. Si la impresora
            // realmente admite borde 0, el contenido comienza inmediatamente.
            graphics.TranslateTransform(
                -e.PageSettings.HardMarginX,
                -e.PageSettings.HardMarginY);

            // Usar Rectangle (entero) fuerza el overload correcto de DrawImage:
            // DrawImage(Image, Rectangle, int, int, int, int, GraphicsUnit).
            var target = new Rectangle(
                0,
                0,
                widthHundredths,
                heightHundredths);

            graphics.DrawImage(
                bitmap,
                target,
                0,
                0,
                bitmap.Width,
                bitmap.Height,
                GraphicsUnit.Pixel);

            e.HasMorePages = false;
        };

        document.Print();
    }

    private static int MmToHundredthsOfInch(double millimeters)
        => (int)Math.Round(millimeters / 25.4 * 100d);
}
