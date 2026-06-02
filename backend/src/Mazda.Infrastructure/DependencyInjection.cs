using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mazda.Application.Common.Abstractions;
using Mazda.Infrastructure.Llm;
using Mazda.Infrastructure.Persistence;
using Mazda.Infrastructure.Persistence.Repositories;
using Mazda.Infrastructure.Security;

namespace Mazda.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration cfg)
    {
        // Persistence
        var conn = cfg.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres missing.");
        services.AddDbContext<MazdaDbContext>(o => o.UseNpgsql(conn));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<MazdaDbContext>());
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ISignatureRepository, SignatureRepository>();
        services.AddScoped<IGenerationRepository, GenerationRepository>();

        // Security
        services.Configure<JwtOptions>(cfg.GetSection("Jwt"));
        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddSingleton<IJwtIssuer, JwtIssuer>();

        // LLM (any OpenAI-compatible provider: OpenAI, Ollama, vLLM, OpenRouter, …)
        services.Configure<LlmOptions>(cfg.GetSection("Llm"));
        services.AddSingleton<IComponentAnalyzer, LlmComponentAnalyzer>();
        services.AddSingleton<IInformationalAnalyzer, LlmInformationalAnalyzer>();
        services.AddSingleton<IFlowAnalyzer, LlmFlowAnalyzer>();
        services.AddSingleton<IScriptGenerator, LlmScriptGenerator>();
        services.AddSingleton<IStateBuilder, LlmStateBuilder>();

        // Script execution (Playwright via Node inside the API container).
        services.AddSingleton<IExecutionRegistry, Runner.InMemoryExecutionRegistry>();
        services.AddSingleton<IScriptRunner, Runner.LocalProcessScriptRunner>();

        return services;
    }
}
