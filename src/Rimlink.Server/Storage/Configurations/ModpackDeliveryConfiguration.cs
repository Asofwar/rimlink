using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rimlink.Server.Storage.Entities;

namespace Rimlink.Server.Storage.Configurations;

public sealed class ModpackPackageConfiguration : IEntityTypeConfiguration<ModpackPackage>
{
    public void Configure(EntityTypeBuilder<ModpackPackage> e)
    {
        e.ToTable("modpack_packages"); e.HasKey(p => p.Id); e.Property(p => p.Id).HasMaxLength(32);
    }
}

public sealed class ModpackPublicationConfiguration : IEntityTypeConfiguration<ModpackPublication>
{
    public void Configure(EntityTypeBuilder<ModpackPublication> e)
    {
        e.ToTable("modpack_publication"); e.HasKey(p => p.Id); e.Property(p => p.Id).ValueGeneratedNever();
    }
}
