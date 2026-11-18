using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;
using Mazda.Infrastructure.Runner;
using Xunit;

namespace Mazda.Tests;

/// <summary>The execute harness limits each step's time, not the whole run's.</summary>
public class ExecuteHarnessTests
{
    internal static string Build(int steps, string body = "await page.waitForTimeout(10);")
    {
        var sig = Signature.Create(Guid.NewGuid(), "Demo", null, null);
        sig.AddFunction(SignatureFunction.Create("DoThing", FunctionKind.Action, "does a thing", "{}", "{}", body, 0));
        var plan = new Plan(null, Enumerable.Range(1, steps).Select(i => new PlanStep($"s{i}", "DoThing", "{}")).ToList());
        return FunctionHarnessBuilder.BuildExecuteHarness(sig, null, plan, new CdpTarget(9333, null));
    }

    [Fact]
    public void Each_step_races_its_own_budget()
    {
        var spec = Build(3);
        Assert.Contains($"const __mz_stepBudgetMs = {FunctionHarnessBuilder.ExecuteStepBudgetMs};", spec);
        Assert.Contains("await Promise.race([", spec);
        Assert.Contains("__mz_handler(page, __mz_step.args ?? {}),", spec);
        Assert.Contains("Step timed out after", spec);
        // The timer is cleared on success and on failure, so it never fires for a finished step.
        Assert.Equal(2, CountOf(spec, "clearTimeout(__mz_timer);"));
    }

    [Fact]
    public void Test_timeout_grows_with_the_plan_instead_of_a_fixed_total()
    {
        var spec = Build(20);
        Assert.Contains("test.setTimeout(__mz_plan.steps.length * (__mz_stepBudgetMs + 30_000) + 60_000);", spec);
        // Set inside the test body, before the first step runs.
        Assert.True(spec.IndexOf("test.setTimeout(", StringComparison.Ordinal)
                    < spec.IndexOf("for (const __mz_step of __mz_plan.steps)", StringComparison.Ordinal));
    }

    private static int CountOf(string s, string part)
    {
        var n = 0;
        for (var i = s.IndexOf(part, StringComparison.Ordinal); i >= 0; i = s.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
