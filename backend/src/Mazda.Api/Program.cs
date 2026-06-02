using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Mazda.Api.Auth;
using Mazda.Api.Endpoints;
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
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
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

    // Lift the legacy varchar(4000) cap on prompt columns. Non-locking conversion
    // since text → varchar(4000) was the original direction; widening is a no-op
    // for existing data. Idempotent — once these are already text, the ALTERs
    // are no-ops at the Postgres planner level.
    db.Database.ExecuteSqlRaw("""
        ALTER TABLE signatures        ALTER COLUMN last_setup_prompt TYPE text;
        ALTER TABLE signatures        ALTER COLUMN last_test_prompt  TYPE text;
        ALTER TABLE generations       ALTER COLUMN setup_prompt      TYPE text;
        ALTER TABLE generations       ALTER COLUMN test_prompt       TYPE text;
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
            ADD COLUMN IF NOT EXISTS last_analyze_state_script text NOT NULL DEFAULT '';
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

app.Run();
