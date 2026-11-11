using Curto.Api.Models;
using Curto.Api.Storage;

namespace Curto.Api.Services;

/// <summary>Why a link could not be created.</summary>
internal enum CreateFailure
{
    /// <summary>The link was created.</summary>
    None,

    /// <summary>The requested custom slug already exists.</summary>
    SlugTaken,

    /// <summary>Every randomly generated slug collided; the slug space is nearly full.</summary>
    SlugSpaceExhausted,
}

/// <summary>Result of <see cref="LinkService.CreateAsync"/>.</summary>
/// <param name="Link">The created link, or null on failure.</param>
/// <param name="Failure">Reason for the failure, or <see cref="CreateFailure.None"/>.</param>
internal readonly record struct CreateResult(Link? Link, CreateFailure Failure);

/// <summary>Link use cases on top of the repository: slug allocation, creation, lookup, listing, patching, deletion.</summary>
/// <param name="links">Persistence.</param>
/// <param name="slugs">Slug source.</param>
/// <param name="time">Clock.</param>
internal sealed class LinkService(LinkRepository links, ISlugGenerator slugs, TimeProvider time)
{
    private const int MaxGeneratedSlugAttempts = 8;

    /// <summary>Creates a link with the requested slug, or with a fresh random one (retrying on collisions).</summary>
    /// <param name="request">Validated request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<CreateResult> CreateAsync(NewLink request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Stored with one-second precision; truncate so the response equals what a later GET returns.
        var createdAt = DateTimeOffset.FromUnixTimeSeconds(time.GetUtcNow().ToUnixTimeSeconds());

        if (request.Slug is { } custom)
        {
            var link = Build(custom, request, createdAt);
            return await links.TryInsertAsync(link, cancellationToken)
                ? new CreateResult(link, CreateFailure.None)
                : new CreateResult(null, CreateFailure.SlugTaken);
        }

        for (var attempt = 0; attempt < MaxGeneratedSlugAttempts; attempt++)
        {
            var slug = slugs.Next();
            if (SlugRules.IsReserved(slug))
            {
                continue;
            }

            var link = Build(slug, request, createdAt);
            if (await links.TryInsertAsync(link, cancellationToken))
            {
                return new CreateResult(link, CreateFailure.None);
            }
        }

        return new CreateResult(null, CreateFailure.SlugSpaceExhausted);
    }

    /// <summary>Loads a link.</summary>
    /// <param name="slug">Case-sensitive slug.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task<Link?> GetAsync(string slug, CancellationToken cancellationToken) => links.FindAsync(slug, cancellationToken);

    /// <summary>Lists links.</summary>
    /// <param name="query">Validated query.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task<(IReadOnlyList<Link> Items, int Total)> ListAsync(LinkQuery query, CancellationToken cancellationToken) =>
        links.ListAsync(query, time.GetUtcNow(), cancellationToken);

    /// <summary>Applies a validated patch.</summary>
    /// <param name="slug">Link to change.</param>
    /// <param name="changes">Validated patch.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task<Link?> UpdateAsync(string slug, LinkChanges changes, CancellationToken cancellationToken) =>
        links.UpdateAsync(slug, changes, cancellationToken);

    /// <summary>Deletes a link together with its analytics.</summary>
    /// <param name="slug">Link to delete.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task<bool> DeleteAsync(string slug, CancellationToken cancellationToken) => links.DeleteAsync(slug, cancellationToken);

    private static Link Build(string slug, NewLink request, DateTimeOffset createdAt) => new(
        slug,
        request.Url,
        request.Title,
        request.Tags,
        createdAt,
        request.ExpiresAt,
        request.MaxClicks,
        Clicks: 0);
}
