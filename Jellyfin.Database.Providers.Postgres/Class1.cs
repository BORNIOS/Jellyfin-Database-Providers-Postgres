using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
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

		// Self-heal migration history if schema was pre-created but __EFMigrationsHistory
		// does not contain the initial migration marker.
		TryRepairInitialMigrationHistory(effectiveConnectionString);
		TryMitigateItemValuesIndexContention(effectiveConnectionString);

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
	public Task RunScheduledOptimisation(CancellationToken cancellationToken)
	{
		_logger.LogInformation("PostgreSQL scheduled optimization is managed externally; no internal task executed.");
		return Task.CompletedTask;
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
