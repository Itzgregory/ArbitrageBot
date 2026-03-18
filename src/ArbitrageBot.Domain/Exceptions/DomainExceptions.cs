using System;

namespace ArbitrageBot.Domain.Exceptions;

public sealed class InsufficientLiquidityException : ArbitrageBotException
{
    public string PoolAddress { get; }
    public decimal RequestedAmount { get; }
    public decimal AvailableLiquidity { get; }

    public InsufficientLiquidityException(
        string poolAddress,
        decimal requestedAmount,
        decimal availableLiquidity)
        : base(
            "INSUFFICIENT_LIQUIDITY",
            $"Pool {poolAddress} has insufficient liquidity. " +
            $"Requested: {requestedAmount}, Available: {availableLiquidity}")
    {
        PoolAddress = poolAddress;
        RequestedAmount = requestedAmount;
        AvailableLiquidity = availableLiquidity;
    }
}

public sealed class UnprofitableOpportunityException : ArbitrageBotException
{
    public decimal ExpectedProfit { get; }
    public decimal MinimumThreshold { get; }

    public UnprofitableOpportunityException(decimal expectedProfit, decimal minimumThreshold)
        : base(
            "UNPROFITABLE_OPPORTUNITY",
            $"Expected profit {expectedProfit} is below minimum threshold {minimumThreshold}")
    {
        ExpectedProfit = expectedProfit;
        MinimumThreshold = minimumThreshold;
    }
}

public sealed class ExecutionFailedException : ArbitrageBotException
{
    public string OpportunityId { get; }

    public ExecutionFailedException(string opportunityId, string reason)
        : base(
            "EXECUTION_FAILED",
            $"Execution of opportunity {opportunityId} failed: {reason}")
    {
        OpportunityId = opportunityId;
    }

    public ExecutionFailedException(string opportunityId, string reason, System.Exception innerException)
        : base(
            "EXECUTION_FAILED",
            $"Execution of opportunity {opportunityId} failed: {reason}",
            innerException)
    {
        OpportunityId = opportunityId;
    }
}

public sealed class FrontRunDetectedException : ArbitrageBotException
{
    public string OpportunityId { get; }
    public string TransactionHash { get; }

    public FrontRunDetectedException(string opportunityId, string transactionHash)
        : base(
            "FRONT_RUN_DETECTED",
            $"Opportunity {opportunityId} was front-run. Competing tx: {transactionHash}")
    {
        OpportunityId = opportunityId;
        TransactionHash = transactionHash;
    }
}

public sealed class InvalidTokenPairException : ArbitrageBotException
{
    public InvalidTokenPairException(string reason)
        : base("INVALID_TOKEN_PAIR", $"Invalid token pair: {reason}")
    {
    }
}

public sealed class BlockchainCommunicationException : ArbitrageBotException
{
    public BlockchainCommunicationException(string message, System.Exception innerException)
        : base("BLOCKCHAIN_COMMUNICATION_ERROR", message, innerException)
    {
    }
}