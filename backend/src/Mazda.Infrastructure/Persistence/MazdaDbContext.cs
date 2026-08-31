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
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<Signature> Signatures => Set<Signature>();
    public DbSet<SignatureElement> SignatureElements => Set<SignatureElement>();
    public DbSet<SignatureDependency> SignatureDependencies => Set<SignatureDependency>();
    public DbSet<SignatureFunction> SignatureFunctions => Set<SignatureFunction>();
    public DbSet<Generation> Generations => Set<Generation>();
    public DbSet<McpTool> McpTools => Set<McpTool>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.ApplyConfiguration(new UserConfig());
        mb.ApplyConfiguration(new UserSettingsConfig());
        mb.ApplyConfiguration(new SignatureConfig());
        mb.ApplyConfiguration(new SignatureElementConfig());
        mb.ApplyConfiguration(new SignatureDependencyConfig());
        mb.ApplyConfiguration(new SignatureFunctionConfig());
        mb.ApplyConfiguration(new GenerationConfig());
        mb.ApplyConfiguration(new McpToolConfig());
    }

    Task<int> IUnitOfWork.SaveChangesAsync(CancellationToken ct) => base.SaveChangesAsync(ct);
}

internal class UserSettingsConfig : IEntityTypeConfiguration<UserSettings>
{
    public void Configure(EntityTypeBuilder<UserSettings> b)
    {
        b.ToTable("user_settings");
        b.HasKey(x => x.Id);
        b.Property(x => x.UserId).HasColumnName("user_id");
        // One settings row per user; the unique index is what makes
        // GetOrCreate safe against a double-create race.
        b.HasIndex(x => x.UserId).IsUnique();
        b.Property(x => x.CdpPort).HasColumnName("cdp_port");
        b.Property(x => x.AttachedTargetId).HasColumnName("attached_target_id").HasMaxLength(200);
        b.Property(x => x.AttachedTitle).HasColumnName("attached_title").HasMaxLength(500);
        b.Property(x => x.AttachedUrl).HasColumnName("attached_url").HasMaxLength(2000);
    }
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
        b.Property(x => x.RelationshipsJson).HasColumnName("relationships_json").HasMaxLength(500_000);
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
        // Function catalogue rows cascade with the signature — deleting a
        // signature must not leave orphan function bodies referencing dead
        // selectors, and repos never load functions independently.
        b.HasMany(x => x.Functions).WithOne()
         .HasForeignKey(f => f.SignatureId).OnDelete(DeleteBehavior.Cascade);
        b.Metadata.FindNavigation(nameof(Signature.Functions))!
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

internal class SignatureFunctionConfig : IEntityTypeConfiguration<SignatureFunction>
{
    public void Configure(EntityTypeBuilder<SignatureFunction> b)
    {
        b.ToTable("signature_functions");
        b.HasKey(x => x.Id);
        b.Property(x => x.SignatureId).HasColumnName("signature_id").IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
        // Kind serialised as its enum name string ("State" / "Action") — same
        // pattern as SignatureElement.Category. Keeps DB values grep-friendly
        // and survives enum-value reordering.
        b.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(20).IsRequired();
        // Description and preconditions are intentionally unbounded — the LLM
        // may produce a multi-sentence description, and preconditions can grow
        // as we surface more author-authored context to the planner.
        b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.Preconditions).HasColumnName("preconditions");
        // Schemas are opaque JSON blobs; storing as `text` (via unbounded
        // string) matches how relationships_json and user_flows are stored.
        // Not jsonb — we only ever round-trip them verbatim, never query into
        // them from SQL.
        b.Property(x => x.ParametersJsonSchema).HasColumnName("parameters_schema");
        b.Property(x => x.ReturnsJsonSchema).HasColumnName("returns_schema");
        // Bodies can be dozens of lines; leave unbounded.
        b.Property(x => x.Body).HasColumnName("body");
        // `order` is a SQL reserved word so it gets quoted by EF's identifier
        // quoting when we override the column name; safe here because HasColumnName
        // routes through the same quoter.
        b.Property(x => x.Order).HasColumnName("order");
        // Uniqueness on (signature, name) — the planner LLM addresses functions
        // by name in the plan JSON, and duplicate names would silently make one
        // of them unreachable. Enforce at the DB so the constraint outlives any
        // application-layer validation drift.
        b.HasIndex(x => new { x.SignatureId, x.Name }).IsUnique();
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
        // Runtime artefacts under the function-catalogue architecture. The
        // legacy columns (setup_prompt / test_prompt / setup_script /
        // analyze_state_* / test_script / final_script) live on in the DB
        // for now, but are not mapped here — EF ignores them entirely.
        // Program.cs's migration drops NOT NULL on the two that were
        // required so INSERTs from the new mapping succeed with them
        // absent.
        b.Property(x => x.Prompt).HasColumnName("prompt");
        b.Property(x => x.GatheredStateJson).HasColumnName("gathered_state_json");
        b.Property(x => x.PlanJson).HasColumnName("plan_json");
        b.Property(x => x.ExecutionEnvelopeJson).HasColumnName("execution_envelope_json");
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
