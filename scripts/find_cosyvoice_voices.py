"""Find callable CosyVoice custom voices and apply one to AIVTuber config."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import socket
import tempfile
from dataclasses import dataclass
from datetime import datetime, time
from pathlib import Path
from typing import Callable, Mapping, Sequence
from urllib import error, request
from zoneinfo import ZoneInfo


CUSTOMIZATION_PATH = "/api/v1/services/audio/tts/customization"
REGION_TIMEZONES = {
    "beijing": "Asia/Shanghai",
    "singapore": "Asia/Singapore",
}


class ApiError(RuntimeError):
    """A sanitized error returned by the voice-management service."""


class ConfigError(RuntimeError):
    """The runtime configuration cannot be safely updated."""


@dataclass(frozen=True)
class VoiceCandidate:
    voice_id: str
    gmt_create: datetime
    status: str
    target_model: str


def build_endpoint(region: str, workspace_id: str | None) -> str:
    """Return the official workspace-specific or legacy endpoint."""
    if region not in REGION_TIMEZONES:
        raise ValueError(f"不支持的地域: {region}")

    workspace_id = (workspace_id or "").strip()
    if workspace_id:
        suffix = (
            "cn-beijing.maas.aliyuncs.com"
            if region == "beijing"
            else "ap-southeast-1.maas.aliyuncs.com"
        )
        return f"https://{workspace_id}.{suffix}{CUSTOMIZATION_PATH}"

    host = "dashscope.aliyuncs.com" if region == "beijing" else "dashscope-intl.aliyuncs.com"
    return f"https://{host}{CUSTOMIZATION_PATH}"


def parse_service_time(value: str, region: str) -> datetime:
    """Parse a service timestamp, treating a missing offset as region-local time."""
    try:
        parsed = datetime.fromisoformat(value.strip())
    except (AttributeError, TypeError, ValueError) as exc:
        raise ApiError(f"无法解析音色创建时间: {value!r}") from exc

    timezone = ZoneInfo(REGION_TIMEZONES[region])
    if parsed.tzinfo is None:
        return parsed.replace(tzinfo=timezone)
    return parsed.astimezone(timezone)


def choose_display_candidates(
    candidates: Sequence[VoiceCandidate],
    now: datetime,
    show_all: bool,
) -> tuple[list[VoiceCandidate], bool]:
    """Sort candidates and prefer those created in today's local morning."""
    sorted_candidates = sorted(candidates, key=lambda item: item.gmt_create, reverse=True)
    if show_all or not sorted_candidates:
        return sorted_candidates, False

    timezone = now.tzinfo or ZoneInfo("Asia/Shanghai")
    local_now = now if now.tzinfo is not None else now.replace(tzinfo=timezone)
    morning_start = datetime.combine(local_now.date(), time.min, tzinfo=timezone)
    morning_end = datetime.combine(local_now.date(), time(12, 0), tzinfo=timezone)
    morning = [
        item
        for item in sorted_candidates
        if morning_start <= item.gmt_create.astimezone(timezone) < morning_end
    ]
    return (morning, True) if morning else (sorted_candidates, False)


def http_post_json(
    url: str,
    api_key: str,
    payload: dict,
    timeout: float,
) -> dict:
    """POST JSON without exposing credentials or raw response bodies in errors."""
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = request.Request(
        url,
        data=body,
        method="POST",
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        },
    )
    try:
        with request.urlopen(req, timeout=timeout) as response:
            raw = response.read()
    except error.HTTPError as exc:
        raise ApiError(f"百炼接口返回 HTTP {exc.code} ({exc.reason})") from exc
    except (error.URLError, TimeoutError, socket.timeout) as exc:
        reason = getattr(exc, "reason", exc)
        raise ApiError(f"无法连接百炼接口: {reason}") from exc

    try:
        decoded = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ApiError("百炼接口返回了无法解析的 JSON") from exc
    if not isinstance(decoded, dict):
        raise ApiError("百炼接口返回格式异常")

    if decoded.get("code"):
        code = str(decoded.get("code"))
        message = str(decoded.get("message") or "服务端错误")
        raise ApiError(f"百炼接口错误 {code}: {message}")
    return decoded


Transport = Callable[[str, str, dict, float], dict]


