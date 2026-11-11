using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;

namespace EventWall.Api.Data;

public static class DatabasePurpose
{
    public const string ConfigurationKey = "Database:Purpose";
    public const string DemoDatabaseName = "EventWallStaging";
    public const string ProductionDatabaseName = "EventWallProd";

    public static void Validate(IConfiguration configuration, string environmentName)
    {
        var environmentPurpose = string.Equals(environmentName, Environments.Staging, StringComparison.OrdinalIgnoreCase)
            ? "Demo"
            : string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase)
                ? "Production"
                : null;
        var configuredPurpose = configuration[ConfigurationKey];
        if (environmentPurpose is not null &&
            configuredPurpose is not null &&
            !string.Equals(environmentPurpose, configuredPurpose, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} must be '{environmentPurpose}' in the {environmentName} environment.");
        }

        var purpose = configuredPurpose ?? environmentPurpose;
        if (purpose is null) return;

        var expectedDatabase = purpose.Equals("Demo", StringComparison.OrdinalIgnoreCase)
            ? DemoDatabaseName
            : purpose.Equals("Production", StringComparison.OrdinalIgnoreCase)
                ? ProductionDatabaseName
                : throw new InvalidOperationException($"{ConfigurationKey} must be either 'Demo' or 'Production'.");
        var connectionString = configuration.GetConnectionString("EventWall");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var actualDatabase = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (!string.Equals(actualDatabase, expectedDatabase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} is '{purpose}', but the EventWall connection string targets '{actualDatabase}'. Expected '{expectedDatabase}'.");
        }
    }

    public static void EnsureDemoConnectionString(string connectionString)
    {
        var databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (!string.Equals(databaseName, DemoDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Demo reset is only permitted for '{DemoDatabaseName}'; the connection targets '{databaseName}'. No data was changed.");
        }
    }
}
