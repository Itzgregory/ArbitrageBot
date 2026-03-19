using ArbitrageBot.Api.Middleware;
using ArbitrageBot.Api.Workers;
using ArbitrageBot.Application.Configuration;
using ArbitrageBot.Application.Services;
using ArbitrageBot.Domain.Interfaces;
using ArbitrageBot.Domain.Models;
using ArbitrageBot.Infrastructure.Dex;
using ArbitrageBot.Infrastructure.FlashLoan;
using ArbitrageBot.Infrastructure.Storage;
using Nethereum.Web3;
using Serilog;
using Serilog.Events;

if (File.Exists(".env"))
    DotNetEnv.Env.Load();

// Bootstrap logger — starts immediately before DI is available.
// Captures startup failures that would otherwise be silent.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "logs/arbitragebot-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate:
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {CorrelationId} {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting ArbitrageBot");

    var builder = WebApplication.CreateBuilder(args);

    // Replace default logging with Serilog.
    // ReadFrom.Configuration allows appsettings.json to override log levels.
    // ReadFrom.Services allows Serilog sinks to use injected services.
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            path: "logs/arbitragebot-.log",
            rollingInterval: RollingInterval.Day,
            outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {CorrelationId} {Message:lj}{NewLine}{Exception}"));

    // -------------------------
    // Configuration
    // -------------------------
    builder.Services.Configure<ScannerOptions>(
        builder.Configuration.GetSection(ScannerOptions.SectionName));

   // -------------------------
   // Blockchain
   // -------------------------
    builder.Services.Configure<BlockchainOptions>(
        builder.Configuration.GetSection(BlockchainOptions.SectionName));

    var blockchainOptions = builder.Configuration
        .GetSection(BlockchainOptions.SectionName)
        .Get<BlockchainOptions>()
        ?? throw new InvalidOperationException("Blockchain configuration is required");

    if (!blockchainOptions.IsConfigured)
        throw new InvalidOperationException(
            "Blockchain:RpcUrl and Blockchain:PrivateKey are required. " +
            "Set them in your .env file.");

    // Register Web3 with the account so all blockchain calls are authenticated.
    // The account is derived from the private key — it pays gas fees on execution.
    // SingletonS — one RPC connection shared across all services.
    builder.Services.AddSingleton<IWeb3>(_ =>
        new Web3(
            new Nethereum.Web3.Accounts.Account(blockchainOptions.PrivateKey),
            blockchainOptions.RpcUrl));
    // -------------------------
    // DEX implementations
    // -------------------------
    // Each DEX is registered with its mainnet contract addresses.
    // Addresses sourced from official Uniswap and SushiSwap documentation.
    builder.Services.AddSingleton<IDex>(sp => new UniswapV2Dex(
        sp.GetRequiredService<IWeb3>(),
        routerAddress: "0x7a250d5630b4cf539739df2c5dacb4c659f2488d",
        factoryAddress: "0x5c69bee701ef814a2b6a3edd4b1652cb9cc5aa6f",
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UniswapV2Dex>>()));

    builder.Services.AddSingleton<IDex>(sp => new SushiSwapDex(
        sp.GetRequiredService<IWeb3>(),
        routerAddress: "0xd9e1ce17f2641f24ae83637ab66a2cca9c378b9f",
        factoryAddress: "0xc0aee478e3658e2610c5f7a4a2e1777ce9e4f2ac",
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SushiSwapDex>>()));

    // -------------------------
    // Flash loan providers
    // -------------------------
    builder.Services.AddSingleton<IFlashLoanProvider>(sp => new AaveFlashLoanProvider(
        sp.GetRequiredService<IWeb3>(),
        // Aave V3 pool proxy address on Ethereum mainnet
        poolAddress: "0x87870bca3f3fd6335c3f4ce8392d69350b4fa4e2",
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AaveFlashLoanProvider>>()));

    // -------------------------
    // Storage
    // -------------------------
    // Switch between InMemory and File storage via appsettings.json.
    // InMemory is default — suitable for development and testing.
    // File storage persists across restarts — use for production.
    var storageProvider = builder.Configuration["Storage:Provider"] ?? "InMemory";

    if (storageProvider == "File")
    {
        var storagePath = builder.Configuration["Storage:Path"]
            ?? throw new InvalidOperationException("Storage:Path is required when Storage:Provider is File");

        builder.Services.AddSingleton<IStorageProvider>(sp =>
            new FileStorageProvider(
                storagePath,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileStorageProvider>>()));
    }
    else
    {
        builder.Services.AddSingleton<IStorageProvider, InMemoryStorageProvider>();
    }

    // -------------------------
    // Monitored token pairs
    // -------------------------
    // These are the pairs the scanner watches on every cycle.
    // ETH/USDC and WBTC/ETH are the highest liquidity pairs on Ethereum mainnet.
    builder.Services.AddSingleton<IEnumerable<TokenPair>>(_ => new[]
    {
        // ETH/USDC — 18 decimals for WETH, 6 for USDC
        new TokenPair(
            tokenA: "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2", // WETH
            tokenB: "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48", // USDC
            decimalsA: 18,
            decimalsB: 6,
            poolAddress: "0xb4e16d0168e52d35cacd2c6185b44281ec28c9dc"),

        // WBTC/ETH — 8 decimals for WBTC, 18 for WETH
        new TokenPair(
            tokenA: "0x2260fac5e5542a773aa44fbcfedf7c193bc2c599", // WBTC
            tokenB: "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2", // WETH
            decimalsA: 8,
            decimalsB: 18,
            poolAddress: "0xbb2b8038a1640196fbe3e38816f3e67cba72d940")
    });

    // -------------------------
    // Application services
    // -------------------------
    builder.Services.AddSingleton<ScannerService>();
    builder.Services.AddSingleton<AnalyzerService>();
    builder.Services.AddSingleton<ExecutorService>();

    // -------------------------
    // Background worker
    // -------------------------
    // Registers the scan/analyze/execute loop as a hosted service.
    // Starts automatically when the host starts.
    builder.Services.AddHostedService<ArbitrageBotWorker>();

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();

    var app = builder.Build();

    // Middleware order matters — CorrelationId must come before
    // ExceptionHandling so every error response includes a correlation ID
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseMiddleware<ExceptionHandlingMiddleware>();

    app.UseHttpsRedirection();
    app.UseAuthorization();
    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "ArbitrageBot terminated unexpectedly");
}
finally
{
    // Flush all buffered log entries before the process exits.
    // Without this the last few log lines before a crash are lost.
    Log.CloseAndFlush();
}