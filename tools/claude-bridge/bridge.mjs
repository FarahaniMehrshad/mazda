#!/usr/bin/env node
// Claude Code bridge: lets Mazda (running in Docker) use the Claude Code CLI
// installed and logged in on this computer.
//
//   node tools/claude-bridge/bridge.mjs
//
// Mazda sends a prompt over HTTP; the bridge runs `claude -p` with it and
// returns the answer. Your Claude login never leaves this computer.
//
// Settings come from the repository's .env (the same file docker compose reads):
//   CLAUDE_BRIDGE_TOKEN   required; shared secret Mazda sends as a Bearer token
//   CLAUDE_BRIDGE_PORT    default 8787
//   CLAUDE_BRIDGE_HOST    default: the docker0 address (what host.docker.internal
//                         resolves to on Linux), else 127.0.0.1
//   CLAUDE_BRIDGE_MAX_PARALLEL  default 2
//   CLAUDE_BIN            default "claude"
//
// Every call is isolated from your own Claude Code setup: no tools, no MCP
// servers, no settings files (so none of your hooks), no CLAUDE.md (it runs in
// an empty temporary folder) and no saved session.

import http from 'node:http';
import { spawn, execFileSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync, readFileSync, existsSync } from 'node:fs';
import { networkInterfaces, tmpdir } from 'node:os';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { timingSafeEqual } from 'node:crypto';

const here = dirname(fileURLToPath(import.meta.url));
const envFile = resolve(here, '..', '..', '.env');

function readDotEnv(file) {
  const out = {};
  if (!existsSync(file)) return out;
  for (const line of readFileSync(file, 'utf8').split(/\r?\n/)) {
    const m = line.match(/^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$/);
    if (!m) continue;
    let v = m[2];
    if ((v.startsWith('"') && v.endsWith('"')) || (v.startsWith("'") && v.endsWith("'"))) v = v.slice(1, -1);
    out[m[1]] = v;
  }
  return out;
}

const fileEnv = readDotEnv(envFile);
const env = (k, d) => process.env[k] ?? fileEnv[k] ?? d;

const TOKEN = env('CLAUDE_BRIDGE_TOKEN', '');
const PORT = Number(env('CLAUDE_BRIDGE_PORT', '8787'));
const MAX_PARALLEL = Math.max(1, Number(env('CLAUDE_BRIDGE_MAX_PARALLEL', '2')));
const CLAUDE = env('CLAUDE_BIN', 'claude');
const MAX_BODY = 50 * 1024 * 1024;

if (!TOKEN) {
  console.error(`CLAUDE_BRIDGE_TOKEN is not set (looked in the environment and ${envFile}).`);
  console.error('Create one, then restart the Mazda API so it picks it up too:');
  console.error(`  echo "CLAUDE_BRIDGE_TOKEN=$(node -e "console.log(require('crypto').randomBytes(32).toString('hex'))")" >> ${envFile}`);
  console.error('  docker compose up -d api');
  process.exit(1);
}

function defaultHost() {
  // On Linux, host.docker.internal:host-gateway is the docker0 address.
  const d0 = networkInterfaces().docker0?.find((a) => a.family === 'IPv4');
  if (d0) return d0.address;
  // docker0 is "down" while no container uses the default network, and Node
  // then leaves it out, but its address still works and containers resolve
  // host.docker.internal to it.
  try {
    const m = execFileSync('ip', ['-4', '-o', 'addr', 'show', 'docker0'], { encoding: 'utf8', timeout: 5000 })
      .match(/inet (\d+\.\d+\.\d+\.\d+)/);
    if (m) return m[1];
  } catch {}
  return '127.0.0.1';
}
const HOST = env('CLAUDE_BRIDGE_HOST', defaultHost());

let version = 'unknown';
try {
  version = execFileSync(CLAUDE, ['--version'], { encoding: 'utf8', timeout: 20000 }).trim();
} catch (e) {
  console.error(`Could not run "${CLAUDE} --version": ${e.message}`);
  console.error('Is Claude Code installed and on PATH? Set CLAUDE_BIN to its full path otherwise.');
  process.exit(1);
}

// ── A small queue: at most MAX_PARALLEL claude processes at once ──
let running = 0;
const waiting = [];
const acquire = () => new Promise((r) => { if (running < MAX_PARALLEL) { running++; r(); } else waiting.push(r); });
const release = () => { const next = waiting.shift(); if (next) next(); else running--; };

const authorized = (req) => {
  const got = Buffer.from(String(req.headers.authorization ?? ''));
  const want = Buffer.from(`Bearer ${TOKEN}`);
  return got.length === want.length && timingSafeEqual(got, want);
};

const send = (res, status, body) => {
  const json = JSON.stringify(body);
  res.writeHead(status, { 'content-type': 'application/json', 'content-length': Buffer.byteLength(json) });
  res.end(json);
};

const readBody = (req) => new Promise((resolveBody, reject) => {
  const chunks = [];
  let size = 0;
  req.on('data', (c) => {
    size += c.length;
    if (size > MAX_BODY) { reject(new Error('request too large')); req.destroy(); return; }
    chunks.push(c);
  });
  req.on('end', () => resolveBody(Buffer.concat(chunks).toString('utf8')));
  req.on('error', reject);
});

