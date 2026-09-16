using Jellyfin.Database.Providers.Postgres.Logging;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// The plugin log is the only place where a failure is recorded, so its content is part of the contract:
/// a line that says only "something failed" is useless when diagnosing a user report.
/// </summary>
public sealed class LoggingTests
{
    [Fact]
    public void FailuresCarryLevelMessageAndStackTrace()
    {
        var path = PostgresLog.CurrentLogPath;
        var offset = File.Exists(path) ? new FileInfo(path).Length : 0;

        Exception thrown;
        try
        {
            throw new InvalidOperationException("fallo de prueba del logger");
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        PostgresLog.Error("[Test] contexto del fallo", thrown);

        var text = ReadAppended(path, offset);
        Assert.Contains("[ERROR]", text, StringComparison.Ordinal);
        Assert.Contains("fallo de prueba del logger", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", text, StringComparison.Ordinal);

        // The stack trace is what makes the failure diagnosable.
        Assert.Contains(nameof(LoggingTests), text, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgresFailuresCarryTheServerDetail()
    {
        var path = PostgresLog.CurrentLogPath;
        var offset = File.Exists(path) ? new FileInfo(path).Length : 0;

        var exception = new PostgresException(
            "duplicate key value violates unique constraint \"PK_UserData\"",
            "ERROR",
            "ERROR",
            "23505",
            detail: "Key (ItemId, UserId, CustomDataKey)=(ae19, 1111, A) already exists.",
            tableName: "UserData",
            constraintName: "PK_UserData");

        PostgresLog.Error("[Test] violación de PK", exception);

        var text = ReadAppended(path, offset);
        Assert.Contains("SqlState=23505", text, StringComparison.Ordinal);
        Assert.Contains("Table=UserData", text, StringComparison.Ordinal);
        Assert.Contains("Constraint=PK_UserData", text, StringComparison.Ordinal);
        Assert.Contains("already exists", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LogFileLivesNextToTheOtherPluginLogs()
    {
        Assert.Equal(
            Path.GetFileName(TestModule.LogDirectory),
            Path.GetFileName(Path.GetDirectoryName(PostgresLog.CurrentLogPath)));
        Assert.StartsWith("Postgres-", Path.GetFileName(PostgresLog.CurrentLogPath), StringComparison.Ordinal);
    }

    private static string ReadAppended(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
