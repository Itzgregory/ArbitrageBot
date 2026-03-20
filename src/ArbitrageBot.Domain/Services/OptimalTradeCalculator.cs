namespace ArbitrageBot.Domain.Services;

public static class OptimalTradeCalculator
{
    // Uniswap V2 charges 0.3% on each swap.
    // Both legs incur this fee so we account for it on both sides.
    private const double UniswapFee = 0.003;
    private const double FeeMultiplier = 1.0 - UniswapFee;

    /// <summary>
    /// Calculates the optimal trade size that maximises profit for a two-pool
    /// Uniswap V2 arbitrage, accounting for price impact on both legs.
    ///
    /// Derived by differentiating the profit function with respect to trade size
    /// and solving for the zero — the point where marginal profit equals zero.
    ///
    /// Returns 0 if no profitable trade size exists.
    /// </summary>
    public static decimal CalculateOptimalTradeSize(
        decimal reserve0Buy,
        decimal reserve1Buy,
        decimal reserve0Sell,
        decimal reserve1Sell,
        decimal maxTradeSize)
    {
        // Use double for intermediate math — the square root and
        // large reserve multiplications overflow decimal
        var r0b = (double)reserve0Buy;
        var r1b = (double)reserve1Buy;
        var r0s = (double)reserve0Sell;
        var r1s = (double)reserve1Sell;

        // Numerator: sqrt(r0_buy × r0_sell × r1_buy × r1_sell × fee²) - r0_buy × r0_sell
        // This is the point where buying on the cheap pool and selling on the
        // expensive pool yields exactly zero marginal profit
        var product = r0b * r0s * r1b * r1s * FeeMultiplier * FeeMultiplier;

        if (product <= 0)
            return 0m;

        var numerator = Math.Sqrt(product) - r0b * r0s;

        if (numerator <= 0)
            return 0m;

        // Denominator: r0_buy + r0_sell × fee
        // Represents the combined pool depth weighted by the fee structure
        var denominator = r0b + r0s * FeeMultiplier;

        if (denominator <= 0)
            return 0m;

        var optimal = numerator / denominator;

        if (optimal <= 0)
            return 0m;

        // Cap at maxTradeSize — even if the math says a larger trade is optimal,
        // we never exceed the configured maximum to control execution risk
        var result = Math.Min(optimal, (double)maxTradeSize);

        return (decimal)result;
    }

    /// <summary>
    /// Calculates the expected gross profit for a given trade size across two pools,
    /// accounting for price impact on both legs using the constant product formula.
    /// Does not include flash loan fees or gas costs — those are subtracted by the caller.
    /// </summary>
    public static decimal CalculateGrossProfit(
        decimal amountIn,
        decimal reserve0Buy,
        decimal reserve1Buy,
        decimal reserve0Sell,
        decimal reserve1Sell)
    {
        var r0b = (double)reserve0Buy;
        var r1b = (double)reserve1Buy;
        var r0s = (double)reserve0Sell;
        var r1s = (double)reserve1Sell;
        var amount = (double)amountIn;

        // Step 1 — how many tokens do we receive from the buy leg?
        var buyAmountOut = CalculateAmountOutInternal(amount, r0b, r1b);

        // Step 2 — how many tokens do we receive selling those on the sell leg?
        var sellAmountOut = CalculateAmountOutInternal(buyAmountOut, r0s, r1s);

        // Gross profit = what we got back minus what we put in
        // If negative, the trade loses money before fees
        return (decimal)(sellAmountOut - amount);
    }

    // Uniswap V2 constant product formula with 0.3% fee
    // amountOut = (amountIn × 997 × reserveOut) / (reserveIn × 1000 + amountIn × 997)
    private static double CalculateAmountOutInternal(
        double amountIn,
        double reserveIn,
        double reserveOut)
    {
        if (reserveIn <= 0 || reserveOut <= 0 || amountIn <= 0)
            return 0;

        var amountInWithFee = amountIn * 997.0;
        var numerator = amountInWithFee * reserveOut;
        var denominator = reserveIn * 1000.0 + amountInWithFee;

        return numerator / denominator;
    }
}