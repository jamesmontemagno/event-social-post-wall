namespace EventWall.Api.Services;

public static class DemoMode
{
    public const string EnabledKey = "Demo:Enabled";

    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool>(EnabledKey);

    public static bool Resolve(IConfiguration configuration, string environmentName)
    {
        var enabled = IsEnabled(configuration);
        if (enabled && string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase) &&
            !configuration.GetValue<bool>("Demo:AllowInProduction"))
        {
            throw new InvalidOperationException(
                "Hosted production demos require explicit Demo:AllowInProduction approval.");
        }

        return enabled;
    }
}
