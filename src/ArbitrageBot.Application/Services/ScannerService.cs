using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArbitrageBot.Application.Configuration;
using ArbitrageBot.Domain.Exceptions;
using ArbitrageBot.Domain.Interfaces;
using ArbitrageBot.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Application.Services;

public sealed class ScannerService
{
    private readonly IEnumerable<IDex> _dexes;
    private readonly IEnumerable<TokenPair> _monitoredPairs;
    private readonly ScannerOptions _options;
    private readonly ILogger<ScannerService> _logger;

    public ScannerService(
        // The scanner monitors multiple DEXes simultaneously.
        // Injecting a collection means we add or remove DEX implementations
        // in Program.cs without touching the scanner at all.
        IEnumerable<IDex> dexes,
        // The list of pairs to watch comes from configuration.
        // We never hardcode pairs here, Program.cs registers them from appsettings.json.
        IEnumerable<TokenPair> monitoredPairs,
        IOptions<ScannerOptions> options,
        ILogger<ScannerService> logger)
    {
        _dexes = dexes ?? throw new ArgumentNullException(nameof(dexes));
        _monitoredPairs = monitoredPairs ?? throw new ArgumentNullException(nameof(monitoredPairs));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<PriceUpdate>> ScanAsync(
        int currentBlockNumber,
        CancellationToken cancellationToken = default)
    {
        LogScanStart(currentBlockNumber);

        var tasks = BuildScanTasks(currentBlockNumber, cancellationToken);
        var rawResults = await ExecuteWithConcurrencyLimitAsync(tasks, cancellationToken);
        var validUpdates = FilterValidUpdates(rawResults, currentBlockNumber);

        LogScanComplete(validUpdates.Count, rawResults.Count());

        return validUpdates;
    }

    private void LogScanStart(int currentBlockNumber)
    {
        _logger.LogInformation(
            "Starting scan cycle. Block: {BlockNumber} | DEXes: {DexCount} | Pairs: {PairCount}",
            currentBlockNumber,
            _dexes.Count(),
            _monitoredPairs.Count());
    }

    private void LogScanComplete(int validCount, int totalCount)
    {
        _logger.LogInformation(
            "Scan cycle complete. Valid updates: {ValidCount}/{TotalCount}",
            validCount,
            totalCount);
    }

    private IEnumerable<Task<PriceUpdate?>> BuildScanTasks(
        int currentBlockNumber,
        CancellationToken cancellationToken)
    {
        foreach (var dex in _dexes)
        foreach (var pair in _monitoredPairs)
        {
            // Pass currentBlockNumber through so DEX layer never fetches it independently
            yield return FetchPriceUpdateAsync(dex, pair, currentBlockNumber, cancellationToken);
        }
    }

    private async Task<PriceUpdate?> FetchPriceUpdateAsync(
        IDex dex,
        TokenPair pair,
        int currentBlockNumber,
        CancellationToken cancellationToken)
    {
        // Health check removed from hot path, if the DEX is down,
        // GetReservesAsync throws BlockchainCommunicationException which
        // is caught below. This saves one eth_blockNumber call per DEX per cycle.
        return await FetchAndBuildPriceUpdateAsync(dex, pair, currentBlockNumber, cancellationToken);
    }

    private async Task<PriceUpdate?> FetchAndBuildPriceUpdateAsync(
        IDex dex,
        TokenPair pair,
        int currentBlockNumber,
        CancellationToken cancellationToken)
    {
        try
        {
            // Pass currentBlockNumber, DEX layer stores it on PoolReserves
            // without making an additional RPC call to fetch it
            var reserves = await dex.GetReservesAsync(pair, currentBlockNumber, cancellationToken);
            var priceUpdate = BuildPriceUpdate(dex, pair, reserves);
            LogPriceUpdate(priceUpdate);
            return priceUpdate;
        }
        catch (BlockchainCommunicationException ex)
        {
            _logger.LogWarning(ex,
                "Blockchain communication failed for {DexName} {TokenA}/{TokenB}",
                dex.Name,
                pair.TokenA,
                pair.TokenB);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error scanning {DexName} {TokenA}/{TokenB}",
                dex.Name,
                pair.TokenA,
                pair.TokenB);
            return null;
        }
    }

    private static PriceUpdate BuildPriceUpdate(
        IDex dex,
        TokenPair pair,
        PoolReserves reserves)
    {
        var spotPrice = reserves.SpotPrice(pair.DecimalsA, pair.DecimalsB);

        return new PriceUpdate(
            dex.Name,
            pair,
            spotPrice,
            reserves.Reserve0,
            reserves.Reserve1,
            reserves.BlockNumber);
    }

    private void LogPriceUpdate(PriceUpdate priceUpdate)
    {
        _logger.LogDebug(
            "Price update: {DexName} {TokenA}/{TokenB} @ {SpotPrice} (block {BlockNumber})",
            priceUpdate.DexName,
            priceUpdate.Pair.TokenA,
            priceUpdate.Pair.TokenB,
            priceUpdate.SpotPrice,
            priceUpdate.BlockNumber);
    }

    private IReadOnlyList<PriceUpdate> FilterValidUpdates(
        IEnumerable<PriceUpdate?> rawResults,
        int currentBlockNumber)
    {
        return rawResults
            .Where(u => u is not null)
            .Cast<PriceUpdate>()
            .Where(u => !u.IsStale(currentBlockNumber, _options.MaxBlockAge))
            .ToList();
    }

    private async Task<IEnumerable<PriceUpdate?>> ExecuteWithConcurrencyLimitAsync(
        IEnumerable<Task<PriceUpdate?>> tasks,
        CancellationToken cancellationToken)
    {
        // Cap concurrent RPC calls to avoid rate limiting on the free tier.
        // Without this, all tasks fire simultaneously, too many requests per second.
        var semaphore = new SemaphoreSlim(_options.MaxConcurrentDexCalls);
        var results = new List<PriceUpdate?>();

        var wrappedTasks = tasks.Select(async task =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await task;
            }
            finally
            {
                semaphore.Release();
            }
        });

        results.AddRange(await Task.WhenAll(wrappedTasks));
        return results;
    }
}