# Resumen ejecutivo de verificación

> Documento generado automáticamente por `tools/Generar-ResumenEjecutivo.ps1` a partir del resultado
> real de la suite de pruebas. No se edita a mano.

| Dato | Valor |
| --- | --- |
| Fecha de ejecución | 2026-09-16 02:12:02 -07:00 |
| Versión del plugin | 3.0.0.0 |
| Jellyfin validado | 12.1.0 |
| Base de datos de pruebas | Jellyfin |
| Entorno | Microsoft Windows 10.0.26200 · SDK 10.0.401 |

## Resultado global

| Verificaciones | Correctas | Con error | Omitidas |
| --- | --- | --- | --- |
| 61 | 61 | 0 | 0 |

## Resultado por área

| Área | Verificaciones | Resultado |
| --- | --- | --- |
| DatabaseConfigurationTests | 2 | ✅ 2/2 |
| LoggingTests | 3 | ✅ 3/3 |
| PostgresIntegrationTests | 11 | ✅ 11/11 |
| ProviderContractTests | 4 | ✅ 4/4 |
| QueryConsoleTests | 26 | ✅ 26/26 |
| SchemaAuditTests | 2 | ✅ 2/2 |
| SqlRewriteTests | 9 | ✅ 9/9 |
| WebPageTests | 4 | ✅ 4/4 |

## Detalle

