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
    //
    // The blocks that used to add and widen the pre-catalogue script/prompt
    // columns are gone — those columns are dropped at the end of this
    // startup sequence, so adding them here first would churn the table on
    // every boot.

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
    // The pre-catalogue columns these replaced are dropped at the end of
    // this sequence, so no DROP NOT NULL dance is needed for them any more.
    db.Database.ExecuteSqlRaw("""
        ALTER TABLE generations
            ADD COLUMN IF NOT EXISTS prompt                   text,
            ADD COLUMN IF NOT EXISTS gathered_state_json      text,
            ADD COLUMN IF NOT EXISTS plan_json                text,
            ADD COLUMN IF NOT EXISTS execution_envelope_json  text;
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

    // Per-user settings. Today it holds only the CDP attachment — which port
    // Chrome is debugging on and which tab (by CDP targetId) MAZDA drives.
    // Before this, every harness took the first page Chrome reported, which is
    // arbitrary once more than one tab is open.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS user_settings (
            "Id"                 uuid          PRIMARY KEY,
            user_id              uuid          NOT NULL,
            cdp_port             integer       NOT NULL DEFAULT 9222,
            attached_target_id   varchar(200)  NOT NULL DEFAULT '',
            attached_title       varchar(500)  NOT NULL DEFAULT '',
            attached_url         varchar(2000) NOT NULL DEFAULT '',
            "CreatedAt"          timestamptz   NOT NULL DEFAULT NOW(),
            "UpdatedAt"          timestamptz   NOT NULL DEFAULT NOW()
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_user_settings_user ON user_settings(user_id);
        """);

    // ── Retire the pre-catalogue schema ──────────────────────────────
    // Every column below belonged to the Setup / Analyze-state / Test /
    // Merge wizard that the function catalogue replaced. Nothing in the EF
    // model maps them and no query reads them, so they are dead weight that
    // also keeps setting the "unmapped column still referenced by an
    // unguarded ALTER" trap.
    //
    // MUST STAY LAST in this sequence — an earlier block that still added or
    // widened one of these would be undone on the same boot.
    //
    // DROP COLUMN IF EXISTS is idempotent, so this is a no-op from the second
    // startup onward and on any freshly-created database. It is, however, a
    // one-way door for existing data in those columns.
    db.Database.ExecuteSqlRaw("""
        ALTER TABLE signatures
            DROP COLUMN IF EXISTS last_setup_prompt,
            DROP COLUMN IF EXISTS last_test_prompt,
            DROP COLUMN IF EXISTS last_setup_script,
            DROP COLUMN IF EXISTS last_test_script,
            DROP COLUMN IF EXISTS last_final_script,
            DROP COLUMN IF EXISTS last_analyze_state_script,
            DROP COLUMN IF EXISTS last_analyze_state_raw_output,
            DROP COLUMN IF EXISTS last_analyze_state_json,
            DROP COLUMN IF EXISTS last_analyze_state_scope;

        ALTER TABLE generations
            DROP COLUMN IF EXISTS setup_prompt,
            DROP COLUMN IF EXISTS test_prompt,
            DROP COLUMN IF EXISTS setup_script,
            DROP COLUMN IF EXISTS test_script,
            DROP COLUMN IF EXISTS final_script,
            DROP COLUMN IF EXISTS analyze_state_script,
            DROP COLUMN IF EXISTS analyze_state_raw_output,
            DROP COLUMN IF EXISTS analyze_state_json,
            DROP COLUMN IF EXISTS analyze_state_scope;
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
