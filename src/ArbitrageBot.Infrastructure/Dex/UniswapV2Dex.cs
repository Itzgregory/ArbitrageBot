using Microsoft.Extensions.Logging;
using Nethereum.Web3;

namespace ArbitrageBot.Infrastructure.Dex;

// UniswapV2Dex is a thin subclass — all logic lives in BaseUniswapV2Dex.
// The only things specific to Uniswap V2 are its name and contract addresses,
// which are injected at registration time in Program.cs.
public sealed class UniswapV2Dex : BaseUniswapV2Dex
{
    public override string Name => "UniswapV2";

    public UniswapV2Dex(
        IWeb3 web3,
        string routerAddress,
        string factoryAddress,
        ILogger<UniswapV2Dex> logger)
        : base(web3, routerAddress, factoryAddress, logger)
    {
    }
}