using System.Threading;
using System.Threading.Tasks;
using ArbitrageBot.Domain.Models;

namespace ArbitrageBot.Domain.Interfaces;

public interface IDex
{
    string Name { get; }
    string RouterAddress { get; }

    Task<PoolReserves> GetReservesAsync(
        TokenPair pair,
        CancellationToken cancellationToken = default);

    Task<decimal> GetSpotPriceAsync(
        TokenPair pair,
        CancellationToken cancellationToken = default);

    Task<decimal> GetAmountOutAsync(
        TokenPair pair,
        decimal amountIn,
        CancellationToken cancellationToken = default);

    Task<bool> IsHealthyAsync(
        CancellationToken cancellationToken = default);
}