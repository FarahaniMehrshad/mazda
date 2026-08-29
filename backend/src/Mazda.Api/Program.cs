using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Mazda.Api.Auth;
using Mazda.Api.Endpoints;
using Mazda.Api.Mcp;
using Mazda.Api.Middleware;
using Mazda.Application;
using Mazda.Application.Common.Abstractions;
using Mazda.Infrastructure;
using Mazda.Infrastructure.Persistence;
using Mazda.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

// ── Layers ────────────────────────────────────────────────────────
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// ── HTTP plumbing ─────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
// Per-process registry of open MCP legacy-SSE sessions (one channel per
// active SSE stream). Singleton so POSTs on /mcp/messages can find the same
// session that the GET handler is draining.
builder.Services.AddSingleton<Mazda.Api.Mcp.McpServer.SseSessionRegistry>();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
// Catch-all handler runs AFTER the validation one. Logs the real exception and,
// in Development, includes the message/stack in the response body so 500s aren't
// blind. Without this, the default ProblemDetails handler returns the bare
// {status:500} shape the user reported.
builder.Services.AddExceptionHandler<DevExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// ── Auth ─────────────────────────────────────────────────────────
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization();

// ── CORS for the Vite dev server ──────────────────────────────────
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(
        builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// ── OpenAPI ──────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ── Create schema on boot in dev (no migrations defined yet) ─────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MazdaDbContext>();
    db.Database.EnsureCreated();

    // EnsureCreated() does nothing on an existing DB, so columns added
    // after the table was first materialized never appear. Patch them in
    // idempotently. Postgres ADD COLUMN IF NOT EXISTS is safe to re-run.
    db.Database.ExecuteSqlRaw("""
        ALTER TABLE signatures
            ADD COLUMN IF NOT EXISTS last_setup_prompt text NOT NULL DEFAULT '',
            ADD COLUMN IF NOT EXISTS last_test_prompt  text NOT NULL DEFAULT '',
            ADD COLUMN IF NOT EXISTS last_setup_script text NOT NULL DEFAULT '',
            ADD COLUMN IF NOT EXISTS last_test_script  text NOT NULL DEFAULT '',
            ADD COLUMN IF NOT EXISTS last_final_script text NOT NULL DEFAULT '';
        """);

    // Lift the legacy varchar(4000) cap on prompt columns. Non-locking
    // conversion since text → varchar(4000) was the original direction;
    // widening is a no-op for existing data.
    //
    // Guarded on column existence: the function-catalogue rewrite dropped
    // setup_prompt / test_prompt from the Generation EF mapping, so on a
    // FRESH database those columns are never created by EnsureCreated and
    // an unguarded ALTER blows up with SQLSTATE 42703. The signatures
    // columns (last_setup_prompt / last_test_prompt) are still mapped
    // today, but wrapping them too keeps the block symmetric and
    // future-proof.
    db.Database.ExecuteSqlRaw("""
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_name='signatures' AND column_name='last_setup_prompt') THEN
                ALTER TABLE signatures ALTER COLUMN last_setup_prompt TYPE text;
            END IF;
            IF EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_name='signatures' AND column_name='last_test_prompt') THEN
                ALTER TABLE signatures ALTER COLUMN last_test_prompt TYPE text;
            END IF;
            IF EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_name='generations' AND column_name='setup_prompt') THEN
                ALTER TABLE generations ALTER COLUMN setup_prompt TYPE text;
            END IF;
            IF EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_name='generations' AND column_name='test_prompt') THEN
                ALTER TABLE generations ALTER COLUMN test_prompt TYPE text;
            END IF;
        END$$;
        """);

    // Category discriminator on signature_elements (Interactive vs Informational).
    // Existing rows back-fill to Interactive — that's what they all were before
    // this feature existed, so the default keeps prior signatures behaving the
    // same. Subsequent analyzer runs can also include Informational rows.
    // element_logic: free-form structural / lifecycle / relational notes —
    // empty by default; populated by the analyzer when worth describing.
    db.Database.ExecuteSqlRaw("""
        ALTER TABLE signature_elements
            ADD COLUMN IF NOT EXISTS category      varchar(20) NOT NULL DEFAULT 'Interactive',
            ADD COLUMN IF NOT EXISTS element_logic text        NOT NULL DEFAULT '';
        """);

    // Analyze-state phase: a deterministic Playwright script that reads the UI's
    // informational elements, the raw stdout from running it, and an LLM-built
    // semantic state JSON. All start NULL/empty; populated only after the user
    // runs the new step. Plus a per-signature memoized script for reuse.
    db.Database.ExecuteSqlRaw("""
        ALTER TABLE generations
            ADD COLUMN IF NOT EXISTS analyze_state_script      text,
            ADD COLUMN IF NOT EXISTS analyze_state_raw_output  text,
            ADD COLUMN IF NOT EXISTS analyze_state_json        text;
        ALTER TABLE signatures
            ADD COLUMN IF NOT EXISTS last_analyze_state_script      text NOT NULL DEFAULT '',
            ADD COLUMN IF NOT EXISTS last_analyze_state_raw_output  text NOT NULL DEFAULT '',
            ADD COLUMN IF NOT EXISTS last_analyze_state_json        text NOT NULL DEFAULT '';
        """);

    // The actual bundled component source (separate from `source` which is the
    // short label like "Component.tsx" / "manage-data-structure/ (33 files)").
    // Re-analyze steps need this to call the LLM again without a fresh upload.
    db.Database.ExecuteSqlRaw("""
        ALTER TABLE signatures
            ADD COLUMN IF NOT EXISTS source_code text NOT NULL DEFAULT '';
        """);

    // MCP server: per-user tool registry + provenance fields on the existing
    // generations table so MCP-driven runs are visible alongside wizard runs.
    //
    // Casing matters here: EF Core quotes identifiers, and Postgres is
    // case-sensitive when quoted. Inherited properties on Entity (Id,
    // CreatedAt, UpdatedAt) are NOT explicitly mapped anywhere in this
    // codebase, so EF emits them as "Id"/"CreatedAt"/"UpdatedAt" (PascalCase).
    // Everything we DO explicitly map via HasColumnName(...) uses snake_case.
    // The CREATE TABLE below mirrors that mixed convention exactly — otherwise
    // INSERT will fail with "column \"Id\" of relation \"mcp_tools\" does not
    // exist" and the endpoint 500s.
    db.Database.ExecuteSqlRaw("""
        -- Self-heal anyone who hit the buggy first cut of this migration where
        -- id/created_at/updated_at were snake_case (EF expects "Id"/"CreatedAt"
        -- /"UpdatedAt" with the casing preserved via quotes). Safe to run on a
        -- table that doesn't exist or already has the right shape.
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_name='mcp_tools' AND column_name='id') THEN
                ALTER TABLE mcp_tools RENAME COLUMN id TO "Id";
            END IF;
            IF EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_name='mcp_tools' AND column_name='created_at') THEN
                ALTER TABLE mcp_tools RENAME COLUMN created_at TO "CreatedAt";
            END IF;
            IF EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_name='mcp_tools' AND column_name='updated_at') THEN
                ALTER TABLE mcp_tools RENAME COLUMN updated_at TO "UpdatedAt";
            END IF;
        END$$;

        CREATE TABLE IF NOT EXISTS mcp_tools (
            "Id"                  uuid        PRIMARY KEY,
            owner_id              uuid        NOT NULL,
            signature_id          uuid        NOT NULL,
            name                  varchar(80) NOT NULL,
            display_name          varchar(120) NOT NULL DEFAULT '',
            description           text        NOT NULL DEFAULT '',
            default_setup_prompt  text        NOT NULL DEFAULT '',
            default_test_prompt   text        NOT NULL DEFAULT '',
            enabled               boolean     NOT NULL DEFAULT TRUE,
            "CreatedAt"           timestamptz NOT NULL DEFAULT NOW(),
            "UpdatedAt"           timestamptz NOT NULL DEFAULT NOW()
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_mcp_tools_owner_name ON mcp_tools(owner_id, name);
        CREATE INDEX IF NOT EXISTS ix_mcp_tools_signature ON mcp_tools(signature_id);
        """);

    db.Database.ExecuteSqlRaw("""
        ALTER TABLE generations
            ADD COLUMN IF NOT EXISTS source       varchar(20) NOT NULL DEFAULT 'Wizard',
            ADD COLUMN IF NOT EXISTS mcp_tool_id  uuid;
        CREATE INDEX IF NOT EXISTS ix_generations_mcp_tool ON generations(mcp_tool_id);
        """);

    // Signature dependencies (component A's signature can depend on component
    // B's signature → A's effective signature unions A + B + transitives).
    // Composite PK (signature_id, depends_on_signature_id). Both FKs cascade
    // so a deleted signature can't leave orphan dependency rows on either side.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS signature_dependencies (
            signature_id              uuid NOT NULL REFERENCES signatures("Id") ON DELETE CASCADE,
            depends_on_signature_id   uuid NOT NULL REFERENCES signatures("Id") ON DELETE CASCADE,
            PRIMARY KEY (signature_id, depends_on_signature_id),
            CHECK (signature_id <> depends_on_signature_id)
        );
        CREATE INDEX IF NOT EXISTS ix_signature_dependencies_target
            ON signature_dependencies(depends_on_signature_id);
        """);

    // Analyze-state scope selector — optional CSS selector that scopes the
    // analyze script to a sub-tree (modal, popover, portal, …). Both per-run
    // (generations) and per-signature (last-used pre-fill).
    db.Database.ExecuteSqlRaw("""
        ALTER TABLE generations
            ADD COLUMN IF NOT EXISTS analyze_state_scope        varchar(500);
        ALTER TABLE signatures
            ADD COLUMN IF NOT EXISTS last_analyze_state_scope   varchar(500) NOT NULL DEFAULT '';
        """);

    // Element relationships graph — output of the dedicated relationship
    // analysis step. Stored as JSON text on the signature; the runtime
    // serializer caps payload at 500_000 chars in the EF mapping. Empty
    // until the user runs the step.
    db.Database.ExecuteSqlRaw("""
        ALTER TABLE signatures
            ADD COLUMN IF NOT EXISTS relationships_json text NOT NULL DEFAULT '';
        """);

    // Generation shape shift — the function-catalogue architecture drops
    // the old per-phase script columns (setup_prompt, test_prompt,
    // setup_script, analyze_state_*, test_script, final_script) in favour
    // of three JSON artefacts: gathered_state_json (state functions'
    // snapshot), plan_json (planner LLM output), execution_envelope_json
    // (interpreter's terminal result). Plus a single free-text `prompt`
    // that replaces the old setup+test split.
    //
    // Legacy columns are intentionally NOT dropped here — this migration
    // is additive so historical rows stay legible in the DB even though
    // the EF mapping ignores them. The NOT NULL constraint is dropped on
    // the two required legacy prompt columns so INSERTs from the new
    // mapping (which doesn't populate them) succeed with them absent.
    // Once the frontend rebuild lands and no reader touches them, a
    // follow-up migration can DROP COLUMN.
    db.Database.ExecuteSqlRaw("""
        ALTER TABLE generations
            ADD COLUMN IF NOT EXISTS prompt                   text,
            ADD COLUMN IF NOT EXISTS gathered_state_json      text,
            ADD COLUMN IF NOT EXISTS plan_json                text,
            ADD COLUMN IF NOT EXISTS execution_envelope_json  text;

        -- Legacy prompt columns were NOT NULL from the initial EnsureCreated.
        -- DROP NOT NULL so new-shape INSERTs (which don't specify them) work.
        -- Idempotent — a no-op once they're already nullable.
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_name='generations' AND column_name='setup_prompt' AND is_nullable='NO') THEN
                ALTER TABLE generations ALTER COLUMN setup_prompt DROP NOT NULL;
            END IF;
            IF EXISTS (SELECT 1 FROM information_schema.columns
                       WHERE table_name='generations' AND column_name='test_prompt' AND is_nullable='NO') THEN
                ALTER TABLE generations ALTER COLUMN test_prompt DROP NOT NULL;
            END IF;
        END$$;
        """);

    // High-level function catalogue — the design-time artifact the runtime
    // planner LLM composes at request time. `kind` splits into 'State'
    // (readers that fire during gatherInformation) and 'Action' (mutators
    // the planner sequences into a plan). `body` is a TypeScript Playwright
    // snippet executed inside a per-run harness that also exposes the
    // __mz* helpers.
    //
    // Casing follows the mcp_tools convention: EF quotes the inherited
    // Entity properties (Id/CreatedAt/UpdatedAt) verbatim, so those columns
    // are PascalCase-quoted while everything explicitly mapped via
    // HasColumnName is snake_case. `order` is a Postgres reserved word so it
    // needs quoting.
    // ExecuteSqlRaw passes the SQL through string.Format(sql, params) before
    // handing it to the provider. Any literal `{` or `}` in the body — like
    // the JSON DEFAULTs on parameters_schema and returns_schema — has to be
    // doubled to survive that pass, otherwise Format treats them as
    // placeholder starts and throws "input string was not in a correct
    // format". After the Format hop, Postgres receives the single braces
    // it actually wants.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS signature_functions (
            "Id"               uuid          PRIMARY KEY,
            signature_id       uuid          NOT NULL REFERENCES signatures("Id") ON DELETE CASCADE,
            name               varchar(120)  NOT NULL,
            kind               varchar(20)   NOT NULL,
            description        text          NOT NULL DEFAULT '',
            parameters_schema  text          NOT NULL DEFAULT '{{"type":"object","properties":{{}},"required":[]}}',
            returns_schema     text          NOT NULL DEFAULT '{{"type":"null"}}',
            preconditions      text          NOT NULL DEFAULT '',
            body               text          NOT NULL DEFAULT '',
            "order"            integer       NOT NULL DEFAULT 0,
            "CreatedAt"        timestamptz   NOT NULL DEFAULT NOW(),
            "UpdatedAt"        timestamptz   NOT NULL DEFAULT NOW()
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_signature_functions_sig_name
            ON signature_functions(signature_id, name);
        CREATE INDEX IF NOT EXISTS ix_signature_functions_sig_order
            ON signature_functions(signature_id, "order");
        """);
}

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "mazda-api" }));
app.MapMazdaEndpoints();
// Mount the MCP Streamable-HTTP transport under /api/mcp so the management
// SPA can keep /mcp for the in-browser UI. Auth is JWT-bearer just like the
// REST API — MCP clients send the user's Mazda token.
app.MapMcp("/api/mcp");

app.Run();
