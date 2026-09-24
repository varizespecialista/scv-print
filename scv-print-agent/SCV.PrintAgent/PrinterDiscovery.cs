using System.Drawing.Printing;

namespace SCV.PrintAgent;

public static class PrinterDiscovery
{
    public static IReadOnlyCollection<PrinterRegistration> GetPrinters()
    {
        var defaultPrinter = new PrinterSettings().PrinterName;
        return PrinterSettings.InstalledPrinters.Cast<string>()
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => new PrinterRegistration(x, string.Equals(x, defaultPrinter, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}
