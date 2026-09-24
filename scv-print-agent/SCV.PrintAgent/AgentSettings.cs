using System.Text.Json;

namespace SCV.PrintAgent;

public sealed class AgentSettings
{
    public string ApiBaseUrl { get; init; } = string.Empty;
    public string AgentCode { get; init; } = string.Empty;
    public string AgentName { get; init; } = string.Empty;
    public string? BranchCode { get; init; }
    public string SharedKey { get; init; } = string.Empty;

    public static AgentSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) throw new FileNotFoundException("No se encontró appsettings.json del SCV Print Agent.", path);

        var settings = JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("No se pudo leer la configuración del SCV Print Agent.");

        return new AgentSettings
        {
            ApiBaseUrl = Environment.GetEnvironmentVariable("SCV_API_URL")?.Trim() is { Length: > 0 } api ? api : settings.ApiBaseUrl.Trim(),
            AgentCode = Environment.GetEnvironmentVariable("SCV_PRINT_AGENT_CODE")?.Trim() is { Length: > 0 } code ? code : settings.AgentCode.Trim(),
            AgentName = settings.AgentName.Trim(),
            BranchCode = settings.BranchCode?.Trim(),
            SharedKey = Environment.GetEnvironmentVariable("SCV_PRINT_AGENT_KEY")?.Trim() is { Length: > 0 } key ? key : settings.SharedKey.Trim()
        };
    }

    public void Validate()
    {
        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out _)) throw new InvalidOperationException("ApiBaseUrl no es una URL válida.");
        if (string.IsNullOrWhiteSpace(AgentCode)) throw new InvalidOperationException("AgentCode es obligatorio.");
        if (string.IsNullOrWhiteSpace(SharedKey) || SharedKey == "CAMBIAR_EN_PRODUCCION")
            throw new InvalidOperationException("Configure SharedKey antes de iniciar SCV Print Agent.");
    }
}
