namespace ArbitrageBot.Application.Configuration;

public sealed class BlockchainOptions
{
    public const string SectionName = "Blockchain";

    public string RpcUrl { get; init; } = string.Empty;
    public string PrivateKey { get; init; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(RpcUrl) &&
        !string.IsNullOrWhiteSpace(PrivateKey);
}