namespace Munibot;

public static class CliOptions
{
    public static bool IsHelpRequested(string[] args)
        => args.Any(arg =>
            string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase));

    public static string GetConfigPath(string[] args)
    {
        const string configFlag = "--config";

        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], configFlag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                throw new ArgumentException("--config requires a file path.");
            }

            return Path.GetFullPath(args[i + 1]);
        }

        return Path.GetFullPath("config.yaml");
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Munibot");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- --config <path-to-config.yaml>");
        Console.WriteLine();
        Console.WriteLine("If --config is omitted, Munibot reads config.yaml in the current directory.");
    }
}
