"""Managed local ASR sidecar with a structured readiness contract."""
import os
import threading
import traceback

import numpy as np
import uvicorn
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse


def _truthy(name: str) -> bool:
    return os.environ.get(name, "").strip().lower() in ("1", "true", "yes", "on")


def configure_hub_access() -> bool:
    allow_online = _truthy("ASR_ALLOW_ONLINE")
    if not allow_online:
        os.environ.setdefault("HF_HUB_OFFLINE", "1")
        os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")
    return allow_online


configure_hub_access()

MODEL_SOURCE = os.environ.get("ASR_MODEL", "Qwen/Qwen3-ASR-0.6B")
MODEL_DEVICE = os.environ.get("ASR_DEVICE", "auto")
ASR_LANGUAGE = os.environ.get("ASR_LANGUAGE", "Chinese")
SERVER_VERSION = "1"

app = FastAPI()
model = None
health_state = "loading"
health_detail = f"Loading model {MODEL_SOURCE}"
health_lock = threading.Lock()


def resolve_device(requested: str) -> str:
    wanted = (requested or "auto").strip().lower()
    try:
        import torch
        if wanted in ("auto", "cuda") and torch.cuda.is_available():
            return "cuda"
    except Exception:
        pass
    if wanted == "cuda":
        print("[ASR] CUDA requested but unavailable; using cpu", flush=True)
    return "cpu" if wanted in ("auto", "cuda") else requested


@app.on_event("startup")
def start_model_load():
    threading.Thread(target=load_model, name="asr-model-loader", daemon=True).start()


def resolve_model_source(source: str) -> str:
    if os.path.isdir(source):
        return source

    hub = os.environ.get("HF_HUB_CACHE")
    if not hub:
        home = os.environ.get("HF_HOME") or os.path.join(os.path.expanduser("~"), ".cache", "huggingface")
        hub = os.path.join(home, "hub")

    repo_dir = os.path.join(hub, "models--" + source.replace("/", "--"))
    refs_main = os.path.join(repo_dir, "refs", "main")
    if os.path.isfile(refs_main):
        with open(refs_main, encoding="utf-8") as handle:
            revision = handle.read().strip()
        snapshot = os.path.join(repo_dir, "snapshots", revision)
        if os.path.isdir(snapshot):
            return snapshot

    snapshots = os.path.join(repo_dir, "snapshots")
    if os.path.isdir(snapshots):
        for name in sorted(os.listdir(snapshots)):
            snapshot = os.path.join(snapshots, name)
            if os.path.isdir(snapshot):
                return snapshot
    return source


def load_model():
    global model, health_state, health_detail
    try:
        allow_online = configure_hub_access()
        from qwen_asr import Qwen3ASRModel

        device = resolve_device(MODEL_DEVICE)
        source = resolve_model_source(MODEL_SOURCE)
        print(f"[ASR] Loading model: {source} (device={device}, online={allow_online})", flush=True)
        loaded_model = Qwen3ASRModel.from_pretrained(
            source,
            device_map=device,
            local_files_only=not allow_online,
        )
        with health_lock:
            model = loaded_model
            health_state = "ready"
            health_detail = f"Model {MODEL_SOURCE} is ready"
        print("[ASR] Model ready.", flush=True)
    except Exception as exc:
        with health_lock:
            health_state = "failed"
            health_detail = f"ASR-SIDECAR-005 {type(exc).__name__}: {exc}"
        print("[ASR] Model load failed:\n" + traceback.format_exc(), flush=True)


@app.get("/health")
def health():
    with health_lock:
        payload = {
            "status": health_state,
            "detail": health_detail,
            "version": SERVER_VERSION,
        }
    return JSONResponse(payload, status_code=200 if payload["status"] == "ready" else 503)


@app.post("/recognize")
async def recognize(request: Request):
    with health_lock:
        current_state = health_state
        current_detail = health_detail
        current_model = model
    if current_state != "ready" or current_model is None:
        return JSONResponse(
            {"status": current_state, "detail": current_detail},
            status_code=503,
        )

    sr = int(request.query_params.get("sr", 16000))
    pcm = await request.body()
    if not pcm:
        return JSONResponse({"text": ""})

    audio = np.frombuffer(pcm, dtype=np.int16).astype(np.float32) / 32768.0
    kwargs = {"language": ASR_LANGUAGE} if ASR_LANGUAGE else {}
    results = current_model.transcribe((audio, sr), **kwargs)
    text = "".join(r.text for r in results) if results else ""
    return {"text": text}


if __name__ == "__main__":
    uvicorn.run(
        app,
        host=os.environ.get("ASR_HOST", "127.0.0.1"),
        port=int(os.environ.get("ASR_PORT", "8765")),
    )
