using System;
using ArbitrageBot.Domain.Exceptions;
using ArbitrageBot.Domain.Interfaces;

namespace ArbitrageBot.Domain.Models;

public sealed record PoolReserves : IStale, ITimestamped
{
    public string PoolAddress { get; init; }
    public decimal Reserve0 { get; init; }
    public decimal Reserve1 { get; init; }
    public DateTime FetchedAt { get; init; }
    public int BlockNumber { get; init; }

    public PoolReserves(
        string poolAddress,
        decimal reserve0,
        decimal reserve1,
        int blockNumber)
    {
        if (string.IsNullOrWhiteSpace(poolAddress))
            throw new InvalidTokenPairException("Pool address cannot be null or empty");

        if (reserve0 < 0)
            throw new InvalidTokenPairException($"Reserve0 cannot be negative, got {reserve0}");

        if (reserve1 < 0)
            throw new InvalidTokenPairException($"Reserve1 cannot be negative, got {reserve1}");

        if (blockNumber < 0)
            throw new InvalidTokenPairException($"Block number cannot be negative, got {blockNumber}");

        PoolAddress = poolAddress.ToLowerInvariant();
        Reserve0 = reserve0;
        Reserve1 = reserve1;
        // on ethereum time is measured in blocks, not wall clocks
        // so when i fetch two reserves at the same time but from different block, are stale to each other, so i store the block number so i can reason about the staleness correctly
        BlockNumber = blockNumber;
        FetchedAt = DateTime.UtcNow;
    }

    // on Ethereum mainnet a new block arrives roughly every 12 seconds
    // If reserves are 2 blocks old, the price could have moved. 
    // This method lets the Scanner decide whether to re-fetch before acting on stale data. 
    // The default of 2 blocks is conservative, i can tune it. 
    // The `= 2` is a default parameter, meaning callers can call `reserves.IsStale(currentBlock)` without specifying the age.
    public bool IsStale(int currentBlockNumber, int maxBlockAge = 2)
        => currentBlockNumber - BlockNumber > maxBlockAge;

    // this is the raw price from the constant product formula. 
    // `Reserve1 / Reserve0` adjusted for decimals. 
    // This is the *spot price*, the price if I traded an infinitely small amount. 
    // Real trades move the price, which is what `GetAmountOut` calculates.
    public decimal SpotPrice(int decimalsToken0, int decimalsToken1)
    {
        if (Reserve0 == 0)
            throw new InvalidTokenPairException("Reserve0 is zero — cannot calculate spot price");
       
       
        // Reserves are already adjusted for decimals by ConvertFromWei in the DEX layer.
        // We divide directly — no further decimal adjustment needed here.
        return Reserve1 / Reserve0;
    }

    // this is the Uniswap V2 constant product formula with the 0.3% fee applied. 
    // Breaking it down:
    // amountInWithFee = amountIn × 997        ← 997/1000 = 99.7% (0.3% fee removed)
    // numerator       = amountInWithFee × Reserve1
    // denominator     = Reserve0 × 1000 + amountInWithFee
    // amountOut       = numerator / denominator
    public decimal GetAmountOut(decimal amountIn, int decimalsIn, int decimalsOut)
    {
        if (amountIn <= 0)
            throw new InvalidTokenPairException($"AmountIn must be positive, got {amountIn}");

        // Reserves are already in human-readable form — apply Uniswap V2 formula directly.
        // The 997/1000 factor represents the 0.3% swap fee.
        var amountInWithFee = amountIn * 997m;
        var numerator = amountInWithFee * Reserve1;
        var denominator = Reserve0 * 1000m + amountInWithFee;

        return numerator / denominator;
    }
}