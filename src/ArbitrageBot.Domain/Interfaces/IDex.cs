using System.Threading;
using System.Threading.Tasks;
using ArbitrageBot.Domain.Models;

namespace ArbitrageBot.Domain.Interfaces;

public interface IDex
{
    string Name { get; }
    string RouterAddress { get; }

    // blockNumber is passed in from the worker, the DEX layer
    // must not fetch it independently as that wastes RPC calls
    Task<PoolReserves> GetReservesAsync(
        TokenPair pair,
        int blockNumber,
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