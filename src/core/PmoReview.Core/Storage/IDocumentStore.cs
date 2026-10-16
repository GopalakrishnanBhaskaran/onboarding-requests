using PmoReview.Core.Domain;

namespace PmoReview.Core.Storage;

/// <summary>A document plus the store's concurrency token.</summary>
public sealed record Versioned<T>(T Value, string ETag);

/// <summary>Id or unique key already exists in the partition.</summary>
public sealed class DocumentConflictException(string message) : Exception(message);

/// <summary>ETag did not match (optimistic concurrency).</summary>
public sealed class ConcurrencyException(string message) : Exception(message);

/// <summary>
/// One container. Every operation is partition-scoped, so a caller can only reach
/// data in a partition it has already been authorised for.
/// </summary>
public interface IDocumentStore<T> where T : class, IDocument
{
    Task<Versioned<T>?> GetAsync(string partitionKey, string id, CancellationToken ct = default);

    /// <summary>Whole partition. Fine at pilot scale; filter in memory.</summary>
    Task<IReadOnlyList<Versioned<T>>> ListAsync(string partitionKey, CancellationToken ct = default);

    /// <exception cref="DocumentConflictException">Id or unique key exists.</exception>
    Task<Versioned<T>> CreateAsync(T document, CancellationToken ct = default);

    /// <exception cref="ConcurrencyException">ETag mismatch or document missing.</exception>
    Task<Versioned<T>> ReplaceAsync(T document, string ifMatchETag, CancellationToken ct = default);
}
