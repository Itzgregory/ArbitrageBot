using System;
using ArbitrageBot.Domain.Exceptions;
using ArbitrageBot.Domain.Interfaces;

namespace ArbitrageBot.Domain.Models;

public sealed class ArbitrageOpportunity : ITimestamped
{
    public string Id { get; private init; }
    public ArbitrageLeg BuyLeg { get; private init; }
    public ArbitrageLeg SellLeg { get; private init; }
    public decimal FlashLoanAmount { get; private init; }
    public decimal FlashLoanFee { get; private init; }
    public decimal EstimatedGasCost { get; private init; }
    public decimal ExpectedGrossProfit { get; private init; }
    public decimal ExpectedNetProfit { get; private init; }
    public OpportunityStatus Status { get; private set; }
    public DateTime FetchedAt { get; private init; }
    public DateTime? ExecutedAt { get; private set; }
    public string? FailureReason { get; private set; }
    public int Version { get; private set; }
    public int RetryCount { get; private set; }

    public ArbitrageOpportunity(
        ArbitrageLeg buyLeg,
        ArbitrageLeg sellLeg,
        decimal flashLoanAmount,
        decimal flashLoanFee,
        decimal estimatedGasCost)
    {
        if (buyLeg is null)
            throw new InvalidTokenPairException("BuyLeg cannot be null");

        if (sellLeg is null)
            throw new InvalidTokenPairException("SellLeg cannot be null");

        if (buyLeg.Side != LegSide.Buy)
            throw new InvalidTokenPairException("First leg must be a Buy leg");

        if (sellLeg.Side != LegSide.Sell)
            throw new InvalidTokenPairException("Second leg must be a Sell leg");

        // check if both legs are on the same DEX there is no arbitrage. 
        // I'd be buying and selling at the same price minus fees, guaranteeing a loss. 
        // This is a business rule that belongs in the domain i will enforce it here so the Analyzer can never accidentally create a same-DEX opportunity.
        if (buyLeg.DexName == sellLeg.DexName)
            throw new InvalidTokenPairException(
                "Buy and sell legs cannot be on the same DEX — no arbitrage possible");

        if (flashLoanAmount <= 0)
            throw new InvalidTokenPairException($"FlashLoanAmount must be positive, got {flashLoanAmount}");

        if (flashLoanFee < 0)
            throw new InvalidTokenPairException($"FlashLoanFee cannot be negative, got {flashLoanFee}");

        if (estimatedGasCost < 0)
            throw new InvalidTokenPairException($"EstimatedGasCost cannot be negative, got {estimatedGasCost}");

        Id = Guid.NewGuid().ToString();
        BuyLeg = buyLeg;
        SellLeg = sellLeg;
        FlashLoanAmount = flashLoanAmount;
        FlashLoanFee = flashLoanFee;
        EstimatedGasCost = estimatedGasCost;
        ExpectedGrossProfit = sellLeg.ExpectedAmountOut - buyLeg.AmountIn;
        ExpectedNetProfit = ExpectedGrossProfit - flashLoanFee - estimatedGasCost;
        Status = OpportunityStatus.Detected;
        FetchedAt = DateTime.UtcNow;
        Version = 1;
        RetryCount = 0;
    }

    public bool IsProfitable(decimal minimumProfitThreshold)
        => ExpectedNetProfit > minimumProfitThreshold;

    // Version increments on every state transition. 
    // FetchedAt.Ticks is unique per opportunity.
    // Together generate and validate produce a string that is unique to this opportunity at this exact version. 
    // When the Executor tries to execute, it passes the ETag it received when it first saw the opportunity. 
    // If another process already transitioned the status, the version will have incremented and the ETag won't match, and the execution is rejected. 
    // This is optimistic concurrency without a database lock.
    public string GenerateETag()
        => $"{Version}-{FetchedAt.Ticks}";

    public bool ValidateETag(string etag)
        => etag == GenerateETag();

    public void MarkExecuting()
    {
        EnsureValidTransition(OpportunityStatus.Executing);
        Status = OpportunityStatus.Executing;
        Version++;
    }

    public void MarkExecuted()
    {
        EnsureValidTransition(OpportunityStatus.Executed);
        Status = OpportunityStatus.Executed;
        ExecutedAt = DateTime.UtcNow;
        Version++;
    }

    public void MarkFailed(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidTokenPairException("Failure reason cannot be empty");

        EnsureValidTransition(OpportunityStatus.Failed);
        Status = OpportunityStatus.Failed;
        FailureReason = reason;
        Version++;
    }

    public void MarkExpired()
    {
        EnsureValidTransition(OpportunityStatus.Expired);
        Status = OpportunityStatus.Expired;
        Version++;
    }

    // this resets Status back to Detected and clears FailureReason, 
    // but increments RetryCount. 
    // The Analyzer checks RetryCount against a maximum before re-queuing. 
    // I don't enforce the maximum here because that's a configuration concern, the maximum retry count belongs in appsettings.json, not in the domain model.
    public void IncrementRetry()
    {
        if (Status != OpportunityStatus.Failed)
            throw new ExecutionFailedException(Id,
                "Cannot retry an opportunity that has not failed");

        RetryCount++;
        Status = OpportunityStatus.Detected;
        FailureReason = null;
        Version++;
    }

    private void EnsureValidTransition(OpportunityStatus newStatus)
    {
        var valid = (Status, newStatus) switch
        {
            (OpportunityStatus.Detected, OpportunityStatus.Executing) => true,
            (OpportunityStatus.Detected, OpportunityStatus.Expired) => true,
            (OpportunityStatus.Executing, OpportunityStatus.Executed) => true,
            (OpportunityStatus.Executing, OpportunityStatus.Failed) => true,
            _ => false
        };

        if (!valid)
            throw new ExecutionFailedException(Id,
                $"Invalid status transition from {Status} to {newStatus}");
    }
}

public enum OpportunityStatus
{
    Detected,
    Executing,
    Executed,
    Failed,
    Expired
}