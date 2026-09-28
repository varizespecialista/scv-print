using System.Text.Json;

namespace SCV.PrintAgent;

public sealed class AgentSettings
{
    public string ApiBaseUrl { get; init; } = string.Empty;
    public string AgentCode { get; init; } = string.Empty;
    public string AgentName { get; init; } = string.Empty;
    public string? BranchCode { get; init; }
    public string SharedKey { get; init; } = string.Empty;

    // Ajustes de impresión térmica.
    // ESC_POS es el modo recomendado para impresoras térmicas compatibles.
    public string TicketPrintMode { get; init; } = "ESC_POS";

    // 384 dots = cabezal típico de 58 mm a 203 DPI.
    public int TicketDotsWidth { get; init; } = 360;

    // Render interno a mayor resolución antes de recortar y ajustar.
    public int TicketRenderScale { get; init; } = 2;

    // Margen final de seguridad a izquierda y derecha del cabezal.
    public int TicketHorizontalPaddingDots { get; init; } = 6;

    // Guarda las imágenes exactas antes/después del normalizado para diagnóstico.
    public bool TicketSaveDiagnostics { get; init; } = true;

    // Se envía el raster por bloques para no saturar el buffer de la térmica.
    public int TicketEscPosChunkRows { get; init; } = 256;

    // Líneas de avance al finalizar cada copia.
    public int TicketFeedLines { get; init; } = 3;

    // Se mantienen para modo GDI y para cálculo de padding vertical.
    public double TicketPaperWidthMm { get; init; } = 58.0;
    public double TicketPrintableWidthMm { get; init; } = 48.0;
    public int TicketRenderDpi { get; init; } = 203;
    public int TicketWhiteThreshold { get; init; } = 248;
    public int TicketBlackThreshold { get; init; } = 220;
    public double TicketTopPaddingMm { get; init; } = 1.0;
    public double TicketBottomPaddingMm { get; init; } = 3.0;

    public static AgentSettings Load()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "appsettings.json");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "No se encontró appsettings.json del SCV Print Agent.",
                path);
        }

        var settings =
            JsonSerializer.Deserialize<AgentSettings>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })
            ?? throw new InvalidOperationException(
                "No se pudo leer la configuración del SCV Print Agent.");

        return new AgentSettings
        {
            ApiBaseUrl =
                Environment.GetEnvironmentVariable(
                    "SCV_API_URL")?.Trim()
                    is { Length: > 0 } api
                        ? api
                        : settings.ApiBaseUrl.Trim(),

            AgentCode =
                Environment.GetEnvironmentVariable(
                    "SCV_PRINT_AGENT_CODE")?.Trim()
                    is { Length: > 0 } code
                        ? code
                        : settings.AgentCode.Trim(),

            AgentName =
                settings.AgentName.Trim(),

            BranchCode =
                settings.BranchCode?.Trim(),

            SharedKey =
                Environment.GetEnvironmentVariable(
                    "SCV_PRINT_AGENT_KEY")?.Trim()
                    is { Length: > 0 } key
                        ? key
                        : settings.SharedKey.Trim(),

            TicketPrintMode =
                string.IsNullOrWhiteSpace(
                    settings.TicketPrintMode)
                    ? "ESC_POS"
                    : settings.TicketPrintMode.Trim(),

            TicketDotsWidth =
                settings.TicketDotsWidth,

            TicketRenderScale =
                settings.TicketRenderScale,

            TicketHorizontalPaddingDots =
                settings.TicketHorizontalPaddingDots,

            TicketSaveDiagnostics =
                settings.TicketSaveDiagnostics,

            TicketEscPosChunkRows =
                settings.TicketEscPosChunkRows,

            TicketFeedLines =
                settings.TicketFeedLines,

            TicketPaperWidthMm =
                settings.TicketPaperWidthMm,

            TicketPrintableWidthMm =
                settings.TicketPrintableWidthMm,

            TicketRenderDpi =
                settings.TicketRenderDpi,

            TicketWhiteThreshold =
                settings.TicketWhiteThreshold,

            TicketBlackThreshold =
                settings.TicketBlackThreshold,

            TicketTopPaddingMm =
                settings.TicketTopPaddingMm,

            TicketBottomPaddingMm =
                settings.TicketBottomPaddingMm
        };
    }

    public void Validate()
    {
        if (!Uri.TryCreate(
                ApiBaseUrl,
                UriKind.Absolute,
                out _))
        {
            throw new InvalidOperationException(
                "ApiBaseUrl no es una URL válida.");
        }

        if (string.IsNullOrWhiteSpace(
                AgentCode))
        {
            throw new InvalidOperationException(
                "AgentCode es obligatorio.");
        }

        if (string.IsNullOrWhiteSpace(
                SharedKey)
            || SharedKey == "CAMBIAR_EN_PRODUCCION")
        {
            throw new InvalidOperationException(
                "Configure SharedKey antes de iniciar SCV Print Agent.");
        }

        if (!string.Equals(
                TicketPrintMode,
                "ESC_POS",
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                TicketPrintMode,
                "GDI",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "TicketPrintMode debe ser ESC_POS o GDI.");
        }

        if (TicketDotsWidth is < 128 or > 2048)
        {
            throw new InvalidOperationException(
                "TicketDotsWidth debe estar entre 128 y 2048 dots.");
        }

        if (TicketRenderScale is < 1 or > 4)
        {
            throw new InvalidOperationException(
                "TicketRenderScale debe estar entre 1 y 4.");
        }

        if (TicketHorizontalPaddingDots is < 0 or > 64)
        {
            throw new InvalidOperationException(
                "TicketHorizontalPaddingDots debe estar entre 0 y 64.");
        }

        if (TicketEscPosChunkRows is < 24 or > 2048)
        {
            throw new InvalidOperationException(
                "TicketEscPosChunkRows debe estar entre 24 y 2048 filas.");
        }

        if (TicketFeedLines is < 0 or > 20)
        {
            throw new InvalidOperationException(
                "TicketFeedLines debe estar entre 0 y 20.");
        }

        if (TicketPaperWidthMm is < 40 or > 120)
        {
            throw new InvalidOperationException(
                "TicketPaperWidthMm debe estar entre 40 y 120 mm.");
        }

        if (TicketPrintableWidthMm is < 30 or > 110)
        {
            throw new InvalidOperationException(
                "TicketPrintableWidthMm debe estar entre 30 y 110 mm.");
        }

        if (TicketPrintableWidthMm > TicketPaperWidthMm)
        {
            throw new InvalidOperationException(
                "TicketPrintableWidthMm no puede ser mayor que TicketPaperWidthMm.");
        }

        if (TicketRenderDpi is < 150 or > 600)
        {
            throw new InvalidOperationException(
                "TicketRenderDpi debe estar entre 150 y 600 DPI.");
        }

        if (TicketWhiteThreshold is < 200 or > 255)
        {
            throw new InvalidOperationException(
                "TicketWhiteThreshold debe estar entre 200 y 255.");
        }

        if (TicketBlackThreshold is < 80 or > 250)
        {
            throw new InvalidOperationException(
                "TicketBlackThreshold debe estar entre 80 y 250.");
        }
    }
}
