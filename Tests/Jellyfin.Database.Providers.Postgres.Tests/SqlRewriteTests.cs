using System.Data.Common;
using Jellyfin.Database.Providers.Postgres.Services;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Covers the statement rewriting the plugin applies to Jellyfin's migration SQL. These are pure unit
/// tests: no database involved, which means a community contributor can inspect exactly what changes.
/// </summary>
public sealed class SqlRewriteTests
{
    /// <summary>
    /// Jellyfin 12.1 cleans up JSON columns with SQLite's <c>json_remove</c>, which PostgreSQL does not
    /// have; the plugin translates it to a jsonb expression.
    /// </summary>
    [Fact]
    public void SqliteJsonCleanupIsTranslatedToPostgres()
    {
        var command = Rewrite(
            $"""UPDATE "BaseItems" SET "Data" = {Jellyfin121MigrationInterceptor.SqliteCleanup} WHERE json_valid("Data") = 1;""");

        Assert.DoesNotContain("json_remove", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("::jsonb - ARRAY[", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("IS JSON OBJECT", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Running the rewrite twice must not change the statement again, otherwise a retried command would
    /// end up with a different (nested) statement.
    /// </summary>
    [Theory]
    [InlineData("""UPDATE "BaseItems" SET "Data" = json_remove("Data", '$.LinkedChildren', '$.ExtraIds', '$.SupportsExternalTransfer') WHERE json_valid("Data") = 1;""")]
    [InlineData("""UPDATE "BaseItems" SET "InheritedParentalRatingValue" = NULL WHERE "OfficialRating" IS NULL OR "OfficialRating" = '';""")]
    [InlineData("""UPDATE "BaseItems" SET "InheritedParentalRatingSubValue" = NULL WHERE "OfficialRating" IS NULL OR "OfficialRating" = '';""")]
    public void RewritingIsIdempotent(string sql)
    {
        var once = Rewrite(sql).CommandText;
        var twice = Rewrite(once).CommandText;

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// <c>MigrateRatingLevels</c> sets the inherited rating to NULL for every item without a rating. On a
    /// large library that rewrites the whole table and aborts the migration on the command timeout, so the
    /// statement is restricted to the rows that actually change.
    /// </summary>
    [Theory]
    [InlineData("InheritedParentalRatingValue")]
    [InlineData("InheritedParentalRatingSubValue")]
    public void RatingLevelsUpdateOnlyTouchesRowsThatChange(string column)
    {
        var command = Rewrite($$"""UPDATE "BaseItems" SET "{{column}}" = NULL WHERE "OfficialRating" IS NULL OR "OfficialRating" = '';""");

        Assert.Contains($"\"{column}\" IS NOT NULL", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Anything the plugin does not know about has to reach PostgreSQL untouched.
    /// </summary>
    [Theory]
    [InlineData("""UPDATE "UserData" SET "Played" = true WHERE "UserId" IS NULL;""")]
    [InlineData("""UPDATE "BaseItems" SET "Name" = 'x' WHERE "Id" = '00000000-0000-0000-0000-000000000001';""")]
    [InlineData("""DELETE FROM "LinkedChildren" WHERE "ParentId" IS NULL;""")]
    public void UnrelatedStatementsAreLeftUntouched(string sql)
    {
        Assert.Equal(sql, Rewrite(sql).CommandText);
    }

    private static DbCommand Rewrite(string sql)
    {
        var command = new NpgsqlCommand(sql);

        // The interceptor only reads and rewrites CommandText, so no connection or event data is needed.
        new Jellyfin121MigrationInterceptor().NonQueryExecuting(command, null!, default);
        return command;
    }

    /// <summary>
    /// Un INSERT duplicado en <c>BaseItems</c> hacia fallar todo el lote con 23505 (items de canal e
    /// imagenes a medio crear). Ignorarlo es lo que hace SQLite y deja la fila que ya existia.
    /// </summary>
    [Fact]
    public void DuplicateBaseItemInsertIsIgnored()
    {
        var command = RewriteUpsert("""INSERT INTO "BaseItems" ("Id", "Name") VALUES (@p0, @p1);""");

        Assert.Contains("ON CONFLICT DO NOTHING", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// An on-demand Person refresh can lose the row between Jellyfin's existence read and its full entity
    /// update. The specific parameter-only update produced by ItemPersistenceService becomes an atomic
    /// upsert, so PostgreSQL can recreate the vanished item rather than reporting zero affected rows.
    /// </summary>
    [Fact]
    public void FullBaseItemUpdateBecomesAnAtomicUpsert()
    {
        var command = RewriteUpsert(
            """
            UPDATE "BaseItems" SET "Name" = @p0, "Overview" = @p1, "Type" = @p2
            WHERE "Id" = @p3;
            """);

        Assert.Contains(
            """INSERT INTO "BaseItems" ("Id", "Name", "Overview", "Type") VALUES (@p3, @p0, @p1, @p2) ON CONFLICT ("Id") DO UPDATE SET "Name" = @p0, "Overview" = @p1, "Type" = @p2;""",
            command.CommandText,
            StringComparison.Ordinal);
    }

    /// <summary>Code-migration updates must retain their original SQL semantics.</summary>
    [Fact]
    public void NonEntityBaseItemUpdatesAreLeftUntouched()
    {
        const string sql = """UPDATE "BaseItems" SET "Data" = json_remove("Data", '$.ExtraIds') WHERE "Id" = @p0;""";

        Assert.Equal(sql, RewriteUpsert(sql).CommandText);
    }

    /// <summary>Partial updates do not contain all required BaseItems columns and must stay updates.</summary>
    [Fact]
    public void PartialBaseItemUpdatesAreLeftUntouched()
    {
        const string sql = """UPDATE "BaseItems" SET "Name" = @p0 WHERE "Id" = @p1;""";

        Assert.Equal(sql, RewriteUpsert(sql).CommandText);
    }

    /// <summary>User playback updates must be atomic: concurrent saves update the existing row.</summary>
    [Fact]
    public void DuplicateUserDataInsertUpdatesTheExistingRow()
    {
        var command = RewriteUpsert(
            """INSERT INTO "UserData" ("ItemId", "UserId", "CustomDataKey", "Played", "PlaybackPositionTicks") VALUES (@p0, @p1, @p2, @p3, @p4);""");

        Assert.Contains("""ON CONFLICT ("ItemId", "UserId", "CustomDataKey") DO UPDATE SET""", command.CommandText, StringComparison.Ordinal);
        Assert.Contains(""""Played" = EXCLUDED."Played"""", command.CommandText, StringComparison.Ordinal);
        Assert.Contains(""""PlaybackPositionTicks" = EXCLUDED."PlaybackPositionTicks"""", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Un valor duplicado tiene que nombrar el par (Type, Value) como objetivo del conflicto: ItemValueId es
    /// un guid nuevo en cada intento, asi que un ON CONFLICT sin objetivo nunca dispararia y el INSERT
    /// seguiria fallando con 23505.
    /// </summary>
    [Fact]
    public void DuplicateItemValueInsertNamesTheUniquePairAsConflictTarget()
    {
        var command = RewriteUpsert("""INSERT INTO "ItemValues" ("ItemValueId", "CleanValue", "Type", "Value") VALUES (@p0, @p1, @p2, @p3);""");

        Assert.Contains("""ON CONFLICT ("Type", "Value") DO NOTHING""", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Si el valor no se inserto porque ya estaba, el INSERT del mapa apuntaria a una fila inexistente y
    /// romperia FK_ItemValuesMap_ItemValues_ItemValueId, que se lleva por delante todo el lote (los items
    /// incluidos). Por eso el mapa solo se inserta cuando el valor existe de verdad.
    /// </summary>
    [Theory]
    [InlineData("""("ItemId", "ItemValueId") VALUES (@p4, @p0)""")]
    [InlineData("""("ItemValueId", "ItemId") VALUES (@p0, @p4)""")]
    public void MappingInsertBecomesConditionalWhenValuesTravelInTheSameBatch(string mapping)
    {
        var command = RewriteUpsert(
            $"""
            INSERT INTO "ItemValues" ("ItemValueId", "CleanValue", "Type", "Value") VALUES (@p0, @p1, @p2, @p3);
            INSERT INTO "ItemValuesMap" {mapping};
            """);

        Assert.Contains(
            """SELECT @p4, @p0 WHERE EXISTS (SELECT 1 FROM "ItemValues" WHERE "ItemValueId" = @p0)""",
            command.CommandText,
            StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT DO NOTHING", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Un mapa que llega solo no se toca: el valor al que apunta puede venir de una fila que ya esta
    /// guardada, y en ese caso la clave foranea se cumple.
    /// </summary>
    [Fact]
    public void MappingInsertAloneKeepsItsShape()
    {
        var command = RewriteUpsert("""INSERT INTO "ItemValuesMap" ("ItemId", "ItemValueId") VALUES (@p0, @p1);""");

        Assert.DoesNotContain("EXISTS", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT DO NOTHING", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// If a concurrent scan removes an item between Jellyfin's existence check and SaveChanges, dependent
    /// image and provider rows must be skipped rather than causing FK_BaseItemImageInfos_BaseItems_ItemId.
    /// The parent is present for ordinary inserts, so this leaves the normal path unchanged.
    /// </summary>
    [Theory]
    [InlineData("BaseItemImageInfos")]
    [InlineData("BaseItemProviders")]
    public void DependentBaseItemInsertIsConditionalOnTheParentStillExisting(string table)
    {
        var command = RewriteUpsert(
            $"INSERT INTO \"{table}\" (\"Id\", \"ItemId\", \"Value\") VALUES (@p0, @p1, @p2);");

        Assert.Contains($"INSERT INTO \"{table}\"", command.CommandText, StringComparison.Ordinal);
        Assert.Contains(
            "WHERE EXISTS (SELECT 1 FROM \"BaseItems\" WHERE \"Id\" = @p1)",
            command.CommandText,
            StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT DO NOTHING", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>Reescribir dos veces no puede cambiar la sentencia: un comando reintentado fallaria.</summary>
    [Fact]
    public void ItemValueRewriteIsIdempotent()
    {
        var once = RewriteUpsert(
            """
            INSERT INTO "ItemValues" ("ItemValueId", "CleanValue", "Type", "Value") VALUES (@p0, @p1, @p2, @p3);
            INSERT INTO "ItemValuesMap" ("ItemId", "ItemValueId") VALUES (@p4, @p0);
            """).CommandText;
        var twice = RewriteUpsert(once).CommandText;

        Assert.Equal(once, twice);
    }

    private static DbCommand RewriteUpsert(string sql)
    {
        var command = new NpgsqlCommand(sql);

        new UpsertConflictInterceptor().NonQueryExecuting(command, null!, default);
        return command;
    }
}
