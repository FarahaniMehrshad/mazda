# Mazda · The one who knows

An AI browser-automation studio. Hand it a React component, a target URL, and a sentence in plain English; it returns a Playwright TypeScript spec you run on your own machine.

## Architecture overview

```
┌────────────────────────┐        ┌────────────────────────┐
│  React + Vite (web)    │  HTTP  │  ASP.NET Core 8 (api)  │
│  feature-based         │ ─────► │  Clean Architecture    │
│  Tanstack Query        │        │  Domain / App / Infra  │
│  Monaco · Zustand      │        │  MediatR · FluentVal   │
└────────────────────────┘        └───────────┬────────────┘
                                              │
                                              ▼
                                  ┌────────────────────────┐
                                  │  PostgreSQL 16         │
                                  └────────────────────────┘
                                              │
                                              ▼
                                     OpenAI-compatible LLM
                                  (OpenAI · Ollama · OpenRouter
                                   vLLM · DeepSeek · Groq · …)
```

### Backend (`/backend`)

Clean Architecture, four projects:

- **Mazda.Domain** — entities only (User, Signature, SignatureElement, Generation). No framework refs.
- **Mazda.Application** — use cases (MediatR commands/queries), validators, port interfaces. Depends on Domain only.
- **Mazda.Infrastructure** — EF Core / Postgres, BCrypt, JWT issuer, OpenAI adapters. Depends on Application.
- **Mazda.Api** — minimal-API endpoints, JWT bearer auth, problem-details exception handler, Swagger.

Modules carved out under `Domain/Modules/*` and `Application/Modules/*`: `Identity`, `Signatures`, `Generations`.

### Frontend (`/frontend`)

Feature-based folder layout:

- `src/app` — router, providers, auth guard
- `src/shared` — design tokens (verbatim from the Mazda design handoff), API client, primitives, icons, brand
- `src/features/auth` — sign in / register
- `src/features/studio` — landing (screen 01)
- `src/features/signatures` — library (04), upload (02), editor (03)
- `src/features/target` — target & prompt (05)
- `src/features/generation` — generated-script preview & download (06), runs index

## Running locally

You need Docker + an OpenAI-compatible LLM endpoint (the public OpenAI API or any drop-in alternative — see below).

```bash
cd mazda-app
cp .env.example .env
# edit .env: set JWT_SECRET and your LLM_BASE_URL / LLM_API_KEY
docker compose --env-file .env up --build
```

- Frontend: http://localhost:5173
- API:      http://localhost:5080/swagger
- Postgres: localhost:5432 (mazda / mazda)

### LLM provider configuration

Mazda speaks the OpenAI Chat Completions API. Point it at whatever serves that protocol by setting four env vars (`.env.example` lists working configs):

| Provider     | `LLM_BASE_URL`                            | Notes                                          |
|--------------|-------------------------------------------|------------------------------------------------|
| OpenAI       | `https://api.openai.com/v1`               | Default. Get a key at platform.openai.com.     |
| Ollama       | `http://host.docker.internal:11434/v1`    | `LLM_API_KEY=ollama` (any non-empty string).   |
| vLLM / LM Studio | `http://host.docker.internal:8000/v1` | Use the served model name.                     |
| OpenRouter   | `https://openrouter.ai/api/v1`            | Models are namespaced, e.g. `openai/gpt-4o`.   |
| DeepSeek     | `https://api.deepseek.com/v1`             | `deepseek-chat`, `deepseek-coder`.             |
| Groq         | `https://api.groq.com/openai/v1`          | Very fast; Llama family.                       |
| Together     | `https://api.together.xyz/v1`             | Lots of open-weight models.                    |
| Azure OpenAI | `https://<resource>.openai.azure.com/openai/deployments/<deployment>` | Model name in config is ignored; deployment name comes from the URL. |

Local servers (Ollama, vLLM) are reached from inside the API container at `host.docker.internal`. On Linux you may need to add `extra_hosts: ["host.docker.internal:host-gateway"]` to the `api` service.

Two compatibility notes worth knowing:

- **JSON mode**: the analyzer relies on the provider supporting `response_format=json_object`. OpenAI, OpenRouter, DeepSeek, Groq, and Together do. Older Ollama builds may not — the parser falls back to defensive parsing if the model returns JSON anyway.
- **Model quality matters**: the analyzer needs a model that can read JSX and emit clean JSON; the generator needs one that writes good Playwright. `gpt-4o-mini` + `gpt-4o` is the safe baseline. Local 7-8B models work for the analyzer but often produce shaky scripts.

### First-time DB migration

The API auto-runs `Database.Migrate()` on boot. To **create** the initial migration locally (before first build), from `backend/`:

```bash
dotnet tool install --global dotnet-ef            # one-time
dotnet ef migrations add Initial \
  --project src/Mazda.Infrastructure \
  --startup-project src/Mazda.Api
```

The migration files are committed and re-applied automatically inside the API container.

## API surface

| Method | Path                          | Auth | Purpose                       |
|--------|-------------------------------|:----:|-------------------------------|
| POST   | `/api/auth/register`          |  –   | Create account                |
| POST   | `/api/auth/login`             |  –   | Sign in                       |
| GET    | `/api/signatures/`            |  ✓   | List your signatures          |
| POST   | `/api/signatures/`            |  ✓   | Create signature              |
| POST   | `/api/signatures/analyze`     |  ✓   | LLM-extract from component    |
| GET    | `/api/signatures/{id}`        |  ✓   | Get signature                 |
| PUT    | `/api/signatures/{id}`        |  ✓   | Update signature              |
| DELETE | `/api/signatures/{id}`        |  ✓   | Delete signature              |
| POST   | `/api/generations/`           |  ✓   | Generate Playwright script    |
| GET    | `/api/generations/recent`     |  ✓   | List recent runs              |
| GET    | `/api/generations/{id}`       |  ✓   | Get a single run + script     |

## Notes on the live-browser-view pivot

Earlier design called for a live VNC stream of headless Chromium running on the backend. We deliberately stepped away from that: the server now generates the script and ships it to the user, who runs Playwright locally. This removed the entire container-orchestration / VNC / session-isolation surface area, which is the right call for an MVP.

If we ever bring the live view back, the natural integration point is a Browserbase-style managed service rather than self-hosted Xvfb + x11vnc + noVNC.
