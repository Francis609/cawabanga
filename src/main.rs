use axum::{
    Json, Router,
    extract::{Path, State},
    http::StatusCode,
    response::IntoResponse,
    routing::{get, post},
};
use chrono::{DateTime, Utc};
use reqwest::Client;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::{
    env,
    net::SocketAddr,
    path::{Path as FsPath, PathBuf},
    sync::Arc,
};
use tokio::sync::Mutex;
use uuid::Uuid;

#[derive(Clone)]
struct AppState {
    store: Arc<ProposalStore>,
    client: Client,
    ollama_url: String,
    model: String,
    resolve_path: Option<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct EditProposal {
    id: Uuid,
    created_at_utc: DateTime<Utc>,
    brief: String,
    target_application: String,
    project_path: String,
    summary: String,
    proposed_steps: Vec<String>,
    status: String,
    decision_note: Option<String>,
    decided_at_utc: Option<DateTime<Utc>>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct CreateProposalRequest {
    brief: String,
    project_path: Option<String>,
    target_application: Option<String>,
}

#[derive(Deserialize)]
struct DecisionRequest {
    decision: String,
}

struct ProposalStore {
    path: PathBuf,
    proposals: Mutex<Vec<EditProposal>>,
}

impl ProposalStore {
    fn load(path: PathBuf) -> Result<Self, String> {
        let proposals = if path.exists() {
            serde_json::from_slice(&std::fs::read(&path).map_err(|e| e.to_string())?).map_err(
                |_| {
                    format!(
                        "Proposal store file could not be parsed: {}",
                        path.display()
                    )
                },
            )?
        } else {
            Vec::new()
        };
        Ok(Self {
            path,
            proposals: Mutex::new(proposals),
        })
    }

    async fn persist(&self, proposals: &[EditProposal]) -> Result<(), String> {
        if let Some(parent) = self.path.parent() {
            tokio::fs::create_dir_all(parent)
                .await
                .map_err(|e| e.to_string())?;
        }
        let temporary = self.path.with_extension(format!("{}.tmp", Uuid::new_v4()));
        let bytes = serde_json::to_vec_pretty(proposals).map_err(|e| e.to_string())?;
        tokio::fs::write(&temporary, bytes)
            .await
            .map_err(|e| e.to_string())?;
        if let Err(error) = tokio::fs::rename(&temporary, &self.path).await {
            let _ = tokio::fs::remove_file(&temporary).await;
            return Err(error.to_string());
        }
        Ok(())
    }
}

#[tokio::main]
async fn main() {
    let store_path =
        env::var("CAWABANGA_PROPOSAL_STORE").unwrap_or_else(|_| "data/proposals.json".into());
    let store = match ProposalStore::load(PathBuf::from(store_path)) {
        Ok(store) => Arc::new(store),
        Err(error) => {
            eprintln!("{error}");
            std::process::exit(1);
        }
    };
    let state = AppState {
        store,
        client: Client::builder()
            .timeout(std::time::Duration::from_secs(600))
            .build()
            .expect("HTTP client"),
        ollama_url: env::var("OLLAMA_BASE_URL").unwrap_or_else(|_| "http://127.0.0.1:11434".into()),
        model: env::var("OLLAMA_MODEL").unwrap_or_else(|_| "qwen2.5:7b".into()),
        resolve_path: env::var("RESOLVE_EXECUTABLE_PATH").ok().or_else(|| {
            Some(r"C:\Program Files\Blackmagic Design\DaVinci Resolve\Resolve.exe".into())
        }),
    };
    let app = router(state);
    let address: SocketAddr = env::var("CAWABANGA_BIND")
        .unwrap_or_else(|_| "127.0.0.1:5207".into())
        .parse()
        .expect("CAWABANGA_BIND must be a socket address");
    let listener = tokio::net::TcpListener::bind(address)
        .await
        .expect("bind API listener");
    println!("CawaBanga API listening on http://{address}");
    axum::serve(listener, app).await.expect("serve API");
}

fn router(state: AppState) -> Router {
    Router::new()
        .route("/health", get(|| async { Json(json!({"status":"ok"})) }))
        .route("/api/integrations", get(integrations))
        .route("/api/proposals", get(list_proposals).post(create_proposal))
        .route("/api/proposals/{id}", get(get_proposal))
        .route("/api/proposals/{id}/decision", post(decide_proposal))
        .with_state(state)
}

async fn integrations(State(state): State<AppState>) -> Json<Value> {
    let executable = state.resolve_path.filter(|path| FsPath::new(path).exists());
    Json(json!({
        "davinciResolve": {"status": if executable.is_some() {"detected"} else {"not-detected"}, "executablePath": executable, "workflow":"Proposal-only; no project is opened or modified without separate user approval."},
        "adobe": {"status":"not-configured","detail":"Adobe integration is planned after Resolve connectivity is verified."},
        "agent": {"provider":"Ollama","configured":!state.ollama_url.is_empty()}
    }))
}

async fn list_proposals(State(state): State<AppState>) -> Json<Vec<EditProposal>> {
    let mut proposals = state.store.proposals.lock().await.clone();
    proposals.sort_by_key(|proposal| std::cmp::Reverse(proposal.created_at_utc));
    Json(proposals)
}

async fn get_proposal(State(state): State<AppState>, Path(id): Path<Uuid>) -> impl IntoResponse {
    let proposals = state.store.proposals.lock().await;
    match proposals.iter().find(|proposal| proposal.id == id) {
        Some(proposal) => (StatusCode::OK, Json(json!(proposal))).into_response(),
        None => StatusCode::NOT_FOUND.into_response(),
    }
}

async fn create_proposal(
    State(state): State<AppState>,
    Json(request): Json<CreateProposalRequest>,
) -> impl IntoResponse {
    if request.brief.trim().is_empty() {
        return (
            StatusCode::BAD_REQUEST,
            Json(json!({"error":"A non-empty editing brief is required."})),
        )
            .into_response();
    }
    match generate_proposal(&state, request).await {
        Ok(proposal) => {
            let mut proposals = state.store.proposals.lock().await;
            proposals.push(proposal.clone());
            if let Err(error) = state.store.persist(&proposals).await {
                proposals.pop();
                return (
                    StatusCode::INTERNAL_SERVER_ERROR,
                    Json(json!({"error":error})),
                )
                    .into_response();
            }
            (
                StatusCode::CREATED,
                [(
                    axum::http::header::LOCATION,
                    format!("/api/proposals/{}", proposal.id),
                )],
                Json(json!(proposal)),
            )
                .into_response()
        }
        Err((status, message)) => (
            status,
            Json(json!({"title":message,"status":status.as_u16()})),
        )
            .into_response(),
    }
}

async fn decide_proposal(
    State(state): State<AppState>,
    Path(id): Path<Uuid>,
    Json(request): Json<DecisionRequest>,
) -> impl IntoResponse {
    let decision = match request.decision.trim().to_ascii_lowercase().as_str() {
        "approve" => "Approved",
        "reject" => "Rejected",
        _ => {
            return (
                StatusCode::BAD_REQUEST,
                Json(json!({"error":"Decision must be 'approve' or 'reject'."})),
            )
                .into_response();
        }
    };
    let mut proposals = state.store.proposals.lock().await;
    let Some(index) = proposals.iter().position(|proposal| proposal.id == id) else {
        return StatusCode::NOT_FOUND.into_response();
    };
    if proposals[index].status == "Pending" {
        proposals[index].status = decision.into();
        proposals[index].decided_at_utc = Some(Utc::now());
        if let Err(error) = state.store.persist(&proposals).await {
            return (
                StatusCode::INTERNAL_SERVER_ERROR,
                Json(json!({"error":error})),
            )
                .into_response();
        }
    }
    (StatusCode::OK, Json(json!(proposals[index]))).into_response()
}

async fn generate_proposal(
    state: &AppState,
    request: CreateProposalRequest,
) -> Result<EditProposal, (StatusCode, String)> {
    let target = if request
        .target_application
        .as_deref()
        .is_some_and(|value| value.eq_ignore_ascii_case("adobe"))
    {
        "adobe"
    } else {
        "resolve"
    };
    let project_path = std::path::absolute(
        request
            .project_path
            .filter(|p| !p.trim().is_empty())
            .unwrap_or_else(|| ".".into()),
    )
    .map_err(|e| (StatusCode::INTERNAL_SERVER_ERROR, e.to_string()))?
    .display()
    .to_string();
    let prompt = format!(
        "You are a cautious local video-editing assistant. Return only JSON with keys summary and steps. Summary briefly describes an edit plan. Steps must be specific, reversible suggestions. Never claim edits executed, scripts run, media exported, files deleted, or projects changed. Do not request credentials or include shell commands. Keep under 1500 characters. Target application: {target}. Project path: {project_path}. Editing brief: {}",
        request.brief
    );
    let response = state
        .client
        .post(format!(
            "{}/api/generate",
            state.ollama_url.trim_end_matches('/')
        ))
        .json(&json!({"model":state.model,"prompt":prompt,"stream":false,"format":"json"}))
        .send()
        .await
        .map_err(|e| {
            (
                StatusCode::SERVICE_UNAVAILABLE,
                format!("The local Ollama service could not be reached: {e}"),
            )
        })?;
    if !response.status().is_success() {
        return Err((
            StatusCode::SERVICE_UNAVAILABLE,
            "The local Ollama service could not be reached.".into(),
        ));
    }
    let outer: Value = response.json().await.map_err(|_| {
        (
            StatusCode::BAD_GATEWAY,
            "The local model returned malformed response JSON.".into(),
        )
    })?;
    let generated = outer.get("response").and_then(Value::as_str).ok_or((
        StatusCode::BAD_GATEWAY,
        "The local model response did not contain a string proposal payload.".into(),
    ))?;
    let parsed: Value = serde_json::from_str(generated).map_err(|_| {
        (
            StatusCode::BAD_GATEWAY,
            "The local model returned malformed proposal JSON.".into(),
        )
    })?;
    let summary = parsed
        .get("summary")
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
        .unwrap_or("Review proposed editing steps.");
    let steps: Vec<String> = parsed
        .get("steps")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(Value::as_str)
        .filter(|s| !s.trim().is_empty())
        .take(20)
        .map(str::to_owned)
        .collect();
    if steps.is_empty() {
        return Err((
            StatusCode::BAD_GATEWAY,
            "The local model returned no usable editing steps.".into(),
        ));
    }
    Ok(EditProposal {
        id: Uuid::new_v4(),
        created_at_utc: Utc::now(),
        brief: request.brief,
        target_application: target.into(),
        project_path,
        summary: summary.into(),
        proposed_steps: steps,
        status: "Pending".into(),
        decision_note: None,
        decided_at_utc: None,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::{body::Body, http::Request};
    use tower::ServiceExt;

    fn test_state(path: PathBuf) -> AppState {
        AppState {
            store: Arc::new(ProposalStore::load(path).unwrap()),
            client: Client::new(),
            ollama_url: "http://127.0.0.1:1".into(),
            model: "test".into(),
            resolve_path: None,
        }
    }

    #[tokio::test]
    async fn health_endpoint_returns_ok() {
        let app = router(test_state(
            std::env::temp_dir().join(format!("cawa-{}.json", Uuid::new_v4())),
        ));
        let response = app
            .oneshot(
                Request::builder()
                    .uri("/health")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
    }

    #[tokio::test]
    async fn whitespace_brief_is_rejected_without_ollama() {
        let app = router(test_state(
            std::env::temp_dir().join(format!("cawa-{}.json", Uuid::new_v4())),
        ));
        let response = app
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/api/proposals")
                    .header("content-type", "application/json")
                    .body(Body::from(r#"{"brief":" "}"#))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::BAD_REQUEST);
    }

    #[tokio::test]
    async fn corrupted_store_is_rejected() {
        let path = std::env::temp_dir().join(format!("cawa-corrupt-{}.json", Uuid::new_v4()));
        std::fs::write(&path, "not JSON").unwrap();
        assert!(ProposalStore::load(path.clone()).is_err());
        let _ = std::fs::remove_file(path);
    }

    #[tokio::test]
    async fn concurrent_decisions_keep_one_final_state_and_persist() {
        let path = std::env::temp_dir().join(format!("cawa-concurrent-{}.json", Uuid::new_v4()));
        let store = ProposalStore::load(path.clone()).unwrap();
        let id = Uuid::new_v4();
        let proposal = EditProposal {
            id,
            created_at_utc: Utc::now(),
            brief: "edit".into(),
            target_application: "resolve".into(),
            project_path: ".".into(),
            summary: "summary".into(),
            proposed_steps: vec!["trim".into()],
            status: "Pending".into(),
            decision_note: None,
            decided_at_utc: None,
        };
        {
            let mut values = store.proposals.lock().await;
            values.push(proposal);
            store.persist(&values).await.unwrap();
        }
        let shared = Arc::new(store);
        let tasks = ["approve", "reject"].into_iter().map(|decision| {
            let shared = shared.clone();
            tokio::spawn(async move {
                let mut values = shared.proposals.lock().await;
                let row = values.iter_mut().find(|p| p.id == id).unwrap();
                if row.status == "Pending" {
                    row.status = if decision == "approve" {
                        "Approved"
                    } else {
                        "Rejected"
                    }
                    .into();
                    row.decided_at_utc = Some(Utc::now());
                }
                let result = row.clone();
                shared.persist(&values).await.unwrap();
                result
            })
        });
        let results = futures_join(tasks).await;
        assert_eq!(results[0].status, results[1].status);
        let reloaded = ProposalStore::load(path.clone()).unwrap();
        assert_eq!(reloaded.proposals.lock().await[0].status, results[0].status);
        let _ = std::fs::remove_file(path);
    }

    async fn futures_join<T: Send + 'static>(
        tasks: impl Iterator<Item = tokio::task::JoinHandle<T>>,
    ) -> Vec<T> {
        let mut out = Vec::new();
        for task in tasks {
            out.push(task.await.unwrap());
        }
        out
    }
}
