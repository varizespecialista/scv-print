namespace SCV.PrintAgent;

public sealed record PrinterRegistration(string Name, bool IsDefault);
public sealed record AgentRegistration(string AgentCode, string AgentName, string MachineName, string? BranchCode, string? Version, IReadOnlyCollection<PrinterRegistration> Printers);
public sealed record PrintJob(long Id, string PrinterName, string DocumentType, string Format, string FileName, int Copies, string Status, DateTime CreatedAt);
public sealed record PrintJobStatusRequest(string Status, string? Error);