| Verificación | Resultado |
| --- | --- |
| DatabaseConfigurationTests.EngineSwitchIsDetectedFromDatabaseXml | ✅ |
| DatabaseConfigurationTests.WrittenDatabaseXmlRoundTripsThroughJellyfinOptions | ✅ |
| LoggingTests.FailuresCarryLevelMessageAndStackTrace | ✅ |
| LoggingTests.LogFileLivesNextToTheOtherPluginLogs | ✅ |
| LoggingTests.PostgresFailuresCarryTheServerDetail | ✅ |
| PostgresIntegrationTests.CompatibilityAggregatesExistAfterSchemaPreparation | ✅ |
| PostgresIntegrationTests.ExportProducesAUsableSqliteDatabase | ✅ |
| PostgresIntegrationTests.InitialSchemaAndPopulatedUpgradeWork | ✅ |
| PostgresIntegrationTests.JsonCleanupWorksInSyncAndAsyncPaths | ✅ |
| PostgresIntegrationTests.OldSqliteSchemaIsRejectedBeforeWriting | ✅ |
| PostgresIntegrationTests.PurgeDatabaseAcceptsEveryTableNameFormatTheServerPasses(tableName: "\"public\".\"PurgeProbe\"") | ✅ |
| PostgresIntegrationTests.PurgeDatabaseAcceptsEveryTableNameFormatTheServerPasses(tableName: "public.\"PurgeProbe\"") | ✅ |
| PostgresIntegrationTests.PurgeDatabaseAcceptsEveryTableNameFormatTheServerPasses(tableName: "public.PurgeProbe") | ✅ |
| PostgresIntegrationTests.PurgeDatabaseAcceptsEveryTableNameFormatTheServerPasses(tableName: "PurgeProbe") | ✅ |
| PostgresIntegrationTests.SqlConsoleReturnsRowsAndRejectsWrites | ✅ |
| PostgresIntegrationTests.SqliteDatabaseImportsIntoPublicAndCustomSchema | ✅ |
| ProviderContractTests.DeclaredJellyfinVersionMatchesTheReferencedSdk | ✅ |
| ProviderContractTests.EntryPointsMatchWhatJellyfinInstantiates | ✅ |
| ProviderContractTests.JellyfinDiscoversExactlyOneProvider | ✅ |
| ProviderContractTests.NoInterfaceMemberIsLeftUnimplemented | ✅ |
| QueryConsoleTests.KeywordsHiddenInLiteralsAreNotRejected | ✅ |
| QueryConsoleTests.MutatingOrMultiStatementSqlIsRejected(sql: "   ") | ✅ |
| QueryConsoleTests.MutatingOrMultiStatementSqlIsRejected(sql: "-- despista\nUPDATE \"BaseItems\" SET \"Name\" = '"···) | ✅ |
| QueryConsoleTests.MutatingOrMultiStatementSqlIsRejected(sql: "") | ✅ |
| QueryConsoleTests.MutatingOrMultiStatementSqlIsRejected(sql: "/* despista */ DELETE FROM \"UserData\"") | ✅ |
| QueryConsoleTests.MutatingOrMultiStatementSqlIsRejected(sql: "ALTER TABLE \"BaseItems\" ADD COLUMN x int") | ✅ |
| QueryConsoleTests.MutatingOrMultiStatementSqlIsRejected(sql: "CREATE INDEX ix ON \"BaseItems\" (\"Name\")") | ✅ |
| QueryConsoleTests.MutatingOrMultiStatementSqlIsRejected(sql: "DELETE FROM \"UserData\"") | ✅ |
| QueryConsoleTests.MutatingOrMultiStatementSqlIsRejected(sql: "DROP TABLE \"BaseItems\"") | ✅ |
| QueryConsoleTests.MutatingOrMultiStatementSqlIsRejected(sql: "INSERT INTO \"UserData\" VALUES (1)") | ✅ |
| QueryConsoleTests.MutatingOrMultiStatementSqlIsRejected(sql: "SELECT * INTO copia FROM \"BaseItems\"") | ✅ |
| QueryConsoleTests.MutatingOrMultiStatementSqlIsRejected(sql: "SELECT 1; DROP TABLE \"BaseItems\"") | ✅ |
| QueryConsoleTests.MutatingOrMultiStatementSqlIsRejected(sql: "SET search_path = public") | ✅ |
| QueryConsoleTests.MutatingOrMultiStatementSqlIsRejected(sql: "TRUNCATE \"UserData\"") | ✅ |
| QueryConsoleTests.MutatingOrMultiStatementSqlIsRejected(sql: "UPDATE \"BaseItems\" SET \"Name\" = 'x'") | ✅ |
| QueryConsoleTests.MutatingOrMultiStatementSqlIsRejected(sql: "VACUUM") | ✅ |
| QueryConsoleTests.ReadOnlyStatementsAreAccepted(sql: "/* plan primero */ EXPLAIN SELECT 1") | ✅ |
| QueryConsoleTests.ReadOnlyStatementsAreAccepted(sql: "SELECT 'UPDATE' AS palabra, \"SET\" FROM (VALUES ("···) | ✅ |
| QueryConsoleTests.ReadOnlyStatementsAreAccepted(sql: "select \"Id\" from \"BaseItems\" limit 5;") | ✅ |
| QueryConsoleTests.ReadOnlyStatementsAreAccepted(sql: "SELECT 1") | ✅ |
| QueryConsoleTests.ReadOnlyStatementsAreAccepted(sql: "SELECT COUNT(*) FROM \"UserData\" -- comentario fi"···) | ✅ |
| QueryConsoleTests.ReadOnlyStatementsAreAccepted(sql: "SHOW search_path") | ✅ |
| QueryConsoleTests.ReadOnlyStatementsAreAccepted(sql: "TABLE \"BaseItems\"") | ✅ |
| QueryConsoleTests.ReadOnlyStatementsAreAccepted(sql: "VALUES (1), (2)") | ✅ |
| QueryConsoleTests.ReadOnlyStatementsAreAccepted(sql: "WITH valores AS (SELECT 1 AS n) SELECT n FROM valo"···) | ✅ |
| QueryConsoleTests.TheStatementIsReturnedUnchangedExceptForComments | ✅ |
| SchemaAuditTests.EveryTableAndQualifiedColumnUsedByRawSqlExistsInTheModel | ✅ |
| SchemaAuditTests.SnapshotMatchesTheJellyfinModel | ✅ |
| SqlRewriteTests.RatingLevelsUpdateOnlyTouchesRowsThatChange(column: "InheritedParentalRatingSubValue") | ✅ |
| SqlRewriteTests.RatingLevelsUpdateOnlyTouchesRowsThatChange(column: "InheritedParentalRatingValue") | ✅ |
| SqlRewriteTests.RewritingIsIdempotent(sql: "UPDATE \"BaseItems\" SET \"Data\" = json_remove(\""···) | ✅ |
| SqlRewriteTests.RewritingIsIdempotent(sql: "UPDATE \"BaseItems\" SET \"InheritedParentalRating"···) | ✅ |
| SqlRewriteTests.RewritingIsIdempotent(sql: "UPDATE \"BaseItems\" SET \"InheritedParentalRating"···) | ✅ |
| SqlRewriteTests.SqliteJsonCleanupIsTranslatedToPostgres | ✅ |
| SqlRewriteTests.UnrelatedStatementsAreLeftUntouched(sql: "DELETE FROM \"LinkedChildren\" WHERE \"ParentId\" "···) | ✅ |
| SqlRewriteTests.UnrelatedStatementsAreLeftUntouched(sql: "UPDATE \"BaseItems\" SET \"Name\" = 'x' WHERE \"Id"···) | ✅ |
| SqlRewriteTests.UnrelatedStatementsAreLeftUntouched(sql: "UPDATE \"UserData\" SET \"Played\" = true WHERE \""···) | ✅ |
| WebPageTests.BothDictionariesContainEveryTranslatedKey | ✅ |
| WebPageTests.ConfigurationPageResourceIsEmbedded | ✅ |
| WebPageTests.EveryElementIdUsedByTheScriptExists | ✅ |
| WebPageTests.EveryTabHasItsPane | ✅ |

## Cómo reproducirlo

```bash
# Sólo verificaciones sin base de datos
dotnet test tests/Jellyfin.Database.Providers.Postgres.Tests

# Verificaciones completas, incluida la integración con PostgreSQL
$env:POSTGRES_TEST_CONNECTION = "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=***"
./tools/Generar-ResumenEjecutivo.ps1
```

Las pruebas de integración crean su propia base de datos desechable y la eliminan al terminar:
la cadena de conexión indicada se usa únicamente para `CREATE DATABASE` / `DROP DATABASE`.

