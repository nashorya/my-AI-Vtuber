"""Managed local ASR sidecar with a structured readiness contract."""
import os
import tempfile
import threading
import traceback
import wave

import uvicorn
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

MODEL_SOURCE = os.environ.get("ASR_MODEL", "Qwen/Qwen3-ASR-0.6B")
MODEL_DEVICE = os.environ.get("ASR_DEVICE", "auto")
SERVER_VERSION = "1"

app = FastAPI()
model = None
health_state = "loading"
health_detail = f"Loading model {MODEL_SOURCE}"
health_lock = threading.Lock()


@app.on_event("startup")
def start_model_load():
    threading.Thread(target=load_model, name="asr-model-loader", daemon=True).start()


def load_model():
    global model, health_state, health_detail
    try:
        from qwen_asr import Qwen3ASRModel

        print(f"[ASR] Loading model: {MODEL_SOURCE} (device={MODEL_DEVICE})", flush=True)
        loaded_model = Qwen3ASRModel.from_pretrained(
            MODEL_SOURCE,
            device_map=MODEL_DEVICE,
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

    # Write PCM bytes to a temp WAV file so qwen_asr can read it
    with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as f:
        tmp_path = f.name
    try:
        with wave.open(tmp_path, "wb") as wf:
            wf.setnchannels(1)
            wf.setsampwidth(2)  # int16 = 2 bytes
            wf.setframerate(sr)
            wf.writeframes(pcm)

        results = current_model.transcribe(tmp_path)
        text = "".join(r.text for r in results) if results else ""
    finally:
        os.unlink(tmp_path)

    return {"text": text}


if __name__ == "__main__":
    uvicorn.run(
        app,
        host=os.environ.get("ASR_HOST", "127.0.0.1"),
        port=int(os.environ.get("ASR_PORT", "8765")),
    )
