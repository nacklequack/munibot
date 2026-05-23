using Microsoft.AspNetCore.Http;
using Munibot;

namespace Munibot.Tests;

public sealed class MunibotAuthenticationTests
{
    [Fact]
    public void TryAuthenticate_AllowsAnonymousDevWhenNoTokensConfigured()
    {
        var context = new DefaultHttpContext();
        var config = new BotConfig();

        var authenticated = MunibotAuthentication.TryAuthenticate(context, config, out var principal);

        Assert.True(authenticated);
        Assert.NotNull(principal);
        Assert.Equal("anonymous-dev", principal.Id);
        Assert.True(MunibotAuthentication.HasScope(principal, AuthScopes.RosterRead));
        Assert.True(MunibotAuthentication.HasAnyScope(principal, [AuthScopes.LocalChatSend, AuthScopes.BotOwner]));
    }

    [Fact]
    public void TryAuthenticate_ReadsTokenFromCustomHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Munibot-Token"] = "secret-token";
        var config = ConfigWithToken("secret-token", AuthScopes.RosterRead);

        var authenticated = MunibotAuthentication.TryAuthenticate(context, config, out var principal);

        Assert.True(authenticated);
        Assert.NotNull(principal);
        Assert.Equal("munibase", principal.Id);
        Assert.True(MunibotAuthentication.HasScope(principal, AuthScopes.RosterRead));
        Assert.False(MunibotAuthentication.HasScope(principal, AuthScopes.WalletPay));
    }

    [Fact]
    public void TryAuthenticate_ReadsBearerToken()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer secret-token";
        var config = ConfigWithToken("secret-token", AuthScopes.BotOwner);

        var authenticated = MunibotAuthentication.TryAuthenticate(context, config, out var principal);

        Assert.True(authenticated);
        Assert.NotNull(principal);
        Assert.True(MunibotAuthentication.HasScope(principal, AuthScopes.BotOwner));
    }

    [Fact]
    public void HasAnyScope_AcceptsAnyMatchingConfiguredScope()
    {
        var principal = new MunibotTokenPrincipal(
            "test",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AuthScopes.BotOwner });

        Assert.True(MunibotAuthentication.HasAnyScope(principal, [AuthScopes.LocalChatSend, AuthScopes.BotOwner]));
        Assert.False(MunibotAuthentication.HasAnyScope(principal, [AuthScopes.WalletPay, AuthScopes.EstateWrite]));
    }

    [Fact]
    public void TryAuthenticate_RejectsMissingConfiguredToken()
    {
        var context = new DefaultHttpContext();
        var config = ConfigWithToken("secret-token", AuthScopes.RosterRead);

        var authenticated = MunibotAuthentication.TryAuthenticate(context, config, out var principal);

        Assert.False(authenticated);
        Assert.Null(principal);
    }

    [Fact]
    public void TryAuthenticate_RejectsWrongConfiguredToken()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Munibot-Token"] = "wrong";
        var config = ConfigWithToken("secret-token", AuthScopes.RosterRead);

        var authenticated = MunibotAuthentication.TryAuthenticate(context, config, out var principal);

        Assert.False(authenticated);
        Assert.Null(principal);
    }

    private static BotConfig ConfigWithToken(string value, params string[] scopes)
        => new()
        {
            Tokens =
            [
                new BotApiTokenConfig
                {
                    Id = "munibase",
                    Value = value,
                    Scopes = scopes.ToList()
                }
            ]
        };
}
