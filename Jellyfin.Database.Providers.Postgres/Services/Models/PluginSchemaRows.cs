using System.Collections.Generic;

namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>A bounded, read-only preview of a private plugin table.</summary>
public sealed record PluginSchemaRows(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, bool Truncated);
