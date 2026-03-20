# DEX Integration Guide

How to add, configure, and optimise new decentralised exchanges in ArbitrageBot.

---

## Overview

The ArbitrageBot supports any number of DEXes running in parallel. Because every DEX is registered behind the `IDex` interface, the scanner, analyzer, and executor never know or care which specific DEX they are talking to. Adding a new DEX is a configuration and implementation task — not an architectural one.

The `ScannerService` receives `IEnumerable<IDex>` via dependency injection. On every cycle it iterates every DEX and every monitored pair, building a task for each combination. With 50 DEXes and 5 pairs that is 250 concurrent tasks, controlled by `SemaphoreSlim` capped at `MaxConcurrentDexCalls`. The pair address cache means after the first cycle, each task makes exactly one RPC call (`getReserves`) instead of two.

---

## Quick start: adding a Uniswap V2 fork

The majority of DEXes on every EVM chain are forks of Uniswap V2. They share the exact same contract interface — `UniswapV2Dex` works unchanged. The only difference is the deployed contract addresses.

### Step 1 — find the contract addresses

You need two addresses:

| Address | What it does | Where to find it |
|---|---|---|
| Router address | Executes swaps. Required by the interface but not used for price reading. | DEX documentation or Etherscan |
| Factory address | Deploys and tracks pool contracts. Used by `getPair` to resolve pool addresses. | DEX documentation |

### Step 2 — add to `.env`

```bash
# Example: QuickSwap on Polygon
DEXES__QUICKSWAP__ROUTERADDRESS=0xa5e0829caced8ffdd4de3c43696c57f7d7a678ff
DEXES__QUICKSWAP__FACTORYADDRESS=0x5757371414417b8c6caad45baef941abc7d3ab32
```

### Step 3 — add to `DexOptions.cs`

```csharp
// src/ArbitrageBot.Application/Configuration/DexOptions.cs
public DexContractOptions QuickSwap { get; init; } = new();
```

### Step 4 — register in `Program.cs`

```csharp
// Validation
if (!dexOptions.QuickSwap.IsConfigured)
    throw new InvalidOperationException(
        "Dexes:QuickSwap RouterAddress and FactoryAddress are required.");

// Registration
builder.Services.AddSingleton<IDex>(sp => new UniswapV2Dex(
    sp.GetRequiredService<IWeb3>(),
    routerAddress: dexOptions.QuickSwap.RouterAddress,
    factoryAddress: dexOptions.QuickSwap.FactoryAddress,
    sp.GetRequiredService<ILogger<UniswapV2Dex>>()));
```

