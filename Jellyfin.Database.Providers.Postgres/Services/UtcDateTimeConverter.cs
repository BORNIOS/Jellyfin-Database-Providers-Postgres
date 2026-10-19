using System;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// EF Core value converter that guarantees every <see cref="DateTime"/> read from PostgreSQL has
/// <see cref="DateTimeKind.Utc"/>.
/// </summary>
/// <remarks>
/// Every Jellyfin date column is <c>timestamp with time zone</c>, so the stored instant is always UTC. Jellyfin
/// writes a <c>DateTime</c> to its API as an ISO string and only adds the <c>Z</c> designator when the kind is
/// <see cref="DateTimeKind.Utc"/>; a value that reaches it with <see cref="DateTimeKind.Unspecified"/> is sent without any
/// offset. The Android client then replaces it with <c>LocalDateTime.MIN</c> and crashes on the next navigation.
/// Forcing the kind at the model level does not depend on which reader (live, buffered or cached) produced the row,
/// the same approach Jellyfin's SQLite provider takes with its own kind converter.
/// </remarks>
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UtcDateTimeConverter"/> class.
    /// </summary>
    public UtcDateTimeConverter()
        : base(
            value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc),
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
    {
    }
}
