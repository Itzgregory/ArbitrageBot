using System;
using ArbitrageBot.Domain.Exceptions;
using ArbitrageBot.Domain.Interfaces;

namespace ArbitrageBot.Domain.Models;

public sealed record PriceUpdate : IStale, ITimestamped
{
    public string DexName { get; init; }
    public TokenPair Pair { get; init; }
    public decimal SpotPrice { get; init; }
    public decimal Reserve0 { get; init; }
    public decimal Reserve1 { get; init; }
    public int BlockNumber { get; init; }
    public DateTime FetchedAt { get; init; }

    public PriceUpdate(
        string dexName,
        TokenPair pair,
        decimal spotPrice,
        decimal reserve0,
        decimal reserve1,
        int blockNumber)
    {
        if (string.IsNullOrWhiteSpace(dexName))
            throw new InvalidTokenPairException("DexName cannot be null or empty");

        if (pair is null)
            throw new InvalidTokenPairException("TokenPair cannot be null");

        if (spotPrice <= 0)
            throw new InvalidTokenPairException($"SpotPrice must be positive, got {spotPrice}");

        if (reserve0 <= 0)
            throw new InvalidTokenPairException($"Reserve0 must be positive, got {reserve0}");

        if (reserve1 <= 0)
            throw new InvalidTokenPairException($"Reserve1 must be positive, got {reserve1}");

        if (blockNumber < 0)
            throw new InvalidTokenPairException($"BlockNumber cannot be negative, got {blockNumber}");

        DexName = dexName;
        Pair = pair;
        SpotPrice = spotPrice;
        Reserve0 = reserve0;
        Reserve1 = reserve1;
        BlockNumber = blockNumber;
        FetchedAt = DateTime.UtcNow;
    }

    public bool IsStale(int currentBlockNumber, int maxBlockAge = 2)
        => currentBlockNumber - BlockNumber > maxBlockAge;

    public bool RepresentsSamePairAs(PriceUpdate other)
        => Pair.TokenA == other.Pair.TokenA
        && Pair.TokenB == other.Pair.TokenB;

    public decimal PriceDifferencePercent(PriceUpdate other)
    {
        if (!RepresentsSamePairAs(other))
            throw new InvalidTokenPairException(
                $"Cannot compare price updates for different pairs: " +
                $"{Pair.TokenA}/{Pair.TokenB} vs {other.Pair.TokenA}/{other.Pair.TokenB}");

        if (other.SpotPrice == 0)
            throw new InvalidTokenPairException("Cannot calculate price difference — other SpotPrice is zero");

        return ((SpotPrice - other.SpotPrice) / other.SpotPrice) * 100m;
    }
}