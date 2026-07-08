"""
MiniMax 音色克隆脚本

用法:
  python clone_voice.py --audio <音频文件路径> --voice-id <自定义音色ID>

示例:
  python clone_voice.py --audio my_voice.wav --voice-id my_custom_voice_001
  python clone_voice.py --audio my_voice.wav --voice-id my_voice --preview-text "你好，我是克隆出来的声音"
"""

import argparse
import os
import sys
import requests


API_BASE = "https://api.minimaxi.com/v1"


def get_api_key() -> str:
    key = os.environ.get("MINIMAX_API_KEY", "")
    if not key:
        sys.exit("错误: 请设置环境变量 MINIMAX_API_KEY")
    return key


def upload_audio(file_path: str, api_key: str) -> int:
    """上传音频文件，返回 file_id"""
    print(f"[1/2] 上传音频: {file_path}")

    with open(file_path, "rb") as f:
        resp = requests.post(
            f"{API_BASE}/files/upload",
            headers={"Authorization": f"Bearer {api_key}"},
            data={"purpose": "voice_clone"},
            files={"file": (os.path.basename(file_path), f)},
            timeout=120,
        )

    resp.raise_for_status()
    body = resp.json()

    status = body.get("base_resp", {})
    if status.get("status_code", -1) != 0:
        sys.exit(f"上传失败: [{status.get('status_code')}] {status.get('status_msg')}")

    file_id: int = body["file"]["file_id"]
    print(f"  file_id: {file_id}  ({body['file']['bytes']} bytes)")
    return file_id


def clone_voice(
    file_id: int,
    voice_id: str,
    api_key: str,
    preview_text: str | None = None,
    model: str = "speech-02-hd",
    language_boost: str = "auto",
    noise_reduction: bool = False,
    volume_normalization: bool = False,
) -> dict:
    """克隆音色，返回响应 body"""
    print(f"[2/2] 克隆音色, voice_id={voice_id!r}")

    payload: dict = {
        "file_id": file_id,
        "voice_id": voice_id,
        "language_boost": language_boost,
        "need_noise_reduction": noise_reduction,
        "need_volume_normalization": volume_normalization,
    }

    if preview_text:
        payload["text"] = preview_text
        payload["model"] = model

    resp = requests.post(
        f"{API_BASE}/voice_clone",
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        },
        json=payload,
        timeout=120,
    )

    resp.raise_for_status()
    body = resp.json()

    status = body.get("base_resp", {})
    if status.get("status_code", -1) != 0:
        sys.exit(f"克隆失败: [{status.get('status_code')}] {status.get('status_msg')}")

    return body


def main() -> None:
    parser = argparse.ArgumentParser(description="MiniMax 音色克隆")
    parser.add_argument("--audio", required=True, help="待克隆的音频文件路径 (mp3/m4a/wav, 10s-5min, 最大20MB)")
    parser.add_argument("--voice-id", required=True, help="自定义音色ID (字母开头, 仅字母/数字/-/_)")
    parser.add_argument("--preview-text", default=None, help="克隆后用于预览的文本 (可选, 最多1000字)")
    parser.add_argument(
        "--model",
        default="speech-02-hd",
        choices=["speech-2.8-hd", "speech-2.8-turbo", "speech-02-hd", "speech-02-turbo",
                 "speech-01-hd", "speech-01-turbo"],
        help="预览 TTS 模型 (仅在指定 --preview-text 时生效)",
    )
    parser.add_argument("--language-boost", default="auto", help="语言增强 (auto/Chinese/English 等)")
    parser.add_argument("--noise-reduction", action="store_true", help="开启降噪")
    parser.add_argument("--volume-normalization", action="store_true", help="开启音量归一化")
    args = parser.parse_args()

    if not os.path.isfile(args.audio):
        sys.exit(f"错误: 文件不存在: {args.audio}")

    api_key = get_api_key()

    file_id = upload_audio(args.audio, api_key)

    result = clone_voice(
        file_id=file_id,
        voice_id=args.voice_id,
        api_key=api_key,
        preview_text=args.preview_text,
        model=args.model,
        language_boost=args.language_boost,
        noise_reduction=args.noise_reduction,
        volume_normalization=args.volume_normalization,
    )

    print("\n克隆成功!")
    demo_url = result.get("demo_audio", "")
    if demo_url:
        print(f"  预览音频: {demo_url}")

    extra = result.get("extra_info", {})
    if extra:
        print(f"  音频时长: {extra.get('audio_length', '-')} ms")
        print(f"  采样率:   {extra.get('audio_sample_rate', '-')} Hz")

    print(f"\n音色 ID '{args.voice_id}' 已就绪，可在 TTS 接口中直接使用该 voice_id。")


if __name__ == "__main__":
    main()
