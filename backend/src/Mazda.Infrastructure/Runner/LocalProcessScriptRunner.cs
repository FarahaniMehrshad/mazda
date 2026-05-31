using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Mazda.Application.Common.Abstractions;
using Microsoft.Extensions.Logging;

namespace Mazda.Infrastructure.Runner;

/// <summary>
/// Spawns "npx playwright test" against a freshly-materialized .spec.ts file
/// in a per-run tmp directory. Streams stdout + stderr into IExecutionRegistry
/// line-by-line, returning the terminal result when the process exits.
///
/// Auto-rewrites loopback hosts (127.0.0.1 / localhost) to host.docker.internal
/// so CDP-attach scripts written from the user's perspective still reach a
/// browser running on the Docker host.
/// </summary>
public sealed class LocalProcessScriptRunner(
    IExecutionRegistry registry,
    ILogger<LocalProcessScriptRunner> log) : IScriptRunner
{
    private static readonly Regex LoopbackHost = new(
        @"\b(?:127\.0\.0\.1|localhost)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string PlaywrightConfig = """
        import { defineConfig } from '@playwright/test';
        export default defineConfig({
          testDir: '.',
          timeout: 60_000,
          // Print each step (await page.click(...), expect(...), etc.) as it runs so
          // a hang shows the LAST step it was stuck on rather than just "timeout".
          reporter: [['list', { printSteps: true }]],
          use: {
            // Per-action timeout (default is 30s = half the test budget). 10s is short
            // enough to fail fast on a stuck locator and surface a precise stack trace
            // pointing at the offending line.
            actionTimeout: 10_000,
            navigationTimeout: 15_000,
            trace: 'retain-on-failure',
            screenshot: 'only-on-failure',
            video: 'off',
          },
        });
        """;

    private static readonly string PackageJson = """
        {
          "name": "mazda-run",
          "version": "0.0.0",
          "private": true,
          "type": "module"
        }
        """;

    public async Task<RunnerCompletion> RunAsync(Guid generationId, string script, CancellationToken ct = default)
    {
        var workdirRoot = Environment.GetEnvironmentVariable("MAZDA_RUNNER_WORKDIR") ?? "/var/mazda/runs";
        var runDir = Path.Combine(workdirRoot, generationId.ToString("N"));
        Directory.CreateDirectory(runDir);

        var rewritten = RewriteHosts(script);
        await File.WriteAllTextAsync(Path.Combine(runDir, "mazda.spec.ts"), rewritten, ct);
        await File.WriteAllTextAsync(Path.Combine(runDir, "playwright.config.ts"), PlaywrightConfig, ct);
        await File.WriteAllTextAsync(Path.Combine(runDir, "package.json"), PackageJson, ct);

        // Node ESM resolution doesn't look at the global node_modules. Symlink the
        // run dir's node_modules to the global one so `@playwright/test` resolves.
        var localNodeModules = Path.Combine(runDir, "node_modules");
        var globalNodeModules = Environment.GetEnvironmentVariable("MAZDA_GLOBAL_NODE_MODULES")
                                ?? "/usr/lib/node_modules";
        if (!Directory.Exists(localNodeModules) && !File.Exists(localNodeModules))
        {
            try { File.CreateSymbolicLink(localNodeModules, globalNodeModules); }
            catch (Exception ex) { log.LogWarning(ex, "Could not symlink node_modules → {Global}", globalNodeModules); }
        }

        registry.Append(generationId, $"$ cd {runDir}");
        registry.Append(generationId, "$ npx playwright test mazda.spec.ts --reporter=list");

        var fullLog = new StringBuilder();
        void Emit(string line)
        {
            registry.Append(generationId, line);
            fullLog.AppendLine(line);
        }

        var psi = new ProcessStartInfo
        {
            FileName = "npx",
            WorkingDirectory = runDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // NO_COLOR so the line reporter emits plain text — easier to render in the browser.
            Environment = { ["NO_COLOR"] = "1", ["FORCE_COLOR"] = "0" },
        };
        psi.ArgumentList.Add("playwright");
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add("mazda.spec.ts");
        // No --reporter flag here: the CLI form `list` would override the config's
        // reporter array and drop `printSteps: true`. Let playwright.config.ts win.

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Emit(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Emit(e.Data); };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            var msg = $"runner: failed to start npx — {ex.Message}";
            Emit(msg);
            // NOTE: do NOT call registry.Complete here. The handler will call it
            // AFTER the terminal status has been persisted, so the SSE end event
            // only fires once the DB reflects the final state.
            return new RunnerCompletion(RunnerExitKind.Errored, -1, fullLog.ToString());
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            Emit("runner: cancelled by user");
            return new RunnerCompletion(RunnerExitKind.Cancelled, -1, fullLog.ToString());
        }

        var kind = proc.ExitCode switch
        {
            0 => RunnerExitKind.Passed,
            1 => RunnerExitKind.FailedTest, // Playwright returns 1 on test failure.
            _ => RunnerExitKind.Errored,
        };

        // Point the user at any trace zips Playwright left behind on failure.
        // `trace: 'retain-on-failure'` writes to <runDir>/test-results/.../trace.zip.
        if (kind != RunnerExitKind.Passed)
        {
            var resultsDir = Path.Combine(runDir, "test-results");
            if (Directory.Exists(resultsDir))
            {
                foreach (var tracePath in Directory.EnumerateFiles(resultsDir, "trace.zip", SearchOption.AllDirectories))
                {
                    Emit($"runner: trace saved → {tracePath}");
                    Emit("runner: open with `npx playwright show-trace " + tracePath + "` (run on host)");
                }
            }
        }
        Emit($"runner: exit {proc.ExitCode} ({kind.ToString().ToLowerInvariant()})");

        // Caller (StartExecutionHandler) is responsible for calling
        // registry.Complete AFTER it has persisted the terminal status so the
        // SSE end event arrives at the client only once the DB reflects it.
        return new RunnerCompletion(kind, proc.ExitCode, fullLog.ToString());
    }

    // Injected helper. Solves the CDP-via-host.docker.internal triple-bind:
    //   1. Chrome v111+ rejects /json/version requests whose Host header isn't
    //      localhost/127.0.0.1. We send Host: localhost:<port> on the probe.
    //   2. Chrome's response advertises the WS URL using the Host we sent —
    //      i.e. ws://localhost:<port>/devtools/... — which from inside the
    //      container resolves to the container's own loopback, where nothing
    //      is listening. We rewrite the ws URL host to host.docker.internal.
    //   3. Playwright's connectOverCDP accepts a ws:// URL directly, so we
    //      hand it the rewritten URL and skip Playwright's own HTTP probe.
    private const string CdpHelperBlock = """

// === Mazda runner: CDP-via-host.docker.internal helper (auto-injected) ===
import * as __mzHttp from 'node:http';
async function __mzConnectViaHost(c: any, host: string, port: number): Promise<any> {
  const payload = await new Promise<string>((resolve, reject) => {
    const req = __mzHttp.request({
      host, port, path: '/json/version', method: 'GET',
      headers: { Host: 'localhost:' + port },
    }, (res: any) => {
      let body = '';
      res.on('data', (chunk: any) => { body += chunk.toString(); });
      res.on('end', () => resolve(body));
    });
    req.on('error', reject);
    req.end();
  });
  const json = JSON.parse(payload);
  const wsUrl = String(json.webSocketDebuggerUrl)
    .replace(/^ws:\/\/[^/:]+(:\d+)?/, 'ws://' + host + ':' + port);
  // Chrome's WebSocket endpoint applies the same Host-header check as
  // /json/version. Spoof Host: localhost:<port> on the upgrade too.
  return await c.connectOverCDP({
    endpointURL: wsUrl,
    headers: { Host: 'localhost:' + port },
  });
}
// === end auto-injected ===

""";

    private static string RewriteHosts(string source)
    {
        // NOTE: we do NOT blanket-rewrite localhost → host.docker.internal anymore.
        // That mangled unrelated URLs (e.g. a TARGET_PREFIX the script matches
        // against the page's actual url). Only connectOverCDP calls get rewritten;
        // everything else (page.goto, prefix matchers, comments) stays verbatim.

        var rewritten = source;
        var changed = false;

        // 1 — string form: chromium.connectOverCDP('http://(localhost|127.0.0.1|host.docker.internal):9222')
        rewritten = Regex.Replace(
            rewritten,
            @"(\w+)\.connectOverCDP\s*\(\s*(['""`])https?://(?:localhost|127\.0\.0\.1|host\.docker\.internal):(\d+)[^'""`]*\2\s*\)",
            m =>
            {
                changed = true;
                var receiver = m.Groups[1].Value;
                var port = m.Groups[3].Value;
                return $"__mzConnectViaHost({receiver}, 'host.docker.internal', {port})";
            },
            RegexOptions.IgnoreCase);

        // 2 — object form: chromium.connectOverCDP({ endpointURL: '...', ... }).
        //   Uses a one-level brace-balancer to handle nested headers: { Host: ... }.
        rewritten = Regex.Replace(
            rewritten,
            @"(\w+)\.connectOverCDP\s*\(\s*\{(?:[^{}]|\{[^{}]*\})*\bendpointURL\s*:\s*['""`]https?://(?:localhost|127\.0\.0\.1|host\.docker\.internal):(\d+)[^'""`]*['""`](?:[^{}]|\{[^{}]*\})*\}\s*\)",
            m =>
            {
                changed = true;
                var receiver = m.Groups[1].Value;
                var port = m.Groups[2].Value;
                return $"__mzConnectViaHost({receiver}, 'host.docker.internal', {port})";
            },
            RegexOptions.IgnoreCase);

        if (!changed) return rewritten;

        // 3 — inject the helper after the last top-level import statement.
        var importMatches = Regex.Matches(rewritten, @"^[ \t]*import\s.*$", RegexOptions.Multiline);
        int insertPos = 0;
        if (importMatches.Count > 0)
        {
            var last = importMatches[importMatches.Count - 1];
            insertPos = last.Index + last.Length;
        }
        return rewritten.Insert(insertPos, CdpHelperBlock);
    }
}
