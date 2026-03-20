using System;
using System.Threading;
using System.Threading.Tasks;
using ArbitrageBot.Application.Configuration;
using ArbitrageBot.Application.Services;
using ArbitrageBot.Domain.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethereum.Web3;

namespace ArbitrageBot.Api.Workers;

public sealed class ArbitrageBotWorker : BackgroundService
{
    private readonly ScannerService _scanner;
    private readonly AnalyzerService _analyzer;
    private readonly ExecutorService _executor;
    private readonly IStorageProvider _storage;
    private readonly IWeb3 _web3;
    private readonly ScannerOptions _options;
    private readonly ILogger<ArbitrageBotWorker> _logger;

    public ArbitrageBotWorker(
        ScannerService scanner,
        AnalyzerService analyzer,
        ExecutorService executor,
        IStorageProvider storage,
        IWeb3 web3,
        IOptions<ScannerOptions> options,
        ILogger<ArbitrageBotWorker> logger)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _web3 = web3 ?? throw new ArgumentNullException(nameof(web3));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ArbitrageBot worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunScanCycleAsync(stoppingToken);
            await WaitForNextCycleAsync(stoppingToken);
        }

        _logger.LogInformation("ArbitrageBot worker stopped");
    }

    private async Task RunScanCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            var blockNumber = await GetCurrentBlockNumberAsync(stoppingToken);
            var priceUpdates = await _scanner.ScanAsync(blockNumber, stoppingToken);
            var opportunities = await _analyzer.AnalyzeAsync(priceUpdates, blockNumber, stoppingToken);

            await ExecuteOpportunitiesAsync(opportunities, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not an error — the host is shutting down gracefully
            throw;
        }
        catch (Exception ex)
        {
            // Log but do not rethrow — a single failed cycle must not stop the worker.
            // The next cycle will run after the configured interval.
            _logger.LogError(ex, "Scan cycle failed unexpectedly");
        }
    }

    private async Task ExecuteOpportunitiesAsync(
        System.Collections.Generic.IReadOnlyList<Domain.Models.ArbitrageOpportunity> opportunities,
        CancellationToken stoppingToken)
    {
        foreach (var opportunity in opportunities)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            await ExecuteSingleOpportunityAsync(opportunity, stoppingToken);
        }
    }

    private async Task ExecuteSingleOpportunityAsync(
        Domain.Models.ArbitrageOpportunity opportunity,
        CancellationToken stoppingToken)
    {
        try
        {
            var eTag = opportunity.GenerateETag();
            var result = await _executor.ExecuteAsync(opportunity, eTag, stoppingToken);

            _logger.LogInformation(
                "Opportunity {Id} result: {Summary}",
                opportunity.Id,
                result.Summary());
        }
        catch (Exception ex)
        {
            // A single failed execution must not stop remaining opportunities
            // from being attempted in this cycle
            _logger.LogError(ex,
                "Failed to execute opportunity {Id}",
                opportunity.Id);
        }
    }

    private async Task<int> GetCurrentBlockNumberAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Read the actual current block number from the blockchain.
            // This is passed to the scanner so price updates carry the correct
            // block number for staleness checks — not fetched per DEX per cycle.
            var blockNumber = await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
            var current = (int)blockNumber.Value;

            _logger.LogDebug("Current block number: {BlockNumber}", current);

            return current;
        }
        catch (Exception ex)
        {
            // If the block number fetch fails, log and return 0.
            // The scanner still runs but staleness checks are degraded.
            // This is preferable to stopping the entire cycle on a transient error.
            _logger.LogWarning(ex, "Failed to fetch current block number, defaulting to 0");
            return 0;
        }
    }

    private async Task WaitForNextCycleAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug(
            "Waiting {Seconds}s before next scan cycle",
            _options.ScanIntervalSeconds);

        await Task.Delay(
            TimeSpan.FromSeconds(_options.ScanIntervalSeconds),
            stoppingToken);
    }
}