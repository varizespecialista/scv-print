using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace SCV.PrintAgent;

/// <summary>
/// Impresor específico para tickets térmicos.
///
/// Modo recomendado: ESC_POS.
/// El PDF se rasteriza exactamente al ancho del cabezal (por defecto 384 dots)
/// y se envía como imagen RAW ESC/POS directamente al spooler de Windows.
/// Esto evita los límites de tamaño de página, márgenes y escalados del driver GDI.
///
/// Se conserva GDI como modo de compatibilidad seleccionable desde appsettings.
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

        var mode = settings.TicketPrintMode.Trim().ToUpperInvariant();

        if (mode == "ESC_POS")
        {
            PrintEscPos(
                bitmap,
                printerName,
                Math.Clamp(copies, 1, 20),
                settings.TicketEscPosChunkRows,
                settings.TicketFeedLines);

            AppendEscPosDiagnostic(
                printerName,
                bitmap.Width,
                bitmap.Height,
                copies,
                settings);
            return;
        }

        if (mode == "GDI")
        {
            PrintBitmapGdi(
                bitmap,
                printerName,
                Math.Clamp(copies, 1, 20),
                settings.TicketPaperWidthMm,
                settings.TicketPrintableWidthMm,
                settings.TicketRenderDpi);
            return;
        }

        throw new InvalidOperationException(
            $"TicketPrintMode '{settings.TicketPrintMode}' no es válido. Use ESC_POS o GDI.");
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
            throw new InvalidOperationException(
                "El PDF del ticket no contiene páginas.");

        // Renderizamos primero a mayor resolución que el cabezal final.
        // Luego recortamos TODO el espacio blanco (horizontal + vertical)
        // y recién al final ajustamos el contenido al ancho real del cabezal.
        // Esto evita que un PDF con página más ancha que su contenido termine
        // imprimiéndose como una columna estrecha tanto en ESC/POS como en GDI.
        var sourceRenderWidth = checked(
            settings.TicketDotsWidth * settings.TicketRenderScale);

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

                var renderHeight = Math.Max(
                    1,
                    (int)Math.Ceiling(
                        sourceRenderWidth * aspectRatio));

                using var output = new InMemoryRandomAccessStream();

                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)sourceRenderWidth,
                    DestinationHeight = (uint)renderHeight
                };

                await page.RenderToStreamAsync(output, options);
                output.Seek(0);

                await using var imageStream = output.AsStreamForRead();
                using var rendered = Image.FromStream(
                    imageStream,
                    useEmbeddedColorManagement: false,
                    validateImageData: true);

                var normalized = new Bitmap(
                    rendered.Width,
                    rendered.Height,
                    PixelFormat.Format24bppRgb);

                normalized.SetResolution(
                    settings.TicketRenderDpi,
                    settings.TicketRenderDpi);

                using (var graphics = Graphics.FromImage(normalized))
                {
                    graphics.Clear(Color.White);
                    graphics.DrawImageUnscaled(rendered, 0, 0);
                }

                pages.Add(normalized);
            }

            using var combined = CombineVertically(
                pages,
                settings.TicketRenderDpi);

            SaveDiagnosticImage(
                combined,
                "last-ticket-source.png",
                settings);

            var bounds = FindContentRectangle(
                combined,
                settings.TicketWhiteThreshold);

            if (bounds is null)
            {
                throw new InvalidOperationException(
                    "No se encontró contenido visible en el PDF del ticket.");
            }

            using var cropped = CropToContent(
                combined,
                bounds.Value,
                settings);

            using var fitted = FitContentToHeadWidth(
                cropped,
                settings.TicketDotsWidth,
                settings.TicketHorizontalPaddingDots,
                settings.TicketRenderDpi);

            ApplyMonochromeThreshold(
                fitted,
                settings.TicketBlackThreshold);

            SaveDiagnosticImage(
                fitted,
                "last-ticket-final.png",
                settings);

            AppendRasterDiagnostic(
                sourceWidth: combined.Width,
                sourceHeight: combined.Height,
                contentBounds: bounds.Value,
                finalWidth: fitted.Width,
                finalHeight: fitted.Height,
                settings: settings);

            return (Bitmap)fitted.Clone();
        }
        finally
        {
            foreach (var page in pages)
                page.Dispose();
        }
    }

    private static Bitmap CropToContent(
        Bitmap source,
        Rectangle bounds,
        AgentSettings settings)
    {
        var sourceScale =
            source.Width / (double)settings.TicketDotsWidth;

        var topPadding = Math.Max(
            0,
            (int)Math.Round(
                settings.TicketTopPaddingMm
                / 25.4
                * settings.TicketRenderDpi
                * sourceScale));

        var bottomPadding = Math.Max(
            0,
            (int)Math.Round(
                settings.TicketBottomPaddingMm
                / 25.4
                * settings.TicketRenderDpi
                * sourceScale));

        // El padding horizontal final se aplica después de escalar.
        // Aquí recortamos al contenido real para eliminar cualquier página
        // PDF artificialmente ancha.
        var left = Math.Max(0, bounds.Left);
        var right = Math.Min(source.Width, bounds.Right);
        var top = Math.Max(0, bounds.Top - topPadding);
        var bottom = Math.Min(
            source.Height,
            bounds.Bottom + bottomPadding);

        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);

        var result = source.Clone(
            new Rectangle(left, top, width, height),
            PixelFormat.Format24bppRgb);

        result.SetResolution(
            settings.TicketRenderDpi,
            settings.TicketRenderDpi);

        return result;
    }

    private static Bitmap FitContentToHeadWidth(
        Bitmap source,
        int headWidthDots,
        int horizontalPaddingDots,
        int dpi)
    {
        var padding = Math.Clamp(
            horizontalPaddingDots,
            0,
            Math.Max(0, headWidthDots / 4));

        var printableContentWidth = Math.Max(
            1,
            headWidthDots - padding * 2);

        var scale =
            printableContentWidth / (double)source.Width;

        var targetHeight = Math.Max(
            1,
            (int)Math.Round(source.Height * scale));

        using var resized = new Bitmap(
            printableContentWidth,
            targetHeight,
            PixelFormat.Format24bppRgb);

        resized.SetResolution(dpi, dpi);

        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.Clear(Color.White);
            graphics.CompositingMode =
                CompositingMode.SourceCopy;
            graphics.CompositingQuality =
                CompositingQuality.HighQuality;
            graphics.InterpolationMode =
                InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode =
                SmoothingMode.HighQuality;
            graphics.PixelOffsetMode =
                PixelOffsetMode.HighQuality;

            graphics.DrawImage(
                source,
                new Rectangle(
                    0,
                    0,
                    printableContentWidth,
                    targetHeight),
                0,
                0,
                source.Width,
                source.Height,
                GraphicsUnit.Pixel);
        }

        var result = new Bitmap(
            headWidthDots,
            targetHeight,
            PixelFormat.Format24bppRgb);

        result.SetResolution(dpi, dpi);

        using (var graphics = Graphics.FromImage(result))
        {
            graphics.Clear(Color.White);
            graphics.DrawImageUnscaled(
                resized,
                padding,
                0);
        }

        return result;
    }

    private static Rectangle? FindContentRectangle(
        Bitmap bitmap,
        int whiteThreshold)
    {
        using var rgb = Ensure24Bpp(bitmap);

        var rect = new Rectangle(
            0,
            0,
            rgb.Width,
            rgb.Height);

        var data = rgb.LockBits(
            rect,
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);

        try
        {
            var stride = Math.Abs(data.Stride);
            var bytes = new byte[stride * rgb.Height];

            Marshal.Copy(
                data.Scan0,
                bytes,
                0,
                bytes.Length);

            var minX = rgb.Width;
            var minY = rgb.Height;
            var maxX = -1;
            var maxY = -1;

            for (var y = 0; y < rgb.Height; y++)
            {
                var rowOffset = data.Stride >= 0
                    ? y * stride
                    : (rgb.Height - 1 - y) * stride;

                for (var x = 0; x < rgb.Width; x++)
                {
                    var index = rowOffset + x * 3;

                    var b = bytes[index];
                    var g = bytes[index + 1];
                    var r = bytes[index + 2];

                    if (r >= whiteThreshold
                        && g >= whiteThreshold
                        && b >= whiteThreshold)
                    {
                        continue;
                    }

                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            if (maxX < minX || maxY < minY)
                return null;

            return Rectangle.FromLTRB(
                minX,
                minY,
                maxX + 1,
                maxY + 1);
        }
        finally
        {
            rgb.UnlockBits(data);
        }
    }

    private static void SaveDiagnosticImage(
        Bitmap bitmap,
        string fileName,
        AgentSettings settings)
    {
        if (!settings.TicketSaveDiagnostics)
            return;

        try
        {
            var logsDirectory = Path.Combine(
                AppContext.BaseDirectory,
                "logs");

            Directory.CreateDirectory(logsDirectory);

            bitmap.Save(
                Path.Combine(logsDirectory, fileName),
                ImageFormat.Png);
        }
        catch
        {
            // El diagnóstico nunca debe impedir la impresión.
        }
    }

    private static void AppendRasterDiagnostic(
        int sourceWidth,
        int sourceHeight,
        Rectangle contentBounds,
        int finalWidth,
        int finalHeight,
        AgentSettings settings)
    {
        if (!settings.TicketSaveDiagnostics)
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
                "raster-normalize",
                $"source={sourceWidth}x{sourceHeight}",
                $"content={contentBounds.X},{contentBounds.Y},{contentBounds.Width},{contentBounds.Height}",
                $"final={finalWidth}x{finalHeight}",
                $"padding-x={settings.TicketHorizontalPaddingDots}",
                $"render-scale={settings.TicketRenderScale}",
                $"source-touches-left={(contentBounds.Left == 0 ? "YES" : "NO")}",
                $"source-touches-right={(contentBounds.Right >= sourceWidth ? "YES" : "NO")}");

            File.AppendAllText(
                logPath,
                line + Environment.NewLine);
        }
        catch
        {
            // El diagnóstico nunca debe impedir la impresión.
        }
    }

    private static void ApplyMonochromeThreshold(
        Bitmap bitmap,
        int threshold)
    {
        var rect = new Rectangle(
            0,
            0,
            bitmap.Width,
            bitmap.Height);

        var data = bitmap.LockBits(
            rect,
            ImageLockMode.ReadWrite,
            PixelFormat.Format24bppRgb);

        try
        {
            var stride = Math.Abs(data.Stride);
            var bytes = new byte[stride * bitmap.Height];

            Marshal.Copy(
                data.Scan0,
                bytes,
                0,
                bytes.Length);

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

                    var luminance =
                        (r * 299 + g * 587 + b * 114) / 1000;

                    var value =
                        luminance < threshold
                            ? (byte)0
                            : (byte)255;

                    bytes[index] = value;
                    bytes[index + 1] = value;
                    bytes[index + 2] = value;
                }
            }

            Marshal.Copy(
                bytes,
                0,
                data.Scan0,
                bytes.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Bitmap CombineVertically(
        IReadOnlyList<Bitmap> pages,
        int dpi)
    {
        if (pages.Count == 0)
            throw new InvalidOperationException(
                "No se pudo rasterizar el ticket.");

        if (pages.Count == 1)
            return (Bitmap)pages[0].Clone();

        var width = pages.Max(x => x.Width);
        var height = pages.Sum(x => x.Height);

        var result = new Bitmap(
            width,
            height,
            PixelFormat.Format24bppRgb);

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

        var copy = new Bitmap(
            source.Width,
            source.Height,
            PixelFormat.Format24bppRgb);

        copy.SetResolution(
            source.HorizontalResolution,
            source.VerticalResolution);

        using var graphics = Graphics.FromImage(copy);
        graphics.Clear(Color.White);
        graphics.DrawImageUnscaled(source, 0, 0);

        return copy;
    }

    // ---------------------------------------------------------------------
    // ESC/POS RAW
    // ---------------------------------------------------------------------

    private static void PrintEscPos(
        Bitmap bitmap,
        string printerName,
        int copies,
        int chunkRows,
        int feedLines)
    {
        if (!OpenPrinter(
                printerName,
                out var printerHandle,
                IntPtr.Zero))
        {
            throw Win32PrintException(
                $"No se pudo abrir la impresora '{printerName}'.");
        }

        try
        {
            var docInfo = new DocInfo1
            {
                DocName = "SCV Ticket",
                OutputFile = null,
                DataType = "RAW"
            };

            var jobId = StartDocPrinter(
                printerHandle,
                1,
                ref docInfo);

            if (jobId == 0)
            {
                throw Win32PrintException(
                    $"No se pudo iniciar el trabajo RAW en '{printerName}'.");
            }

            try
            {
                if (!StartPagePrinter(printerHandle))
                {
                    throw Win32PrintException(
                        $"No se pudo iniciar la página RAW en '{printerName}'.");
                }

                try
                {
                    // ESC @ = inicializa la impresora.
                    WriteRaw(
                        printerHandle,
                        new byte[] { 0x1B, 0x40 });

                    using var rgb = Ensure24Bpp(bitmap);

                    var rect = new Rectangle(
                        0,
                        0,
                        rgb.Width,
                        rgb.Height);

                    var data = rgb.LockBits(
                        rect,
                        ImageLockMode.ReadOnly,
                        PixelFormat.Format24bppRgb);

                    try
                    {
                        var stride = Math.Abs(data.Stride);
                        var pixels = new byte[stride * rgb.Height];

                        Marshal.Copy(
                            data.Scan0,
                            pixels,
                            0,
                            pixels.Length);

                        for (var copy = 0; copy < copies; copy++)
                        {
                            var y = 0;

                            while (y < rgb.Height)
                            {
                                var rows = Math.Min(
                                    chunkRows,
                                    rgb.Height - y);

                                var raster = BuildEscPosRasterChunk(
                                    pixels,
                                    data.Stride,
                                    stride,
                                    rgb.Width,
                                    rgb.Height,
                                    y,
                                    rows);

                                var widthBytes =
                                    (rgb.Width + 7) / 8;

                                // GS v 0 m xL xH yL yH
                                // Raster bit image, densidad normal.
                                var header = new byte[]
                                {
                                    0x1D,
                                    0x76,
                                    0x30,
                                    0x00,
                                    (byte)(widthBytes & 0xFF),
                                    (byte)((widthBytes >> 8) & 0xFF),
                                    (byte)(rows & 0xFF),
                                    (byte)((rows >> 8) & 0xFF)
                                };

                                WriteRaw(
                                    printerHandle,
                                    header);

                                WriteRaw(
                                    printerHandle,
                                    raster);

                                y += rows;
                            }

                            if (feedLines > 0)
                            {
                                // ESC d n = alimenta n líneas.
                                WriteRaw(
                                    printerHandle,
                                    new byte[]
                                    {
                                        0x1B,
                                        0x64,
                                        (byte)Math.Clamp(
                                            feedLines,
                                            0,
                                            255)
                                    });
                            }
                        }
                    }
                    finally
                    {
                        rgb.UnlockBits(data);
                    }
                }
                finally
                {
                    EndPagePrinter(printerHandle);
                }
            }
            finally
            {
                EndDocPrinter(printerHandle);
            }
        }
        finally
        {
            ClosePrinter(printerHandle);
        }
    }

    private static byte[] BuildEscPosRasterChunk(
        byte[] pixels,
        int signedStride,
        int stride,
        int width,
        int height,
        int startY,
        int rows)
    {
        var widthBytes = (width + 7) / 8;
        var raster = new byte[widthBytes * rows];

        for (var row = 0; row < rows; row++)
        {
            var y = startY + row;

            var rowOffset = signedStride >= 0
                ? y * stride
                : (height - 1 - y) * stride;

            var rasterRowOffset =
                row * widthBytes;

            for (var x = 0; x < width; x++)
            {
                var index =
                    rowOffset + x * 3;

                // La imagen ya fue binarizada.
                // Aun así se usa luminancia para tolerar cualquier píxel intermedio.
                var b = pixels[index];
                var g = pixels[index + 1];
                var r = pixels[index + 2];

                var luminance =
                    (r * 299 + g * 587 + b * 114) / 1000;

                if (luminance >= 128)
                    continue;

                raster[
                    rasterRowOffset + x / 8]
                    |= (byte)(0x80 >> (x & 7));
            }
        }

        return raster;
    }

    private static void WriteRaw(
        IntPtr printerHandle,
        byte[] data)
    {
        if (data.Length == 0)
            return;

        if (!WritePrinter(
                printerHandle,
                data,
                data.Length,
                out var written))
        {
            throw Win32PrintException(
                "Windows no pudo enviar datos RAW a la impresora.");
        }

        if (written != data.Length)
        {
            throw new InvalidOperationException(
                $"La impresora recibió {written} de {data.Length} bytes RAW.");
        }
    }

    private static Exception Win32PrintException(
        string message)
    {
        var error =
            Marshal.GetLastWin32Error();

        return new InvalidOperationException(
            $"{message} Win32Error={error}.");
    }

    private static void AppendEscPosDiagnostic(
        string printerName,
        int bitmapWidth,
        int bitmapHeight,
        int copies,
        AgentSettings settings)
    {
        try
        {
            var logsDirectory =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "logs");

            Directory.CreateDirectory(
                logsDirectory);

            var logPath =
                Path.Combine(
                    logsDirectory,
                    "print-agent.log");

            var line = string.Join(
                " | ",
                DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss"),
                "mode=ESC_POS",
                $"printer={printerName}",
                $"dots={bitmapWidth}x{bitmapHeight}",
                $"copies={copies}",
                $"chunk-rows={settings.TicketEscPosChunkRows}",
                $"feed-lines={settings.TicketFeedLines}",
                $"render-dpi={settings.TicketRenderDpi}");

            File.AppendAllText(
                logPath,
                line + Environment.NewLine);
        }
        catch
        {
            // El diagnóstico nunca debe bloquear una impresión.
        }
    }

    // ---------------------------------------------------------------------
    // GDI - compatibilidad
    // ---------------------------------------------------------------------

    private static void PrintBitmapGdi(
        Bitmap bitmap,
        string printerName,
        int copies,
        double paperWidthMm,
        double printableWidthMm,
        int dpi)
    {
        using var document = new PrintDocument
        {
            PrintController = new StandardPrintController(),
            OriginAtMargins = false,
            DocumentName = "SCV Ticket"
        };

        document.PrinterSettings.PrinterName =
            printerName;

        if (!document.PrinterSettings.IsValid)
        {
            throw new InvalidOperationException(
                $"La impresora '{printerName}' no está disponible en Windows.");
        }

        var paperWidthHundredths =
            Math.Max(
                1,
                MmToHundredthsOfInch(
                    paperWidthMm));

        var desiredPrintableWidthHundredths =
            Math.Max(
                1,
                MmToHundredthsOfInch(
                    printableWidthMm));

        var bitmapHeightHundredths =
            Math.Max(
                1,
                (int)Math.Ceiling(
                    bitmap.Height * 100d / dpi));

        document.DefaultPageSettings.Margins =
            new Margins(0, 0, 0, 0);

        document.DefaultPageSettings.Landscape =
            false;

        document.DefaultPageSettings.PaperSize =
            new PaperSize(
                "SCV Ticket",
                paperWidthHundredths,
                bitmapHeightHundredths);

        document.PrinterSettings.Copies =
            (short)Math.Clamp(
                copies,
                1,
                short.MaxValue);

        document.PrintPage += (_, e) =>
        {
            var graphics = e.Graphics
                ?? throw new InvalidOperationException(
                    "Windows no proporcionó un contexto gráfico para la impresión.");

            graphics.PageUnit =
                GraphicsUnit.Display;

            graphics.SmoothingMode =
                SmoothingMode.None;

            graphics.InterpolationMode =
                InterpolationMode.NearestNeighbor;

            graphics.PixelOffsetMode =
                PixelOffsetMode.Half;

            graphics.CompositingQuality =
                CompositingQuality.HighSpeed;

            var driverPrintableWidth =
                e.PageSettings.PrintableArea.Width;

            var targetWidth =
                desiredPrintableWidthHundredths;

            if (driverPrintableWidth > 0)
            {
                targetWidth = Math.Min(
                    targetWidth,
                    (int)Math.Floor(
                        driverPrintableWidth));
            }

            targetWidth =
                Math.Max(1, targetWidth);

            var targetHeight =
                Math.Max(
                    1,
                    (int)Math.Round(
                        bitmap.Height
                        * (targetWidth
                           / (double)bitmap.Width)));

            var target =
                new Rectangle(
                    0,
                    0,
                    targetWidth,
                    targetHeight);

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

    private static int MmToHundredthsOfInch(
        double millimeters)
        => (int)Math.Round(
            millimeters / 25.4 * 100d);

    // ---------------------------------------------------------------------
    // WinSpool RAW
    // ---------------------------------------------------------------------

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    private struct DocInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string DocName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? OutputFile;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string DataType;
    }

    [DllImport(
        "winspool.drv",
        EntryPoint = "OpenPrinterW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter(
        string printerName,
        out IntPtr printerHandle,
        IntPtr printerDefaults);

    [DllImport(
        "winspool.drv",
        EntryPoint = "ClosePrinter",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(
        IntPtr printerHandle);

    [DllImport(
        "winspool.drv",
        EntryPoint = "StartDocPrinterW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(
        IntPtr printerHandle,
        int level,
        ref DocInfo1 docInfo);

    [DllImport(
        "winspool.drv",
        EntryPoint = "EndDocPrinter",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndDocPrinter(
        IntPtr printerHandle);

    [DllImport(
        "winspool.drv",
        EntryPoint = "StartPagePrinter",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartPagePrinter(
        IntPtr printerHandle);

    [DllImport(
        "winspool.drv",
        EntryPoint = "EndPagePrinter",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPagePrinter(
        IntPtr printerHandle);

    [DllImport(
        "winspool.drv",
        EntryPoint = "WritePrinter",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrinter(
        IntPtr printerHandle,
        byte[] bytes,
        int count,
        out int written);
}
