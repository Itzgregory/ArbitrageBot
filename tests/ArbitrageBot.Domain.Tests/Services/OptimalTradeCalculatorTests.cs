using ArbitrageBot.Domain.Services;
using FluentAssertions;

namespace ArbitrageBot.Domain.Tests.Services;

public sealed class OptimalTradeCalculatorTests
{
    [Fact]
    public void CalculateOptimalTradeSize_WithPriceDifference_ReturnsPositiveSize()
    {
        var optimal = OptimalTradeCalculator.CalculateOptimalTradeSize(
            reserve0Buy: 1000m,
            reserve1Buy: 2_000_000m,
            reserve0Sell: 1000m,
            reserve1Sell: 2_100_000m,
            maxTradeSize: 100m);

        optimal.Should().BeGreaterThan(0m);
        optimal.Should().BeLessThanOrEqualTo(100m);
    }

    [Fact]
    public void CalculateOptimalTradeSize_WithNoPriceDifference_ReturnsZero()
    {
        var optimal = OptimalTradeCalculator.CalculateOptimalTradeSize(
            reserve0Buy: 1000m,
            reserve1Buy: 2_000_000m,
            reserve0Sell: 1000m,
            reserve1Sell: 2_000_000m,
            maxTradeSize: 100m);

        optimal.Should().Be(0m);
    }

    [Fact]
    public void CalculateOptimalTradeSize_NeverExceedsMaxTradeSize()
    {
        var optimal = OptimalTradeCalculator.CalculateOptimalTradeSize(
            reserve0Buy: 1000m,
            reserve1Buy: 1_000_000m,
            reserve0Sell: 1000m,
            reserve1Sell: 10_000_000m,
            maxTradeSize: 10m);

        optimal.Should().BeLessThanOrEqualTo(10m);
    }

    [Fact]
    public void CalculateGrossProfit_WithPriceDifference_ReturnsPositiveProfit()
    {
        var profit = OptimalTradeCalculator.CalculateGrossProfit(
            amountIn: 1m,
            reserve0Buy: 1000m,
            reserve1Buy: 2_000_000m,
            reserve0Sell: 1000m,
            reserve1Sell: 2_100_000m);

        profit.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void CalculateGrossProfit_WithReversedPools_ReturnsNegativeProfit()
    {
        var profit = OptimalTradeCalculator.CalculateGrossProfit(
            amountIn: 1m,
            reserve0Buy: 1000m,
            reserve1Buy: 2_100_000m,
            reserve0Sell: 1000m,
            reserve1Sell: 2_000_000m);

        profit.Should().BeLessThan(0m);
    }

    [Fact]
    public void CalculateGrossProfit_WithZeroAmountIn_ReturnsZero()
    {
        var profit = OptimalTradeCalculator.CalculateGrossProfit(
            amountIn: 0m,
            reserve0Buy: 1000m,
            reserve1Buy: 2_000_000m,
            reserve0Sell: 1000m,
            reserve1Sell: 2_100_000m);

        profit.Should().Be(0m);
    }

    [Fact]
    public void CalculateOptimalTradeSize_LargerPriceDiff_ReturnsLargerTradeSize()
    {
        var smallDiff = OptimalTradeCalculator.CalculateOptimalTradeSize(
            1000m, 2_000_000m, 1000m, 2_010_000m, 100m);

        var largeDiff = OptimalTradeCalculator.CalculateOptimalTradeSize(
            1000m, 2_000_000m, 1000m, 2_100_000m, 100m);

        largeDiff.Should().BeGreaterThan(smallDiff);
    }
}