/** Runs one isolated `claude -p` call. Resolves to { status, body }. */
function complete({ model, system, content, timeoutSec }) {
  return new Promise((done) => {
    const dir = mkdtempSync(join(tmpdir(), 'mazda-claude-'));
    const sysFile = join(dir, '.system-prompt.txt');
    writeFileSync(sysFile, system ?? '');
    const args = [
      '-p', '--input-format', 'stream-json', '--output-format', 'stream-json', '--verbose',
      '--system-prompt-file', sysFile,
      '--tools', '',
      '--strict-mcp-config',
      '--setting-sources', '',
      '--no-session-persistence',
    ];
    if (model) args.push('--model', model);

    const blocks = (content ?? []).map((p) => p.type === 'image'
      ? { type: 'image', source: { type: 'base64', media_type: p.mediaType || 'image/jpeg', data: p.data } }
      : { type: 'text', text: String(p.text ?? '') });
    const message = JSON.stringify({ type: 'user', message: { role: 'user', content: blocks } }) + '\n';

    const started = Date.now();
    const child = spawn(CLAUDE, args, { cwd: dir, stdio: ['pipe', 'pipe', 'pipe'] });
    let out = '';
    let err = '';
    let finished = false;
    const finish = (status, body) => {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      try { rmSync(dir, { recursive: true, force: true }); } catch {}
      done({ status, body });
    };
    const limit = Math.max(10, Number(timeoutSec) || 600) * 1000;
    const timer = setTimeout(() => {
      child.kill('SIGKILL');
      finish(504, { error: `Claude Code did not answer within ${Math.round(limit / 1000)} s.` });
    }, limit);

    child.stdout.on('data', (c) => { out += c; });
    child.stderr.on('data', (c) => { err += c; if (err.length > 20000) err = err.slice(-20000); });
    child.on('error', (e) => finish(500, { error: `Could not start Claude Code: ${e.message}` }));
    child.on('close', (code) => {
      let result = null;
      for (const line of out.split('\n')) {
        if (!line.trim()) continue;
        try { const d = JSON.parse(line); if (d.type === 'result') result = d; } catch {}
      }
      const ms = Date.now() - started;
      if (!result) {
        finish(502, { error: `Claude Code exited (code ${code}) without an answer. ${err.trim().slice(-600)}`.trim() });
        return;
      }
      const u = result.usage ?? {};
      const inputTokens = (u.input_tokens ?? 0) + (u.cache_creation_input_tokens ?? 0) + (u.cache_read_input_tokens ?? 0);
      const outputTokens = u.output_tokens ?? 0;
      if (result.is_error || result.subtype !== 'success') {
        const why = typeof result.result === 'string' && result.result.trim()
          ? result.result.trim()
          : (result.errors?.join?.('; ') || result.subtype || 'unknown error');
        finish(502, { error: `Claude Code failed: ${why}` });
        return;
      }
      console.log(`${new Date().toISOString()}  ${model || 'default'}  ${ms} ms  ${inputTokens} in / ${outputTokens} out`);
      finish(200, {
        text: result.result ?? '',
        model: model || '',
        inputTokens,
        outputTokens,
        costUsd: result.total_cost_usd ?? 0,
        durationMs: ms,
      });
    });
    child.stdin.on('error', () => {});
    child.stdin.end(message);
  });
}

const server = http.createServer(async (req, res) => {
  try {
    if (!authorized(req)) return send(res, 401, { error: 'Missing or wrong bridge token.' });
    const path = (req.url ?? '').split('?')[0];
    if (req.method === 'GET' && path === '/health') return send(res, 200, { ok: true, version, running, waiting: waiting.length });
    if (req.method === 'POST' && path === '/v1/complete') {
      let body;
      try { body = JSON.parse(await readBody(req)); } catch (e) { return send(res, 400, { error: `Bad request: ${e.message}` }); }
      if (!Array.isArray(body?.content) || body.content.length === 0) return send(res, 400, { error: 'content must be a non-empty array.' });
      await acquire();
      try {
        const r = await complete(body);
        return send(res, r.status, r.body);
      } finally {
        release();
      }
    }
    return send(res, 404, { error: 'Not found.' });
  } catch (e) {
    return send(res, 500, { error: String(e?.message ?? e) });
  }
});
server.requestTimeout = 0;   // calls can take minutes; each one has its own timeout
server.headersTimeout = 60_000;

server.on('error', (e) => {
  console.error(`Could not listen on ${HOST}:${PORT}: ${e.message}`);
  if (e.code === 'EADDRNOTAVAIL') console.error('Set CLAUDE_BRIDGE_HOST to an address of this computer (e.g. 127.0.0.1).');
  process.exit(1);
});
server.listen(PORT, HOST, () => {
  console.log(`Claude Code bridge (${version}) listening on http://${HOST}:${PORT}`);
  console.log(`Mazda reaches it at http://host.docker.internal:${PORT} — choose "Claude Code on this computer" in Settings.`);
  console.log(`At most ${MAX_PARALLEL} call(s) at once. Ctrl+C to stop.`);
});
