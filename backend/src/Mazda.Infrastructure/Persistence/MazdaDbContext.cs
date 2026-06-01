using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Identity;
using Mazda.Domain.Modules.Signatures;
using Mazda.Domain.Modules.Generations;

namespace Mazda.Infrastructure.Persistence;

public class MazdaDbContext(DbContextOptions<MazdaDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Signature> Signatures => Set<Signature>();
    public DbSet<SignatureElement> SignatureElements => Set<SignatureElement>();
    public DbSet<Generation> Generations => Set<Generation>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.ApplyConfiguration(new UserConfig());
        mb.ApplyConfiguration(new SignatureConfig());
        mb.ApplyConfiguration(new SignatureElementConfig());
        mb.ApplyConfiguration(new GenerationConfig());
    }

    Task<int> IUnitOfWork.SaveChangesAsync(CancellationToken ct) => base.SaveChangesAsync(ct);
}

internal class UserConfig : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Email).HasMaxLength(320).IsRequired();
        b.HasIndex(x => x.Email).IsUnique();
        b.Property(x => x.DisplayName).HasMaxLength(120).IsRequired();
        b.Property(x => x.PasswordHash).HasMaxLength(200).IsRequired();
    }
}

internal class SignatureConfig : IEntityTypeConfiguration<Signature>
{
    public void Configure(EntityTypeBuilder<Signature> b)
    {
        b.ToTable("signatures");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Version).HasMaxLength(20);
        b.Property(x => x.Source).HasMaxLength(500);
        b.Property(x => x.Type).HasMaxLength(40);
        b.Property(x => x.HumanDescription).HasColumnName("human_description").HasMaxLength(50_000);
        b.Property(x => x.UserFlowsJson).HasColumnName("user_flows").HasMaxLength(200_000);
        // Prompts are intentionally unbounded so the user can paste arbitrarily
        // large context (long English instructions, copied logs, etc.).
        b.Property(x => x.LastSetupPrompt).HasColumnName("last_setup_prompt");
        b.Property(x => x.LastTestPrompt).HasColumnName("last_test_prompt");
        b.Property(x => x.LastSetupScript).HasColumnName("last_setup_script");
        b.Property(x => x.LastTestScript).HasColumnName("last_test_script");
        b.Property(x => x.LastFinalScript).HasColumnName("last_final_script");
        b.HasIndex(x => new { x.OwnerId, x.Name });
        b.HasMany(x => x.Elements).WithOne()
         .HasForeignKey(e => e.SignatureId).OnDelete(DeleteBehavior.Cascade);
        b.Metadata.FindNavigation(nameof(Signature.Elements))!
         .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal class SignatureElementConfig : IEntityTypeConfiguration<SignatureElement>
{
    public void Configure(EntityTypeBuilder<SignatureElement> b)
    {
        b.ToTable("signature_elements");
        b.HasKey(x => x.Id);
        b.Property(x => x.ElementSelector).HasColumnName("element_selector").HasMaxLength(500).IsRequired();
        b.Property(x => x.Category).HasColumnName("category").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ElementType).HasMaxLength(40).IsRequired();
        b.Property(x => x.ElementLogic).HasColumnName("element_logic");
        b.Property(x => x.ElementDescription).HasMaxLength(1000);
        b.HasIndex(x => new { x.SignatureId, x.Order });
    }
}

internal class GenerationConfig : IEntityTypeConfiguration<Generation>
{
    public void Configure(EntityTypeBuilder<Generation> b)
    {
        b.ToTable("generations");
        b.HasKey(x => x.Id);
        b.Property(x => x.SetupPrompt).HasColumnName("setup_prompt");
        b.Property(x => x.SetupScript).HasColumnName("setup_script");
        b.Property(x => x.TestPrompt).HasColumnName("test_prompt");
        b.Property(x => x.TestScript).HasColumnName("test_script");
        b.Property(x => x.FinalScript).HasColumnName("final_script");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ExecutionStatus).HasColumnName("execution_status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ExecutionLog).HasColumnName("execution_log");
        b.Property(x => x.ExitCode).HasColumnName("exit_code");
        b.Property(x => x.ExecutionStartedAt).HasColumnName("execution_started_at");
        b.Property(x => x.ExecutionEndedAt).HasColumnName("execution_ended_at");
        b.HasIndex(x => new { x.OwnerId, x.CreatedAt });
    }
}