> If the DEX is on a different chain, it needs its own `IWeb3` instance. See [Multi-chain configuration](#multi-chain-configuration).

### Step 5 — add token pairs to `.env`

```bash
SCANNER__TOKENPAIRS__2__TOKENA=0x0d500b1d8e8ef31e21c99d1db9a6444d3adf1270
SCANNER__TOKENPAIRS__2__TOKENB=0x2791bca1f2de4661ed88a30c99a7a9449aa84174
SCANNER__TOKENPAIRS__2__DECIMALSA=18
SCANNER__TOKENPAIRS__2__DECIMALSB=6
SCANNER__TOKENPAIRS__2__POOLADDRESS=0x6e7a5fafcec6bb1e78bae2a1f0b612012bf14827
SCANNER__TOKENPAIRS__2__LABEL=WMATIC/USDC
```

That is the entire integration for a Uniswap V2 fork.

---

## Adding a custom DEX protocol

If the DEX does not use the Uniswap V2 interface, implement `IDex` directly. This applies to Uniswap V3, Curve, Balancer, and any protocol with a different contract structure.

### IDex contract

```csharp
public interface IDex
{
    string Name { get; }
    string RouterAddress { get; }

    Task<PoolReserves> GetReservesAsync(TokenPair pair, int blockNumber,
        CancellationToken cancellationToken = default);

    Task<decimal> GetSpotPriceAsync(TokenPair pair,
        CancellationToken cancellationToken = default);

    Task<decimal> GetAmountOutAsync(TokenPair pair, decimal amountIn,
        CancellationToken cancellationToken = default);

    Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
}
```

### Critical implementation rules

- **Never fetch the block number inside `GetReservesAsync`.** Accept it as a parameter. The worker fetches it once per cycle and passes it down. Independent fetches waste RPC calls and cause rate limiting.
- **Always wrap Nethereum exceptions in `BlockchainCommunicationException`.** The Application layer must never receive library-specific exceptions.
- **Always rethrow `ArbitrageBotException` before the generic catch.** Domain exceptions must not be swallowed into communication exceptions.
- **Normalize all Ethereum addresses to lowercase in the constructor.** Inconsistent casing causes dictionary lookup failures.
- **Cache any addresses you resolve from on-chain registries.** Pool addresses are immutable — fetching them every cycle wastes RPC calls.
- **Use `double` for intermediate blockchain math.** `decimal` overflows on large reserve values. Compute in `double`, return as `decimal`.

### Uniswap V3 specifics

- Pools are identified by token pair plus fee tier (0.05%, 0.3%, 1%). The factory's `getPool` takes three parameters: `tokenA`, `tokenB`, `fee`.
- Price is stored as `sqrtPriceX96`. Convert with: `price = (sqrtPriceX96 / 2^96)^2`.
- Use the Quoter contract (`0x61fFE014bA17989E743c5F6cB21bF9697530B21e` on mainnet) for accurate `getAmountOut` quotes rather than computing from reserves.
- A `UniswapV3Dex` does not inherit from `BaseUniswapV2Dex` — it implements `IDex` directly.

---

## Factors to consider for every new DEX

### Liquidity depth

Thin pools with under $100,000 TVL will have frequent price discrepancies but the optimal trade size will be too small to cover gas costs. A minimum of $1,000,000 TVL per pool is a reasonable starting threshold.

### Fee structure

The `CalculateAmountOut` formula in `BaseUniswapV2Dex` hardcodes the Uniswap V2 fee of 0.3% (`997/1000`). Many forks use different fees:

| DEX | Swap fee | Action required |
|---|---|---|
| Uniswap V2 | 0.30% | No change — default |
| SushiSwap V2 | 0.30% | No change |
| PancakeSwap V2 | 0.25% | Override with `9975/10000` |
| SpookySwap | 0.20% | Override with `998/1000` |
| BiSwap | 0.10% | Override with `999/1000` |
| QuickSwap | 0.30% | No change |

> **Using the wrong fee makes the profit calculation incorrect.** The system will either miss profitable opportunities or incorrectly flag unprofitable ones.

To override the fee, subclass `BaseUniswapV2Dex` and add a `feeNumerator` constructor parameter used in `BuildPoolReserves`.

### RPC rate limits

Every additional DEX adds RPC calls per cycle. With the pair address cache, each DEX/pair combination costs one `eth_call` per cycle after warmup:

| Setup | Calls per cycle (after warmup) | Minimum RPC capacity |
|---|---|---|
| 2 DEXes, 2 pairs | 4 | Free tier (10 req/s) |
| 10 DEXes, 5 pairs | 50 | Growth tier (25 req/s) |
| 50 DEXes, 10 pairs | 500 | Dedicated node or paid tier (100+ req/s) |

Increase `MaxConcurrentDexCalls` in `.env` proportionally with your RPC capacity.

### Block time

Set `ScanIntervalSeconds` close to the block time of the chain you are scanning:

| Chain | Block time | Recommended `ScanIntervalSeconds` |
|---|---|---|
| Ethereum mainnet | ~12s | 12 |
| Arbitrum | ~0.25s | 1–2 |
| Base | ~2s | 2 |
| Polygon | ~2s | 2 |
| Avalanche C-Chain | ~2s | 2 |
| BNB Chain | ~3s | 3 |

### Gas costs

The `EstimateGasCost` method in `AnalyzerService` returns a hardcoded estimate. Tune this per chain:

- **Ethereum mainnet** — 200,000 gas is reasonable. Gas price varies 10–200 gwei.
- **Arbitrum** — similar gas units but 0.01–0.1 gwei. Cost is 100–1000x cheaper than mainnet.
- **Polygon** — similar gas units, 30–100 gwei in MATIC. Very low USD cost.

For production, replace the hardcoded estimate with a live gas price fetch at the start of each cycle.

### Flash loan provider availability

| Provider | Fee | Chains |
|---|---|---|
| Aave V3 | 0.09% | Ethereum, Arbitrum, Polygon, Avalanche, Base, Optimism |
| Balancer | 0% | Ethereum, Arbitrum, Polygon |
| dYdX | 0% | Ethereum only |
| Uniswap V3 | 0% | Any chain with Uniswap V3 |

Register multiple providers as `IFlashLoanProvider` — the Analyzer automatically selects the cheapest one ordered by `FeePercent`.

---

## Multi-chain configuration

The current architecture uses a single `IWeb3` instance. To support multiple chains simultaneously, register one `Web3` per chain.

### Add chain-specific RPC URLs to `.env`

```bash
BLOCKCHAIN__ETHEREUM__RPCURL=https://mainnet.infura.io/v3/your_key
BLOCKCHAIN__ARBITRUM__RPCURL=https://arb-mainnet.infura.io/v3/your_key
BLOCKCHAIN__POLYGON__RPCURL=https://polygon-mainnet.infura.io/v3/your_key
```

### Register named Web3 instances in `Program.cs`

```csharp
builder.Services.AddKeyedSingleton<IWeb3>("ethereum", (sp, _) =>
    new Web3(account, config["Blockchain:Ethereum:RpcUrl"]));

builder.Services.AddKeyedSingleton<IWeb3>("arbitrum", (sp, _) =>
    new Web3(account, config["Blockchain:Arbitrum:RpcUrl"]));
```

### Inject the correct Web3 per DEX

```csharp
builder.Services.AddSingleton<IDex>(sp => new UniswapV2Dex(
    sp.GetRequiredKeyedService<IWeb3>("arbitrum"),
    routerAddress: dexOptions.UniswapArbitrum.RouterAddress,
    factoryAddress: dexOptions.UniswapArbitrum.FactoryAddress,
    sp.GetRequiredService<ILogger<UniswapV2Dex>>()));
```

The scanner, analyzer, and executor are completely unaware of which chain a DEX is on. Cross-chain price comparison works automatically.

---

## Integration checklist

Run through this before going live with any new DEX.

**Configuration**
- [ ] Router address added to `.env` and validated against official documentation
- [ ] Factory address added to `.env` and validated
- [ ] `DexOptions.cs` updated with new property
- [ ] `Program.cs` registration added with validation guard
- [ ] Token pairs added to `.env` with correct decimals
- [ ] Pool addresses verified on block explorer

**Technical validation**
- [ ] `dotnet build` passes with zero warnings
- [ ] First scan cycle resolves and caches all pair addresses successfully
- [ ] Price updates logged at correct values — cross-reference with DEX UI
- [ ] No overflow exceptions in `CalculateAmountOut` or `SpotPrice`
- [ ] Flash loan liquidity check returns non-zero values

**Economic validation**
- [ ] Swap fee confirmed and implemented correctly
- [ ] Gas estimate tuned for the target chain
- [ ] `MinimumProfitThreshold` set appropriately for the chain's gas costs
- [ ] `MaxFlashLoanEth` set to a sensible value for the pool liquidity
- [ ] RPC capacity sufficient for the number of DEX/pair combinations

**Testing**
- [ ] Unit tests pass for `CalculateAmountOut` with the new fee structure
- [ ] Integration test confirms `GetReservesAsync` returns valid data
- [ ] Run for at least 10 cycles without errors before increasing trade sizes

---

## Known DEX registry

Verified contract addresses. Always cross-reference with official documentation before use.

### Ethereum mainnet

| DEX | Router | Factory |
|---|---|---|
| Uniswap V2 | `0x7a250d5630b4cf539739df2c5dacb4c659f2488d` | `0x5c69bee701ef814a2b6a3edd4b1652cb9cc5aa6f` |
| SushiSwap V2 | `0xd9e1ce17f2641f24ae83637ab66a2cca9c378b9f` | `0xc0aee478e3658e2610c5f7a4a2e1777ce9e4f2ac` |

### Arbitrum

| DEX | Router | Factory |
|---|---|---|
| SushiSwap V2 | `0x1b02dA8Cb0d097eB8D57A175b88c7D8b47997506` | `0xc35DADB65012eC5796536bD9864eD8773aBc74C4` |
| Camelot V2 | `0xc873fEcbd354f5A56E00E710B90EF4201db2448d` | `0x6EcCab422D763aC031210895C81787E87B43A652` |

### Polygon

| DEX | Router | Factory |
|---|---|---|
| QuickSwap V2 | `0xa5E0829CaCEd8fFDD4De3c43696c57F7D7A678ff` | `0x5757371414417b8C6CAad45bAeF941aBc7d3Ab32` |
| SushiSwap V2 | `0x1b02dA8Cb0d097eB8D57A175b88c7D8b47997506` | `0xc35DADB65012eC5796536bD9864eD8773aBc74C4` |

### BNB Chain

| DEX | Router | Factory | Note |
|---|---|---|---|
| PancakeSwap V2 | `0x10ED43C718714eb63d5aA57B78B54704E256024E` | `0xcA143Ce32Fe78f1f7019d7d551a6402fC5350c73` | 0.25% fee — override required |
| BiSwap | `0x3a6d8cA21D1CF76F653A67577FA0D27453350dD8` | `0x858E3312ed3A876947EA49d572A7C42DE08af7EE` | 0.10% fee — override required |