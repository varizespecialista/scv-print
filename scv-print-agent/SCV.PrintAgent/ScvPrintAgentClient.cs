using System.Net;
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
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _pollTask;

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

        if (_hub.State == HubConnectionState.Disconnected)
            await _hub.StartAsync(cancellationToken);

        await RegisterAndDrainAsync(cancellationToken);
        _pollTask ??= PollPendingJobsAsync(_disposeCts.Token);
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

        await DrainPendingJobsAsync(cancellationToken);
    }

    private async Task DrainPendingJobsAsync(CancellationToken cancellationToken)
    {
        var pending = await _http.GetFromJsonAsync<PrintJob[]>(
            "api/impresion/agent/jobs/pending",
            cancellationToken) ?? [];

        foreach (var job in pending)
        {
            await ProcessJobSafeAsync(job.Id, cancellationToken);
        }
    }

    private async Task PollPendingJobsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await DrainPendingJobsAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // SignalR sigue siendo la vía inmediata. Este polling es solo
                    // una red de seguridad para trabajos cuya notificación se perdió.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cierre normal del agente.
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

            // El claim es atómico en backend. Si existen dos instancias con el
            // mismo AgentCode, únicamente una obtiene el trabajo y lo imprime.
            if (!await TryClaimJobAsync(jobId, cancellationToken))
                return;

            try
            {
                var bytes = await _http.GetByteArrayAsync(
                    $"api/impresion/agent/jobs/{jobId}/file",
                    cancellationToken);

                await _printForm.PrintPdfAsync(
                    bytes,
                    job.PrinterName,
                    job.Format,
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

    private async Task<bool> TryClaimJobAsync(
        long jobId,
        CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(
            $"api/impresion/agent/jobs/{jobId}/claim",
            content: null,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Conflict)
            return false;

        response.EnsureSuccessStatusCode();
        return true;
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
        _disposeCts.Cancel();

        if (_pollTask is not null)
        {
            try { await _pollTask; }
            catch (OperationCanceledException) { }
        }

        await _hub.DisposeAsync();
        _http.Dispose();
        _processGate.Dispose();
        _disposeCts.Dispose();
    }
}
