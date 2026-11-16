using Mazda.Application.Modules.RoundTrip;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Runner;

/// <summary>Holds automatically fixed bodies to the same static rules as generated ones.</summary>
public sealed class CatalogueLintService : ICatalogueLint
{
    public IReadOnlyList<string> Issues(SignatureFunction function, string body)
        => Llm.CatalogueBodyLint.IsAction(function.Kind.ToString())
            ? Llm.CatalogueBodyLint.Issues(body)
            : Array.Empty<string>();
}
