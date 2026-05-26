using Munibot;

namespace Munibot.Tests;

public sealed class InventoryRezRequestValidatorTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(50, 50)]
    public void NormalizeRezCount_AcceptsValidCounts(int? count, int expected)
    {
        var normalized = InventoryRequestValidator.NormalizeRezCount(count);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    [InlineData(-1)]
    public void NormalizeRezCount_RejectsOutOfRangeCounts(int count)
    {
        Assert.Throws<ArgumentException>(() => InventoryRequestValidator.NormalizeRezCount(count));
    }

    [Fact]
    public void RequireRezConfirmation_RequiresExplicitConfirmation()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            InventoryRequestValidator.RequireRezConfirmation(false));

        Assert.Contains("confirmRez", ex.Message);
    }
}
