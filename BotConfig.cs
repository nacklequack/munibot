using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Munibot;

public sealed class BotConfig
{
    public BotLoginConfig Login { get; init; } = new();
    public BotRuntimeConfig Runtime { get; init; } = new();
    public BotApiConfig Api { get; init; } = new();

    public static BotConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Config file not found. Create one from config.example.yaml or pass --config <path>.",
                path);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        using var reader = File.OpenText(path);
        var config = deserializer.Deserialize<BotConfig>(reader)
            ?? throw new InvalidOperationException("Config file was empty.");

        config.Validate(path);
        return config;
    }

    private void Validate(string path)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(Login.FirstName))
        {
            missing.Add("login.first_name");
        }

        if (string.IsNullOrWhiteSpace(Login.LastName))
        {
            missing.Add("login.last_name");
        }

        if (string.IsNullOrWhiteSpace(Login.Password))
        {
            missing.Add("login.password");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Config file {path} is missing required setting(s): {string.Join(", ", missing)}.");
        }

        if (Login.TimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("login.timeout_seconds must be greater than zero.");
        }

        if (Runtime.ExitAfterLoginSeconds < 0)
        {
            throw new InvalidOperationException("runtime.exit_after_login_seconds cannot be negative.");
        }
    }
}

public sealed class BotLoginConfig
{
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = "Resident";
    public string Password { get; init; } = string.Empty;
    public string? MfaToken { get; init; }
    public string? MfaHash { get; init; }
    public string Channel { get; init; } = "Munibot";
    public string Version { get; init; } = "0.1.0";
    public string Start { get; init; } = "last";
    public string? LoginUri { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
}

public sealed class BotRuntimeConfig
{
    public int ExitAfterLoginSeconds { get; init; }
}

public sealed class BotApiConfig
{
    public int GroupRosterTimeoutSeconds { get; init; } = 30;
}
