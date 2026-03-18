using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArbitrageBot.Application.Configuration;
using ArbitrageBot.Domain.Interfaces;
using ArbitrageBot.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Application.Services;

public sealed class AnalyzerService
{
    private readonly IEnumerable<IFlashLoanProvider> _flashLoanProviders;
    private readonly ScannerOptions _options;
    private readonly ILogger<AnalyzerService> _logger;

    public AnalyzerService(
        IEnumerable<IFlashLoanProvider> flashLoanProviders,
        IOptions<ScannerOptions> options,
        ILogger<AnalyzerService> logger)
    {
        _flashLoanProviders = flashLoanProviders
            ?? throw new ArgumentNullException(nameof(flashLoanProviders));
        _options = options?.Value
            ?? throw new ArgumentNullException(nameof(options));
        _logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<ArbitrageOpportunity>> AnalyzeAsync(
        IReadOnlyList<PriceUpdate> priceUpdates,
        int currentBlockNumber,
        CancellationToken cancellationToken = default)
    {
        if (!HasEnoughUpdates(priceUpdates))
            return Array.Empty<ArbitrageOpportunity>();

        _logger.LogInformation(
            "Analyzing {Count} price updates for arbitrage opportunities",
            priceUpdates.Count);

        var opportunities = await FindOpportunitiesAsync(priceUpdates, currentBlockNumber, cancellationToken);

        _logger.LogInformation(
            "Analysis complete. Found {Count} profitable opportunities",
            opportunities.Count);

        return opportunities;
    }

    private bool HasEnoughUpdates(IReadOnlyList<PriceUpdate> priceUpdates)
    {
        if (priceUpdates is null || priceUpdates.Count == 0)
        {
            _logger.LogDebug("No price updates to analyze");
            return false;
        }

        return true;
    }

    private async Task<List<ArbitrageOpportunity>> FindOpportunitiesAsync(
        IReadOnlyList<PriceUpdate> priceUpdates,
        int currentBlockNumber,
        CancellationToken cancellationToken)
    {
        var opportunities = new List<ArbitrageOpportunity>();
        var pairGroups = GroupUpdatesByPair(priceUpdates);

        foreach (var group in pairGroups)
        {
            var found = await AnalyzePairGroupAsync(group, currentBlockNumber, cancellationToken);
            opportunities.AddRange(found);
        }

        return opportunities;
    }

    private IEnumerable<IGrouping<string, PriceUpdate>> GroupUpdatesByPair(
        IReadOnlyList<PriceUpdate> priceUpdates)
    {
        return priceUpdates
            .GroupBy(u => $"{u.Pair.TokenA}-{u.Pair.TokenB}")
            .Where(g => g.Count() > 1);
    }

    private async Task<List<ArbitrageOpportunity>> AnalyzePairGroupAsync(
        IGrouping<string, PriceUpdate> group,
        int currentBlockNumber,
        CancellationToken cancellationToken)
    {
        var opportunities = new List<ArbitrageOpportunity>();
        var updates = group.ToList();

        for (int i = 0; i < updates.Count; i++)
        for (int j = i + 1; j < updates.Count; j++)
        {
            var opportunity = await TryBuildOpportunityAsync(
                updates[i],
                updates[j],
                currentBlockNumber,
                cancellationToken);

            if (opportunity is not null)
                opportunities.Add(opportunity);
        }

        return opportunities;
    }

    private async Task<ArbitrageOpportunity?> TryBuildOpportunityAsync(
        PriceUpdate updateA,
        PriceUpdate updateB,
        int currentBlockNumber,
        CancellationToken cancellationToken)
    {
        var priceDiff = updateA.PriceDifferencePercent(updateB);

        if (!IsPriceDiffSufficient(priceDiff, updateA))
            return null;

        var (buyUpdate, sellUpdate) = DetermineBuySellSides(updateA, updateB, priceDiff);

        var provider = await SelectFlashLoanProviderAsync(
            buyUpdate.Pair.TokenA,
            buyUpdate.Reserve0,
            cancellationToken);

        if (provider is null)
        {
            LogNoProviderAvailable(buyUpdate);
            return null;
        }

        return BuildOpportunity(buyUpdate, sellUpdate, provider, priceDiff);
    }

    private bool IsPriceDiffSufficient(decimal priceDiff, PriceUpdate update)
    {
        if (Math.Abs(priceDiff) < _options.MinimumProfitThreshold)
        {
            _logger.LogDebug(
                "Price difference {Diff:F4}% below threshold {Threshold}% for {TokenA}/{TokenB}",
                priceDiff,
                _options.MinimumProfitThreshold,
                update.Pair.TokenA,
                update.Pair.TokenB);
            return false;
        }

        return true;
    }

    private static (PriceUpdate buy, PriceUpdate sell) DetermineBuySellSides(
        PriceUpdate updateA,
        PriceUpdate updateB,
        decimal priceDiff)
    {
        return priceDiff < 0
            ? (updateA, updateB)
            : (updateB, updateA);
    }

    private void LogNoProviderAvailable(PriceUpdate buyUpdate)
    {
        _logger.LogWarning(
            "No flash loan provider available for {TokenA}/{TokenB}",
            buyUpdate.Pair.TokenA,
            buyUpdate.Pair.TokenB);
    }

    private ArbitrageOpportunity? BuildOpportunity(
        PriceUpdate buyUpdate,
        PriceUpdate sellUpdate,
        IFlashLoanProvider provider,
        decimal priceDiff)
    {
        var flashLoanAmount = buyUpdate.Reserve0 * 0.1m;
        var buyAmountOut = CalculateAmountOut(buyUpdate, flashLoanAmount);

        if (buyAmountOut <= 0)
            return null;

        var buyLeg = BuildLeg(buyUpdate, flashLoanAmount, buyAmountOut, LegSide.Buy);
        var sellAmountOut = CalculateAmountOut(sellUpdate, buyAmountOut);
        var sellLeg = BuildLeg(sellUpdate, buyAmountOut, sellAmountOut, LegSide.Sell);

        var flashLoanFee = flashLoanAmount * provider.FeePercent;
        var estimatedGasCost = EstimateGasCost();

        var opportunity = new ArbitrageOpportunity(
            buyLeg,
            sellLeg,
            flashLoanAmount,
            flashLoanFee,
            estimatedGasCost);

        if (!opportunity.IsProfitable(_options.MinimumProfitThreshold))
        {
            LogUnprofitable(opportunity);
            return null;
        }

        LogOpportunityFound(opportunity, buyUpdate, sellUpdate, priceDiff);

        return opportunity;
    }

    private ArbitrageLeg BuildLeg(
        PriceUpdate update,
        decimal amountIn,
        decimal amountOut,
        LegSide side)
    {
        return new ArbitrageLeg(
            update.DexName,
            update.Pair,
            amountIn,
            amountOut,
            _options.SlippageTolerance,
            side);
    }

    private void LogUnprofitable(ArbitrageOpportunity opportunity)
    {
        _logger.LogDebug(
            "Opportunity {Id} not profitable after fees. NetProfit: {NetProfit}",
            opportunity.Id,
            opportunity.ExpectedNetProfit);
    }

    private void LogOpportunityFound(
        ArbitrageOpportunity opportunity,
        PriceUpdate buyUpdate,
        PriceUpdate sellUpdate,
        decimal priceDiff)
    {
        _logger.LogInformation(
            "Opportunity found: {Id} | Buy: {BuyDex} | Sell: {SellDex} | " +
            "NetProfit: {NetProfit} | PriceDiff: {PriceDiff:F4}%",
            opportunity.Id,
            buyUpdate.DexName,
            sellUpdate.DexName,
            opportunity.ExpectedNetProfit,
            Math.Abs(priceDiff));
    }

    private async Task<IFlashLoanProvider?> SelectFlashLoanProviderAsync(
        string tokenAddress,
        decimal requiredAmount,
        CancellationToken cancellationToken)
    {
        foreach (var provider in _flashLoanProviders.OrderBy(p => p.FeePercent))
        {
            try
            {
                var available = await provider.GetAvailableLiquidityAsync(
                    tokenAddress,
                    cancellationToken);

                if (available >= requiredAmount)
                    return provider;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to check liquidity on provider {Provider}",
                    provider.Name);
            }
        }

        return null;
    }

    private static decimal CalculateAmountOut(PriceUpdate update, decimal amountIn)
    {
        if (update.Reserve0 <= 0 || update.Reserve1 <= 0)
            return 0m;

        var amountInWithFee = amountIn * 997m;
        var numerator = amountInWithFee * update.Reserve1;
        var denominator = update.Reserve0 * 1000m + amountInWithFee;

        return numerator / denominator;
    }

    private static decimal EstimateGasCost()
    {
        const decimal averageGasUnits = 200_000m;
        const decimal gasPriceGwei = 30m;
        const decimal gweiToEth = 0.000_000_001m;

        return averageGasUnits * gasPriceGwei * gweiToEth;
    }
}