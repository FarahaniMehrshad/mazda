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
        Assert.Contains("__mzRunStep(__mz_handler, __mz_step),", spec);
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

    [Fact]
    public void A_step_whose_input_was_lost_is_retried_once()
    {
        var spec = Build(2);
        // Only non-GET xhr/fetch requests count: those could have changed something on the server.
        Assert.Contains("!['GET', 'HEAD', 'OPTIONS'].includes(r.method())) { __mz_writes++;", spec);
        // Retried only when it timed out waiting for the app and nothing was written.
        Assert.Contains("if (__mz_writes !== before || !/waitFor(Response|Request)/.test(msg) || !/Timeout/i.test(msg)) throw e;", spec);
        Assert.Contains("type: 'step-retried'", spec);
        // Exactly one retry: the handler appears twice in the helper, never in a loop.
        Assert.Equal(2, CountOf(spec, "await handler(page, step.args ?? {})"));
        // The retry runs inside the step's own budget race.
        Assert.True(spec.IndexOf("const __mzRunStep", StringComparison.Ordinal)
                    < spec.IndexOf("for (const __mz_step of __mz_plan.steps)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_server_error_on_a_write_the_step_sent_fails_that_step()
    {
        var spec = Build(2);
        // Each write is tagged with the step that sent it, so a late 5xx to an earlier step can't fail this one.
        Assert.Contains("__mz_writeStep.set(r, __mz_curStep);", spec);
        Assert.Contains("if (__mz_writeStep.has(req) && resp.status() >= 500)", spec);
        Assert.Contains("__mz_serverErrors.filter((x) => x.step === stepId)", spec);
        // Checked after the step finishes and before a retry: a step that wrote and got a 5xx is never retried.
        Assert.Equal(2, CountOf(spec, "__mzServerFailed(step.id);"));
        Assert.True(spec.IndexOf("      __mzServerFailed(step.id);\n      const msg", StringComparison.Ordinal) > 0
                    || spec.IndexOf("      __mzServerFailed(step.id);\r\n      const msg", StringComparison.Ordinal) > 0);
    }

    [Fact]
    public void An_empty_plan_is_nothing_to_do_and_says_why()
    {
        var empty = new Plan("No card index to select.", []);
        Assert.Equal("The plan has no steps, so nothing was done. The planner said: No card index to select.", empty.NothingToDo);
        Assert.Equal("The plan has no steps, so nothing was done.", new Plan(null, []).NothingToDo);
        Assert.Null(new Plan(null, [new PlanStep("s1", "DoThing", "{}")]).NothingToDo);
    }

    private static int CountOf(string s, string part)
    {
        var n = 0;
        for (var i = s.IndexOf(part, StringComparison.Ordinal); i >= 0; i = s.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
