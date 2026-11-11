using EventWall.Api.Data;
using EventWall.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

var resetDemo = args.SequenceEqual(
    ["--reset-demo", "--confirm-demo-reset"],
    StringComparer.Ordinal);
var validateDemoTarget = args.SequenceEqual(["--validate-demo-target"], StringComparer.Ordinal);
if (args.Length > 0 && !resetDemo && !validateDemoTarget)
{
    throw new InvalidOperationException("Usage: EventWall.DatabaseSetup [--validate-demo-target | --reset-demo --confirm-demo-reset]");
}

var connectionString = Required("EVENTWALL_DEPLOYMENT_SQL");
if (resetDemo || validateDemoTarget)
{
    DatabasePurpose.EnsureDemoConnectionString(connectionString);
}
if (validateDemoTarget)
{
    Console.WriteLine($"Configured database target is '{DatabasePurpose.DemoDatabaseName}'.");
    return;
}
var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
var options = new DbContextOptionsBuilder<EventWallDbContext>().UseSqlServer(connectionString).Options;
await using (var database = new EventWallDbContext(options))
{
    await database.Database.MigrateAsync();
    await EventSeeder.SeedAsync(database, configuration);
    if (resetDemo)
    {
        var result = await DemoDatabaseReset.ResetAsync(database, DateTime.UtcNow, CancellationToken.None);
        Console.WriteLine(
            $"Demo reset complete: removed {result.PostsRemoved} posts, {result.ModerationActionsRemoved} moderation actions, and {result.BlockedAuthorsRemoved} blocked authors; fictional fixtures were reseeded.");
    }
}

await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();
foreach (var (user, passwordKey) in new[]
{
    ("eventwall_public", "EVENTWALL_PUBLIC_SQL_PASSWORD"),
    ("eventwall_submission", "EVENTWALL_SUBMISSION_SQL_PASSWORD"),
    ("eventwall_admin", "EVENTWALL_ADMIN_SQL_PASSWORD")
})
{
    var password = Required(passwordKey).Replace("'", "''");
    await using var command = connection.CreateCommand();
    command.CommandText = $"""
        IF DATABASE_PRINCIPAL_ID(N'{user}') IS NULL
            CREATE USER [{user}] WITH PASSWORD = '{password}';
        ELSE
            ALTER USER [{user}] WITH PASSWORD = '{password}';
        """;
    await command.ExecuteNonQueryAsync();
}

