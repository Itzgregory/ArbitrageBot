using System;
using ArbitrageBot.Domain.Exceptions;

namespace ArbitrageBot.Domain.Models;

public sealed record FlashLoanRequest
{
    public string Id { get; init; }
    public string TokenAddress { get; init; }
    public decimal Amount { get; init; }
    public string Provider { get; init; }
    public decimal FeePercent { get; init; }
    public decimal FeeAmount { get; init; }
    public decimal TotalRepayment { get; init; }
    public DateTime RequestedAt { get; init; }

    public FlashLoanRequest(
        string tokenAddress,
        decimal amount,
        string provider,
        decimal feePercent)
    {
        if (string.IsNullOrWhiteSpace(tokenAddress))
            throw new InvalidTokenPairException("Token address cannot be null or empty");

        if (amount <= 0)
            throw new InvalidTokenPairException($"Loan amount must be positive, got {amount}");

        if (string.IsNullOrWhiteSpace(provider))
            throw new InvalidTokenPairException("Provider cannot be null or empty");

        if (feePercent < 0 || feePercent > 1)
            throw new InvalidTokenPairException(
                $"FeePercent must be between 0 and 1, got {feePercent}");

        Id = Guid.NewGuid().ToString();
        TokenAddress = tokenAddress.ToLowerInvariant();
        Amount = amount;
        Provider = provider;
        FeePercent = feePercent;
        FeeAmount = amount * feePercent;
        TotalRepayment = amount + FeeAmount;
        RequestedAt = DateTime.UtcNow;
    }
}

public sealed record FlashLoanReceipt
{
    public string RequestId { get; init; }
    public string TransactionHash { get; init; }
    public decimal AmountBorrowed { get; init; }
    public decimal FeeCharged { get; init; }
    public decimal TotalRepaid { get; init; }
    public bool IsRepaid { get; init; }
    public DateTime GrantedAt { get; init; }

    public FlashLoanReceipt(
        FlashLoanRequest request,
        string transactionHash,
        bool isRepaid)
    {
        if (request is null)
            throw new InvalidTokenPairException("FlashLoanRequest cannot be null");

        if (string.IsNullOrWhiteSpace(transactionHash))
            throw new InvalidTokenPairException("Transaction hash cannot be null or empty");

        RequestId = request.Id;
        TransactionHash = transactionHash.ToLowerInvariant();
        AmountBorrowed = request.Amount;
        FeeCharged = request.FeeAmount;
        TotalRepaid = request.TotalRepayment;
        IsRepaid = isRepaid;
        GrantedAt = DateTime.UtcNow;
    }
}