class CosyVoiceClient:
    def __init__(
        self,
        endpoint: str,
        api_key: str,
        region: str = "beijing",
        timeout: float = 30.0,
        transport: Transport = http_post_json,
    ) -> None:
        self.endpoint = endpoint
        self.api_key = api_key
        self.region = region
        self.timeout = timeout
        self.transport = transport

    @staticmethod
    def _output(response: dict) -> dict:
        output = response.get("output")
        if not isinstance(output, dict):
            raise ApiError("百炼接口响应缺少 output 对象")
        return output

    def _post(self, input_payload: dict) -> dict:
        response = self.transport(
            self.endpoint,
            self.api_key,
            {"model": "voice-enrollment", "input": input_payload},
            self.timeout,
        )
        if not isinstance(response, dict):
            raise ApiError("百炼接口返回格式异常")
        return self._output(response)

    def _query_candidate(self, voice_id: str, summary: Mapping[str, object]) -> VoiceCandidate | None:
        detail = self._post({"action": "query_voice", "voice_id": voice_id})
        status = str(detail.get("status") or summary.get("status") or "").upper()
        target_model = str(detail.get("target_model") or "").strip()
        created_value = str(detail.get("gmt_create") or summary.get("gmt_create") or "").strip()
        if status != "OK" or not target_model or not created_value:
            return None
        created = parse_service_time(created_value, self.region)
        return VoiceCandidate(voice_id, created, status, target_model)

    def get_candidate(self, voice_id: str) -> VoiceCandidate:
        """Query one exact voice ID so selection is stable across separate runs."""
        normalized_id = voice_id.strip()
        if not normalized_id:
            raise ApiError("voice_id 不能为空")
        candidate = self._query_candidate(normalized_id, {})
        if candidate is None:
            raise ApiError(f"音色 {normalized_id} 当前不可用或缺少绑定模型")
        return candidate

    def list_candidates(
        self,
        prefix: str | None = None,
        page_size: int = 100,
        max_pages: int = 100,
    ) -> list[VoiceCandidate]:
        if page_size < 1 or max_pages < 1:
            raise ValueError("page_size 和 max_pages 必须大于 0")

        listed: dict[str, dict] = {}
        page_signatures: set[tuple[str, ...]] = set()

        for page_index in range(max_pages):
            list_input: dict = {
                "action": "list_voice",
                "page_size": page_size,
                "page_index": page_index,
            }
            if prefix:
                list_input["prefix"] = prefix

            output = self._post(list_input)
            voice_list = output.get("voice_list")
            if not isinstance(voice_list, list):
                raise ApiError("百炼接口响应缺少 voice_list 数组")

            signature = tuple(
                str(item.get("voice_id", ""))
                for item in voice_list
                if isinstance(item, dict)
            )
            if signature in page_signatures and signature:
                break
            page_signatures.add(signature)

            for item in voice_list:
                if not isinstance(item, dict):
                    continue
                voice_id = str(item.get("voice_id") or "").strip()
                status = str(item.get("status") or "").upper()
                if voice_id and status == "OK":
                    listed.setdefault(voice_id, item)

            if len(voice_list) < page_size:
                break

        candidates: list[VoiceCandidate] = []
        detail_failures = 0
        for voice_id, summary in listed.items():
            try:
                candidate = self._query_candidate(voice_id, summary)
            except ApiError:
                detail_failures += 1
                continue
            if candidate is not None:
                candidates.append(candidate)

        if listed and detail_failures == len(listed):
            raise ApiError("所有可用音色的详情查询都失败，无法验证绑定模型")
        return candidates


def locate_config(
    explicit_path: str | os.PathLike[str] | None = None,
    *,
    cwd: Path | None = None,
    repo_root: Path | None = None,
) -> Path:
    """Find the real runtime config without selecting templates or artifacts."""
    if explicit_path is not None:
        explicit = Path(explicit_path).expanduser().resolve()
        if not explicit.is_file():
            raise ConfigError(f"配置文件不存在: {explicit}")
        return explicit

    current_root = (cwd or Path.cwd()).resolve()
    source_root = (repo_root or Path(__file__).resolve().parents[1]).resolve()
    candidates = [
        current_root / "config.json",
        source_root / "App" / "bin" / "Debug" / "net10.0-windows" / "win-x64" / "config.json",
    ]
    for candidate in candidates:
        if candidate.is_file():
            return candidate.resolve()
    raise ConfigError("未找到运行配置；请使用 --config 指定现有 config.json")


def update_tts_config(config_path: Path, candidate: VoiceCandidate) -> Path:
    """Back up and atomically update the selected voice/model pair."""
    path = Path(config_path).resolve()
    try:
        original_text = path.read_text(encoding="utf-8")
        config = json.loads(original_text)
    except FileNotFoundError as exc:
        raise ConfigError(f"配置文件不存在: {path}") from exc
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ConfigError(f"无法读取配置文件: {path}") from exc

    if not isinstance(config, dict):
        raise ConfigError("配置文件根节点必须是 JSON 对象")
    if "tts" not in config:
        config["tts"] = {}
    if not isinstance(config["tts"], dict):
        raise ConfigError("配置字段 tts 必须是 JSON 对象")

    config["tts"].update(
        {
            "provider": "aliyun",
            "voice_id": candidate.voice_id,
            "model": candidate.target_model,
        }
    )

    backup_path = path.with_name(path.name + ".bak")
    temp_path: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-8",
            newline="\n",
            dir=path.parent,
            prefix=f".{path.name}.",
            suffix=".tmp",
            delete=False,
        ) as temp_file:
            json.dump(config, temp_file, ensure_ascii=False, indent=2)
            temp_file.write("\n")
            temp_file.flush()
            os.fsync(temp_file.fileno())
            temp_path = Path(temp_file.name)

        shutil.copy2(path, backup_path)
        os.replace(temp_path, path)
        temp_path = None
    except OSError as exc:
        raise ConfigError(f"写入配置失败: {exc}") from exc
    finally:
        if temp_path is not None:
            try:
                temp_path.unlink(missing_ok=True)
            except OSError:
                pass
    return backup_path


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="查找并配置可用的 CosyVoice 自定义音色")
    parser.add_argument("--config", help="要更新的运行 config.json")
    parser.add_argument(
        "--region",
        choices=tuple(REGION_TIMEZONES),
        default="beijing",
        help="百炼服务地域（默认: beijing）",
    )
    parser.add_argument("--workspace-id", help="百炼 Workspace ID（也可设置 DASHSCOPE_WORKSPACE_ID）")
    parser.add_argument("--prefix", help="仅查询此前以该前缀创建的音色")
    parser.add_argument("--voice-id", help="精确查询并选择一个完整 voice_id")
    parser.add_argument("--all", action="store_true", help="显示全部可用音色，不优先筛选今早音色")
    return parser


