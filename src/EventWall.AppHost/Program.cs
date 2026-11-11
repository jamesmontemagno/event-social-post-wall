using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
if (!builder.ExecutionContext.IsRunMode)
{
    throw new InvalidOperationException("This AppHost is for local development. Use the existing Azure deployment workflow for hosting.");
}

var demo = builder.Configuration.GetValue("Demo:Enabled", true);
var dataDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "EventWall.Api", "App_Data"));
Directory.CreateDirectory(dataDirectory);
var connectionString = $"Data Source={Path.Combine(dataDirectory, demo ? "eventwall-demo.db" : "eventwall.db")}";
var staffPasscode = builder.AddParameter("staff-passcode",
    new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true);

IResourceBuilder<ProjectResource> AddApi(string role, int port)
{
    var webRoot = role == "submission"
        ? Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "EventWall.Api", "wwwroot"))
        : Path.Combine(dataDirectory, $"aspire-{role}-wwwroot");
    Directory.CreateDirectory(webRoot);
    return builder.AddProject<Projects.EventWall_Api>($"{role}-api", launchProfileName: null)
        .WithHttpEndpoint(port: port, targetPort: port, isProxied: false)
        .WithEndpoint("http", endpoint => endpoint.TargetHost = "127.0.0.1")
        .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
        .WithEnvironment("ASPNETCORE_WEBROOT", webRoot)
        .WithEnvironment("Deployment__Role", role)
        .WithEnvironment("Database__Provider", "Sqlite")
        .WithEnvironment("ConnectionStrings__EventWall", connectionString)
        .WithEnvironment("Demo__Enabled", demo.ToString())
        .WithEnvironment("Event__SubmissionsInitiallyOpen", demo.ToString())
        .WithEnvironment("Event__Slug", builder.Configuration["Event:Slug"] ?? "communityevent")
        .WithEnvironment("Event__Name", builder.Configuration["Event:Name"] ?? "Community Event")
        .WithEnvironment("Event__QuestionHashtag", builder.Configuration["Event:QuestionHashtag"] ?? "#communityevent")
        .WithHttpHealthCheck("/api/health");
}

var admin = AddApi("admin", 5081)
    .WithEnvironment("STAFF_PASSCODE", staffPasscode)
    .WithEnvironment("Public__WallSiteUrl", "http://127.0.0.1:5173")
    .WithEnvironment("Public__SubmissionSiteUrl", "http://127.0.0.1:5082");
foreach (var setting in builder.Configuration.GetSection("Social").AsEnumerable().Where(setting => setting.Value is not null))
{
    admin.WithEnvironment(setting.Key.Replace(":", "__"), setting.Value!);
}
var publicApi = AddApi("public", 5080).WaitFor(admin);
publicApi.WithEnvironment("Public__SubmissionSiteUrl", "http://127.0.0.1:5082");
AddApi("submission", 5082).WaitFor(admin);

void AddWeb(string role, int port, IResourceBuilder<ProjectResource> api)
{
    builder.AddViteApp($"{role}-web", Path.Combine("..", "EventWall.Web"))
        .WithNpm(install: false)
        .WithEndpoint("http", endpoint =>
        {
            endpoint.Port = port;
            endpoint.IsProxied = false;
            endpoint.TargetHost = "127.0.0.1";
        })
        .WithEnvironment("VITE_SITE_ROLE", role)
        .WithEnvironment("API_PROXY_TARGET", api.GetEndpoint("http"))
        .WaitFor(api);
}

AddWeb("public", 5173, publicApi);
AddWeb("admin", 5174, admin);

builder.Build().Run();
