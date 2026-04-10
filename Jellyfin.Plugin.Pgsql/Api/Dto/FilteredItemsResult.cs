using System.Collections.Generic;

namespace Jellyfin.Plugin.Pgsql.Api.Dto;

/// <summary>
/// Paginated result returned by the tag/genre exclude-filter endpoint.
/// </summary>
public class FilteredItemsResult
{
    /// <summary>Gets or sets the total number of items available (before pagination).</summary>
    public int TotalRecordCount { get; set; }

    /// <summary>Gets or sets the zero-based index of the first item in this page.</summary>
    public int StartIndex { get; set; }

    /// <summary>Gets or sets the items on this page.</summary>
    public IReadOnlyList<FilteredItemDto> Items { get; set; } = [];
}
