namespace ArbitrageBot.Application.Configuration;

public sealed class DexContractOptions
{
    public string RouterAddress { get; init; } = string.Empty;
    public string FactoryAddress { get; init; } = string.Empty;
    public string PoolAddress { get; init; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(RouterAddress) &&
        !string.IsNullOrWhiteSpace(FactoryAddress);
}

public sealed class DexOptions
{
    public const string SectionName = "Dexes";

    public DexContractOptions UniswapV2 { get; init; } = new();
    public DexContractOptions SushiSwap { get; init; } = new();
    public DexContractOptions Aave { get; init; } = new();
}