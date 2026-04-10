using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Pgsql.Api.Dto;

/// <summary>
/// Represents a single library item returned by the tag/genre exclude-filter endpoint.
/// </summary>
public class FilteredItemDto
{
    /// <summary>Gets or sets the item identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the item name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the item type (e.g. "Movie", "Series", "Episode").</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets a value indicating whether this item is a folder/container.</summary>
    public bool IsFolder { get; set; }

    /// <summary>Gets or sets the item overview / synopsis.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the official parental rating (e.g. "PG-13").</summary>
    public string? OfficialRating { get; set; }

    /// <summary>Gets or sets the community rating.</summary>
    public float? CommunityRating { get; set; }

    /// <summary>Gets or sets the production year.</summary>
    public int? ProductionYear { get; set; }

    /// <summary>Gets or sets the identifier of the direct parent item.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>Gets or sets the genres assigned to this item.</summary>
    public IReadOnlyList<string> Genres { get; set; } = [];

    /// <summary>Gets or sets the tags assigned to this item.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];
}
