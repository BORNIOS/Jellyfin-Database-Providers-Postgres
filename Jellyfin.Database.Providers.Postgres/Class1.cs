using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Providers.Postgres.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Configures Jellyfin to use PostgreSQL through EF Core.
/// </summary>
[JellyfinDatabaseProviderKey("Jellyfin-Postgres")]
public sealed class PostgresDatabaseProvider : IJellyfinDatabaseProvider
{
	private const string InitialMigrationId = "00000000000000_InitialCreate";
	private const string InitialMigrationProductVersion = "9.0.11";

	private readonly IApplicationPaths? _applicationPaths;
	private readonly ILogger<PostgresDatabaseProvider> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="PostgresDatabaseProvider"/> class.
	/// </summary>
	/// <param name="applicationPaths">Application paths service. Kept for DI compatibility with other providers.</param>
	/// <param name="logger">A logger.</param>
	public PostgresDatabaseProvider(IApplicationPaths? applicationPaths, ILogger<PostgresDatabaseProvider> logger)
	{
		_applicationPaths = applicationPaths;
		_logger = logger;
		Current = this;
	}

	/// <inheritdoc/>
	public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

	/// <inheritdoc/>
	public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
	{
		static T? GetOption<T>(ICollection<CustomDatabaseOption>? customOptions, string key, Func<string, T> converter, Func<T>? defaultValue = null)
		{
			if (customOptions is null)
			{
				return defaultValue is not null ? defaultValue() : default;
			}

			var item = customOptions.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
			if (item is null)
			{
				return defaultValue is not null ? defaultValue() : default;
			}

			return converter(item.Value);
		}

		var customProviderOptions = databaseConfiguration.CustomProviderOptions
			?? throw new InvalidOperationException("CustomProviderOptions must be configured for PLUGIN_PROVIDER.");

		if (string.IsNullOrWhiteSpace(customProviderOptions.ConnectionString))
		{
			throw new InvalidOperationException("CustomProviderOptions.ConnectionString is required for Jellyfin-Postgres.");
		}

		// Do NOT enable Npgsql.EnableLegacyTimestampBehavior.
		// That switch causes Npgsql to apply the PostgreSQL session timezone when reading
		// timestamptz columns, returning DateTimeKind.Local. Jellyfin then double-converts
		// those values back to UTC before serializing, causing the dashboard to show UTC
		// timestamps (+7 h shift on UTC-7 servers). The correct approach is strict UTC:
		// store as UTC, let the UI/browser render in local time.

		var customOptions = customProviderOptions.Options;
		var commandTimeout = GetOption(customOptions, "command-timeout", e => int.Parse(e, CultureInfo.InvariantCulture), () => 60);

		// Inject the host timezone into the connection string so that raw SQL clients
		// (psql, pgAdmin) display timestamps in local time. Npgsql uses binary protocol
		// and ignores this for its own reads, so it does NOT affect C# DateTime values.
		var connBuilder = new NpgsqlConnectionStringBuilder(customProviderOptions.ConnectionString);
		if (string.IsNullOrEmpty(connBuilder.Timezone))
		{
			connBuilder.Timezone = GetLocalIanaTimezone();
		}

		// Connection pool and auto-prepare tuning — loaded from plugin config
		var perfConfig = PostgresPlugin.Instance?.Configuration;
		connBuilder.MinPoolSize = perfConfig?.MinPoolSize ?? 4;
		connBuilder.MaxPoolSize = perfConfig?.MaxPoolSize ?? 100;
		if (connBuilder.MaxAutoPrepare == 0)
		{
			connBuilder.MaxAutoPrepare = perfConfig?.MaxAutoPrepare ?? 50;
			connBuilder.AutoPrepareMinUsages = 5;
		}

		var effectiveConnectionString = connBuilder.ConnectionString;

		options.UseNpgsql(
			effectiveConnectionString,
			npgsqlOptions =>
			{
				// Must use simple assembly name, not FullName (which includes version/token).
				npgsqlOptions.MigrationsAssembly(GetType().Assembly.GetName().Name!);
				npgsqlOptions.CommandTimeout(commandTimeout);
				// EnableRetryOnFailure is intentionally omitted: Jellyfin opens explicit user
				// transactions in BaseItemRepository which are incompatible with the Npgsql
				// retrying execution strategy. Transient resilience is handled by the connection
				// pool and PostgreSQL's own WAL recovery.
			});

		// Home-page query cache: intercepts SELECT on BaseItems/UserData/ItemValues and
		// serves cached DataTable results for 30 s. Reduces the ~15-query home-page fan-out
		// to zero DB round-trips on cache hits. Benefits all clients equally (web, mobile,
		// Roku, Xbox, Samsung, iOS, Android TV).
		options.AddInterceptors(new HomeQueryCacheInterceptor(_logger));

		// Self-heal migration history if schema was pre-created but __EFMigrationsHistory
		// does not contain the initial migration marker.
		TryRepairInitialMigrationHistory(effectiveConnectionString);
		TryMitigateItemValuesIndexContention(effectiveConnectionString);

		// Kick off performance index creation and autovacuum tuning in the background
		// so they don't block Jellyfin startup. Uses CONCURRENTLY = zero downtime.
		var enableSearch = perfConfig?.EnableSearchOptimizations ?? true;
		var enableVacuum = perfConfig?.EnableAutovacuumTuning ?? true;
		if (enableSearch || enableVacuum)
		{
			var capturedConnStr = effectiveConnectionString;
			_ = Task.Run(async () =>
			{
				// Small delay — let EF Core finish its migrations before running DDL
				await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
				await TryApplyPerformanceOptimizationsAsync(
					capturedConnStr, enableSearch, enableVacuum, CancellationToken.None)
					.ConfigureAwait(false);
			});
		}

		_logger.LogWarning("PostgreSQL provider initialized for Jellyfin. Session timezone for SQL clients: {Timezone}. All DateTime values persisted as strict UTC (no legacy timestamp behavior).", connBuilder.Timezone);
	}

