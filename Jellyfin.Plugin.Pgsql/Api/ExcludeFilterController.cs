using System;
using System.Collections.Generic;
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
    [Authorize]
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
                var cleanTags = NormalizeValues(excludeTags);
                query = query.Where(item => !context.ItemValuesMap.Any(ivm =>
                    ivm.ItemId == item.Id
                    && (ivm.ItemValue.Type == ItemValueType.Tags
                        || ivm.ItemValue.Type == ItemValueType.InheritedTags)
                    && cleanTags.Contains(ivm.ItemValue.CleanValue)));
            }

            // --- Genre exclusion (server-side NOT EXISTS in PostgreSQL) ---
            if (excludeGenres is { Length: > 0 })
            {
                var cleanGenres = NormalizeValues(excludeGenres);
                query = query.Where(item => !context.ItemValuesMap.Any(ivm =>
                    ivm.ItemId == item.Id
                    && ivm.ItemValue.Type == ItemValueType.Genre
                    && cleanGenres.Contains(ivm.ItemValue.CleanValue)));
            }

            // Total count (before pagination)
            var totalCount = await query
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);

            // Fetch paginated item IDs ordered by sort name
            var resolvedStartIndex = Math.Max(startIndex ?? 0, 0);
            var resolvedLimit = Math.Min(limit ?? 100, MaxPageSize);

            var entities = await query
                .OrderBy(item => item.SortName)
                .ThenBy(item => item.Name)
                .Skip(resolvedStartIndex)
                .Take(resolvedLimit)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Load only the Genre and Tags values for the fetched items (targeted query).
            // Npgsql translates entityIds.Contains(...) to "= ANY(@array)", which is
            // efficient in PostgreSQL (especially with an index on ItemValuesMap.ItemId).
            var entityIds = entities.Select(e => e.Id).ToArray();
            var itemValues = await context.ItemValuesMap
                .AsNoTracking()
                .Where(ivm => entityIds.Contains(ivm.ItemId)
                    && (ivm.ItemValue.Type == ItemValueType.Genre
                        || ivm.ItemValue.Type == ItemValueType.Tags))
                .Select(ivm => new { ivm.ItemId, ivm.ItemValue.Type, ivm.ItemValue.Value })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var genresByItem = itemValues
                .Where(v => v.Type == ItemValueType.Genre)
                .ToLookup(v => v.ItemId, v => v.Value);
            var tagsByItem = itemValues
                .Where(v => v.Type == ItemValueType.Tags)
                .ToLookup(v => v.ItemId, v => v.Value);

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
                    Genres = genresByItem[entity.Id].ToList(),
                    Tags = tagsByItem[entity.Id].ToList()
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

    private static string[] NormalizeValues(string[] values)
        => values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim().ToLowerInvariant())
            .ToArray();
}
