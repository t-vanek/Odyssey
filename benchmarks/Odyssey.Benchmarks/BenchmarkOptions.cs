namespace Odyssey.Benchmarks;

internal sealed record BenchmarkOptions(
    int FileCount,
    int RichDocumentCount,
    int SearchIterations,
    int TransferMiB,
    string OutputPath,
    string? WorkspaceBase,
    bool KeepWorkspace)
{
    public static string Usage => """
        Odyssey reproducible benchmark

        Options:
          --files <count>          Plain-text file count (default: 5000, minimum: 100)
          --rich-documents <count> PDF/Office/ZIP file count (default: 25, minimum: 0)
          --iterations <count>     Search iterations per query (default: 5, minimum: 1)
          --transfer-mib <count>   Verified copy size in MiB (default: 64, minimum: 1)
          --output <path>          JSON result path (default: artifacts/benchmarks/latest.json)
          --workspace <directory>  Parent for the isolated run directory (default: OS temp)
          --keep-workspace         Preserve the generated run directory
          --help                   Show this help
        """;

    public static BenchmarkOptions Parse(IReadOnlyList<string> arguments)
    {
        var fileCount = 5_000;
        var richDocumentCount = 25;
        var iterations = 5;
        var transferMiB = 64;
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "benchmarks", "latest.json");
        string? workspace = null;
        var keepWorkspace = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--files":
                    fileCount = ParseInteger(NextValue(arguments, ref index, argument), argument, 100);
                    break;
                case "--rich-documents":
                    richDocumentCount = ParseInteger(NextValue(arguments, ref index, argument), argument, 0);
                    break;
                case "--iterations":
                    iterations = ParseInteger(NextValue(arguments, ref index, argument), argument, 1);
                    break;
                case "--transfer-mib":
                    transferMiB = ParseInteger(NextValue(arguments, ref index, argument), argument, 1);
                    break;
                case "--output":
                    output = Path.GetFullPath(NextValue(arguments, ref index, argument));
                    break;
                case "--workspace":
                    workspace = Path.GetFullPath(NextValue(arguments, ref index, argument));
                    break;
                case "--keep-workspace":
                    keepWorkspace = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown benchmark option: {argument}");
            }
        }

        return new BenchmarkOptions(fileCount, richDocumentCount, iterations, transferMiB,
            Path.GetFullPath(output), workspace, keepWorkspace);
    }

    private static string NextValue(IReadOnlyList<string> arguments, ref int index, string option)
    {
        if (++index >= arguments.Count) throw new ArgumentException($"Missing value for {option}.");
        return arguments[index];
    }

    private static int ParseInteger(string value, string option, int minimum)
    {
        if (!int.TryParse(value, out var result) || result < minimum)
            throw new ArgumentException($"{option} must be an integer greater than or equal to {minimum}.");
        return result;
    }
}
