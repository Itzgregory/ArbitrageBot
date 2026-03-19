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

// Load .env file in development.
// In production, variables are set directly in the host environment.
// .env is never committed to git — see .gitignore.
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

    builder.Services.Configure<DexOptions>(
        builder.Configuration.GetSection(DexOptions.SectionName));

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
    // Singleton — one RPC connection shared across all services.
    builder.Services.AddSingleton<IWeb3>(_ =>
        new Web3(
            new Nethereum.Web3.Accounts.Account(blockchainOptions.PrivateKey),
            blockchainOptions.RpcUrl));

    // -------------------------
    // DEX and flash loan configuration
    // -------------------------
    var dexOptions = builder.Configuration
        .GetSection(DexOptions.SectionName)
        .Get<DexOptions>()
        ?? throw new InvalidOperationException("Dexes configuration is required");

    if (!dexOptions.UniswapV2.IsConfigured)
        throw new InvalidOperationException(
            "Dexes:UniswapV2 RouterAddress and FactoryAddress are required. " +
            "Set DEXES__UNISWAPV2__ROUTERADDRESS and DEXES__UNISWAPV2__FACTORYADDRESS in your .env file.");

    if (!dexOptions.SushiSwap.IsConfigured)
        throw new InvalidOperationException(
            "Dexes:SushiSwap RouterAddress and FactoryAddress are required. " +
            "Set DEXES__SUSHISWAP__ROUTERADDRESS and DEXES__SUSHISWAP__FACTORYADDRESS in your .env file.");

    if (string.IsNullOrWhiteSpace(dexOptions.Aave.PoolAddress))
        throw new InvalidOperationException(
            "Dexes:Aave PoolAddress is required. " +
            "Set DEXES__AAVE__POOLADDRESS in your .env file.");

    // -------------------------
    // DEX implementations
    // -------------------------
    // Addresses come from .env — no hardcoded values in code.
    // On testnet, set different addresses in .env without touching code.
    builder.Services.AddSingleton<IDex>(sp => new UniswapV2Dex(
        sp.GetRequiredService<IWeb3>(),
        routerAddress: dexOptions.UniswapV2.RouterAddress,
        factoryAddress: dexOptions.UniswapV2.FactoryAddress,
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UniswapV2Dex>>()));

    builder.Services.AddSingleton<IDex>(sp => new SushiSwapDex(
        sp.GetRequiredService<IWeb3>(),
        routerAddress: dexOptions.SushiSwap.RouterAddress,
        factoryAddress: dexOptions.SushiSwap.FactoryAddress,
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SushiSwapDex>>()));

    // -------------------------
    // Flash loan providers
    // -------------------------
    builder.Services.AddSingleton<IFlashLoanProvider>(sp => new AaveFlashLoanProvider(
        sp.GetRequiredService<IWeb3>(),
        poolAddress: dexOptions.Aave.PoolAddress,
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AaveFlashLoanProvider>>()));

    // -------------------------
    // Storage
    // -------------------------
    // Switch between InMemory and File storage via .env.
    // File storage persists across restarts — use for production.
    // InMemory is suitable for testing only.
    var storageProvider = builder.Configuration["Storage:Provider"] ?? "File";

    if (storageProvider == "File")
    {
        var storagePath = builder.Configuration["Storage:Path"]
            ?? throw new InvalidOperationException(
                "Storage:Path is required when Storage:Provider is File. " +
                "Set STORAGE__PATH in your .env file.");

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
    // Pairs are loaded from .env — add or remove pairs there without
    // touching code. Use indexed env vars: SCANNER__TOKENPAIRS__0__TOKENA etc.
    var scannerOptions = builder.Configuration
        .GetSection(ScannerOptions.SectionName)
        .Get<ScannerOptions>()
        ?? throw new InvalidOperationException("Scanner configuration is required");

    if (!scannerOptions.TokenPairs.Any())
        throw new InvalidOperationException(
            "At least one token pair must be configured. " +
            "Set SCANNER__TOKENPAIRS__0__TOKENA etc. in your .env file.");

    builder.Services.AddSingleton<IEnumerable<TokenPair>>(_ =>
        scannerOptions.TokenPairs.Select(p => new TokenPair(
            p.TokenA,
            p.TokenB,
            p.DecimalsA,
            p.DecimalsB,
            p.PoolAddress)).ToList());

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