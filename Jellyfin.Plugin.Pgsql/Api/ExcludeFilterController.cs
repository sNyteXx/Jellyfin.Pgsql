using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Pgsql.Api.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.Pgsql.Api;

/// <summary>
/// Provides library-item queries with optional server-side tag and genre exclusion,
/// executed entirely in PostgreSQL.
/// </summary>
[ApiController]
[Route("Items/Filtered")]
[Produces("application/json")]
public class ExcludeFilterController : ControllerBase
{
    private const int MaxPageSize = 1000;

    private readonly IDbContextFactory<JellyfinDbContext> _dbContextFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExcludeFilterController"/> class.
    /// </summary>
    /// <param name="dbContextFactory">The database context factory.</param>
    public ExcludeFilterController(IDbContextFactory<JellyfinDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Returns a paginated list of library items with optional server-side tag and genre exclusions.
    /// All filtering is performed in PostgreSQL before results are sent to the client.
    /// </summary>
    /// <param name="parentId">
    /// Optional. When supplied, only items that are descendants of this folder/library are returned.
    /// </param>
    /// <param name="excludeTags">
    /// Optional. One or more tag values to exclude.
    /// Items that carry <em>any</em> of the listed tags (including inherited tags) are omitted.
    /// Example: <c>excludeTags=bloody&amp;excludeTags=violence</c>.
    /// </param>
    /// <param name="excludeGenres">
    /// Optional. One or more genre values to exclude.
    /// Items that belong to <em>any</em> of the listed genres are omitted.
    /// Example: <c>excludeGenres=Horror</c>.
    /// </param>
    /// <param name="startIndex">Optional. Zero-based index of the first record to return. Defaults to 0.</param>
    /// <param name="limit">Optional. Maximum number of records to return. Defaults to 100, maximum 1000.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="FilteredItemsResult"/> containing the matching items and total count.</returns>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(FilteredItemsResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<FilteredItemsResult>> GetFilteredItemsAsync(
        [FromQuery] Guid? parentId = null,
        [FromQuery] string[]? excludeTags = null,
        [FromQuery] string[]? excludeGenres = null,
        [FromQuery] int? startIndex = null,
        [FromQuery] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var context = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var query = context.BaseItems
                .AsNoTracking()
                .Where(item => !item.IsVirtualItem);

            // --- Parent filter (recursive descendant lookup) ---
            if (parentId.HasValue)
            {
                var pid = parentId.Value;
                query = query.Where(item =>
                    context.AncestorIds.Any(a => a.ParentItemId == pid && a.ItemId == item.Id));
            }

            // --- Tag exclusion (server-side NOT EXISTS in PostgreSQL) ---
            // Matches against both Tags (type 4) and InheritedTags (type 6).
            if (excludeTags is { Length: > 0 })
            {
                var cleanTags = excludeTags
                    .Select(t => t.Trim().ToLowerInvariant())
                    .ToArray();

                query = query.Where(item => !context.ItemValuesMap.Any(ivm =>
                    ivm.ItemId == item.Id
                    && (ivm.ItemValue.Type == ItemValueType.Tags
                        || ivm.ItemValue.Type == ItemValueType.InheritedTags)
                    && cleanTags.Contains(ivm.ItemValue.CleanValue)));
            }

            // --- Genre exclusion (server-side NOT EXISTS in PostgreSQL) ---
            if (excludeGenres is { Length: > 0 })
            {
                var cleanGenres = excludeGenres
                    .Select(g => g.Trim().ToLowerInvariant())
                    .ToArray();

                query = query.Where(item => !context.ItemValuesMap.Any(ivm =>
                    ivm.ItemId == item.Id
                    && ivm.ItemValue.Type == ItemValueType.Genre
                    && cleanGenres.Contains(ivm.ItemValue.CleanValue)));
            }

            // Total count (before pagination)
            var totalCount = await query
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);

            // Fetch paginated entities and eagerly load their ItemValues for genre/tag mapping
            var resolvedStartIndex = Math.Max(startIndex ?? 0, 0);
            var resolvedLimit = Math.Min(limit ?? 100, MaxPageSize);

            var entities = await query
                .Include(item => item.ItemValues!)
                    .ThenInclude(ivm => ivm.ItemValue)
                .OrderBy(item => item.SortName)
                .ThenBy(item => item.Name)
                .Skip(resolvedStartIndex)
                .Take(resolvedLimit)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Map to DTOs (in memory, after SQL fetch)
            var items = entities
                .Select(entity => new FilteredItemDto
                {
                    Id = entity.Id,
                    Name = entity.Name,
                    Type = entity.Type,
                    IsFolder = entity.IsFolder,
                    Overview = entity.Overview,
                    OfficialRating = entity.OfficialRating,
                    CommunityRating = entity.CommunityRating,
                    ProductionYear = entity.ProductionYear,
                    ParentId = entity.ParentId,
                    Genres = (entity.ItemValues ?? [])
                        .Where(ivm => ivm.ItemValue.Type == ItemValueType.Genre)
                        .Select(ivm => ivm.ItemValue.Value)
                        .ToList(),
                    Tags = (entity.ItemValues ?? [])
                        .Where(ivm => ivm.ItemValue.Type == ItemValueType.Tags)
                        .Select(ivm => ivm.ItemValue.Value)
                        .ToList()
                })
                .ToArray();

            return Ok(new FilteredItemsResult
            {
                TotalRecordCount = totalCount,
                StartIndex = resolvedStartIndex,
                Items = items
            });
        }
    }
}
