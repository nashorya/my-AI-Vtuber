"""
Local ASR HTTP server (Qwen3-ASR-0.6B).
POST /recognize  Content-Type: application/octet-stream  body=raw int16 PCM
                 query: sr=16000 (sample rate, default 16000)
GET  /health     returns {"status": "ok"}
"""
import io
import wave
import tempfile
import os
import uvicorn
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

MODEL_DIR = r"C:\Users\nashorya\.cache\modelscope\hub\models\Qwen\Qwen3-ASR-0___6B"

app = FastAPI()
model = None


@app.on_event("startup")
def load_model():
    global model
    from qwen_asr import Qwen3ASRModel
    print("[ASR] Loading model...")
    model = Qwen3ASRModel.from_pretrained(MODEL_DIR, device_map="cuda")
    print("[ASR] Model ready.")


@app.get("/health")
def health():
    return {"status": "ok" if model is not None else "loading"}


@app.post("/recognize")
async def recognize(request: Request):
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

        results = model.transcribe(tmp_path)
        text = "".join(r.text for r in results) if results else ""
    finally:
        os.unlink(tmp_path)

    return {"text": text}


if __name__ == "__main__":
    uvicorn.run(app, host="127.0.0.1", port=8765)
