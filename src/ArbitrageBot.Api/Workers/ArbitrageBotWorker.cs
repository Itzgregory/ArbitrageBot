using System;
using System.Threading;
using System.Threading.Tasks;
using ArbitrageBot.Application.Configuration;
using ArbitrageBot.Application.Services;
using ArbitrageBot.Domain.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Api.Workers;

public sealed class ArbitrageBotWorker : BackgroundService
{
    private readonly ScannerService _scanner;
    private readonly AnalyzerService _analyzer;
    private readonly ExecutorService _executor;
    private readonly IStorageProvider _storage;
    private readonly ScannerOptions _options;
    private readonly ILogger<ArbitrageBotWorker> _logger;

    public ArbitrageBotWorker(
        ScannerService scanner,
        AnalyzerService analyzer,
        ExecutorService executor,
        IStorageProvider storage,
        IOptions<ScannerOptions> options,
        ILogger<ArbitrageBotWorker> logger)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
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
        // Placeholder — in production this reads from the blockchain gateway.
        // For now we return 0 so the scanner can still run without a live node.
        await Task.CompletedTask;
        return 0;
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