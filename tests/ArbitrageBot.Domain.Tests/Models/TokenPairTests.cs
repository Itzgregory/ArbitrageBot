using ArbitrageBot.Domain.Exceptions;
using ArbitrageBot.Domain.Models;
using FluentAssertions;

namespace ArbitrageBot.Domain.Tests.Models;

public sealed class TokenPairTests
{
    private const string ValidTokenA = "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2";
    private const string ValidTokenB = "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48";
    private const string ValidPoolAddress = "0xb4e16d0168e52d35cacd2c6185b44281ec28c9dc";

    [Fact]
    public void Constructor_WithValidInputs_CreatesTokenPair()
    {
        var pair = new TokenPair(ValidTokenA, ValidTokenB, 18, 6, ValidPoolAddress);

        pair.TokenA.Should().Be(ValidTokenA);
        pair.TokenB.Should().Be(ValidTokenB);
        pair.DecimalsA.Should().Be(18);
        pair.DecimalsB.Should().Be(6);
        pair.PoolAddress.Should().Be(ValidPoolAddress);
    }

    [Fact]
    public void Constructor_NormalizesAddressesToLowercase()
    {
        var pair = new TokenPair(
            ValidTokenA.ToUpperInvariant(),
            ValidTokenB.ToUpperInvariant(),
            18, 6,
            ValidPoolAddress.ToUpperInvariant());

        pair.TokenA.Should().Be(ValidTokenA.ToLowerInvariant());
        pair.TokenB.Should().Be(ValidTokenB.ToLowerInvariant());
        pair.PoolAddress.Should().Be(ValidPoolAddress.ToLowerInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithInvalidTokenA_ThrowsInvalidTokenPairException(string? invalidAddress)
    {
        var act = () => new TokenPair(invalidAddress!, ValidTokenB, 18, 6, ValidPoolAddress);

        act.Should().Throw<InvalidTokenPairException>()
            .WithMessage("*TokenA*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithInvalidTokenB_ThrowsInvalidTokenPairException(string? invalidAddress)
    {
        var act = () => new TokenPair(ValidTokenA, invalidAddress!, 18, 6, ValidPoolAddress);

        act.Should().Throw<InvalidTokenPairException>()
            .WithMessage("*TokenB*");
    }

    [Fact]
    public void Constructor_WithSameTokenAAndTokenB_ThrowsInvalidTokenPairException()
    {
        var act = () => new TokenPair(ValidTokenA, ValidTokenA, 18, 18, ValidPoolAddress);

        act.Should().Throw<InvalidTokenPairException>()
            .WithMessage("*same token*");
    }

    [Fact]
    public void Constructor_WithNegativeDecimalsA_ThrowsInvalidTokenPairException()
    {
        var act = () => new TokenPair(ValidTokenA, ValidTokenB, -1, 6, ValidPoolAddress);

        act.Should().Throw<InvalidTokenPairException>()
            .WithMessage("*DecimalsA*");
    }

    [Fact]
    public void Constructor_WithNegativeDecimalsB_ThrowsInvalidTokenPairException()
    {
        var act = () => new TokenPair(ValidTokenA, ValidTokenB, 18, -1, ValidPoolAddress);

        act.Should().Throw<InvalidTokenPairException>()
            .WithMessage("*DecimalsB*");
    }

    [Fact]
    public void TwoIdenticalPairs_AreEqual()
    {
        var pair1 = new TokenPair(ValidTokenA, ValidTokenB, 18, 6, ValidPoolAddress);
        var pair2 = new TokenPair(ValidTokenA, ValidTokenB, 18, 6, ValidPoolAddress);

        pair1.Should().Be(pair2);
    }

    [Fact]
    public void TwoDifferentPairs_AreNotEqual()
    {
        var pair1 = new TokenPair(ValidTokenA, ValidTokenB, 18, 6, ValidPoolAddress);
        var pair2 = new TokenPair(ValidTokenA, ValidTokenB, 18, 8, ValidPoolAddress);

        pair1.Should().NotBe(pair2);
    }
}