using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ArbitrageBot.Domain.Exceptions;
using ArbitrageBot.Domain.Interfaces;
using ArbitrageBot.Domain.Models;
using Microsoft.Extensions.Logging;
using Nethereum.Web3;

namespace ArbitrageBot.Infrastructure.Dex;

public abstract class BaseUniswapV2Dex : IDex
{
    private readonly IWeb3 _web3;
    protected readonly ILogger Logger;

    // Cache pair addresses after first resolution — pool addresses never change
    // on a deployed DEX. This halves our RPC calls per cycle after warmup.
    // First cycle: getPair + getReserves = 2 calls per pair.
    // Every subsequent cycle: getReserves only = 1 call per pair.
    private readonly ConcurrentDictionary<string, string> _pairAddressCache = new();

    // Minimal ABI for the Uniswap V2 pair contract.
    // We only define getReserves because that is the only function we call.
    // Defining the full ABI would add hundreds of lines for no benefit.
    private const string PairAbi = """
        [
            {
                "name": "getReserves",
                "type": "function",
                "inputs": [],
                "outputs": [
                    { "name": "reserve0", "type": "uint112" },
                    { "name": "reserve1", "type": "uint112" },
                    { "name": "blockTimestampLast", "type": "uint32" }
                ]
            }
        ]
        """;

    // Minimal ABI for the Uniswap V2 factory contract.
    // We call getPair to resolve the pool address for a given token pair.
    // The factory returns the zero address if no pool exists for the pair.
    private const string FactoryAbi = """
        [
            {
                "name": "getPair",
                "type": "function",
                "inputs": [
                    { "name": "tokenA", "type": "address" },
                    { "name": "tokenB", "type": "address" }
                ],
                "outputs": [
                    { "name": "pair", "type": "address" }
                ]
            }
        ]
        """;

    // Subclasses define their own Name, RouterAddress, and FactoryAddress
    // because these are the only things that differ between DEX implementations
    public abstract string Name { get; }
    public string RouterAddress { get; }
    public string FactoryAddress { get; }

    protected BaseUniswapV2Dex(
        IWeb3 web3,
        string routerAddress,
        string factoryAddress,
        ILogger logger)
    {
        _web3 = web3 ?? throw new ArgumentNullException(nameof(web3));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(routerAddress))
            throw new ArgumentNullException(nameof(routerAddress));

        if (string.IsNullOrWhiteSpace(factoryAddress))
            throw new ArgumentNullException(nameof(factoryAddress));