await using (var grants = connection.CreateCommand())
{
    grants.CommandText = """
        GRANT SELECT ON OBJECT::dbo.Events TO eventwall_public;
        GRANT SELECT ON OBJECT::dbo.Promos TO eventwall_public;
        GRANT SELECT ON OBJECT::dbo.Posts TO eventwall_public;
        REVOKE INSERT ON OBJECT::dbo.Posts FROM eventwall_public;
        REVOKE INSERT ON OBJECT::dbo.ModerationActions FROM eventwall_public;
        GRANT SELECT ON OBJECT::dbo.Events TO eventwall_submission;
        GRANT SELECT, INSERT ON OBJECT::dbo.Posts TO eventwall_submission;
        GRANT INSERT ON OBJECT::dbo.ModerationActions TO eventwall_submission;
        GRANT SELECT ON OBJECT::dbo.__EFMigrationsHistory TO eventwall_admin;
        GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.Events TO eventwall_admin;
        GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.Posts TO eventwall_admin;
        GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.ModerationActions TO eventwall_admin;
        GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.ConnectorDailyUsage TO eventwall_admin;
        GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.ProviderCredentials TO eventwall_admin;
        GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.BlockedAuthors TO eventwall_admin;
        GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.ConnectorCheckpoints TO eventwall_admin;
        GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.StaffMembers TO eventwall_admin;
        GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.Promos TO eventwall_admin;
        """;
    await grants.ExecuteNonQueryAsync();
}
Console.WriteLine("Schema initialized; public, submission, and admin runtime users have separate table-scoped grants.");
foreach (var (user, passwordKey, canModerate, canSubmit) in new[]
{
    ("eventwall_public", "EVENTWALL_PUBLIC_SQL_PASSWORD", false, false),
    ("eventwall_submission", "EVENTWALL_SUBMISSION_SQL_PASSWORD", false, true),
    ("eventwall_admin", "EVENTWALL_ADMIN_SQL_PASSWORD", true, true)
})
{
    var runtime = new SqlConnectionStringBuilder(connectionString)
    {
        UserID = user,
        Password = Required(passwordKey)
    };
    await using var runtimeConnection = new SqlConnection(runtime.ConnectionString);
    await runtimeConnection.OpenAsync();
    await using var check = runtimeConnection.CreateCommand();
    check.CommandText = """
        SELECT HAS_PERMS_BY_NAME('dbo.Posts', 'OBJECT', 'INSERT'),
               HAS_PERMS_BY_NAME('dbo.Posts', 'OBJECT', 'UPDATE'),
               HAS_PERMS_BY_NAME('dbo.ProviderCredentials', 'OBJECT', 'SELECT'),
               HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE'),
               HAS_PERMS_BY_NAME('dbo.BlockedAuthors', 'OBJECT', 'SELECT'),
               HAS_PERMS_BY_NAME('dbo.BlockedAuthors', 'OBJECT', 'INSERT'),
               HAS_PERMS_BY_NAME('dbo.ConnectorCheckpoints', 'OBJECT', 'SELECT'),
               HAS_PERMS_BY_NAME('dbo.ConnectorCheckpoints', 'OBJECT', 'INSERT'),
               HAS_PERMS_BY_NAME('dbo.ConnectorCheckpoints', 'OBJECT', 'UPDATE'),
               HAS_PERMS_BY_NAME('dbo.ModerationActions', 'OBJECT', 'INSERT'),
               HAS_PERMS_BY_NAME('dbo.ModerationActions', 'OBJECT', 'SELECT'),
               HAS_PERMS_BY_NAME('dbo.ModerationActions', 'OBJECT', 'UPDATE'),
               HAS_PERMS_BY_NAME('dbo.ModerationActions', 'OBJECT', 'DELETE'),
               HAS_PERMS_BY_NAME('dbo.StaffMembers', 'OBJECT', 'SELECT'),
               HAS_PERMS_BY_NAME('dbo.StaffMembers', 'OBJECT', 'INSERT'),
               HAS_PERMS_BY_NAME('dbo.StaffMembers', 'OBJECT', 'UPDATE'),
               HAS_PERMS_BY_NAME('dbo.StaffMembers', 'OBJECT', 'DELETE'),
               HAS_PERMS_BY_NAME('dbo.Promos', 'OBJECT', 'SELECT'),
               HAS_PERMS_BY_NAME('dbo.Promos', 'OBJECT', 'INSERT'),
               HAS_PERMS_BY_NAME('dbo.Promos', 'OBJECT', 'UPDATE'),
               HAS_PERMS_BY_NAME('dbo.Promos', 'OBJECT', 'DELETE');
        """;
    await using var reader = await check.ExecuteReaderAsync();
    await reader.ReadAsync();
    var expected = new[]
    {
        canSubmit, canModerate, canModerate, false, canModerate, canModerate,
        canModerate, canModerate, canModerate, canSubmit, canModerate, canModerate,
        canModerate, canModerate, canModerate, canModerate, canModerate,
        user != "eventwall_submission", canModerate, canModerate, canModerate
    };
    if (expected.Where((allowed, index) => (reader.IsDBNull(index) ? 0 : reader.GetInt32(index)) != (allowed ? 1 : 0)).Any())
    {
        throw new InvalidOperationException($"Unexpected runtime database privileges for {user}.");
    }
}
Console.WriteLine("Verified all runtime logins: public is read-only, submission can append questions, only admin can moderate. None can create tables.");

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Set {name} in the deployment process environment.");
