namespace SCV.PrintAgent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var settings = AgentSettings.Load();
            settings.Validate();
            Application.Run(new PrintAgentApplicationContext(settings));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "SCV Print Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
