using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Munibot;

public sealed class BotConfig
{
    public BotLoginConfig Login { get; init; } = new();
    public BotRuntimeConfig Runtime { get; init; } = new();
    public BotApiConfig Api { get; init; } = new();
    public BotDiagnosticsConfig Diagnostics { get; init; } = new();
    public List<BotApiTokenConfig> Tokens { get; init; } = [];

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

        if (Runtime.ReconnectDelaySeconds <= 0)
        {
            throw new InvalidOperationException("runtime.reconnect_delay_seconds must be greater than zero.");
        }

        if (Runtime.MaxReconnectAttempts < 0)
        {
            throw new InvalidOperationException("runtime.max_reconnect_attempts cannot be negative.");
        }

        if (Api.GroupRosterTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("api.group_roster_timeout_seconds must be greater than zero.");
        }

        if (Api.AvatarLookupTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("api.avatar_lookup_timeout_seconds must be greater than zero.");
        }

        if (Api.GroupOperationTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("api.group_operation_timeout_seconds must be greater than zero.");
        }

        if (Diagnostics.MaxLoggedBodyBytes < 0)
        {
            throw new InvalidOperationException("diagnostics.max_logged_body_bytes cannot be negative.");
        }

        foreach (var token in Tokens)
        {
            if (string.IsNullOrWhiteSpace(token.Id))
            {
                throw new InvalidOperationException("tokens[].id is required when API tokens are configured.");
            }

            if (string.IsNullOrWhiteSpace(token.Value))
            {
                throw new InvalidOperationException($"tokens[{token.Id}].value is required.");
            }
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
    public bool Reconnect { get; init; } = true;
    public int ReconnectDelaySeconds { get; init; } = 15;
    public int MaxReconnectAttempts { get; init; }
}

public sealed class BotApiConfig
{
    public int GroupRosterTimeoutSeconds { get; init; } = 30;
    public int AvatarLookupTimeoutSeconds { get; init; } = 30;
    public int GroupOperationTimeoutSeconds { get; init; } = 30;
}

public sealed class BotDiagnosticsConfig
{
    public bool LogApiCalls { get; init; } = true;
    public bool LogApiBodies { get; init; }
    public bool LogSecondLifeEvents { get; init; } = true;
    public int MaxLoggedBodyBytes { get; init; } = 4096;
}

public sealed class BotApiTokenConfig
{
    public string Id { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public List<string> Scopes { get; init; } = [];
}
