using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Identity;
using Mazda.Domain.Modules.Signatures;
using Mazda.Domain.Modules.Generations;
using Mazda.Domain.Modules.Mcp;

namespace Mazda.Infrastructure.Persistence;

public class MazdaDbContext(DbContextOptions<MazdaDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Signature> Signatures => Set<Signature>();
    public DbSet<SignatureElement> SignatureElements => Set<SignatureElement>();
    public DbSet<SignatureDependency> SignatureDependencies => Set<SignatureDependency>();
    public DbSet<Generation> Generations => Set<Generation>();
    public DbSet<McpTool> McpTools => Set<McpTool>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.ApplyConfiguration(new UserConfig());
        mb.ApplyConfiguration(new SignatureConfig());
        mb.ApplyConfiguration(new SignatureElementConfig());
        mb.ApplyConfiguration(new SignatureDependencyConfig());
        mb.ApplyConfiguration(new GenerationConfig());
        mb.ApplyConfiguration(new McpToolConfig());
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
        b.Property(x => x.SourceCode).HasColumnName("source_code");
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
        b.Property(x => x.LastAnalyzeStateScript).HasColumnName("last_analyze_state_script");
        b.Property(x => x.LastAnalyzeStateRawOutput).HasColumnName("last_analyze_state_raw_output");
        b.Property(x => x.LastAnalyzeStateJson).HasColumnName("last_analyze_state_json");
        b.HasIndex(x => new { x.OwnerId, x.Name });
        b.HasMany(x => x.Elements).WithOne()
         .HasForeignKey(e => e.SignatureId).OnDelete(DeleteBehavior.Cascade);
        b.Metadata.FindNavigation(nameof(Signature.Elements))!
         .SetPropertyAccessMode(PropertyAccessMode.Field);
        // Dependencies (signature → signatures it depends on). The cascade is
        // on the signature->dependency edge: if THIS signature is deleted, the
        // join rows go too. The reverse edge (dep target deleted) is handled
        // by a separate FK without a navigation; see SignatureDependencyConfig.
        b.HasMany(x => x.Dependencies).WithOne()
         .HasForeignKey(d => d.SignatureId).OnDelete(DeleteBehavior.Cascade);
        b.Metadata.FindNavigation(nameof(Signature.Dependencies))!
         .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal class SignatureDependencyConfig : IEntityTypeConfiguration<SignatureDependency>
{
    public void Configure(EntityTypeBuilder<SignatureDependency> b)
    {
        b.ToTable("signature_dependencies");
        // Composite PK — a signature can appear once per (target) dependency.
        b.HasKey(x => new { x.SignatureId, x.DependsOnSignatureId });
        b.Property(x => x.SignatureId).HasColumnName("signature_id");
        b.Property(x => x.DependsOnSignatureId).HasColumnName("depends_on_signature_id");
        // FK to the TARGET signature, so when the dep target is deleted these
        // rows are cleaned up too. The dependency-OWNER side is configured on
        // SignatureConfig (HasMany Dependencies WithOne) — we leave THAT FK
        // implicit here (no .HasOne to Signatures on the owner side) so EF
        // doesn't double-register the relationship.
        b.HasOne<Signature>().WithMany()
         .HasForeignKey(d => d.DependsOnSignatureId)
         .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.DependsOnSignatureId);
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
        b.Property(x => x.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.McpToolId).HasColumnName("mcp_tool_id");
        b.HasIndex(x => x.McpToolId);
        b.Property(x => x.SetupPrompt).HasColumnName("setup_prompt");
        b.Property(x => x.SetupScript).HasColumnName("setup_script");
        b.Property(x => x.AnalyzeStateScript).HasColumnName("analyze_state_script");
        b.Property(x => x.AnalyzeStateRawOutput).HasColumnName("analyze_state_raw_output");
        b.Property(x => x.AnalyzeStateJson).HasColumnName("analyze_state_json");
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

internal class McpToolConfig : IEntityTypeConfiguration<McpTool>
{
    public void Configure(EntityTypeBuilder<McpTool> b)
    {
        b.ToTable("mcp_tools");
        b.HasKey(x => x.Id);
        b.Property(x => x.OwnerId).HasColumnName("owner_id").IsRequired();
        b.Property(x => x.SignatureId).HasColumnName("signature_id").IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(80).IsRequired();
        b.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(120);
        b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.DefaultSetupPrompt).HasColumnName("default_setup_prompt");
        b.Property(x => x.DefaultTestPrompt).HasColumnName("default_test_prompt");
        b.Property(x => x.Enabled).HasColumnName("enabled");
        // Tool NAMES are how the calling LLM addresses a tool (`tools/call name=...`).
        // They must be unique per user. The DB index enforces it.
        b.HasIndex(x => new { x.OwnerId, x.Name }).IsUnique();
        b.HasIndex(x => x.SignatureId);
    }
}
