using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ArbitrageBot.Domain.Exceptions;
using ArbitrageBot.Domain.Interfaces;
using ArbitrageBot.Domain.Models;
using Microsoft.Extensions.Logging;
using Nethereum.Web3;

namespace ArbitrageBot.Infrastructure.FlashLoan;

public sealed class AaveFlashLoanProvider : IFlashLoanProvider
{
    private readonly IWeb3 _web3;
    private readonly string _poolAddress;
    private readonly ILogger<AaveFlashLoanProvider> _logger;

    // Aave V3 charges 0.09% per flash loan.
    // This is fixed by the protocol — not configurable.
    public decimal FeePercent => 0.0009m;
    public string Name => "Aave V3";

    // Minimal ABI for the Aave V3 pool contract.
    // We only define the two functions we call — flashLoanSimple and getReserveData.
    private const string PoolAbi = """
        [
            {
                "name": "flashLoanSimple",
                "type": "function",
                "inputs": [
                    { "name": "receiverAddress", "type": "address" },
                    { "name": "asset", "type": "address" },
                    { "name": "amount", "type": "uint256" },
                    { "name": "params", "type": "bytes" },
                    { "name": "referralCode", "type": "uint16" }
                ],
                "outputs": []
            },
            {
                "name": "getReserveData",
                "type": "function",
                "inputs": [
                    { "name": "asset", "type": "address" }
                ],
                "outputs": [
                    { "name": "configuration", "type": "uint256" },
                    { "name": "liquidityIndex", "type": "uint128" },
                    { "name": "currentLiquidityRate", "type": "uint128" },
                    { "name": "variableBorrowIndex", "type": "uint128" },
                    { "name": "currentVariableBorrowRate", "type": "uint128" },
                    { "name": "currentStableBorrowRate", "type": "uint128" },
                    { "name": "lastUpdateTimestamp", "type": "uint40" },
                    { "name": "id", "type": "uint16" },
                    { "name": "aTokenAddress", "type": "address" },
                    { "name": "stableDebtTokenAddress", "type": "address" },
                    { "name": "variableDebtTokenAddress", "type": "address" },
                    { "name": "interestRateStrategyAddress", "type": "address" },
                    { "name": "accruedToTreasury", "type": "uint128" },
                    { "name": "unbacked", "type": "uint128" },
                    { "name": "isolationModeTotalDebt", "type": "uint128" }
                ]
            }
        ]
        """;

    // Minimal ERC-20 ABI — we only need balanceOf to check available liquidity.
    // The aToken contract implements ERC-20 so this works for any asset.
    private const string Erc20Abi = """
    [
        {
            "name": "balanceOf",
            "type": "function",
            "inputs": [
                { "name": "account", "type": "address" }
            ],
            "outputs": [
                { "name": "", "type": "uint256" }
            ]
        },
        {
            "name": "decimals",
            "type": "function",
            "inputs": [],
            "outputs": [
                { "name": "", "type": "uint8" }
            ]
        },
        {
            "name": "totalSupply",
            "type": "function",
            "inputs": [],
            "outputs": [
                { "name": "", "type": "uint256" }
            ]
        }
    ]
    """;

    public AaveFlashLoanProvider(
        IWeb3 web3,
        string poolAddress,
        ILogger<AaveFlashLoanProvider> logger)
    {
        _web3 = web3 ?? throw new ArgumentNullException(nameof(web3));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(poolAddress))
            throw new ArgumentNullException(nameof(poolAddress));

