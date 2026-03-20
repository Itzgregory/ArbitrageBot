using ArbitrageBot.Domain.Exceptions;
using ArbitrageBot.Domain.Models;
using FluentAssertions;

namespace ArbitrageBot.Domain.Tests.Models;

public sealed class PoolReservesTests
{
    private const string PoolAddress = "0xb4e16d0168e52d35cacd2c6185b44281ec28c9dc";

    [Fact]
    public void Constructor_WithValidInputs_CreatesPoolReserves()
    {
        var reserves = new PoolReserves(PoolAddress, 1000m, 2000m, 100);

        reserves.PoolAddress.Should().Be(PoolAddress);
        reserves.Reserve0.Should().Be(1000m);
        reserves.Reserve1.Should().Be(2000m);
        reserves.BlockNumber.Should().Be(100);
        reserves.FetchedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void SpotPrice_WithValidReserves_ReturnsCorrectPrice()
    {
        // 2000 USDC / 1 WETH pool — spot price should be 2000
        var reserves = new PoolReserves(PoolAddress, 1m, 2000m, 100);

        var price = reserves.SpotPrice(18, 6);

        price.Should().BeApproximately(2000m, 0.01m);
    }

    [Fact]
    public void SpotPrice_WithZeroReserve0_ThrowsInvalidTokenPairException()
    {
        var reserves = new PoolReserves(PoolAddress, 0m, 2000m, 100);

        var act = () => reserves.SpotPrice(18, 6);

        act.Should().Throw<InvalidTokenPairException>()
            .WithMessage("*Reserve0 is zero*");
    }

    [Fact]
    public void IsStale_WhenBlockDifferenceExceedsMaxAge_ReturnsTrue()
    {
        var reserves = new PoolReserves(PoolAddress, 1000m, 2000m, 100);

        reserves.IsStale(currentBlockNumber: 103, maxBlockAge: 2).Should().BeTrue();
    }

    [Fact]
    public void IsStale_WhenBlockDifferenceWithinMaxAge_ReturnsFalse()
    {
        var reserves = new PoolReserves(PoolAddress, 1000m, 2000m, 100);

        reserves.IsStale(currentBlockNumber: 101, maxBlockAge: 2).Should().BeFalse();
    }

    [Fact]
    public void GetAmountOut_WithValidInputs_ReturnsCorrectAmount()
    {
        // Pool: 1000 WETH, 2000000 USDC
        // Buying with 1 WETH — should receive slightly less than 2000 USDC due to fee
        var reserves = new PoolReserves(PoolAddress, 1000m, 2_000_000m, 100);

        var amountOut = reserves.GetAmountOut(1m, 18, 6);

        // Expected: (1 × 997 × 2000000) / (1000 × 1000 + 997) ≈ 1994
        amountOut.Should().BeGreaterThan(1990m).And.BeLessThan(2000m);
    }

    [Fact]
    public void GetAmountOut_WithZeroAmountIn_ThrowsInvalidTokenPairException()
    {
        var reserves = new PoolReserves(PoolAddress, 1000m, 2000m, 100);

        var act = () => reserves.GetAmountOut(0m, 18, 6);

        act.Should().Throw<InvalidTokenPairException>()
            .WithMessage("*AmountIn*");
    }

    [Fact]
    public void Constructor_WithNegativeReserve_ThrowsInvalidTokenPairException()
    {
        var act = () => new PoolReserves(PoolAddress, -1m, 2000m, 100);

        act.Should().Throw<InvalidTokenPairException>()
            .WithMessage("*Reserve0*");
    }
}