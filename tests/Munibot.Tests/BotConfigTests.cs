using Munibot;

namespace Munibot.Tests;

public sealed class BotConfigTests
{
    [Fact]
    public void Load_ReadsDiagnosticsAndTokens()
    {
        var path = WriteConfig("""
            login:
              first_name: Test
              last_name: Resident
              password: secret
            runtime:
              exit_after_login_seconds: 0
              reconnect: true
              reconnect_delay_seconds: 7
              max_reconnect_attempts: 3
            api:
              group_roster_timeout_seconds: 11
            diagnostics:
              log_api_calls: true
              log_api_bodies: true
              log_second_life_events: false
              max_logged_body_bytes: 128
            tokens:
              - id: munibase
                value: token-value
                scopes:
                  - sl.roster.read
            """);

        var config = BotConfig.Load(path);

        Assert.Equal("Test", config.Login.FirstName);
        Assert.Equal(7, config.Runtime.ReconnectDelaySeconds);
        Assert.Equal(3, config.Runtime.MaxReconnectAttempts);
        Assert.Equal(11, config.Api.GroupRosterTimeoutSeconds);
        Assert.True(config.Diagnostics.LogApiBodies);
        Assert.False(config.Diagnostics.LogSecondLifeEvents);
        var token = Assert.Single(config.Tokens);
        Assert.Equal("munibase", token.Id);
        Assert.Equal("token-value", token.Value);
        Assert.Contains(AuthScopes.RosterRead, token.Scopes);
    }

    [Fact]
    public void Load_RejectsMissingRequiredLoginFields()
    {
        var path = WriteConfig("""
            login:
              first_name: Test
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => BotConfig.Load(path));

        Assert.Contains("login.password", ex.Message);
    }

    [Theory]
    [InlineData("runtime:\n  reconnect_delay_seconds: 0", "runtime.reconnect_delay_seconds")]
    [InlineData("runtime:\n  max_reconnect_attempts: -1", "runtime.max_reconnect_attempts")]
    [InlineData("api:\n  group_roster_timeout_seconds: 0", "api.group_roster_timeout_seconds")]
    [InlineData("diagnostics:\n  max_logged_body_bytes: -1", "diagnostics.max_logged_body_bytes")]
    public void Load_RejectsInvalidPhaseOneSettings(string yamlFragment, string expectedMessage)
    {
        var path = WriteConfig($"""
            login:
              first_name: Test
              last_name: Resident
              password: secret
            {yamlFragment}
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => BotConfig.Load(path));

        Assert.Contains(expectedMessage, ex.Message);
    }

    private static string WriteConfig(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }
}
