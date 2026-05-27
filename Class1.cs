using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
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

	private readonly ILogger<PostgresDatabaseProvider> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="PostgresDatabaseProvider"/> class.
	/// </summary>
	/// <param name="applicationPaths">Application paths service. Kept for DI compatibility with other providers.</param>
	/// <param name="logger">A logger.</param>
	public PostgresDatabaseProvider(IApplicationPaths? applicationPaths, ILogger<PostgresDatabaseProvider> logger)
	{
		// applicationPaths is intentionally unused; nullable to allow design-time instantiation.
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

		// Npgsql 6+ enforces DateTimeKind.Utc strictly. Jellyfin stores DateTimes without
		// explicit UTC kind — this switch restores legacy permissive behavior.
		AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

		var customOptions = customProviderOptions.Options;
		var commandTimeout = GetOption(customOptions, "command-timeout", e => int.Parse(e, CultureInfo.InvariantCulture), () => 60);

		options.UseNpgsql(
			customProviderOptions.ConnectionString,
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
		TryRepairInitialMigrationHistory(customProviderOptions.ConnectionString);

		_logger.LogInformation("PostgreSQL provider initialized for Jellyfin.");
	}

	private void TryRepairInitialMigrationHistory(string connectionString)
	{
		try
		{
			using var conn = new NpgsqlConnection(connectionString);
			conn.Open();

			// If there are no core tables, this is likely a clean DB and no repair is needed.
			using (var hasActivityCmd = new NpgsqlCommand(
				"SELECT to_regclass('public.\"ActivityLogs\"') IS NOT NULL;",
				conn))
			{
				var hasActivityTable = Convert.ToBoolean(hasActivityCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
				if (!hasActivityTable)
				{
					return;
				}
			}

			using (var createHistoryCmd = new NpgsqlCommand(@"
				CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
					""MigrationId"" character varying(150) NOT NULL,
					""ProductVersion"" character varying(32) NOT NULL,
					CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY (""MigrationId"")
				);", conn))
			{
				createHistoryCmd.ExecuteNonQuery();
			}

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
