using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Database.Providers.Postgres.Api;

internal static class PluginSchemaMigrationChecksum
{
    internal static string Compute(string id, string sql)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(sql);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(id, "\n", sql)));
        return Convert.ToHexString(bytes);
    }
}