        // Normalize to lowercase — Ethereum addresses are case-insensitive
        // but inconsistent casing causes equality mismatches in dictionaries
        RouterAddress = routerAddress.ToLowerInvariant();
        FactoryAddress = factoryAddress.ToLowerInvariant();
    }

    // blockNumber is passed in from the worker — we must not fetch it here
    // as that would add an extra eth_blockNumber RPC call per DEX per cycle
    public async Task<PoolReserves> GetReservesAsync(
        TokenPair pair,
        int blockNumber,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pairAddress = await GetPairAddressAsync(pair, cancellationToken);
            var reserves = await FetchReservesFromChainAsync(pairAddress, cancellationToken);

            LogReservesFetched(pair, reserves.reserve0, reserves.reserve1, blockNumber);

            return BuildPoolReserves(pairAddress, reserves, blockNumber, pair);
        }
        catch (ArbitrageBotException)
        {
            // Rethrow domain exceptions unchanged — InvalidTokenPairException
            // from ValidatePairAddress must not be swallowed into a
            // BlockchainCommunicationException or it loses its specific type
            throw;
        }
        catch (Exception ex)
        {
            // Wrap all unknown exceptions in our domain type so the middleware
            // and application layer never have to deal with Nethereum internals
            throw new BlockchainCommunicationException(
                $"Failed to get reserves for {pair.TokenA}/{pair.TokenB} on {Name}",
                ex);
        }
    }

    public async Task<decimal> GetSpotPriceAsync(
        TokenPair pair,
        CancellationToken cancellationToken = default)
    {
        // Reuse GetReservesAsync to avoid a separate RPC call.
        // SpotPrice is computed locally from reserves — no extra network round trip.
        // Block number is not meaningful here so we pass 0.
        var reserves = await GetReservesAsync(pair, 0, cancellationToken);
        return reserves.SpotPrice(pair.DecimalsA, pair.DecimalsB);
    }

    public async Task<decimal> GetAmountOutAsync(
        TokenPair pair,
        decimal amountIn,
        CancellationToken cancellationToken = default)
    {
        // Same pattern as GetSpotPriceAsync — one RPC call, math done locally
        var reserves = await GetReservesAsync(pair, 0, cancellationToken);
        return reserves.GetAmountOut(amountIn, pair.DecimalsA, pair.DecimalsB);
    }

    public async Task<bool> IsHealthyAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            // A successful block number fetch proves the RPC node is reachable.
            // We do not call getReserves here — that would require a token pair
            // and health checks should have no dependency on specific pairs.
            // IsHealthyAsync is the only place we fetch block number independently
            // because it runs on a slower interval outside the scan hot path.
            var blockNumber = await GetCurrentBlockNumberAsync(cancellationToken);
            return blockNumber > 0;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{DexName} health check failed", Name);
            return false;
        }
    }

    private async Task<string> GetPairAddressAsync(
        TokenPair pair,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildPairCacheKey(pair);

        // Return cached address if we already resolved this pair.
        // Pool addresses are immutable on-chain — safe to cache forever.
        if (_pairAddressCache.TryGetValue(cacheKey, out var cachedAddress))
        {
            Logger.LogDebug(
                "Using cached pair address for {TokenA}/{TokenB}: {Address}",
                pair.TokenA,
                pair.TokenB,
                cachedAddress);
            return cachedAddress;
        }

        // Cache miss — fetch from chain and store for future cycles
        var factoryContract = _web3.Eth.GetContract(FactoryAbi, FactoryAddress);
        var getPairFunction = factoryContract.GetFunction("getPair");

        var pairAddress = await getPairFunction.CallAsync<string>(
            pair.TokenA,
            pair.TokenB);

        ValidatePairAddress(pairAddress, pair);

        var normalizedAddress = pairAddress.ToLowerInvariant();

        // Store in cache — all future cycles skip this RPC call entirely
        _pairAddressCache.TryAdd(cacheKey, normalizedAddress);

        Logger.LogInformation(
            "Resolved and cached pair address for {TokenA}/{TokenB} on {DexName}: {Address}",
            pair.TokenA,
            pair.TokenB,
            Name,
            normalizedAddress);

        return normalizedAddress;
    }

    private static string BuildPairCacheKey(TokenPair pair)
    {
        // Sort addresses so the key is identical regardless of token order.
        // WETH/USDC and USDC/WETH must resolve to the same pool address.
        var addresses = new[] { pair.TokenA, pair.TokenB };
        Array.Sort(addresses, StringComparer.OrdinalIgnoreCase);
        return $"{addresses[0]}-{addresses[1]}";
    }

    private async Task<(BigInteger reserve0, BigInteger reserve1)> FetchReservesFromChainAsync(
        string pairAddress,
        CancellationToken cancellationToken)
    {
        var pairContract = _web3.Eth.GetContract(PairAbi, pairAddress);
        var getReservesFunction = pairContract.GetFunction("getReserves");

        // Nethereum deserializes the three return values into ReservesOutput
        // using the Parameter attributes to match positions
        var result = await getReservesFunction
            .CallDeserializingToObjectAsync<ReservesOutput>();

        return (result.Reserve0, result.Reserve1);
    }

    // Only used by IsHealthyAsync — not in the scan hot path
    private async Task<int> GetCurrentBlockNumberAsync(
        CancellationToken cancellationToken)
    {
        var blockNumber = await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
        return (int)blockNumber.Value;
    }

    private static PoolReserves BuildPoolReserves(
        string pairAddress,
        (BigInteger reserve0, BigInteger reserve1) reserves,
        int blockNumber,
        TokenPair pair)
    {
        // Blockchain values are integers in the token's smallest unit (like wei).
        // We convert to human-readable decimals before passing to the domain model.
        // A token with 18 decimals stores 1.5 as 1500000000000000000.
        var reserve0 = ConvertFromWei(reserves.reserve0, pair.DecimalsA);
        var reserve1 = ConvertFromWei(reserves.reserve1, pair.DecimalsB);

        return new PoolReserves(
            pairAddress,
            reserve0,
            reserve1,
            blockNumber);
    }

    private static decimal ConvertFromWei(BigInteger value, int decimals)
    {
        // Divide by 10^decimals to shift from integer representation
        // to human-readable decimal. This must happen before any price math.
        return (decimal)value / (decimal)Math.Pow(10, decimals);
    }

    private void ValidatePairAddress(string pairAddress, TokenPair pair)
    {
        // Uniswap V2 factory returns the zero address when no pool exists
        // for the given token pair. We treat this as a domain validation failure
        // not a communication error — the pair is simply not supported.
        var zeroAddress = "0x0000000000000000000000000000000000000000";

        if (string.IsNullOrWhiteSpace(pairAddress) || pairAddress == zeroAddress)
            throw new InvalidTokenPairException(
                $"No pool exists for {pair.TokenA}/{pair.TokenB} on {Name}");
    }

    private void LogReservesFetched(
        TokenPair pair,
        BigInteger reserve0,
        BigInteger reserve1,
        int blockNumber)
    {
        Logger.LogDebug(
            "{DexName} reserves fetched | {TokenA}/{TokenB} | R0: {Reserve0} | R1: {Reserve1} | Block: {Block}",
            Name,
            pair.TokenA,
            pair.TokenB,
            reserve0,
            reserve1,
            blockNumber);
    }

    // Nethereum deserializes contract outputs into a class.
    // [FunctionOutput] is required on the class itself — without it the
    // deserializer refuses to decode regardless of [Parameter] attributes.
    // This class is private — nothing outside this file needs to know
    // how Uniswap V2 encodes its reserve data on-chain.
    [Nethereum.ABI.FunctionEncoding.Attributes.FunctionOutput]
    private sealed class ReservesOutput
    {
        [Nethereum.ABI.FunctionEncoding.Attributes.Parameter("uint112", "reserve0", 1)]
        public BigInteger Reserve0 { get; set; }

        [Nethereum.ABI.FunctionEncoding.Attributes.Parameter("uint112", "reserve1", 2)]
        public BigInteger Reserve1 { get; set; }

        [Nethereum.ABI.FunctionEncoding.Attributes.Parameter("uint32", "blockTimestampLast", 3)]
        public uint BlockTimestampLast { get; set; }
    }
}