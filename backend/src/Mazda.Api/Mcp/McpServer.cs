using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mazda.Api.Auth;
using Mazda.Application.Common.Abstractions;
using Mazda.Application.Modules.Mcp;

namespace Mazda.Api.Mcp;

/// <summary>
/// MCP transport. We support BOTH HTTP transports the MCP ecosystem ships:
///
///   1. Streamable HTTP (newer, 2025-03-26). Single endpoint at /mcp. POST a
///      JSON-RPC message, get a single JSON response back (we don't stream
///      tool results). GET with `Accept: text/event-stream` would upgrade to
///      a server-initiated SSE channel, but we have nothing to push, so plain
///      GET returns 405.
///
///   2. Legacy HTTP+SSE (2024-11-05). Client opens an SSE stream on GET /mcp.
///      Server immediately emits an `endpoint` event whose value is the URL
///      the client should POST JSON-RPC messages to (/mcp/messages?sessionId=…).
///      Each client POST gets a 202 right away; the actual JSON-RPC response
///      is written back as a `message` event on the open SSE stream.
///
/// Auth: every endpoint goes behind RequireAuthorization() so the same JWT
/// bearer token used for the web UI authenticates the MCP client. ICurrentUser
/// is then populated automatically by the existing CurrentUser binding.
/// </summary>
public static class McpServer
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "mazda";
    private const string ServerVersion = "0.1.0";

    public static void MapMcp(this IEndpointRouteBuilder app, string path = "/mcp")
    {
        // GET is shared between both transports:
        //   • Streamable HTTP: server-initiated SSE — we have nothing to push,
        //     so return 405 unless the client explicitly negotiates SSE.
        //   • Legacy HTTP+SSE: opens the per-session SSE stream and emits the
        //     `endpoint` event with the messages POST URL.
        app.MapGet(path, HandleGet)
            .RequireAuthorization()
            .WithTags("Mcp");

        // Streamable HTTP — single-shot JSON-RPC POST.
        app.MapPost(path, HandleStreamablePost)
            .RequireAuthorization()
            .WithTags("Mcp");

        // Legacy HTTP+SSE — messages from the client to the server. Body is a
        // JSON-RPC message; response goes out over the matching SSE stream.
        app.MapPost(path + "/messages", HandleSessionPost)
            .RequireAuthorization()
            .WithTags("Mcp");
    }

    // ── Legacy HTTP+SSE: session registry ─────────────────────────
    // One SSE stream per session. The POST handler writes JSON-RPC responses
    // to the session's channel; the GET handler drains the channel and writes
    // each entry as a `message` event. Singleton, in-memory — fine for the
    // current single-instance API container.

    public sealed class SseSession
    {
        public Guid OwnerId { get; init; }
        public Channel<JsonNode> Out { get; } = Channel.CreateUnbounded<JsonNode>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    }

    public sealed class SseSessionRegistry
    {
        private readonly ConcurrentDictionary<string, SseSession> _sessions = new();
        public string Create(Guid ownerId)
        {
            var id = Guid.NewGuid().ToString("N");
            _sessions[id] = new SseSession { OwnerId = ownerId };
            return id;
        }
        public bool TryGet(string sessionId, out SseSession session) => _sessions.TryGetValue(sessionId, out session!);
        public void Remove(string sessionId)
        {
            if (_sessions.TryRemove(sessionId, out var s)) s.Out.Writer.TryComplete();
        }
    }

    // ── GET handler — content-negotiated between Streamable HTTP and legacy SSE

    private static async Task HandleGet(HttpContext ctx, CancellationToken ct)
    {
        // Streamable HTTP server-pushed-SSE is optional; if the client doesn't
        // explicitly want event-stream we have nothing useful to do.
        var wantsSse = ctx.Request.Headers.Accept.Any(a =>
            a is not null && a.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase));
        if (!wantsSse)
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        // From here on we're serving the legacy HTTP+SSE transport.
        var me = ctx.RequestServices.GetRequiredService<ICurrentUser>();
        if (me.UserId is null) { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }

        var registry = ctx.RequestServices.GetRequiredService<SseSessionRegistry>();
        var sessionId = registry.Create(me.UserId.Value);

        try
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache, no-transform";
            ctx.Response.Headers["X-Accel-Buffering"] = "no"; // nginx: don't buffer
            await ctx.Response.Body.FlushAsync(ct);

            // Tell the client where to POST messages for this session. The URL
            // is relative to the SSE stream's location — clients resolve it
            // against the SSE URL, so a server-absolute path (with PathBase)
            // is the safe shape.
            var endpoint = $"{ctx.Request.PathBase}{ctx.Request.Path}/messages?sessionId={sessionId}";
            await WriteSseEvent(ctx, "endpoint", endpoint, ct);

            if (!registry.TryGet(sessionId, out var session)) return;

            // Drain the per-session channel. ReadAllAsync completes when the
            // writer side completes (i.e. when Remove() is called below).
            await foreach (var msg in session.Out.Reader.ReadAllAsync(ct))
            {
                await WriteSseEvent(ctx, "message", msg.ToJsonString(), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — fine.
        }
        finally
        {
            registry.Remove(sessionId);
        }
    }

    private static async Task WriteSseEvent(HttpContext ctx, string @event, string data, CancellationToken ct)
    {
        // SSE wire format: each event is a sequence of "name: value\n" lines,
        // terminated by an empty line. We split `data` on \n so payloads with
        // embedded newlines stay spec-compliant.
        var sb = new StringBuilder();
        sb.Append("event: ").Append(@event).Append('\n');
        foreach (var line in data.Split('\n'))
        {
            sb.Append("data: ").Append(line).Append('\n');
        }
        sb.Append('\n');
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await ctx.Response.Body.WriteAsync(bytes, ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    // ── POST handler — Streamable HTTP (single-shot JSON-RPC) ─────

    private static async Task<IResult> HandleStreamablePost(HttpContext ctx, ISender sender, CancellationToken ct)
        => await HandlePost(ctx, sender, ct);

    // ── POST handler — Legacy SSE session message ─────────────────

    private static async Task<IResult> HandleSessionPost(
        HttpContext ctx,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var sessionId = ctx.Request.Query["sessionId"].FirstOrDefault();
        if (string.IsNullOrEmpty(sessionId)) return Results.BadRequest(new { error = "missing sessionId" });

        var registry = ctx.RequestServices.GetRequiredService<SseSessionRegistry>();
        if (!registry.TryGet(sessionId, out var session))
            return Results.NotFound(new { error = "session not found" });

        // The SSE stream and the POST that feeds it MUST belong to the same
        // user — otherwise a stolen sessionId would let someone redirect another
        // user's tool-call responses into their own SSE stream.
        var me = ctx.RequestServices.GetRequiredService<ICurrentUser>();
        if (me.UserId is null || me.UserId.Value != session.OwnerId)
            return Results.Forbid();
        var userId = me.UserId.Value;

        JsonNode? root;
        try
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ct);
            root = JsonNode.Parse(ms.ToArray());
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "invalid JSON" });
        }

        // Dispatch each message in a fresh DI scope on a background task so the
        // POST returns 202 immediately. Long-running tool calls (multiple LLM
        // round-trips + Playwright execution = minutes) then surface back to
        // the client as `message` events on the SSE stream rather than blocking
        // the POST until they finish.
        //
        // Each background task:
        //   1. Creates its own AsyncScope — the request scope is already gone
        //      by the time the task awaits, so any scoped services we capture
        //      from `ctx.RequestServices` would be disposed.
        //   2. Impersonates the original POSTer via AsyncLocal — CurrentUser
        //      would otherwise return null because there's no HttpContext.
        //   3. Resolves ISender from the new scope and dispatches.
        //   4. Writes the response (if any) back into the session's SSE channel,
        //      swallowing ChannelClosedException — the SSE stream may have
        //      ended while the tool call was still running.
        async Task DispatchInBackground(JsonNode? item)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            using var _ = CurrentUser.Impersonate(userId);
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            JsonNode? resp;
            try
            {
                resp = await DispatchOne(item, sender, CancellationToken.None);
            }
            catch (Exception ex)
            {
                resp = ErrorResponse(item?["id"], -32603, $"Internal error: {ex.Message}");
            }
            if (resp is null) return;
            try { await session.Out.Writer.WriteAsync(resp, CancellationToken.None); }
            catch (ChannelClosedException) { /* SSE stream closed — drop response */ }
            catch (InvalidOperationException) { /* same — race with channel completion */ }
        }

        if (root is JsonArray arr)
        {
            foreach (var item in arr) _ = DispatchInBackground(item);
        }
        else
        {
            _ = DispatchInBackground(root);
        }

        return Results.Accepted();
    }

    // ── JSON-RPC dispatcher ───────────────────────────────────────

    private static async Task<IResult> HandlePost(HttpContext ctx, ISender sender, CancellationToken ct)
    {
        // Parse the incoming JSON-RPC envelope. We tolerate either a single
        // request or a batch (the spec allows both); for batches we run each
        // request and return an array of responses.
        JsonNode? root;
        try
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ct);
            root = JsonNode.Parse(ms.ToArray());
        }
        catch (JsonException)
        {
            return Results.Json(ErrorResponse(null, -32700, "Parse error"));
        }

        if (root is JsonArray arr)
        {
            var responses = new JsonArray();
            foreach (var item in arr)
            {
                var resp = await DispatchOne(item, sender, ct);
                if (resp is not null) responses.Add(resp);
            }
            // Empty batch (all notifications) → 202 with no body.
            return responses.Count == 0
                ? Results.StatusCode(StatusCodes.Status202Accepted)
                : Results.Json(responses);
        }

        var single = await DispatchOne(root, sender, ct);
        // Notifications produce no response.
        return single is null
            ? Results.StatusCode(StatusCodes.Status202Accepted)
            : Results.Json(single);
    }

    private static async Task<JsonNode?> DispatchOne(JsonNode? msg, ISender sender, CancellationToken ct)
    {
        if (msg is not JsonObject obj) return ErrorResponse(null, -32600, "Invalid Request");

        var id = obj["id"];
        var method = obj["method"]?.GetValue<string>();
        var paramsNode = obj["params"];

        if (string.IsNullOrEmpty(method)) return ErrorResponse(id, -32600, "Invalid Request");

        // Notifications (no id) — handle and return null (no response).
        var isNotification = id is null;

        try
        {
            return method switch
            {
                "initialize"                 => Ok(id, InitializeResult()),
                "notifications/initialized"  => null, // notification ack only
                "tools/list"                 => Ok(id, await ListTools(sender, ct)),
                "tools/call"                 => Ok(id, await CallTool(paramsNode, sender, ct)),
                // Methods we don't implement but the client may probe — return
                // empty results so initialization doesn't trip.
                "prompts/list"               => Ok(id, new JsonObject { ["prompts"] = new JsonArray() }),
                "resources/list"             => Ok(id, new JsonObject { ["resources"] = new JsonArray() }),
                "resources/templates/list"   => Ok(id, new JsonObject { ["resourceTemplates"] = new JsonArray() }),
                "ping"                       => Ok(id, new JsonObject()),
                _ when isNotification        => null,
                _                            => ErrorResponse(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (Exception ex)
        {
            return ErrorResponse(id, -32603, $"Internal error: {ex.Message}");
        }
    }

    // ── Method implementations ────────────────────────────────────

    private static JsonObject InitializeResult() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            // We announce a tools capability. listChanged: false because we
            // don't push notifications — clients should re-list on demand.
            ["tools"] = new JsonObject { ["listChanged"] = false },
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = ServerName,
            ["version"] = ServerVersion,
        },
    };

    private static async Task<JsonObject> ListTools(ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ListMcpToolDescriptorsQuery(), ct);
        var tools = new JsonArray();
        if (result.IsSuccess)
        {
            foreach (var d in result.Value)
                tools.Add(BuildToolDescriptor(d));
        }
        return new JsonObject { ["tools"] = tools };
    }

    private static JsonObject BuildToolDescriptor(McpToolDescriptor d)
    {
        // Embed the signature's interactable/informational/user-flows JSON in
        // the tool description so the calling LLM can craft its setup_prompt
        // and test_prompt with concrete selector knowledge.
        var descriptionParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(d.Description))
            descriptionParts.Add(d.Description);
        descriptionParts.Add(
            $"Backing signature: \"{d.SignatureName}\". The signature, including " +
            "its interactable elements, informational elements, and user flows, " +
            "is provided below as JSON context — reference its selectors and flows " +
            "when crafting setup_prompt and test_prompt.\n\n" +
            "Signature JSON:\n" + d.SignatureContextJson);

        var hasSetupDefault = !string.IsNullOrWhiteSpace(d.DefaultSetupPrompt);
        var hasTestDefault  = !string.IsNullOrWhiteSpace(d.DefaultTestPrompt);

        var setupDesc = "Free-form English describing what Playwright setup should do " +
            "before the test (e.g. \"connect via CDP to Chrome on localhost:9222 and " +
            "navigate to /checkout\").";
        if (hasSetupDefault)
            setupDesc += " A default is configured for this tool — omit to use it.";

        var testDesc = "Free-form English describing what the test should do, what to " +
            "assert, and which signature elements to interact with.";
        if (hasTestDefault)
            testDesc += " A default is configured for this tool — omit to use it.";

        var properties = new JsonObject
        {
            ["setup_prompt"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = setupDesc,
            },
            ["test_prompt"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = testDesc,
            },
        };

        // Required only when no default is configured — otherwise the client
        // can rely on the tool's pre-set defaults.
        var required = new JsonArray();
        if (!hasSetupDefault) required.Add("setup_prompt");
        if (!hasTestDefault) required.Add("test_prompt");

        return new JsonObject
        {
            ["name"] = d.Name,
            ["description"] = string.Join("\n\n", descriptionParts),
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            },
        };
    }

    private static async Task<JsonObject> CallTool(JsonNode? paramsNode, ISender sender, CancellationToken ct)
    {
        if (paramsNode is not JsonObject p)
            return ToolErrorContent("Missing params object.");
        var name = p["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name)) return ToolErrorContent("Missing tool name.");

        var args = p["arguments"] as JsonObject ?? new JsonObject();
        var setupPrompt = args["setup_prompt"]?.GetValue<string>();
        var testPrompt  = args["test_prompt"]?.GetValue<string>();

        // Look up the tool by name (scoped to the authenticated user via the
        // descriptors query — same ICurrentUser ambient context).
        var listResult = await sender.Send(new ListMcpToolDescriptorsQuery(), ct);
        if (!listResult.IsSuccess) return ToolErrorContent($"Failed to list tools: {listResult.Error.Message}");
        var match = listResult.Value.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is null) return ToolErrorContent($"Tool '{name}' not found (or disabled).");

        // Hand off to the invocation handler.
        var invoke = await sender.Send(new InvokeMcpToolCommand(match.Id, setupPrompt, testPrompt), ct);
        if (!invoke.IsSuccess) return ToolErrorContent(invoke.Error.Message);

        var r = invoke.Value;
        var headline = $"status: {r.Status}{(r.ExitCode is { } code ? $" (exit code {code})" : string.Empty)}\nsummary: {r.Summary}\ngeneration_id: {r.GenerationId}";
        var body = $"{headline}\n\n--- Execution log ---\n{(string.IsNullOrEmpty(r.FullLog) ? "(no log captured)" : r.FullLog)}";

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = body,
                },
            },
            ["isError"] = r.IsError,
        };
    }

    // ── JSON-RPC envelope helpers ─────────────────────────────────

    private static JsonObject Ok(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone() ?? null,
        ["result"] = result,
    };

    private static JsonObject ErrorResponse(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone() ?? null,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    private static JsonObject ToolErrorContent(string message) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = message,
            },
        },
        ["isError"] = true,
    };
}
