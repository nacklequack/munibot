using Munibot;

namespace Munibot.Tests;

public sealed class WalletRequestValidatorTests
{
    [Fact]
    public void NormalizeAvatarId_RequiresNonZeroUuid()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            WalletRequestValidator.NormalizeAvatarId("00000000-0000-0000-0000-000000000000"));

        Assert.Contains("avatar UUID", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void NormalizeAmount_RequiresPositiveAmount(int? amount)
    {
        var ex = Assert.Throws<ArgumentException>(() => WalletRequestValidator.NormalizeAmount(amount));

        Assert.Contains("greater than zero", ex.Message);
    }

    [Fact]
    public void NormalizeAmount_RejectsLargePayment()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            WalletRequestValidator.NormalizeAmount(WalletRequestValidator.MaxPaymentAmountLinden + 1));

        Assert.Contains("or less", ex.Message);
    }

    [Fact]
    public void NormalizeDescription_TrimsAndAllowsEmpty()
    {
        Assert.Equal("hello", WalletRequestValidator.NormalizeDescription(" hello "));
        Assert.Equal(string.Empty, WalletRequestValidator.NormalizeDescription("   "));
    }

    [Fact]
    public void NormalizeDescription_RejectsLongDescription()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            WalletRequestValidator.NormalizeDescription(new string('x', WalletRequestValidator.MaxPaymentDescriptionLength + 1)));

        Assert.Contains("description", ex.Message);
    }

    [Fact]
    public void RequirePaymentConfirmation_RequiresExplicitTrue()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            WalletRequestValidator.RequirePaymentConfirmation(false));

        Assert.Contains("confirmPayment", ex.Message);
    }
}
