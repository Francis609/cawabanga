# CawaBanga

CawaBanga is a Rust API foundation for an AI-assisted DaVinci Resolve video editor. It asks a local Ollama model for reversible editing proposals, persists proposals as JSON, and detects a configured Resolve executable. It does not modify Resolve projects or execute generated edits.

## Requirements

- Rust toolchain with Cargo
- Docker Desktop with Compose (optional)
- Ollama running locally for proposal generation

## Build and test

```powershell
cargo fmt --all -- --check
cargo test --locked
cargo build --release --locked
```

## Run the API

```powershell
$env:CAWABANGA_BIND = "127.0.0.1:5207"
$env:OLLAMA_BASE_URL = "http://127.0.0.1:11434"
$env:OLLAMA_MODEL = "qwen2.5:7b"
$env:RESOLVE_EXECUTABLE_PATH = "C:\Program Files\Blackmagic Design\DaVinci Resolve\Resolve.exe"
cargo run --release
```

The API listens on port `5207`. Check `/health` and `/api/integrations`. Proposal routes are `POST /api/proposals`, `GET /api/proposals`, `GET /api/proposals/{id}`, and `POST /api/proposals/{id}/decision`. Proposal state is stored at `data/proposals.json` by default. Override it with `CAWABANGA_PROPOSAL_STORE`.

## Docker Compose

```powershell
docker compose up --pull always --build -d
docker compose logs -f cawabanga-api
docker compose down
```

Compose uses `host.docker.internal` to reach local Ollama. Set `RESOLVE_EXECUTABLE_PATH` if Resolve detection should inspect a custom path. Containerized API access does not launch or control the Windows Resolve GUI.
