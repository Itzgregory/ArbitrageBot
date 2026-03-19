using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArbitrageBot.Domain.Interfaces;
using ArbitrageBot.Domain.Models;
using Microsoft.Extensions.Logging;

namespace ArbitrageBot.Infrastructure.Storage;

public sealed class FileStorageProvider : IStorageProvider
{
    private readonly string _storagePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly ILogger<FileStorageProvider> _logger;
    private const int CurrentSchemaVersion = 2;

    private sealed class StorageFile
    {
        public int SchemaVersion { get; set; }
        public Dictionary<string, ArbitrageOpportunity> Opportunities { get; set; } = new();
        public Dictionary<string, ExecutionResult> ExecutionResults { get; set; } = new();
        public DateTime LastSaved { get; set; }
    }

    public FileStorageProvider(
        string storagePath,
        ILogger<FileStorageProvider> logger)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            throw new ArgumentNullException(nameof(storagePath));

        _storagePath = storagePath;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        EnsureStorageDirectoryExists();
    }

    public async Task SaveOpportunityAsync(
        ArbitrageOpportunity opportunity,
        CancellationToken cancellationToken = default)
    {
        if (opportunity is null)
            throw new ArgumentNullException(nameof(opportunity));

        await WithLockAsync(async file =>
        {
            file.Opportunities[opportunity.Id] = opportunity;
            await WriteStorageFileAsync(file);
            LogSaved("opportunity", opportunity.Id, opportunity.Status.ToString(), opportunity.Version.ToString());
        }, cancellationToken);
    }

    public async Task<ArbitrageOpportunity?> GetOpportunityAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);

        return await WithLockAsync(file =>
        {
            file.Opportunities.TryGetValue(id, out var opportunity);
            return Task.FromResult(opportunity);
        }, cancellationToken);
    }

    public async Task<IEnumerable<ArbitrageOpportunity>> GetOpportunitiesAsync(
        DateTime from,
        DateTime to,
        OpportunityStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        return await WithLockAsync(file =>
        {
            var results = FilterByDateRange(file.Opportunities.Values, from, to, status);
            return Task.FromResult(results);
        }, cancellationToken);
    }

    public async Task SaveExecutionResultAsync(
        ExecutionResult result,
        CancellationToken cancellationToken = default)
    {
        if (result is null)
            throw new ArgumentNullException(nameof(result));

        await WithLockAsync(async file =>
        {
            file.ExecutionResults[result.OpportunityId] = result;
            await WriteStorageFileAsync(file);
            LogSaved("execution result", result.OpportunityId, result.Success.ToString(), null);
        }, cancellationToken);
    }

    public async Task<ExecutionResult?> GetExecutionResultAsync(
        string opportunityId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(opportunityId);

        return await WithLockAsync(file =>
        {
            file.ExecutionResults.TryGetValue(opportunityId, out var result);
            return Task.FromResult(result);
        }, cancellationToken);
    }

    public async Task<IEnumerable<ExecutionResult>> GetExecutionResultsAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default)
    {
        return await WithLockAsync(file =>
        {
            var results = FilterByDateRange(file.ExecutionResults.Values, from, to);
            return Task.FromResult(results);
        }, cancellationToken);
    }

    public async Task<int> GetSchemaVersionAsync(
        CancellationToken cancellationToken = default)
    {
        return await WithLockAsync(file =>
            Task.FromResult(file.SchemaVersion), cancellationToken);
    }

    private async Task WithLockAsync(
        Func<StorageFile, Task> action,
        CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var file = await ReadStorageFileAsync();
            await action(file);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<T> WithLockAsync<T>(
        Func<StorageFile, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var file = await ReadStorageFileAsync();
            return await action(file);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<StorageFile> ReadStorageFileAsync()
    {
        var filePath = GetStorageFilePath();

        if (!File.Exists(filePath))
            return CreateFreshStorageFile();

        var json = await File.ReadAllTextAsync(filePath);
        return DeserializeStorageFile(json) ?? CreateFreshStorageFile();
    }

    private async Task WriteStorageFileAsync(StorageFile file)
    {
        file.LastSaved = DateTime.UtcNow;
        file.SchemaVersion = CurrentSchemaVersion;

        var json = SerializeStorageFile(file);
        await AtomicWriteAsync(json);
    }

    private async Task AtomicWriteAsync(string json)
    {
        var finalPath = GetStorageFilePath();
        var tempPath = GetTempFilePath();

        try
        {
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write storage file");
            DeleteTempFileIfExists(tempPath);
            throw;
        }
    }

    private static IEnumerable<ArbitrageOpportunity> FilterByDateRange(
        IEnumerable<ArbitrageOpportunity> opportunities,
        DateTime from,
        DateTime to,
        OpportunityStatus? status)
    {
        return opportunities
            .Where(o => o.FetchedAt >= from && o.FetchedAt <= to)
            .Where(o => status is null || o.Status == status)
            .OrderByDescending(o => o.FetchedAt);
    }

    private static IEnumerable<ExecutionResult> FilterByDateRange(
        IEnumerable<ExecutionResult> results,
        DateTime from,
        DateTime to)
    {
        return results
            .Where(r => r.FetchedAt >= from && r.FetchedAt <= to)
            .OrderByDescending(r => r.FetchedAt);
    }

    private void LogSaved(string entityType, string id, string status, string? version)
    {
        _logger.LogDebug(
            "Saved {EntityType} {Id} | Status: {Status} | Version: {Version}",
            entityType,
            id,
            status,
            version ?? "N/A");
    }

    private void EnsureStorageDirectoryExists()
    {
        if (!Directory.Exists(_storagePath))
        {
            Directory.CreateDirectory(_storagePath);
            _logger.LogInformation(
                "Created storage directory at {Path}",
                _storagePath);
        }
    }

    private static StorageFile CreateFreshStorageFile()
        => new()
        {
            SchemaVersion = CurrentSchemaVersion,
            LastSaved = DateTime.UtcNow
        };

    private static StorageFile? DeserializeStorageFile(string json)
        => JsonSerializer.Deserialize<StorageFile>(json);

    private static string SerializeStorageFile(StorageFile file)
        => JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });

    private static void DeleteTempFileIfExists(string tempPath)
    {
        if (File.Exists(tempPath))
            File.Delete(tempPath);
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentNullException(nameof(id));
    }

    private string GetStorageFilePath()
        => Path.Combine(_storagePath, "storage.json");

    private string GetTempFilePath()
        => Path.Combine(_storagePath, $"temp_{Guid.NewGuid()}.json");
}