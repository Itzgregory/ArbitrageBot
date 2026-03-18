using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ArbitrageBot.Application.Configuration;
using ArbitrageBot.Domain.Exceptions;
using ArbitrageBot.Domain.Interfaces;
using ArbitrageBot.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArbitrageBot.Application.Services;

public sealed class ExecutorService
{
    private readonly IEnumerable<IFlashLoanProvider> _flashLoanProviders;
    private readonly IStorageProvider _storage;
    private readonly ScannerOptions _options;
    private readonly ILogger<ExecutorService> _logger;

    public ExecutorService(
        IEnumerable<IFlashLoanProvider> flashLoanProviders,
        IStorageProvider storage,
        IOptions<ScannerOptions> options,
        ILogger<ExecutorService> logger)
    {
        _flashLoanProviders = flashLoanProviders
            ?? throw new ArgumentNullException(nameof(flashLoanProviders));
        _storage = storage
            ?? throw new ArgumentNullException(nameof(storage));
        _options = options?.Value
            ?? throw new ArgumentNullException(nameof(options));
        _logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ExecutionResult> ExecuteAsync(
        ArbitrageOpportunity opportunity,
        string eTag,
        CancellationToken cancellationToken = default)
    {
        ValidatePreconditions(opportunity, eTag);

        var stopwatch = Stopwatch.StartNew();

        LogExecutionStart(opportunity);

        opportunity.MarkExecuting();
        await _storage.SaveOpportunityAsync(opportunity, cancellationToken);

        var result = await AttemptExecutionAsync(opportunity, stopwatch, cancellationToken);

        await PersistResultAsync(opportunity, result, cancellationToken);

        return result;
    }

    private void ValidatePreconditions(ArbitrageOpportunity opportunity, string eTag)
    {
        if (opportunity is null)
            throw new ArgumentNullException(nameof(opportunity));

        if (string.IsNullOrWhiteSpace(eTag))
            throw new ArgumentNullException(nameof(eTag));

        if (!opportunity.ValidateETag(eTag))
            throw new ExecutionFailedException(
                opportunity.Id,
                "ETag mismatch — opportunity was modified by another process");

        if (opportunity.RetryCount >= _options.MaxRetryCount)
            throw new ExecutionFailedException(
                opportunity.Id,
                $"Max retry count of {_options.MaxRetryCount} exceeded");
    }

    private void LogExecutionStart(ArbitrageOpportunity opportunity)
    {
        _logger.LogInformation(
            "Executing opportunity {Id} | Buy: {BuyDex} | Sell: {SellDex} | " +
            "ExpectedNetProfit: {NetProfit} | Attempt: {Attempt}",
            opportunity.Id,
            opportunity.BuyLeg.DexName,
            opportunity.SellLeg.DexName,
            opportunity.ExpectedNetProfit,
            opportunity.RetryCount + 1);
    }

    private async Task<ExecutionResult> AttemptExecutionAsync(
        ArbitrageOpportunity opportunity,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        try
        {
            var receipt = await ExecuteFlashLoanAsync(opportunity, cancellationToken);
            stopwatch.Stop();
            return BuildSuccessResult(opportunity, receipt, stopwatch.ElapsedMilliseconds);
        }
        catch (BlockchainCommunicationException ex)
        {
            stopwatch.Stop();
            return HandleFailure(opportunity, ex, ExecutionFailureType.BlockchainCommunicationError, stopwatch.ElapsedMilliseconds);
        }
        catch (InsufficientLiquidityException ex)
        {
            stopwatch.Stop();
            return HandleFailure(opportunity, ex, ExecutionFailureType.InsufficientLiquidity, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return HandleFailure(opportunity, ex, ExecutionFailureType.Unknown, stopwatch.ElapsedMilliseconds);
        }
    }

    private async Task<FlashLoanReceipt> ExecuteFlashLoanAsync(
        ArbitrageOpportunity opportunity,
        CancellationToken cancellationToken)
    {
        var provider = SelectProvider(opportunity);
        var request = BuildFlashLoanRequest(opportunity, provider);

        _logger.LogInformation(
            "Requesting flash loan from {Provider} | Amount: {Amount} | Fee: {Fee}",
            provider.Name,
            request.Amount,
            request.FeeAmount);

        return await provider.ExecuteAsync(request, opportunity, cancellationToken);
    }

    private static FlashLoanRequest BuildFlashLoanRequest(
        ArbitrageOpportunity opportunity,
        IFlashLoanProvider provider)
    {
        return new FlashLoanRequest(
            opportunity.BuyLeg.Pair.TokenA,
            opportunity.FlashLoanAmount,
            provider.Name,
            provider.FeePercent);
    }

    private ExecutionResult BuildSuccessResult(
        ArbitrageOpportunity opportunity,
        FlashLoanReceipt receipt,
        long elapsedMs)
    {
        if (!receipt.IsRepaid)
        {
            return ExecutionResult.Failed(
                opportunity.Id,
                ExecutionFailureType.FlashLoanDenied,
                "Flash loan was not repaid — transaction reverted",
                elapsedMs);
        }

        var actualProfit = receipt.TotalRepaid
            - receipt.AmountBorrowed
            - receipt.FeeCharged;

        _logger.LogInformation(
            "Execution succeeded {Id} | Profit: {Profit} | Duration: {Duration}ms",
            opportunity.Id,
            actualProfit,
            elapsedMs);

        return ExecutionResult.Succeeded(
            opportunity.Id,
            receipt.TransactionHash,
            actualProfit,
            0m,
            opportunity.EstimatedGasCost,
            receipt,
            elapsedMs);
    }

    private ExecutionResult HandleFailure(
        ArbitrageOpportunity opportunity,
        Exception ex,
        ExecutionFailureType failureType,
        long elapsedMs)
    {
        var logLevel = failureType == ExecutionFailureType.Unknown
            ? LogLevel.Error
            : LogLevel.Warning;

        _logger.Log(logLevel, ex,
            "Execution failed for opportunity {Id} | Type: {FailureType}",
            opportunity.Id,
            failureType);

        return ExecutionResult.Failed(
            opportunity.Id,
            failureType,
            ex.Message,
            elapsedMs);
    }

    private async Task PersistResultAsync(
        ArbitrageOpportunity opportunity,
        ExecutionResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            TransitionOpportunityState(opportunity, result);
            await SaveToStorageAsync(opportunity, result, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to persist result for opportunity {Id}",
                opportunity.Id);
        }
    }

    private void TransitionOpportunityState(
        ArbitrageOpportunity opportunity,
        ExecutionResult result)
    {
        if (result.Success)
        {
            opportunity.MarkExecuted();
            return;
        }

        opportunity.MarkFailed(result.FailureReason ?? "Unknown failure");

        if (opportunity.RetryCount < _options.MaxRetryCount)
            opportunity.IncrementRetry();
    }

    private async Task SaveToStorageAsync(
        ArbitrageOpportunity opportunity,
        ExecutionResult result,
        CancellationToken cancellationToken)
    {
        await _storage.SaveOpportunityAsync(opportunity, cancellationToken);
        await _storage.SaveExecutionResultAsync(result, cancellationToken);

        _logger.LogInformation(
            "Persisted result for opportunity {Id} | Success: {Success} | Summary: {Summary}",
            opportunity.Id,
            result.Success,
            result.Summary());
    }

    private IFlashLoanProvider SelectProvider(ArbitrageOpportunity opportunity)
    {
        var provider = _flashLoanProviders
            .OrderBy(p => p.FeePercent)
            .FirstOrDefault();

        if (provider is null)
            throw new ExecutionFailedException(
                opportunity.Id,
                "No flash loan providers registered");

        return provider;
    }
}