def _print_table(candidates: Sequence[VoiceCandidate], output_fn: Callable[[str], None]) -> None:
    id_width = max(8, *(len(item.voice_id) for item in candidates))
    model_width = max(12, *(len(item.target_model) for item in candidates))
    output_fn(f"{'序号':>4}  {'voice_id':<{id_width}}  {'创建时间':<19}  {'状态':<6}  {'target_model':<{model_width}}")
    output_fn(f"{'-' * 4}  {'-' * id_width}  {'-' * 19}  {'-' * 6}  {'-' * model_width}")
    for index, item in enumerate(candidates, start=1):
        created = item.gmt_create.strftime("%Y-%m-%d %H:%M:%S")
        output_fn(
            f"{index:>4}  {item.voice_id:<{id_width}}  {created:<19}  "
            f"{item.status:<6}  {item.target_model:<{model_width}}"
        )


def run(
    argv: Sequence[str] | None = None,
    *,
    environ: Mapping[str, str] | None = None,
    input_fn: Callable[[str], str] = input,
    output_fn: Callable[[str], None] = print,
    client_factory: Callable[..., CosyVoiceClient] = CosyVoiceClient,
    now: datetime | None = None,
) -> int:
    args = build_parser().parse_args(argv)
    environment = os.environ if environ is None else environ
    api_key = str(environment.get("DASHSCOPE_API_KEY") or "").strip()
    if not api_key:
        output_fn("错误: 请先设置环境变量 DASHSCOPE_API_KEY")
        return 2

    try:
        config_path = locate_config(args.config)
        workspace_id = args.workspace_id or environment.get("DASHSCOPE_WORKSPACE_ID")
        endpoint = build_endpoint(args.region, workspace_id)
        client = client_factory(endpoint=endpoint, api_key=api_key, region=args.region)
        candidates = (
            [client.get_candidate(args.voice_id)]
            if args.voice_id
            else client.list_candidates(prefix=args.prefix)
        )
    except (ApiError, ConfigError, OSError, ValueError) as exc:
        output_fn(f"错误: {exc}")
        return 1

    timezone = ZoneInfo(REGION_TIMEZONES[args.region])
    local_now = now or datetime.now(timezone)
    display, used_morning = choose_display_candidates(candidates, local_now, args.all)
    if not display:
        output_fn("没有找到状态为 OK 且能确认绑定模型的 CosyVoice 自定义音色。")
        return 0

    if args.voice_id:
        output_fn("已精确找到指定的可用 CosyVoice 音色：")
    elif used_morning:
        output_fn("找到今天上午创建的可用 CosyVoice 音色：")
    elif not args.all:
        output_fn("今天上午没有匹配音色，以下是当前全部可用的 CosyVoice 自定义音色：")
    else:
        output_fn("当前全部可用的 CosyVoice 自定义音色：")
    _print_table(display, output_fn)

    while True:
        try:
            answer = input_fn("选择序号 [默认 1，q 取消]: ").strip()
        except (EOFError, KeyboardInterrupt):
            output_fn("已取消，配置未修改。")
            return 0
        if answer.lower() == "q":
            output_fn("已取消，配置未修改。")
            return 0
        if not answer:
            selected_index = 1
        else:
            try:
                selected_index = int(answer)
            except ValueError:
                output_fn("请输入表格中的序号，或输入 q 取消。")
                continue
        if 1 <= selected_index <= len(display):
            break
        output_fn(f"序号必须在 1 到 {len(display)} 之间。")

    selected = display[selected_index - 1]
    output_fn(f"将更新配置: {config_path}")
    output_fn("  tts.provider = aliyun")
    output_fn(f"  tts.voice_id = {selected.voice_id}")
    output_fn(f"  tts.model = {selected.target_model}")
    try:
        confirmed = input_fn("确认写入？[y/N]: ").strip().lower()
    except (EOFError, KeyboardInterrupt):
        confirmed = ""
    if confirmed not in {"y", "yes"}:
        output_fn("已取消，配置未修改。")
        return 0

    try:
        backup_path = update_tts_config(config_path, selected)
    except ConfigError as exc:
        output_fn(f"错误: {exc}")
        return 1
    output_fn(f"配置已更新；原配置备份: {backup_path}")
    return 0


def main() -> None:
    raise SystemExit(run())


if __name__ == "__main__":
    main()
