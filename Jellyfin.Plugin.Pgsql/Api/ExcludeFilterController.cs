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

    /// <summary>
    /// Maps short BaseItemKind names to full .NET type names stored in the database.
    /// </summary>
    private static readonly Dictionary<string, string> TypeNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Series"] = "MediaBrowser.Controller.Entities.TV.Series",
        ["Movie"] = "MediaBrowser.Controller.Entities.Movies.Movie",
        ["Episode"] = "MediaBrowser.Controller.Entities.TV.Episode",
        ["Season"] = "MediaBrowser.Controller.Entities.TV.Season",
        ["MusicAlbum"] = "MediaBrowser.Controller.Entities.Audio.MusicAlbum",
        ["MusicArtist"] = "MediaBrowser.Controller.Entities.Audio.MusicArtist",
        ["Audio"] = "MediaBrowser.Controller.Entities.Audio.Audio",
        ["BoxSet"] = "MediaBrowser.Controller.Entities.BoxSet",
        ["Book"] = "MediaBrowser.Controller.Entities.Book",
        ["Playlist"] = "MediaBrowser.Controller.Playlists.Playlist",
        ["Video"] = "MediaBrowser.Controller.Entities.Video",
        ["MusicVideo"] = "MediaBrowser.Controller.Entities.MusicVideo",
        ["Trailer"] = "MediaBrowser.Controller.Entities.Trailer",
    };

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
    /// Returns a paginated list of library items with optional server-side tag
    /// and genre exclusions.
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
    /// <param name="includeItemTypes">
    /// Optional. One or more item type names to include (e.g. "Series", "Movie", "Episode").
    /// Accepts both short names (BaseItemKind) and full .NET type names.
    /// Example: <c>includeItemTypes=Series</c>.
    /// </param>
    /// <param name="sortBy">
    /// Optional. The field to sort by. Supported values: SortName (default), DateCreated,
    /// PremiereDate, CommunityRating, ProductionYear.
    /// </param>
    /// <param name="sortOrder">
    /// Optional. The sort direction: "Ascending" (default) or "Descending".
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
        [FromQuery] string[]? includeItemTypes = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortOrder = null,
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

            // --- Item type filter ---
            if (includeItemTypes is { Length: > 0 })
            {
                var resolvedTypes = ResolveTypeNames(includeItemTypes);
                if (resolvedTypes.Length > 0)
                {
                    query = query.Where(item => resolvedTypes.Contains(item.Type));
                }
            }

            // --- Tag exclusion (server-side NOT EXISTS in PostgreSQL) ---
            // Matches against both Tags (type 4) and InheritedTags (type 6).
            if (excludeTags is { Length: > 0 })
            {
                var cleanTags = NormalizeValues(excludeTags);
                query = query.Where(item =>
                    !context.ItemValuesMap.Any(ivm =>
                        ivm.ItemId == item.Id
                        && (ivm.ItemValue.Type == ItemValueType.Tags
                            || ivm.ItemValue.Type == ItemValueType.InheritedTags)
                        && cleanTags.Contains(ivm.ItemValue.CleanValue)));
            }

            // --- Genre exclusion (server-side NOT EXISTS in PostgreSQL) ---
            if (excludeGenres is { Length: > 0 })
            {
                var cleanGenres = NormalizeValues(excludeGenres);
                query = query.Where(item =>
                    !context.ItemValuesMap.Any(ivm =>
                        ivm.ItemId == item.Id
                        && ivm.ItemValue.Type == ItemValueType.Genre
                        && cleanGenres.Contains(ivm.ItemValue.CleanValue)));
            }

            // Total count (before pagination)
            var totalCount = await query
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);

            // --- Sorting ---
            var sortField = (sortBy ?? "SortName").Trim();
            var descending = string.Equals(sortOrder?.Trim(), "Descending", StringComparison.OrdinalIgnoreCase);

            IOrderedQueryable<BaseItemEntity> orderedQuery = sortField.ToLowerInvariant() switch
            {
                "datecreated" => descending
                    ? query.OrderByDescending(item => item.DateCreated)
                    : query.OrderBy(item => item.DateCreated),
                "premieredate" => descending
                    ? query.OrderByDescending(item => item.PremiereDate)
                    : query.OrderBy(item => item.PremiereDate),
                "communityrating" => descending
                    ? query.OrderByDescending(item => item.CommunityRating)
                    : query.OrderBy(item => item.CommunityRating),
                "productionyear" => descending
                    ? query.OrderByDescending(item => item.ProductionYear)
                    : query.OrderBy(item => item.ProductionYear),
                _ => descending
                    ? query.OrderByDescending(item => item.SortName)
                    : query.OrderBy(item => item.SortName),
            };

            orderedQuery = orderedQuery.ThenBy(item => item.Name);

            // Fetch paginated items
            var resolvedStartIndex = Math.Max(startIndex ?? 0, 0);
            var resolvedLimit = Math.Min(limit ?? 100, MaxPageSize);

            var entities = await orderedQuery
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
                .Where(ivm =>
                    entityIds.Contains(ivm.ItemId)
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

    /// <summary>
    /// Resolves item type names to full .NET type names used in the database.
    /// Accepts both short names (e.g. "Series") and full type names (e.g. "MediaBrowser.Controller.Entities.TV.Series").
    /// </summary>
    private static string[] ResolveTypeNames(string[] inputTypes)
    {
        var result = new List<string>();
        foreach (var t in inputTypes)
        {
            var trimmed = t.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            // If it contains a dot, assume it's already a full type name
            if (trimmed.Contains('.', StringComparison.Ordinal))
            {
                result.Add(trimmed);
            }
            else if (TypeNameMap.TryGetValue(trimmed, out var fullName))
            {
                result.Add(fullName);
            }
        }

        return result.ToArray();
    }

    private static string[] NormalizeValues(string[] values)
        => values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim().ToLowerInvariant())
            .ToArray();
}
