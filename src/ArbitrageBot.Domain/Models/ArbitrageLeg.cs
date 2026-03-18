using ArbitrageBot.Domain.Exceptions;

namespace ArbitrageBot.Domain.Models;

public sealed record ArbitrageLeg
{
    public string DexName { get; init; }
    public TokenPair Pair { get; init; }
    public decimal AmountIn { get; init; }
    public decimal ExpectedAmountOut { get; init; }
    public decimal AmountOutMinimum { get; init; }
    public LegSide Side { get; init; }

    public ArbitrageLeg(
        string dexName,
        TokenPair pair,
        decimal amountIn,
        decimal expectedAmountOut,
        decimal slippageTolerance,
        // every leg is either buy or sell
        // this makes the code that consumes leg self documenting
        // when executor builds a transaction, it can check `leg.Side == LegSide.Buy` instead of inferring the side from the leg's position in a tuple or array.
        LegSide side)
    {
        if (string.IsNullOrWhiteSpace(dexName))
            throw new InvalidTokenPairException("DexName cannot be null or empty");

        if (pair is null)
            throw new InvalidTokenPairException("TokenPair cannot be null");

        if (amountIn <= 0)
            throw new InvalidTokenPairException($"AmountIn must be positive, got {amountIn}");

        if (expectedAmountOut <= 0)
            throw new InvalidTokenPairException($"ExpectedAmountOut must be positive, got {expectedAmountOut}");

        // validating between 0 and 1 means that i wont tolerate any for of slippage
        // if the prices moves, then the transaction reverts
        if (slippageTolerance < 0 || slippageTolerance > 1)
            throw new InvalidTokenPairException(
                $"SlippageTolerance must be between 0 and 1, got {slippageTolerance}");

        DexName = dexName;
        Pair = pair;
        AmountIn = amountIn;
        ExpectedAmountOut = expectedAmountOut;

        // this is calculated from `slippageTolerance`
        // I don't store `slippageTolerance` as a property. 
        // Instead i compute `AmountOutMinimum` from it immediately and store that.
        //  Here's why, `slippageTolerance` is an input parameter, a configuration concern. 
        // `AmountOutMinimum` is the actual value the smart contract needs. 
        // When the Executor builds the on-chain transaction it passes `AmountOutMinimum` directly to the DEX contract. 
        // Storing the derived value rather than the input keeps the model focused on what the system needs, not how it was configured.
        AmountOutMinimum = expectedAmountOut * (1 - slippageTolerance);
        Side = side;
    }

    // It tells me how much this leg moves the price of the pool.
    // A large trade on a shallow pool has high price impact
    // meaning I get less out than the spot price suggested. 
    // The Analyzer uses this to decide if a trade is still worth executing after accounting for impact.
    public decimal PriceImpactPercent
        => ((ExpectedAmountOut - AmountIn) / AmountIn) * 100m;

    // this is used at execution time, not detection time. 
    // After the flash loan executes the buy leg, we check the actual amount received against AmountOutMinimum. 
    // If it's below the minimum, we abort before attempting the sell leg rather than locking in a loss.
    public bool HasAcceptableSlippage(decimal actualAmountOut)
        => actualAmountOut >= AmountOutMinimum;
}

public enum LegSide
{
    Buy,
    Sell
}