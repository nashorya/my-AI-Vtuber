import time
from modelscope import snapshot_download

t0 = time.time()
from qwen_asr import Qwen3ASRModel  # noqa: E402

t1 = time.time()
print(f"import time: {t1 - t0:.2f}s")

print("Downloading model from ModelScope...")
model_dir = r"C:\Users\nashorya\.cache\modelscope\hub\models\Qwen\Qwen3-ASR-0___6B"
print(f"Loading model from: {model_dir}")

model = Qwen3ASRModel.from_pretrained(model_dir, device_map="cuda")
t2 = time.time()
print(f"model load time: {t2 - t1:.2f}s")

# Warm-up run (first inference pays for CUDA kernel compilation / cache warmup)
_ = model.transcribe("test_asr_sample.wav")
t3 = time.time()
print(f"warm-up inference: {t3 - t2:.2f}s")

t4 = time.time()
result = model.transcribe("test_asr_sample.wav")
t5 = time.time()
print(f"steady-state inference: {t5 - t4:.2f}s")
print("transcript:", result)
