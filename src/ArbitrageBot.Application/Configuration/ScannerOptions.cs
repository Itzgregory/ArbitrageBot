namespace ArbitrageBot.Application.Configuration;

public sealed class TokenPairConfig
{
    public string TokenA { get; init; } = string.Empty;
    public string TokenB { get; init; } = string.Empty;
    public int DecimalsA { get; init; }
    public int DecimalsB { get; init; }
    public string PoolAddress { get; init; } = string.Empty;

    // Human-readable label for logs — not used in any calculation
    public string Label { get; init; } = string.Empty;
}

public sealed class ScannerOptions
{
    // This is the key in appsettings.json that maps to this options class.
    // Making it a constant means we never hardcode it in two places.
    // When registering options in Program.cs we write:
    // configuration.GetSection(ScannerOptions.SectionName)
    // If the section name changes, we change it in one place only.
    public const string SectionName = "Scanner";

    // Every property has a sensible default.
    // ScanIntervalSeconds = 12 matches Ethereum's block time.
    // SlippageTolerance = 0.005m is 0.5% — conservative but realistic.
    // These defaults mean the scanner works out of the box without any
    // configuration — operators only override what they need to tune.
    public int ScanIntervalSeconds { get; init; } = 12;
    public int MaxBlockAge { get; init; } = 2;
    public int MaxConcurrentDexCalls { get; init; } = 5;

    // 1% minimum net profit after all fees.
    // Below this the opportunity isn't worth the execution risk.
    public decimal MinimumProfitThreshold { get; init; } = 0.01m;
    public decimal SlippageTolerance { get; init; } = 0.005m;
    public int MaxRetryCount { get; init; } = 3;

    // Token pairs to monitor — loaded from .env at runtime.
    // Empty by default — must be configured via environment variables.
    // Add pairs using indexed env vars:
    // SCANNER__TOKENPAIRS__0__TOKENA, SCANNER__TOKENPAIRS__0__TOKENB etc.
    public List<TokenPairConfig> TokenPairs { get; init; } = new();
}