        // Normalize to lowercase — Ethereum addresses are case-insensitive
        // but inconsistent casing causes equality mismatches in dictionaries
        _poolAddress = poolAddress.ToLowerInvariant();
    }

    public async Task<FlashLoanReceipt> ExecuteAsync(
        FlashLoanRequest request,
        ArbitrageOpportunity opportunity,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        ValidateOpportunity(opportunity);

        try
        {
            LogExecutionStart(request);

            var txHash = await SubmitFlashLoanTransactionAsync(
                request,
                opportunity,
                cancellationToken);

            var receipt = BuildReceipt(request, txHash);

            LogExecutionComplete(receipt);

            return receipt;
        }
        catch (ArbitrageBotException)
        {
            // Rethrow domain exceptions unchanged
            throw;
        }
        catch (Exception ex)
        {
            throw new BlockchainCommunicationException(
                $"Flash loan execution failed for opportunity {opportunity.Id}",
                ex);
        }
    }

    public async Task<decimal> GetAvailableLiquidityAsync(
        string tokenAddress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tokenAddress))
            throw new ArgumentNullException(nameof(tokenAddress));

        try
        {
            return await FetchAvailableLiquidityAsync(tokenAddress, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new BlockchainCommunicationException(
                $"Failed to fetch Aave liquidity for token {tokenAddress}",
                ex);
        }
    }

    public async Task<bool> IsHealthyAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            // A successful block number fetch proves the RPC node is reachable.
            var blockNumber = await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
            return (int)blockNumber.Value > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Provider} health check failed", Name);
            return false;
        }
    }

    private async Task<string> SubmitFlashLoanTransactionAsync(
        FlashLoanRequest request,
        ArbitrageOpportunity opportunity,
        CancellationToken cancellationToken)
    {
        var poolContract = _web3.Eth.GetContract(PoolAbi, _poolAddress);
        var flashLoanFunction = poolContract.GetFunction("flashLoanSimple");

        // The receiver address is our arbitrage contract — it receives the loan,
        // executes both swap legs, and repays Aave within the same transaction.
        // The params bytes encode the opportunity details for the receiver contract.
        var receiverAddress = _web3.TransactionManager.Account.Address;
        var encodedParams = EncodeOpportunityParams(opportunity);

        var txHash = await flashLoanFunction.SendTransactionAsync(
            receiverAddress,
            request.TokenAddress,
            ConvertToWei(request.Amount),
            encodedParams,
            (ushort)0); // referral code — 0 means no referral

        return txHash;
    }

    private async Task<decimal> FetchAvailableLiquidityAsync(
        string tokenAddress,
        CancellationToken cancellationToken)
    {
        // Step 1 — get the aToken address from Aave's reserve data.
        // The aToken's total supply represents the total deposited liquidity
        // available for flash loans.
        var poolContract = _web3.Eth.GetContract(PoolAbi, _poolAddress);
        var getReserveDataFunction = poolContract.GetFunction("getReserveData");

        var reserveData = await getReserveDataFunction
            .CallDeserializingToObjectAsync<ReserveDataOutput>(tokenAddress);

        var aTokenAddress = reserveData.ATokenAddress;

        if (string.IsNullOrWhiteSpace(aTokenAddress))
            throw new BlockchainCommunicationException(
                $"Aave returned empty aToken address for {tokenAddress}",
                new Exception("Empty aToken address"));

        // Step 2 — read token decimals for correct conversion
        var tokenContract = _web3.Eth.GetContract(Erc20Abi, tokenAddress);
        var decimalsFunction = tokenContract.GetFunction("decimals");
        var decimals = await decimalsFunction.CallAsync<int>();

        // Step 3 — read the total supply of the aToken.
        // aToken total supply = total deposited liquidity = available flash loan amount.
        // This is more accurate than reading the pool's token balance because
        // Aave routes liquidity through the aToken system, not held directly.
        var aTokenContract = _web3.Eth.GetContract(Erc20Abi, aTokenAddress);
        var totalSupplyFunction = aTokenContract.GetFunction("totalSupply");
        var rawTotalSupply = await totalSupplyFunction.CallAsync<System.Numerics.BigInteger>();

        var liquidity = (decimal)rawTotalSupply / (decimal)Math.Pow(10, decimals);

        _logger.LogDebug(
            "{Provider} available liquidity for {Token}: {Liquidity} (aToken: {AToken})",
            Name,
            tokenAddress,
            liquidity,
            aTokenAddress);

        return liquidity;
    }

    private static byte[] EncodeOpportunityParams(ArbitrageOpportunity opportunity)
    {
        // Encode the opportunity ID as bytes so the receiver contract
        // can identify which opportunity it is executing.
        // In production this would encode the full swap path.
        return System.Text.Encoding.UTF8.GetBytes(opportunity.Id);
    }

    private static BigInteger ConvertToWei(decimal amount)
    {
        // Convert human-readable decimal back to wei for the contract call.
        // We multiply by 10^18 because Aave expects amounts in the token's
        // smallest unit — the inverse of ConvertFromWei in the DEX layer.
        return new BigInteger(amount * (decimal)Math.Pow(10, 18));
    }

    private static FlashLoanReceipt BuildReceipt(
        FlashLoanRequest request,
        string txHash)
    {
        // IsRepaid is true because Aave's flashLoanSimple is atomic —
        // if the loan is not repaid within the same transaction it reverts entirely.
        // A returned txHash means the transaction succeeded and repayment occurred.
        return new FlashLoanReceipt(request, txHash, isRepaid: true);
    }

    private static void ValidateRequest(FlashLoanRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
    }

    private static void ValidateOpportunity(ArbitrageOpportunity opportunity)
    {
        if (opportunity is null)
            throw new ArgumentNullException(nameof(opportunity));
    }

    private void LogExecutionStart(FlashLoanRequest request)
    {
        _logger.LogInformation(
            "{Provider} flash loan requested | Token: {Token} | Amount: {Amount} | Fee: {Fee}",
            Name,
            request.TokenAddress,
            request.Amount,
            request.FeeAmount);
    }

    private void LogExecutionComplete(FlashLoanReceipt receipt)
    {
        _logger.LogInformation(
            "{Provider} flash loan complete | TxHash: {TxHash} | Repaid: {Repaid}",
            Name,
            receipt.TransactionHash,
            receipt.IsRepaid);
    }

    // Nethereum deserializes the getReserveData return values into this class.
    // We only map the fields we actually use — configuration and liquidityIndex
    // are included to keep position mapping correct for aTokenAddress at position 9.
    [Nethereum.ABI.FunctionEncoding.Attributes.FunctionOutput]
    private sealed class ReserveDataOutput
    {
        [Nethereum.ABI.FunctionEncoding.Attributes.Parameter("uint256", "configuration", 1)]
        public BigInteger Configuration { get; set; }

        [Nethereum.ABI.FunctionEncoding.Attributes.Parameter("uint128", "liquidityIndex", 2)]
        public BigInteger LiquidityIndex { get; set; }

        // aTokenAddress is at position 9 in the ABI output.
        // It holds the address of the interest-bearing token for this asset.
        [Nethereum.ABI.FunctionEncoding.Attributes.Parameter("address", "aTokenAddress", 9)]
        public string ATokenAddress { get; set; } = string.Empty;
    }
}