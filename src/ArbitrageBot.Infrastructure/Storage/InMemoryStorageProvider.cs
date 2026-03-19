using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArbitrageBot.Domain.Interfaces;
using ArbitrageBot.Domain.Models;
using Microsoft.Extensions.Logging;

namespace ArbitrageBot.Infrastructure.Storage;

public sealed class InMemoryStorageProvider : IStorageProvider
{
    private readonly ConcurrentDictionary<string, ArbitrageOpportunity> _opportunities = new();
    private readonly ConcurrentDictionary<string, ExecutionResult> _executionResults = new();
    private readonly ILogger<InMemoryStorageProvider> _logger;
    private const int SchemaVersion = 1;

    public InMemoryStorageProvider(ILogger<InMemoryStorageProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task SaveOpportunityAsync(
        ArbitrageOpportunity opportunity,
        CancellationToken cancellationToken = default)
    {
        ValidateNotNull(opportunity, nameof(opportunity));
        _opportunities[opportunity.Id] = opportunity;
        LogSaved("opportunity", opportunity.Id, opportunity.Status.ToString(), opportunity.Version.ToString());
        return Task.CompletedTask;
    }

    public Task<ArbitrageOpportunity?> GetOpportunityAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        _opportunities.TryGetValue(id, out var opportunity);
        return Task.FromResult(opportunity);
    }

    public Task<IEnumerable<ArbitrageOpportunity>> GetOpportunitiesAsync(
        DateTime from,
        DateTime to,
        OpportunityStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var results = FilterByDateRange(_opportunities.Values, from, to, status);
        return Task.FromResult(results);
    }

    public Task SaveExecutionResultAsync(
        ExecutionResult result,
        CancellationToken cancellationToken = default)
    {
        ValidateNotNull(result, nameof(result));
        _executionResults[result.OpportunityId] = result;
        LogSaved("execution result", result.OpportunityId, result.Success.ToString(), null);
        return Task.CompletedTask;
    }

    public Task<ExecutionResult?> GetExecutionResultAsync(
        string opportunityId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(opportunityId);
        _executionResults.TryGetValue(opportunityId, out var result);
        return Task.FromResult(result);
    }

    public Task<IEnumerable<ExecutionResult>> GetExecutionResultsAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default)
    {
        var results = FilterByDateRange(_executionResults.Values, from, to);
        return Task.FromResult(results);
    }

    public Task<int> GetSchemaVersionAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(SchemaVersion);
    }

    private static void ValidateNotNull<T>(T value, string paramName) where T : class
    {
        if (value is null)
            throw new ArgumentNullException(paramName);
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentNullException(nameof(id));
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
}