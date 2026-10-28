using Microsoft.EntityFrameworkCore;

namespace Rimlink.Server.Storage;

public static class StorageSetup
{
    public static IServiceCollection AddRimlinkStorage(this IServiceCollection services, DataDirectory dataDirectory)
    {
        services.AddSingleton(dataDirectory);
        services.AddDbContext<RimlinkDbContext>(options =>
            options.UseSqlite($"Data Source={dataDirectory.DatabasePath};Pooling=True"));
        return services;
    }

    /// <summary>Applies pending migrations and the connection-wide pragmas (WAL, 5 s busy timeout).</summary>
    public static void MigrateRimlinkStorage(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RimlinkDbContext>();
        db.Database.Migrate();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
    }
}
