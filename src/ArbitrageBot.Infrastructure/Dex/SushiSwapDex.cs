using Microsoft.Extensions.Logging;
using Nethereum.Web3;

namespace ArbitrageBot.Infrastructure.Dex;

// I discorved that SushiSwap V2 is a fork of Uniswap V2, identical contract interface,
// different deployed addresses. The base class handles all the logic.
// Adding a new DEX in future means creating a file this small.
public sealed class SushiSwapDex : BaseUniswapV2Dex
{
    public override string Name => "SushiSwap";

    public SushiSwapDex(
        IWeb3 web3,
        string routerAddress,
        string factoryAddress,
        ILogger<SushiSwapDex> logger)
        : base(web3, routerAddress, factoryAddress, logger)
    {
    }
}