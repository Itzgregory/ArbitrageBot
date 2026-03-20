using ArbitrageBot.Domain.Exceptions;
using ArbitrageBot.Domain.Models;
using FluentAssertions;

namespace ArbitrageBot.Domain.Tests.Models;

public sealed class ArbitrageOpportunityTests
{
    private static readonly TokenPair TestPair = new(
        "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2",
        "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48",
        18, 6,
        "0xb4e16d0168e52d35cacd2c6185b44281ec28c9dc");

    private static ArbitrageLeg BuildBuyLeg(decimal amountIn = 1m, decimal amountOut = 1.05m)
        => new("UniswapV2", TestPair, amountIn, amountOut, 0.005m, LegSide.Buy);

    private static ArbitrageLeg BuildSellLeg(decimal amountIn = 1.05m, decimal amountOut = 1.08m)
        => new("SushiSwap", TestPair, amountIn, amountOut, 0.005m, LegSide.Sell);

    [Fact]
    public void Constructor_WithValidLegs_CreatesOpportunity()
    {
        var opportunity = new ArbitrageOpportunity(
            BuildBuyLeg(), BuildSellLeg(),
            flashLoanAmount: 1m,
            flashLoanFee: 0.0009m,
            estimatedGasCost: 0.006m);

        opportunity.Status.Should().Be(OpportunityStatus.Detected);
        opportunity.Version.Should().Be(1);
        opportunity.RetryCount.Should().Be(0);
        opportunity.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Constructor_CalculatesNetProfitCorrectly()
    {
        // Gross profit = sellAmountOut - buyAmountIn = 1.08 - 1.0 = 0.08
        // Net profit = 0.08 - 0.0009 (fee) - 0.006 (gas) = 0.0731
        var opportunity = new ArbitrageOpportunity(
            BuildBuyLeg(amountIn: 1m, amountOut: 1.05m),
            BuildSellLeg(amountIn: 1.05m, amountOut: 1.08m),
            flashLoanAmount: 1m,
            flashLoanFee: 0.0009m,
            estimatedGasCost: 0.006m);

        opportunity.ExpectedGrossProfit.Should().BeApproximately(0.08m, 0.0001m);
        opportunity.ExpectedNetProfit.Should().BeApproximately(0.0731m, 0.0001m);
    }

    [Fact]
    public void IsProfitable_WhenNetProfitAboveThreshold_ReturnsTrue()
    {
        var opportunity = new ArbitrageOpportunity(
            BuildBuyLeg(), BuildSellLeg(),
            1m, 0.0009m, 0.006m);

        opportunity.IsProfitable(minimumProfitThreshold: 0.01m).Should().BeTrue();
    }

    [Fact]
    public void IsProfitable_WhenNetProfitBelowThreshold_ReturnsFalse()
    {
        var opportunity = new ArbitrageOpportunity(
            BuildBuyLeg(), BuildSellLeg(),
            1m, 0.0009m, 0.006m);

        opportunity.IsProfitable(minimumProfitThreshold: 1m).Should().BeFalse();
    }

    [Fact]
    public void MarkExecuting_TransitionsFromDetectedToExecuting()
    {
        var opportunity = new ArbitrageOpportunity(
            BuildBuyLeg(), BuildSellLeg(), 1m, 0.0009m, 0.006m);

        opportunity.MarkExecuting();

        opportunity.Status.Should().Be(OpportunityStatus.Executing);
        opportunity.Version.Should().Be(2);
    }

    [Fact]
    public void MarkExecuted_TransitionsFromExecutingToExecuted()
    {
        var opportunity = new ArbitrageOpportunity(
            BuildBuyLeg(), BuildSellLeg(), 1m, 0.0009m, 0.006m);

        opportunity.MarkExecuting();
        opportunity.MarkExecuted();

        opportunity.Status.Should().Be(OpportunityStatus.Executed);
        opportunity.ExecutedAt.Should().NotBeNull();
        opportunity.Version.Should().Be(3);
    }

    [Fact]
    public void MarkFailed_TransitionsFromExecutingToFailed()
    {
        var opportunity = new ArbitrageOpportunity(
            BuildBuyLeg(), BuildSellLeg(), 1m, 0.0009m, 0.006m);

        opportunity.MarkExecuting();
        opportunity.MarkFailed("Transaction reverted");

        opportunity.Status.Should().Be(OpportunityStatus.Failed);
        opportunity.FailureReason.Should().Be("Transaction reverted");
        opportunity.Version.Should().Be(3);
    }

    [Fact]
    public void MarkFailed_DirectlyFromDetected_ThrowsExecutionFailedException()
    {
        var opportunity = new ArbitrageOpportunity(
            BuildBuyLeg(), BuildSellLeg(), 1m, 0.0009m, 0.006m);

        // Cannot go Detected -> Failed directly — must go through Executing
        var act = () => opportunity.MarkFailed("reason");

        act.Should().Throw<ExecutionFailedException>();
    }

    [Fact]
    public void IncrementRetry_AfterFailure_ResetsToDetected()
    {
        var opportunity = new ArbitrageOpportunity(
            BuildBuyLeg(), BuildSellLeg(), 1m, 0.0009m, 0.006m);

        opportunity.MarkExecuting();
        opportunity.MarkFailed("front-run");
        opportunity.IncrementRetry();

        opportunity.Status.Should().Be(OpportunityStatus.Detected);
        opportunity.RetryCount.Should().Be(1);
        opportunity.FailureReason.Should().BeNull();
    }

    [Fact]
    public void GenerateETag_ReturnsDifferentValueAfterStateTransition()
    {
        var opportunity = new ArbitrageOpportunity(
            BuildBuyLeg(), BuildSellLeg(), 1m, 0.0009m, 0.006m);

        var eTagBefore = opportunity.GenerateETag();
        opportunity.MarkExecuting();
        var eTagAfter = opportunity.GenerateETag();

        eTagBefore.Should().NotBe(eTagAfter);
    }

    [Fact]
    public void ValidateETag_WithCorrectETag_ReturnsTrue()
    {
        var opportunity = new ArbitrageOpportunity(
            BuildBuyLeg(), BuildSellLeg(), 1m, 0.0009m, 0.006m);

        var eTag = opportunity.GenerateETag();

        opportunity.ValidateETag(eTag).Should().BeTrue();
    }

    [Fact]
    public void ValidateETag_WithStaleETag_ReturnsFalse()
    {
        var opportunity = new ArbitrageOpportunity(
            BuildBuyLeg(), BuildSellLeg(), 1m, 0.0009m, 0.006m);

        var staleETag = opportunity.GenerateETag();
        opportunity.MarkExecuting();

        opportunity.ValidateETag(staleETag).Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithSameDexOnBothLegs_ThrowsInvalidTokenPairException()
    {
        var buyLeg = new ArbitrageLeg("UniswapV2", TestPair, 1m, 1.05m, 0.005m, LegSide.Buy);
        var sellLeg = new ArbitrageLeg("UniswapV2", TestPair, 1.05m, 1.08m, 0.005m, LegSide.Sell);

        var act = () => new ArbitrageOpportunity(buyLeg, sellLeg, 1m, 0.0009m, 0.006m);

        act.Should().Throw<InvalidTokenPairException>()
            .WithMessage("*same DEX*");
    }
}