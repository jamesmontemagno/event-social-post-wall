using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EventWall.Api.Data;

public sealed class EventWallDbContextFactory : IDesignTimeDbContextFactory<EventWallDbContext>
{
    public EventWallDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<EventWallDbContext>()
            .UseSqlServer(
                Environment.GetEnvironmentVariable("ConnectionStrings__EventWall") ??
                "Server=localhost;Database=EventWall;Integrated Security=true;TrustServerCertificate=true;")
            .Options;
        return new EventWallDbContext(options);
    }
}
