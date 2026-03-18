using ArbitrageBot.Domain.Exceptions;

namespace ArbitrageBot.Domain.Models;

public sealed record TokenPair
{
    public string TokenA { get; init; }
    public string TokenB { get; init; }
    public int DecimalsA { get; init; }
    public int DecimalsB { get; init; }
    public string PoolAddress { get; init; }

    public TokenPair(
        string tokenA,
        string tokenB,
        int decimalsA,
        int decimalsB,
        string poolAddress)
    {
        if (string.IsNullOrWhiteSpace(tokenA))
            throw new InvalidTokenPairException("TokenA address cannot be null or empty");

        if (string.IsNullOrWhiteSpace(tokenB))
            throw new InvalidTokenPairException("TokenB address cannot be null or empty");

        if (string.IsNullOrWhiteSpace(poolAddress))
            throw new InvalidTokenPairException("Pool address cannot be null or empty");

        if (decimalsA < 0)
            throw new InvalidTokenPairException($"DecimalsA cannot be negative, got {decimalsA}");

        if (decimalsB < 0)
            throw new InvalidTokenPairException($"DecimalsB cannot be negative, got {decimalsB}");

        if (tokenA.Equals(tokenB, StringComparison.OrdinalIgnoreCase))
            throw new InvalidTokenPairException("TokenA and TokenB cannot be the same token");

        TokenA = tokenA.ToLowerInvariant();
        TokenB = tokenB.ToLowerInvariant();
        DecimalsA = decimalsA;
        DecimalsB = decimalsB;
        PoolAddress = poolAddress.ToLowerInvariant();
    }
}