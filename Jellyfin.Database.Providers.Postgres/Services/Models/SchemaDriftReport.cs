using System;
using System.Collections.Generic;

namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>Report of the last schema drift check.</summary>
/// <param name="CheckedAtUtc">When the check ran.</param>
/// <param name="Mode">Mode it ran with.</param>
/// <param name="Outcome">Result.</param>
/// <param name="JellyfinVersion">Version of Jellyfin's database model assembly.</param>
/// <param name="ProviderVersion">Version of this provider.</param>
/// <param name="Operations">Operations of the drift; empty when in sync.</param>
/// <param name="Message">Readable summary.</param>
public sealed record SchemaDriftReport(
    DateTime CheckedAtUtc,
    SchemaDriftMode Mode,
    SchemaDriftOutcome Outcome,
    string JellyfinVersion,
    string ProviderVersion,
    IReadOnlyList<SchemaDriftOperation> Operations,
    string Message);
