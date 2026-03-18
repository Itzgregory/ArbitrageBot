using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArbitrageBot.Domain.Models;

namespace ArbitrageBot.Domain.Interfaces;

public interface IStorageProvider
{
    Task SaveOpportunityAsync(
        ArbitrageOpportunity opportunity,
        CancellationToken cancellationToken = default);

    Task<ArbitrageOpportunity?> GetOpportunityAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task<IEnumerable<ArbitrageOpportunity>> GetOpportunitiesAsync(
        DateTime from,
        DateTime to,
        OpportunityStatus? status = null,
        CancellationToken cancellationToken = default);

    Task SaveExecutionResultAsync(
        ExecutionResult result,
        CancellationToken cancellationToken = default);

    Task<ExecutionResult?> GetExecutionResultAsync(
        string opportunityId,
        CancellationToken cancellationToken = default);

    Task<IEnumerable<ExecutionResult>> GetExecutionResultsAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default);

    Task<int> GetSchemaVersionAsync(
        CancellationToken cancellationToken = default);
}