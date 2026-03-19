namespace ArbitrageBot.Application.Configuration;

public sealed class ScannerOptions
{
    // this will be the key in appsettings.json that will map this options class
    // making it a constant here means i never have to harcode it in two places
    // so when i register options in programs.cs, i will just write configuration.GetSection(ScannerOptions.SectionName) 
    // if the section name ever changes, i change it in one place.
    public const string SectionName = "Scanner";

    // every property has a sensible default. 
    // ScanIntervalSeconds = 12 matches Ethereum's block time. 
    // SlippageTolerance = 0.005m is 0.5% — conservative but realistic. 
    // These defaults mean the scanner works out of the box without any configuration, and operators only override what they need to tune.
    public int ScanIntervalSeconds { get; init; } = 12;
    public int MaxBlockAge { get; init; } = 2;
    public int MaxConcurrentDexCalls { get; init; } = 5;
    // MinimumProfitThreshold = 0.01m
    //  1% minimum net profit after all fees. 
    // Below this the opportunity isn't worth the execution risk.
    public decimal MinimumProfitThreshold { get; init; } = 0.01m;
    public decimal SlippageTolerance { get; init; } = 0.005m;
    public int MaxRetryCount { get; init; } = 3;
}