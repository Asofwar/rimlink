using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Rimlink.Server.Storage;

/// <summary>Used only by `dotnet ef` at design time.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RimlinkDbContext>
{
    public RimlinkDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RimlinkDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
