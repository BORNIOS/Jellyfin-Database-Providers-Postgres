# Resumen ejecutivo de verificación

> Documento generado automáticamente por `Tools/Generar-ResumenEjecutivo.ps1` a partir del resultado
> real de la suite de pruebas. No se edita a mano.

| Dato | Valor |
| --- | --- |
| Fecha de ejecución | 2026-09-17 21:07:41 -07:00 |
| Versión del plugin | 3.0.0.0 |
| Jellyfin validado | 12.1.0 |
| Base de datos de pruebas | no configurada |
| Entorno | Microsoft Windows 10.0.26200 · SDK 10.0.401 |

## Resultado global

| Verificaciones | Correctas | Con error | Omitidas |
| --- | --- | --- | --- |
| 74 | 63 | 0 | 11 |

## Resultado por área

| Área | Verificaciones | Resultado |
| --- | --- | --- |
| AdvancedOptionTests | 5 | ✅ 5/5 |
| DatabaseConfigurationTests | 2 | ✅ 2/2 |
| LoggingTests | 3 | ✅ 3/3 |
| PostgresIntegrationTests | 11 | ⚠️ omitida |
| ProviderContractTests | 4 | ✅ 4/4 |
| QueryConsoleTests | 28 | ✅ 28/28 |
| SchemaAuditTests | 8 | ✅ 8/8 |
| SqlRewriteTests | 9 | ✅ 9/9 |
| WebPageTests | 4 | ✅ 4/4 |

## Detalle

| Verificación | Resultado |
| --- | --- |
| AdvancedOptionTests.CommandTimeoutIsAlwaysApplied | ✅ |
| AdvancedOptionTests.PoolFallsBackToTheSavedConfiguration | ✅ |
| AdvancedOptionTests.PoolFromTheConnectionStringIsRespected | ✅ |
| AdvancedOptionTests.RequestDefaultsMatchThePluginDefaults | ✅ |
| AdvancedOptionTests.SavedConfigurationSurvivesTheRoundTrip | ✅ |
| DatabaseConfigurationTests.EngineSwitchIsDetectedFromDatabaseXml | ✅ |
| DatabaseConfigurationTests.WrittenDatabaseXmlRoundTripsThroughJellyfinOptions | ✅ |
| LoggingTests.FailuresCarryLevelMessageAndStackTrace | ✅ |
| LoggingTests.LogFileLivesNextToTheOtherPluginLogs | ✅ |
| LoggingTests.PostgresFailuresCarryTheServerDetail | ✅ |
| PostgresIntegrationTests.CompatibilityAggregatesExistAfterSchemaPreparation | ⚠️ omitida |
| PostgresIntegrationTests.ExportProducesAUsableSqliteDatabase | ⚠️ omitida |
| PostgresIntegrationTests.InitialSchemaAndPopulatedUpgradeWork | ⚠️ omitida |
| PostgresIntegrationTests.JsonCleanupWorksInSyncAndAsyncPaths | ⚠️ omitida |
| PostgresIntegrationTests.OldSqliteSchemaIsRejectedBeforeWriting | ⚠️ omitida |
| PostgresIntegrationTests.PurgeDatabaseAcceptsEveryTableNameFormatTheServerPasses(tableName: "\"public\".\"PurgeProbe\"") | ⚠️ omitida |
| PostgresIntegrationTests.PurgeDatabaseAcceptsEveryTableNameFormatTheServerPasses(tableName: "public.\"PurgeProbe\"") | ⚠️ omitida |
| PostgresIntegrationTests.PurgeDatabaseAcceptsEveryTableNameFormatTheServerPasses(tableName: "public.PurgeProbe") | ⚠️ omitida |
| PostgresIntegrationTests.PurgeDatabaseAcceptsEveryTableNameFormatTheServerPasses(tableName: "PurgeProbe") | ⚠️ omitida |
| PostgresIntegrationTests.SqlConsoleReturnsRowsAndRejectsWrites | ⚠️ omitida |
| PostgresIntegrationTests.SqliteDatabaseImportsIntoPublicAndCustomSchema | ⚠️ omitida |
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
| QueryConsoleTests.PanelTemplatesAreReadOnlyStatements | ✅ |
| QueryConsoleTests.PanelTemplatesDoNotShowStatisticsFromOtherDatabases | ✅ |
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
| SchemaAuditTests.IgnoredSourcePathsMatchOnEveryPlatform(filePath: "/home/runner/work/repo/Jellyfin.Database.Providers"···, expected: False) | ✅ |
| SchemaAuditTests.IgnoredSourcePathsMatchOnEveryPlatform(filePath: "/home/runner/work/repo/Jellyfin.Database.Providers"···, expected: True) | ✅ |
| SchemaAuditTests.IgnoredSourcePathsMatchOnEveryPlatform(filePath: "/home/runner/work/repo/Jellyfin.Database.Providers"···, expected: True) | ✅ |
| SchemaAuditTests.IgnoredSourcePathsMatchOnEveryPlatform(filePath: "D:\\repo\\Jellyfin.Database.Providers.Postgres\\Mi"···, expected: True) | ✅ |
| SchemaAuditTests.IgnoredSourcePathsMatchOnEveryPlatform(filePath: "D:\\repo\\Jellyfin.Database.Providers.Postgres\\ob"···, expected: True) | ✅ |
| SchemaAuditTests.IgnoredSourcePathsMatchOnEveryPlatform(filePath: "D:\\repo\\Jellyfin.Database.Providers.Postgres\\Se"···, expected: False) | ✅ |
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

## Verificaciones omitidas

Estas pruebas necesitan un PostgreSQL accesible y se omitieron porque la ejecución no recibió
`POSTGRES_TEST_CONNECTION`. El resultado global **no** las cuenta como correctas.

- PostgresIntegrationTests.PurgeDatabaseAcceptsEveryTableNameFormatTheServerPasses(tableName: "\"public\".\"PurgeProbe\"")
- PostgresIntegrationTests.ExportProducesAUsableSqliteDatabase
- PostgresIntegrationTests.PurgeDatabaseAcceptsEveryTableNameFormatTheServerPasses(tableName: "PurgeProbe")
- PostgresIntegrationTests.JsonCleanupWorksInSyncAndAsyncPaths
- PostgresIntegrationTests.CompatibilityAggregatesExistAfterSchemaPreparation
- PostgresIntegrationTests.SqlConsoleReturnsRowsAndRejectsWrites
- PostgresIntegrationTests.PurgeDatabaseAcceptsEveryTableNameFormatTheServerPasses(tableName: "public.\"PurgeProbe\"")
- PostgresIntegrationTests.SqliteDatabaseImportsIntoPublicAndCustomSchema
- PostgresIntegrationTests.InitialSchemaAndPopulatedUpgradeWork
- PostgresIntegrationTests.PurgeDatabaseAcceptsEveryTableNameFormatTheServerPasses(tableName: "public.PurgeProbe")
- PostgresIntegrationTests.OldSqliteSchemaIsRejectedBeforeWriting

## Cómo reproducirlo

```bash
# Sólo verificaciones sin base de datos
dotnet test Tests/Jellyfin.Database.Providers.Postgres.Tests

# Verificaciones completas, incluida la integración con PostgreSQL
$env:POSTGRES_TEST_CONNECTION = "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=***"
./Tools/Generar-ResumenEjecutivo.ps1
```

Las pruebas de integración crean su propia base de datos desechable y la eliminan al terminar:
la cadena de conexión indicada se usa únicamente para `CREATE DATABASE` / `DROP DATABASE`.

