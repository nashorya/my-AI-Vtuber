"""dots.tts HTTP service for AIVTuber.

Run on the GPU box (AutoDL), reached from the desktop app over an SSH tunnel:

    export TTS_API_KEY='...'          # must differ from the server login password
    uvicorn dots_tts_api_server:app --host 127.0.0.1 --port 6006 --workers 1

One worker only: each worker would load its own copy of the model.

The C# AudioPlayer decodes raw 16-bit mono PCM at a fixed 24000 Hz
(AudioPlayer.DefaultSampleRate) and does no resampling of its own, so the
conversion from dots.tts's native 48kHz has to happen here. Returning any other
rate plays back at the wrong speed and pitch.
"""
import asyncio
import os

import numpy as np
import torch
import torchaudio
from fastapi import FastAPI, Header, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel, Field

from dots_tts.runtime import DotsTtsRuntime

MODEL_PATH = (
    "/root/autodl-tmp/checkpoints/lillia_soar/checkpoint-00000300/model"
)

# Must equal AudioPlayer.DefaultSampleRate in AIVTuber.Core/Audio/AudioPlayer.cs.
OUTPUT_SAMPLE_RATE = 24000
API_KEY = os.getenv("TTS_API_KEY", "")

app = FastAPI(title="dots.tts API")

print("[TTS] 正在加载模型……", flush=True)
runtime = DotsTtsRuntime.from_pretrained(
    MODEL_PATH,
    precision="bfloat16",
    optimize=False,
)
print(f"[TTS] 模型加载完成，输出 {OUTPUT_SAMPLE_RATE}Hz", flush=True)

# Serialize generation: concurrent requests on one card exhaust VRAM.
gpu_lock = asyncio.Lock()


class TtsRequest(BaseModel):
    text: str = Field(min_length=1, max_length=500)
    language: str = "ZH"
    seed: int = 42
    num_steps: int = Field(default=10, ge=1, le=32)
    guidance_scale: float = Field(default=1.2, ge=0.1, le=3.0)
    # The client states the rate it will decode at. Honouring it here means a
    # mismatch fails loudly on the client instead of sounding subtly wrong.
    sample_rate: int = Field(default=OUTPUT_SAMPLE_RATE, ge=8000, le=48000)


def check_auth(authorization: str | None) -> None:
    if not API_KEY:
        return
    if authorization != f"Bearer {API_KEY}":
        raise HTTPException(status_code=401, detail="Invalid API key")


def synthesize(req: TtsRequest) -> tuple[bytes, int]:
    torch.manual_seed(req.seed)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(req.seed)

    with torch.inference_mode():
        result = runtime.generate(
            text=req.text,
            language=req.language,
            normalize_text=True,
            num_steps=req.num_steps,
            guidance_scale=req.guidance_scale,
        )

    audio = result["audio"].detach().float().cpu()
    if audio.ndim == 1:
        audio = audio.unsqueeze(0)

    source_rate = int(result["sample_rate"])
    target_rate = req.sample_rate

    if source_rate != target_rate:
        audio = torchaudio.functional.resample(audio, source_rate, target_rate)

    samples = audio.squeeze(0).clamp(-1.0, 1.0).numpy()
    pcm = np.round(samples * 32767.0).astype("<i2")  # signed 16-bit LE
    return pcm.tobytes(), target_rate


@app.get("/health")
def health():
    return {
        "status": "ok",
        "sample_rate": OUTPUT_SAMPLE_RATE,
        "format": "pcm_s16le",
        "channels": 1,
    }


@app.post("/v1/tts")
async def tts(req: TtsRequest, authorization: str | None = Header(default=None)):
    check_auth(authorization)

    async with gpu_lock:
        pcm, rate = await asyncio.to_thread(synthesize, req)

    return Response(
        content=pcm,
        media_type="application/octet-stream",
        headers={
            "X-Audio-Format": "pcm_s16le",
            "X-Sample-Rate": str(rate),
            "X-Channels": "1",
        },
    )
