namespace ArbitrageBot.Application.Configuration;

public sealed class ScannerOptions
{
    public const string SectionName = "Scanner";

    public int ScanIntervalSeconds { get; init; } = 12;
    public int MaxBlockAge { get; init; } = 2;
    public int MaxConcurrentDexCalls { get; init; } = 5;
    public decimal MinimumProfitThreshold { get; init; } = 0.01m;
    public decimal SlippageTolerance { get; init; } = 0.005m;
    public int MaxRetryCount { get; init; } = 3;
}