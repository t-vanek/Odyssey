using System.Diagnostics;

namespace Odyssey.Updater;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = UpdateInstallerOptions.Parse(args);
            var installer = new UpdateInstaller(message => File.AppendAllText(options.LogPath, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}"));
            await installer.InstallAsync(options);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                var logPath = UpdateInstallerOptions.TryResolveLogPath(args);
                File.AppendAllText(logPath, $"{DateTimeOffset.UtcNow:O} FATAL {ex}{Environment.NewLine}");
            }
            catch { }
            return 1;
        }
    }
}
