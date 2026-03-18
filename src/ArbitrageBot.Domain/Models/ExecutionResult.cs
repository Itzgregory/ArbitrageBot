using System;
using ArbitrageBot.Domain.Exceptions;
using ArbitrageBot.Domain.Interfaces;

namespace ArbitrageBot.Domain.Models;

public sealed class ExecutionResult : ITimestamped
{
    public string Id { get; private init; }
    public string OpportunityId { get; private init; }
    public bool Success { get; private init; }
    public string? TransactionHash { get; private init; }
    public decimal? ActualProfit { get; private init; }
    public decimal? GasUsed { get; private init; }
    public decimal? GasCostEth { get; private init; }
    public string? FailureReason { get; private init; }
    public ExecutionFailureType? FailureType { get; private init; }
    public FlashLoanReceipt? FlashLoanReceipt { get; private init; }
    public DateTime FetchedAt { get; private init; }
    public long ExecutionDurationMs { get; private init; }

    private ExecutionResult()
    {
        Id = string.Empty;
        OpportunityId = string.Empty;
    }

    public static ExecutionResult Succeeded(
        string opportunityId,
        string transactionHash,
        decimal actualProfit,
        decimal gasUsed,
        decimal gasCostEth,
        FlashLoanReceipt flashLoanReceipt,
        long executionDurationMs)
    {
        if (string.IsNullOrWhiteSpace(opportunityId))
            throw new ExecutionFailedException("unknown", "OpportunityId cannot be null or empty");

        if (string.IsNullOrWhiteSpace(transactionHash))
            throw new ExecutionFailedException(opportunityId, "TransactionHash cannot be null or empty");

        if (flashLoanReceipt is null)
            throw new ExecutionFailedException(opportunityId, "FlashLoanReceipt cannot be null on success");

        return new ExecutionResult
        {
            Id = Guid.NewGuid().ToString(),
            OpportunityId = opportunityId,
            Success = true,
            TransactionHash = transactionHash.ToLowerInvariant(),
            ActualProfit = actualProfit,
            GasUsed = gasUsed,
            GasCostEth = gasCostEth,
            FlashLoanReceipt = flashLoanReceipt,
            FetchedAt = DateTime.UtcNow,
            ExecutionDurationMs = executionDurationMs
        };
    }

    public static ExecutionResult Failed(
        string opportunityId,
        ExecutionFailureType failureType,
        string failureReason,
        long executionDurationMs)
    {
        if (string.IsNullOrWhiteSpace(opportunityId))
            throw new ExecutionFailedException("unknown", "OpportunityId cannot be null or empty");

        if (string.IsNullOrWhiteSpace(failureReason))
            throw new ExecutionFailedException(opportunityId, "FailureReason cannot be null or empty");

        return new ExecutionResult
        {
            Id = Guid.NewGuid().ToString(),
            OpportunityId = opportunityId,
            Success = false,
            FailureReason = failureReason,
            FailureType = failureType,
            FetchedAt = DateTime.UtcNow,
            ExecutionDurationMs = executionDurationMs
        };
    }

    public bool WasProfitable()
        => Success && ActualProfit.HasValue && ActualProfit.Value > 0;

    public string Summary()
        => Success
            ? $"Execution succeeded. Profit: {ActualProfit} | Gas: {GasCostEth} ETH | Duration: {ExecutionDurationMs}ms"
            : $"Execution failed. Reason: {FailureReason} | Type: {FailureType} | Duration: {ExecutionDurationMs}ms";
}

public enum ExecutionFailureType
{
    FrontRun,
    InsufficientLiquidity,
    TransactionReverted,
    BlockchainCommunicationError,
    SlippageExceeded,
    FlashLoanDenied,
    Unknown
}