	/// <summary>
	/// Returns the host timezone as a PostgreSQL-compatible IANA name.
	/// On Windows, converts from Windows timezone ID to IANA. Falls back to "UTC".
	/// </summary>
	private static string GetLocalIanaTimezone()
	{
		var localTz = TimeZoneInfo.Local;

		// On Linux/macOS the Id is already an IANA name (contains '/').
		if (localTz.Id.Contains('/', StringComparison.Ordinal))
		{
			return localTz.Id;
		}

		// On Windows, try to convert the Windows timezone ID to IANA.
		if (TimeZoneInfo.TryConvertWindowsIdToIanaId(localTz.Id, out var ianaId)
			&& !string.IsNullOrEmpty(ianaId))
		{
			return ianaId;
		}

		return "UTC";
	}

	private void TryRepairInitialMigrationHistory(string connectionString)
	{
		try
		{
			using var conn = new NpgsqlConnection(connectionString);
			conn.Open();

			// Always ensure __EFMigrationsHistory exists before any Jellyfin migration stage
			// runs. Jellyfin's PreInitialisation code migrations try to INSERT completion markers
			// into this table before EF Core's CoreInitialisation stage has a chance to create it.
			// On a fresh PostgreSQL schema (e.g. switching from SQLite on an existing install),
			// the table won't exist yet and every PreInitialisation migration would crash with
			// 42P01. Creating it here (idempotent) is safe: EF Core will use it normally.
			using (var createHistoryCmd = new NpgsqlCommand(@"
				CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
					""MigrationId"" character varying(150) NOT NULL,
					""ProductVersion"" character varying(32) NOT NULL,
					CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY (""MigrationId"")
				);", conn))
			{
				createHistoryCmd.ExecuteNonQuery();
			}

			// If core tables are absent this is a completely fresh schema.
			// EF Core's InitialCreate migration will populate __EFMigrationsHistory itself
			// during CoreInitialisation — no need to stamp it here.
			using (var hasActivityCmd = new NpgsqlCommand(
				"SELECT to_regclass('public.\"ActivityLogs\"') IS NOT NULL;",
				conn))
			{
				var hasActivityTable = Convert.ToBoolean(hasActivityCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
				if (!hasActivityTable)
				{
					var importedMigrations = TryImportMigrationHistoryFromSqlite(conn);
					if (importedMigrations > 0)
					{
						_logger.LogWarning("PostgresDatabaseProvider: Fresh PostgreSQL schema detected. Imported {ImportedMigrations} migration history rows from SQLite.", importedMigrations);
					}
					else
					{
						_logger.LogWarning("PostgresDatabaseProvider: Fresh PostgreSQL schema detected. __EFMigrationsHistory created; EF Core will apply InitialCreate during CoreInitialisation.");
					}

					return;
				}
			}

			// Schema already exists (pre-created outside EF Core migrations).
			// Insert the InitialCreate marker so EF Core does not try to re-run it.
			using (var insertCmd = new NpgsqlCommand(
				"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES (@id, @ver) ON CONFLICT (\"MigrationId\") DO NOTHING;",
				conn))
			{
				insertCmd.Parameters.AddWithValue("id", InitialMigrationId);
				insertCmd.Parameters.AddWithValue("ver", InitialMigrationProductVersion);
				var affected = insertCmd.ExecuteNonQuery();
				if (affected > 0)
				{
					_logger.LogWarning("Detected existing PostgreSQL schema without migration marker. Inserted {MigrationId} into __EFMigrationsHistory.", InitialMigrationId);
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not repair PostgreSQL migration history automatically. Startup will continue.");
		}
	}

	private void TryMitigateItemValuesIndexContention(string connectionString)
	{
		try
		{
			using var conn = new NpgsqlConnection(connectionString);
			conn.Open();

			using (var hasTableCmd = new NpgsqlCommand(
				"SELECT to_regclass('public.\"ItemValues\"') IS NOT NULL;",
				conn))
			{
				var hasItemValuesTable = Convert.ToBoolean(hasTableCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
				if (!hasItemValuesTable)
				{
					return;
				}
			}

			bool hasUniqueIndex;
			using (var uniqueCheckCmd = new NpgsqlCommand(@"
				SELECT COALESCE(i.indisunique, FALSE)
				FROM pg_class c
				JOIN pg_namespace n ON n.oid = c.relnamespace
				JOIN pg_index i ON i.indexrelid = c.oid
				WHERE n.nspname = 'public'
				  AND c.relname = 'IX_ItemValues_Type_Value'
				LIMIT 1;", conn))
			{
				var scalar = uniqueCheckCmd.ExecuteScalar();
				hasUniqueIndex = scalar is not null && Convert.ToBoolean(scalar, CultureInfo.InvariantCulture);
			}

			if (hasUniqueIndex)
			{
				using var dropAndCreateCmd = new NpgsqlCommand(@"
					DROP INDEX IF EXISTS ""IX_ItemValues_Type_Value"";
					CREATE INDEX IF NOT EXISTS ""IX_ItemValues_Type_Value"" ON ""ItemValues"" (""Type"", ""Value"");", conn);
				dropAndCreateCmd.ExecuteNonQuery();
				_logger.LogWarning("Mitigation applied: converted IX_ItemValues_Type_Value from UNIQUE to non-unique to reduce runtime collisions during concurrent library refresh.");
			}
			else
			{
				using var ensureIndexCmd = new NpgsqlCommand(
					"CREATE INDEX IF NOT EXISTS \"IX_ItemValues_Type_Value\" ON \"ItemValues\" (\"Type\", \"Value\");",
					conn);
				ensureIndexCmd.ExecuteNonQuery();
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not apply ItemValues index contention mitigation automatically. Startup will continue.");
		}
	}

	/// <summary>
	/// Exposed for <see cref="PostgresDatabaseProvider"/> and <see cref="Tasks.OptimizeIndexesTask"/>
	/// so the scheduled task can trigger the same work.
	/// </summary>
	internal static bool TrgmAvailable { get; private set; }

	/// <summary>The singleton instance set when the provider is initialized by Jellyfin.</summary>
	internal static PostgresDatabaseProvider? Current { get; private set; }

	/// <summary>
	/// Instance wrapper — delegates to the static overload using this instance's logger.
	/// </summary>
	internal Task TryApplyPerformanceOptimizationsAsync(
		string connectionString,
		bool enableSearch,
		bool enableVacuum,
		CancellationToken ct)
		=> RunOptimizationsAsync(connectionString, enableSearch, enableVacuum, _logger, ct);

	/// <summary>
	/// Static entry point — safe to call without an active provider instance.
	/// Creates pg_trgm GIN indexes for near-instant search and tunes autovacuum on hot
	/// tables. All statements are idempotent (IF NOT EXISTS / SET).
	/// </summary>
	internal static async Task RunOptimizationsAsync(
		string connectionString,
		bool enableSearch,
		bool enableVacuum,
		Microsoft.Extensions.Logging.ILogger logger,
		CancellationToken ct)
	{
		// DDL cannot be prepared; use a dedicated non-pooled connection
		var maintBuilder = new NpgsqlConnectionStringBuilder(connectionString)
		{
			MaxAutoPrepare = 0,
			Pooling = false,
		};

		try
		{
			await using var conn = new NpgsqlConnection(maintBuilder.ConnectionString);
			await conn.OpenAsync(ct).ConfigureAwait(false);

			if (enableSearch)
			{
				await ApplySearchIndexesAsync(conn, logger, ct).ConfigureAwait(false);
			}

			if (enableVacuum)
			{
				await ApplyAutovacuumTuningAsync(conn, logger, ct).ConfigureAwait(false);
			}

			// Always apply — pure read-path wins, no config flag needed
			await ApplyNavigationIndexesAsync(conn, logger, ct).ConfigureAwait(false);		await ApplyServerMemoryTuningAsync(conn, logger, ct).ConfigureAwait(false);		}
		catch (Exception ex)
		{
			// Never crash Jellyfin startup — optimizations are best-effort
			logger.LogWarning(ex, "PostgreSQL performance optimizations partially failed. Server will continue normally.");
		}
	}

	private static async Task ApplySearchIndexesAsync(NpgsqlConnection conn, Microsoft.Extensions.Logging.ILogger logger, CancellationToken ct)
	{
		// Step 1 — enable pg_trgm (bundled with PostgreSQL 17, needs CREATE EXTENSION privilege)
		try
		{
			await using var extCmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS pg_trgm;", conn);
			await extCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
			TrgmAvailable = true;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex,
				"Could not enable pg_trgm extension. GIN text-search indexes skipped. " +
				"Grant CREATE EXTENSION to the Jellyfin DB user, or run: CREATE EXTENSION pg_trgm; as superuser.");
			TrgmAvailable = false;
			return;
		}

		// Step 2 — GIN trigram indexes on BaseItems text-search columns.
		// CONCURRENTLY = zero exclusive lock. WHERE col IS NOT NULL = partial index (smaller + faster).
		var ginIndexes = new (string Name, string Sql)[]
		{
			("IX_BaseItems_Name_gin_trgm",
			 """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_Name_gin_trgm" ON "BaseItems" USING gin ("Name" gin_trgm_ops) WHERE "Name" IS NOT NULL;"""),

			("IX_BaseItems_OriginalTitle_gin_trgm",
			 """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_OriginalTitle_gin_trgm" ON "BaseItems" USING gin ("OriginalTitle" gin_trgm_ops) WHERE "OriginalTitle" IS NOT NULL;"""),

			("IX_BaseItems_Album_gin_trgm",
			 """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_Album_gin_trgm" ON "BaseItems" USING gin ("Album" gin_trgm_ops) WHERE "Album" IS NOT NULL;"""),

			("IX_BaseItems_Artists_gin_trgm",
			 """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_Artists_gin_trgm" ON "BaseItems" USING gin ("Artists" gin_trgm_ops) WHERE "Artists" IS NOT NULL;"""),

			("IX_BaseItems_AlbumArtists_gin_trgm",
			 """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_AlbumArtists_gin_trgm" ON "BaseItems" USING gin ("AlbumArtists" gin_trgm_ops) WHERE "AlbumArtists" IS NOT NULL;"""),

			("IX_BaseItems_SeriesName_gin_trgm",
			 """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_SeriesName_gin_trgm" ON "BaseItems" USING gin ("SeriesName" gin_trgm_ops) WHERE "SeriesName" IS NOT NULL;"""),

			// ItemValues: genre / tag / studio values queried on every filter panel open
			("IX_ItemValues_Value_gin_trgm",
			 """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ItemValues_Value_gin_trgm" ON "ItemValues" USING gin ("Value" gin_trgm_ops);"""),

			("IX_ItemValues_CleanValue_gin_trgm",
			 """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ItemValues_CleanValue_gin_trgm" ON "ItemValues" USING gin ("CleanValue" gin_trgm_ops);"""),

			// Peoples: actor / director search
			("IX_Peoples_Name_gin_trgm",
			 """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Peoples_Name_gin_trgm" ON "Peoples" USING gin ("Name" gin_trgm_ops) WHERE "Name" IS NOT NULL;"""),
		};

		foreach (var (name, sql) in ginIndexes)
		{
			try
			{
				await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
				await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
				logger.LogDebug("PostgreSQL GIN index ensured: {IndexName}", name);
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Failed to create GIN index {IndexName} — skipping.", name);
			}
		}

		logger.LogInformation("PostgreSQL GIN trigram indexes applied. Text search now uses index scans instead of sequential scans.");
	}

	private static async Task ApplyAutovacuumTuningAsync(NpgsqlConnection conn, Microsoft.Extensions.Logging.ILogger logger, CancellationToken ct)
	{
		// UserData: progress UPDATE fires every few seconds during playback.
		// Default scale_factor=0.2 → autovacuum only after 20% dead tuples (100k rows → 20k dead).
		// Dropping to 1% keeps the table tight and planner statistics fresh.
		var statements = new[]
		{
			// Highest churn: progress-tick updates
			"""
			ALTER TABLE "UserData" SET (
				autovacuum_vacuum_scale_factor  = 0.01,
				autovacuum_analyze_scale_factor = 0.005,
				autovacuum_vacuum_cost_delay    = 2
			);
			""",
			// High insert volume: activity log never stops growing
			"""
			ALTER TABLE "ActivityLogs" SET (
				autovacuum_vacuum_scale_factor  = 0.05,
				autovacuum_analyze_scale_factor = 0.02
			);
			""",
			// Bulk upserts during library scans
			"""
			ALTER TABLE "BaseItems" SET (
				autovacuum_vacuum_scale_factor  = 0.02,
				autovacuum_analyze_scale_factor = 0.01
			);
			""",
		};

		foreach (var sql in statements)
		{
			try
			{
				await using var cmd = new NpgsqlCommand(sql, conn);
				await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Autovacuum tuning statement failed (non-fatal, server continues).");
			}
		}

		logger.LogInformation("PostgreSQL autovacuum tuning applied to high-churn tables (UserData, ActivityLogs, BaseItems).");
	}

	private static async Task ApplyNavigationIndexesAsync(NpgsqlConnection conn, Microsoft.Extensions.Logging.ILogger logger, CancellationToken ct)
	{
		// Partial composite indexes covering the exact column order PostgreSQL needs for
		// each home-page query pattern. WHERE IsVirtualItem=false shrinks the index to real items only.
		var navIndexes = new (string Name, string Sql)[]
		{
			// Latest per library: WHERE ParentId=X AND IsVirtualItem=false ORDER BY DateCreated DESC LIMIT N
			("IX_BaseItems_ParentId_DateCreated_Partial",
			 """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_ParentId_DateCreated_Partial" ON "BaseItems" ("ParentId", "DateCreated" DESC) WHERE "IsVirtualItem" = false;"""),

			// Latest across root library (TopParentId variant used by Jellyfin home sections)
			("IX_BaseItems_TopParentId_DateCreated_Partial",
			 """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_TopParentId_DateCreated_Partial" ON "BaseItems" ("TopParentId", "DateCreated" DESC) WHERE "IsVirtualItem" = false;"""),

			// Type-filtered library views: WHERE Type=X AND TopParentId=Y ORDER BY DateCreated DESC
			("IX_BaseItems_Type_TopParentId_DateCreated_Partial",
			 """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_BaseItems_Type_TopParentId_DateCreated_Partial" ON "BaseItems" ("Type", "TopParentId", "DateCreated" DESC) WHERE "IsVirtualItem" = false;"""),

			// Resume watching: WHERE UserId=X AND PlaybackPositionTicks>0 ORDER BY LastPlayedDate DESC
			// The PK (ItemId, UserId, CustomDataKey) forces full scans when filtering by UserId alone.
			("IX_UserData_UserId_LastPlayedDate_Partial",
			 """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_UserData_UserId_LastPlayedDate_Partial" ON "UserData" ("UserId", "LastPlayedDate" DESC) WHERE "PlaybackPositionTicks" > 0;"""),

			// Favorites per user (IsFavorite=true rows are a small fraction — partial index is tiny)
			("IX_UserData_UserId_IsFavorite_Partial",
			 """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_UserData_UserId_IsFavorite_Partial" ON "UserData" ("UserId") WHERE "IsFavorite" = true;"""),
		};

		foreach (var (name, sql) in navIndexes)
		{
			try
			{
				await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
				await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
				logger.LogDebug("PostgreSQL navigation index ensured: {IndexName}", name);
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Failed to create navigation index {IndexName} — skipping.", name);
			}
		}

		logger.LogInformation("PostgreSQL navigation indexes applied. Home page, library browser and resume queries optimized.");
	}

	private static async Task ApplyServerMemoryTuningAsync(NpgsqlConnection conn, Microsoft.Extensions.Logging.ILogger logger, CancellationToken ct)
	{
		// Tune the query planner and sort memory for a typical home-media-server workload.
		// ALTER DATABASE SET is idempotent (latest SET wins) and applies to all connections.
		// These are planner hints + memory limits — zero extra RAM allocated on idle connections.
		var params_ = new (string Name, string Value, string Rationale)[]
		{
			("work_mem",              "16MB",
			 "Default 4MB → 16MB. Speeds up in-memory sorts (ORDER BY DateCreated DESC, RANDOM(), etc.)."),
			("effective_cache_size",  "1GB",
			 "Planner hint: how much OS page cache is available for PG data. Default 4GB on most installs;\n" +
			 "  lowering to 1GB makes the planner favour index scans over seq scans for medium tables."),
			("random_page_cost",      "1.1",
			 "Default 4.0 → 1.1. Tells the planner that random reads cost ~ the same as sequential (SSD/NVMe).\n" +
			 "  Critical: without this, PG avoids indexes even when they'd be faster."),
		};

		var dbNameSql = "SELECT current_database();";
		await using var nameCmd = new NpgsqlCommand(dbNameSql, conn);
		var dbName = (string?)await nameCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

		if (string.IsNullOrEmpty(dbName))
		{
			logger.LogWarning("Could not determine current database name — memory tuning skipped.");
			return;
		}

		foreach (var (name, value, rationale) in params_)
		{
			try
			{
				var sql = $"ALTER DATABASE \"{dbName}\" SET {name} = '{value}';";
				await using var cmd = new NpgsqlCommand(sql, conn);
				await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
				logger.LogDebug("PostgreSQL {ParamName} = {ParamValue}. {Rationale}", name, value, rationale);
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex,
					"Cannot set {ParamName} (needs superuser/owner). Server continues with PG defaults — still correct, just suboptimal.",
					name);
			}
		}

		logger.LogInformation("PostgreSQL server memory tuning applied (effective after next connection).");
	}

	private int TryImportMigrationHistoryFromSqlite(NpgsqlConnection postgresConnection)
	{
		try
		{
			if (_applicationPaths is null)
			{
				return 0;
			}

			var sqlitePath = Path.Combine(_applicationPaths.DataPath, "jellyfin.db");
			if (!File.Exists(sqlitePath))
			{
				return 0;
			}

			var sqliteBuilder = new SqliteConnectionStringBuilder
			{
				DataSource = sqlitePath,
				Mode = SqliteOpenMode.ReadOnly
			};

			using var sqliteConnection = new SqliteConnection(sqliteBuilder.ToString());
			sqliteConnection.Open();

			using var selectCommand = sqliteConnection.CreateCommand();
			selectCommand.CommandText = "SELECT \"MigrationId\", \"ProductVersion\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\";";

			using var reader = selectCommand.ExecuteReader();
			var inserted = 0;
			while (reader.Read())
			{
				var migrationId = reader.GetString(0);
				var productVersion = reader.IsDBNull(1) ? InitialMigrationProductVersion : reader.GetString(1);

				using var insertCommand = new NpgsqlCommand(
					"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES (@id, @ver) ON CONFLICT (\"MigrationId\") DO NOTHING;",
					postgresConnection);
				insertCommand.Parameters.AddWithValue("id", migrationId);
				insertCommand.Parameters.AddWithValue("ver", productVersion);
				inserted += insertCommand.ExecuteNonQuery();
			}

			return inserted;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not import migration history from SQLite.");
			return 0;
		}
	}

	/// <inheritdoc/>
	public async Task RunScheduledOptimisation(CancellationToken cancellationToken)
	{
		var connStr = PostgresPlugin.Instance?.Configuration?.ConnectionString;
		if (string.IsNullOrWhiteSpace(connStr))
		{
			connStr = _applicationPaths is not null
				? PostgresPlugin.ReadActivePgConnectionString(_applicationPaths)
				: null;
		}

		if (string.IsNullOrWhiteSpace(connStr))
		{
			_logger.LogWarning("RunScheduledOptimisation: no connection string available — skipping.");
			return;
		}

		var config = PostgresPlugin.Instance?.Configuration;
		await TryApplyPerformanceOptimizationsAsync(
			connStr,
			enableSearch: config?.EnableSearchOptimizations ?? true,
			enableVacuum: config?.EnableAutovacuumTuning ?? true,
			cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public Task RunShutdownTask(CancellationToken cancellationToken)
	{
		return Task.CompletedTask;
	}

	/// <inheritdoc/>
	public void OnModelCreating(ModelBuilder modelBuilder)
	{
		ArgumentNullException.ThrowIfNull(modelBuilder);
	}

	/// <inheritdoc/>
	public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
	{
		ArgumentNullException.ThrowIfNull(configurationBuilder);

		// Npgsql 6+ requires DateTimeKind.Utc for timestamptz columns.
		// Jellyfin has code paths that produce DateTimeKind.Unspecified or DateTimeKind.Local.
		// These converters normalize every DateTime to Utc on both reads and writes,
		// preventing InvalidCastException and ensuring correct UTC round-trips.
		configurationBuilder.Properties<DateTime>()
			.HaveConversion<UtcDateTimeConverter>();
		configurationBuilder.Properties<DateTime?>()
			.HaveConversion<NullableUtcDateTimeConverter>();
	}

	private sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
	{
		public UtcDateTimeConverter()
			: base(
				// WRITE: convert Local → UTC; for Utc/Unspecified just relabel as Utc.
				// SpecifyKind does NOT shift the clock — it only sets Kind — which is correct
				// for Unspecified values that are already UTC (Jellyfin uses DateTime.UtcNow).
				v => v.Kind == DateTimeKind.Local
					? v.ToUniversalTime()
					: DateTime.SpecifyKind(v, DateTimeKind.Utc),
				// READ: Npgsql v6+ already returns DateTimeKind.Utc from timestamptz.
				// SpecifyKind here is belt-and-suspenders for any edge case.
				v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
		{
		}
	}

	private sealed class NullableUtcDateTimeConverter : ValueConverter<DateTime?, DateTime?>
	{
		public NullableUtcDateTimeConverter()
			: base(
				v => v == null ? v : (DateTime?)(v.Value.Kind == DateTimeKind.Local
					? v.Value.ToUniversalTime()
					: DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)),
				v => v == null ? v : (DateTime?)DateTime.SpecifyKind(v.Value, DateTimeKind.Utc))
		{
		}
	}

	/// <inheritdoc />
	/// <remarks>
	/// Migration backup hooks from Jellyfin core are not used by this provider.
	/// Backups are implemented in the plugin UI/tasks via pg_dump.
	/// </remarks>
	public Task<string> MigrationBackupFast(CancellationToken cancellationToken)
	{
		_logger.LogInformation("PostgreSQL provider: MigrationBackupFast is a no-op. Use plugin backup actions/tasks.");
		return Task.FromResult("postgres-no-backup");
	}

	/// <inheritdoc />
	public Task RestoreBackupFast(string key, CancellationToken cancellationToken)
	{
		_logger.LogInformation("PostgreSQL provider: RestoreBackupFast called with key '{Key}'. No-op.", key);
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public Task DeleteBackup(string key)
	{
		return Task.CompletedTask;
	}

	/// <inheritdoc/>
	public async Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
	{
		ArgumentNullException.ThrowIfNull(dbContext);
		ArgumentNullException.ThrowIfNull(tableNames);

		var list = tableNames.Where(static t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (list.Count == 0)
		{
			return;
		}

		// Truncate with CASCADE keeps behavior close to a full data reset for selected tables.
		var truncateStatements = list.Select(static table => $"TRUNCATE TABLE \"{table}\" RESTART IDENTITY CASCADE;");
		var sql = string.Join('\n', truncateStatements);
		await dbContext.Database.ExecuteSqlRawAsync(sql).ConfigureAwait(false);
	}
}
