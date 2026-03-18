using System.Threading;
using System.Threading.Tasks;
using ArbitrageBot.Domain.Models;

namespace ArbitrageBot.Domain.Interfaces;

public interface IFlashLoanProvider
{
    string Name { get; }
    decimal FeePercent { get; }

    Task<FlashLoanReceipt> ExecuteAsync(
        FlashLoanRequest request,
        ArbitrageOpportunity opportunity,
        CancellationToken cancellationToken = default);

    Task<decimal> GetAvailableLiquidityAsync(
        string tokenAddress,
        CancellationToken cancellationToken = default);

    Task<bool> IsHealthyAsync(
        CancellationToken cancellationToken = default);
}