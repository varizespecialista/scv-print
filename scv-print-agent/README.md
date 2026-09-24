# SCV Print Agent

Agente Windows para impresión silenciosa de PDFs desde SCV. Se conecta al backend mediante SignalR, registra las impresoras instaladas y procesa trabajos persistidos en PostgreSQL.

## Configuración

1. En el backend configure `ScvPrintAgent__SharedKey` con una clave larga y aleatoria.
2. En `SCV.PrintAgent/appsettings.json` configure `ApiBaseUrl`, `AgentCode`, `AgentName`, `BranchCode` y la misma `SharedKey`. En producción se recomienda usar la variable de entorno `SCV_PRINT_AGENT_KEY`.
3. Publique el agente:

```powershell
dotnet publish .\SCV.PrintAgent\SCV.PrintAgent.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

4. Ejecute `publish\SCV.PrintAgent.exe`.
5. Para inicio automático con Windows:

```powershell
powershell -ExecutionPolicy Bypass -File .\install-startup.ps1
```

Requiere Microsoft Edge WebView2 Runtime, incluido normalmente en Windows 10/11 actualizado.
