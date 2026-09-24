using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace SCV.PrintAgent;

public sealed class ScvPrintAgentClient : IAsyncDisposable
{
    private readonly AgentSettings _settings;
    private readonly PdfPrintForm _printForm;
    private readonly HttpClient _http;
    private readonly HubConnection _hub;
    private readonly SemaphoreSlim _processGate = new(1, 1);

    public event Action<string>? StatusChanged;

    public ScvPrintAgentClient(AgentSettings settings, PdfPrintForm printForm)
    {
        _settings = settings;
        _printForm = printForm;
        _http = new HttpClient
        {
            BaseAddress = new Uri(settings.ApiBaseUrl.TrimEnd('/') + "/")
        };

        _http.DefaultRequestHeaders.Add("X-SCV-Print-Agent-Key", settings.SharedKey);
        _http.DefaultRequestHeaders.Add("X-SCV-Print-Agent-Code", settings.AgentCode);

        var hubUrl = settings.ApiBaseUrl.TrimEnd('/')
            + "/hubs/scv-print-agent?agentCode="
            + Uri.EscapeDataString(settings.AgentCode);

        _hub = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.Headers.Add("X-SCV-Print-Agent-Key", settings.SharedKey);
            })
            .WithAutomaticReconnect()
            .Build();

        _hub.On<long>("PrintJobAvailable", jobId =>
        {
            _ = ProcessJobNotificationSafeAsync(jobId);
        });

        _hub.Reconnecting += exception =>
        {
            StatusChanged?.Invoke("Reconectando...");
            return Task.CompletedTask;
        };

        _hub.Reconnected += async connectionId =>
        {
            try
            {
                await RegisterAndDrainAsync();
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Reconectado, pero no se pudo sincronizar: {ex.Message}");
            }
        };

        _hub.Closed += exception =>
        {
            StatusChanged?.Invoke("Desconectado");
            return Task.CompletedTask;
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        StatusChanged?.Invoke("Conectando...");
        await _hub.StartAsync(cancellationToken);
        await RegisterAndDrainAsync(cancellationToken);
    }

    public async Task RegisterAndDrainAsync(CancellationToken cancellationToken = default)
    {
        var registration = new AgentRegistration(
            _settings.AgentCode,
            string.IsNullOrWhiteSpace(_settings.AgentName)
                ? _settings.AgentCode
                : _settings.AgentName,
            Environment.MachineName,
            _settings.BranchCode,
            typeof(ScvPrintAgentClient).Assembly.GetName().Version?.ToString(),
            PrinterDiscovery.GetPrinters());

        using var response = await _http.PostAsJsonAsync(
            "api/impresion/agent/register",
            registration,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        StatusChanged?.Invoke($"Conectado · {registration.Printers.Count} impresoras");

        var pending = await _http.GetFromJsonAsync<PrintJob[]>(
            "api/impresion/agent/jobs/pending",
            cancellationToken) ?? [];

        foreach (var job in pending)
        {
            await ProcessJobSafeAsync(job.Id, cancellationToken);
        }
    }

    private async Task ProcessJobNotificationSafeAsync(long jobId)
    {
        try
        {
            await ProcessJobSafeAsync(jobId);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Error al recibir trabajo #{jobId}: {ex.Message}");
        }
    }

    private async Task ProcessJobSafeAsync(
        long jobId,
        CancellationToken cancellationToken = default)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            var pending = await _http.GetFromJsonAsync<PrintJob[]>(
                "api/impresion/agent/jobs/pending",
                cancellationToken) ?? [];

            var job = pending.FirstOrDefault(x => x.Id == jobId);
            if (job is null)
                return;

            await SetStatusAsync(jobId, "IMPRIMIENDO", null, cancellationToken);

            try
            {
                var bytes = await _http.GetByteArrayAsync(
                    $"api/impresion/agent/jobs/{jobId}/file",
                    cancellationToken);

                await _printForm.PrintPdfAsync(
                    bytes,
                    job.PrinterName,
                    job.Copies,
                    cancellationToken);

                await SetStatusAsync(jobId, "COMPLETADO", null, cancellationToken);
                StatusChanged?.Invoke($"Impreso #{jobId} · {job.PrinterName}");
            }
            catch (Exception ex)
            {
                try
                {
                    await SetStatusAsync(jobId, "ERROR", ex.Message, cancellationToken);
                }
                catch
                {
                    // El error original de impresión tiene prioridad para el estado local.
                }

                StatusChanged?.Invoke($"Error #{jobId}: {ex.Message}");
            }
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task SetStatusAsync(
        long jobId,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            $"api/impresion/agent/jobs/{jobId}/status",
            new PrintJobStatusRequest(status, error),
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        await _hub.DisposeAsync();
        _http.Dispose();
        _processGate.Dispose();
    }
}
