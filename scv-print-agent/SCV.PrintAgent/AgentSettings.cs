using System.Text.Json;

namespace SCV.PrintAgent;

public sealed class AgentSettings
{
    public string ApiBaseUrl { get; init; } = string.Empty;
    public string AgentCode { get; init; } = string.Empty;
    public string AgentName { get; init; } = string.Empty;
    public string? BranchCode { get; init; }
    public string SharedKey { get; init; } = string.Empty;

    // Escala observada como correcta en la impresión manual de Chrome/WebView.
    // 0.82 equivale a 82%.
    public double TicketScaleFactor { get; init; } = 0.82;

    // WebView2 usará el tamaño de papel configurado en el driver de Windows
    // (por ejemplo DIR-E58: 58(48) x 297 mm).
    public bool TicketUsePrinterDefaultMedia { get; init; } = true;

    // Márgenes adicionales del motor WebView2.
    // La plantilla FastReport ya contiene sus propios márgenes.
    public double TicketMarginLeftMm { get; init; } = 0.0;
    public double TicketMarginRightMm { get; init; } = 0.0;
    public double TicketMarginTopMm { get; init; } = 0.0;
    public double TicketMarginBottomMm { get; init; } = 0.0;

    public bool TicketSaveDiagnostics { get; init; } = true;

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

            TicketScaleFactor =
                settings.TicketScaleFactor,

            TicketUsePrinterDefaultMedia =
                settings.TicketUsePrinterDefaultMedia,

            TicketMarginLeftMm =
                settings.TicketMarginLeftMm,

            TicketMarginRightMm =
                settings.TicketMarginRightMm,

            TicketMarginTopMm =
                settings.TicketMarginTopMm,

            TicketMarginBottomMm =
                settings.TicketMarginBottomMm,

            TicketSaveDiagnostics =
                settings.TicketSaveDiagnostics
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

        if (string.IsNullOrWhiteSpace(AgentCode))
        {
            throw new InvalidOperationException(
                "AgentCode es obligatorio.");
        }

        if (string.IsNullOrWhiteSpace(SharedKey)
            || SharedKey == "CAMBIAR_EN_PRODUCCION")
        {
            throw new InvalidOperationException(
                "Configure SharedKey antes de iniciar SCV Print Agent.");
        }

        if (TicketScaleFactor is < 0.1 or > 2.0)
        {
            throw new InvalidOperationException(
                "TicketScaleFactor debe estar entre 0.1 y 2.0.");
        }

        if (TicketMarginLeftMm < 0
            || TicketMarginRightMm < 0
            || TicketMarginTopMm < 0
            || TicketMarginBottomMm < 0)
        {
            throw new InvalidOperationException(
                "Los márgenes del ticket no pueden ser negativos.");
        }
    }
}
