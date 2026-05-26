using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Munibot;

public sealed class BotConfig
{
    public BotLoginConfig Login { get; init; } = new();
    public BotRuntimeConfig Runtime { get; init; } = new();
    public BotApiConfig Api { get; init; } = new();
    public BotDiagnosticsConfig Diagnostics { get; init; } = new();
    public BotExperiencesConfig Experiences { get; init; } = new();
    public BotAccountHistoryConfig AccountHistory { get; init; } = new();
    public BotMunibaseConfig Munibase { get; init; } = new();
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

        if (Runtime.MovementKeepaliveSeconds < 0)
        {
            throw new InvalidOperationException("runtime.movement_keepalive_seconds cannot be negative.");
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

        if (Api.InventoryOperationTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("api.inventory_operation_timeout_seconds must be greater than zero.");
        }

        if (Api.TextureUploadTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("api.texture_upload_timeout_seconds must be greater than zero.");
        }

        if (Api.WalletOperationTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("api.wallet_operation_timeout_seconds must be greater than zero.");
        }

        if (Api.EstateOperationTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("api.estate_operation_timeout_seconds must be greater than zero.");
        }

        if (AccountHistory.TimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("account_history.timeout_seconds must be greater than zero.");
        }

        if (Munibase.WalletEvents.TimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("munibase.wallet_events.timeout_seconds must be greater than zero.");
        }

        if (Munibase.WalletEvents.MaxDeliveryAttempts <= 0)
        {
            throw new InvalidOperationException("munibase.wallet_events.max_delivery_attempts must be greater than zero.");
        }

        if (Munibase.WalletEvents.RetryDelaySeconds < 0)
        {
            throw new InvalidOperationException("munibase.wallet_events.retry_delay_seconds cannot be negative.");
        }

        if (Munibase.WalletEvents.HistoryLookbackMinutes <= 0)
        {
            throw new InvalidOperationException("munibase.wallet_events.history_lookback_minutes must be greater than zero.");
        }

        if (Munibase.WalletEvents.HistoryReconcileAttempts <= 0)
        {
            throw new InvalidOperationException("munibase.wallet_events.history_reconcile_attempts must be greater than zero.");
        }

        if (Munibase.WalletEvents.HistoryReconcileDelaySeconds < 0)
        {
            throw new InvalidOperationException("munibase.wallet_events.history_reconcile_delay_seconds cannot be negative.");
        }

        if (!string.IsNullOrWhiteSpace(Munibase.WalletEvents.EndpointUrl) &&
            string.IsNullOrWhiteSpace(Munibase.WalletEvents.SharedSecret))
        {
            throw new InvalidOperationException(
                "munibase.wallet_events.shared_secret is required when munibase.wallet_events.endpoint_url is configured.");
        }

        if (Diagnostics.MaxLoggedBodyBytes < 0)
        {
            throw new InvalidOperationException("diagnostics.max_logged_body_bytes cannot be negative.");
        }

        foreach (var experience in Experiences.AutoAllow)
        {
            if (string.IsNullOrWhiteSpace(experience.Id))
            {
                throw new InvalidOperationException("experiences.auto_allow[].id is required.");
            }

            if (!OpenMetaverse.UUID.TryParse(experience.Id, out var experienceId) ||
                experienceId == OpenMetaverse.UUID.Zero)
            {
                throw new InvalidOperationException(
                    $"experiences.auto_allow[{experience.Id}].id must be a valid non-zero Second Life experience UUID.");
            }
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
    public int MovementKeepaliveSeconds { get; init; } = 20;
}

public sealed class BotApiConfig
{
    public int GroupRosterTimeoutSeconds { get; init; } = 30;
    public int AvatarLookupTimeoutSeconds { get; init; } = 30;
    public int GroupOperationTimeoutSeconds { get; init; } = 30;
    public int InventoryOperationTimeoutSeconds { get; init; } = 30;
    public int TextureUploadTimeoutSeconds { get; init; } = 60;
    public int WalletOperationTimeoutSeconds { get; init; } = 30;
    public int EstateOperationTimeoutSeconds { get; init; } = 30;
}

public sealed class BotDiagnosticsConfig
{
    public bool LogApiCalls { get; init; } = true;
    public bool LogApiBodies { get; init; }
    public bool LogSecondLifeEvents { get; init; } = true;
    public int MaxLoggedBodyBytes { get; init; } = 4096;
}

public sealed class BotExperiencesConfig
{
    public List<BotExperienceAllowConfig> AutoAllow { get; init; } = [];
}

public sealed class BotAccountHistoryConfig
{
    public string? Username { get; init; }
    public int TimeoutSeconds { get; init; } = 45;
}

public sealed class BotMunibaseConfig
{
    public BotMunibaseWalletEventsConfig WalletEvents { get; init; } = new();
}

public sealed class BotMunibaseWalletEventsConfig
{
    public string? EndpointUrl { get; init; }
    public string? SharedSecret { get; init; }
    public int TimeoutSeconds { get; init; } = 10;
    public int MaxDeliveryAttempts { get; init; } = 3;
    public int RetryDelaySeconds { get; init; } = 2;
    public int HistoryLookbackMinutes { get; init; } = 10;
    public int HistoryReconcileAttempts { get; init; } = 3;
    public int HistoryReconcileDelaySeconds { get; init; } = 5;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(EndpointUrl) &&
        !string.IsNullOrWhiteSpace(SharedSecret);
}

public sealed class BotExperienceAllowConfig
{
    public string Id { get; init; } = string.Empty;
    public string? Name { get; init; }
}

public sealed class BotApiTokenConfig
{
    public string Id { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public List<string> Scopes { get; init; } = [];
}
