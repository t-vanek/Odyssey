namespace Odyssey.Infrastructure;

public sealed class ApplicationStorage
{
    public ApplicationStorage(string? overrideDirectory = null)
    {
        DirectoryPath = overrideDirectory ?? ResolveDefaultDirectory();
        Directory.CreateDirectory(DirectoryPath);
        DatabasePath = Path.Combine(DirectoryPath, "odyssey.db");
    }

    public string DirectoryPath { get; }
    public string DatabasePath { get; }

    private static string ResolveDefaultDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        return Path.Combine(root, "Odyssey");
    }
}
