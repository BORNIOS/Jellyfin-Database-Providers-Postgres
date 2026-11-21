namespace Jellyfin.Database.Providers.Postgres.Services.Models;

/// <summary>One operation of the difference between the provider's migrations and Jellyfin's model.</summary>
/// <param name="Description">Readable description, e.g. <c>CreateIndex IX_BaseItems_OwnerId on BaseItems</c>.</param>
/// <param name="Sql">Idempotent SQL that applies it.</param>
/// <param name="Safe">Whether it can be applied without losing or rewriting data.</param>
/// <param name="Applied">Whether it is applied in the database (in this run or an earlier one).</param>
public sealed record SchemaDriftOperation(string Description, string Sql, bool Safe, bool Applied);
