using Microsoft.Data.Sqlite;

namespace AAEmu.Zone.Tests;

public sealed class NativeSqliteVersionTests
{
    [Test]
    public async Task LoadedNativeSqliteIncludesTheCve20256965Fix()
    {
        // Exercise the loaded native library, rather than only checking package metadata.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        var version = Version.Parse((string)command.ExecuteScalar()!);
        await Assert.That(version >= new Version(3, 50, 2)).IsTrue();
    }
}
