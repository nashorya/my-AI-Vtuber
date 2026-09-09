# CosyVoice Voice Finder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a dependency-free Python CLI that finds callable CosyVoice custom voices, prioritizes voices cloned this morning, and safely writes the selected voice/model pair into the AI VTuber runtime configuration.

**Architecture:** One importable script contains a small HTTP client, pure normalization/filtering functions, atomic configuration update functions, and a thin interactive CLI. Unit tests inject fake transports and temporary files so every behavior except the final read-only live query is deterministic.

**Tech Stack:** Python 3 standard library (`argparse`, `dataclasses`, `datetime`, `json`, `pathlib`, `shutil`, `tempfile`, `urllib`, `zoneinfo`) and `unittest`.

## Global Constraints

- Query only CosyVoice / Qwen-Audio-TTS voices through `model=voice-enrollment`; do not query Qwen3-TTS, MiniMax, or system voices.
- Read credentials only from `DASHSCOPE_API_KEY`; never accept or log an API key as a CLI argument.
- Update `tts.provider`, `tts.voice_id`, and `tts.model` together only after an explicit selection and confirmation.
- Preserve unknown JSON fields, create `config.json.bak`, and use a temporary file plus `os.replace`.
- Add no third-party Python dependency.
- Do not modify the C# client, configuration UI, or application startup flow.

---

### Task 1: Endpoint, timestamp, and morning-selection domain logic

**Files:**
- Create: `scripts/find_cosyvoice_voices.py`
- Create: `tests/test_find_cosyvoice_voices.py`

**Interfaces:**
- Produces: `VoiceCandidate`, `build_endpoint(region, workspace_id)`, `parse_service_time(value, region)`, and `choose_display_candidates(candidates, now, show_all)`.
- Consumes: no project code; Python standard library only.

- [ ] **Step 1: Write failing pure-function tests**

Add tests that assert:

```python
self.assertEqual(
    finder.build_endpoint("beijing", "ws-123"),
    "https://ws-123.cn-beijing.maas.aliyuncs.com/api/v1/services/audio/tts/customization",
)
self.assertEqual(
    finder.build_endpoint("singapore", None),
    "https://dashscope-intl.aliyuncs.com/api/v1/services/audio/tts/customization",
)
```

Create candidates at `00:00`, `11:59:59`, and `12:00` Asia/Shanghai and assert the first two, but not the third, are selected when `show_all=False`. Assert candidates are sorted newest-first and that no morning match falls back to every callable candidate.

- [ ] **Step 2: Run the tests and verify failure**

Run:

```powershell
python -m unittest discover -s tests -p "test_find_cosyvoice_voices.py" -v
```

Expected: import failure because `scripts/find_cosyvoice_voices.py` does not exist.

- [ ] **Step 3: Implement the pure domain layer**

Define:

```python
@dataclass(frozen=True)
class VoiceCandidate:
    voice_id: str
    gmt_create: datetime
    status: str
    target_model: str

def build_endpoint(region: str, workspace_id: str | None) -> str: ...
def parse_service_time(value: str, region: str) -> datetime: ...
def choose_display_candidates(
    candidates: Sequence[VoiceCandidate],
    now: datetime,
    show_all: bool,
) -> tuple[list[VoiceCandidate], bool]: ...
```

Use `ZoneInfo("Asia/Shanghai")` for Beijing and `ZoneInfo("Asia/Singapore")` for Singapore. Return `(items, used_morning_filter)` so the CLI can distinguish a morning result from an all-voices fallback.

- [ ] **Step 4: Run tests and verify pass**

Run the discovery command from Step 2. Expected: all Task 1 tests pass.

- [ ] **Step 5: Commit Task 1**

```powershell
git add scripts/find_cosyvoice_voices.py tests/test_find_cosyvoice_voices.py
git commit -m "feat: add CosyVoice voice selection logic"
```

### Task 2: Paginated CosyVoice management client

**Files:**
- Modify: `scripts/find_cosyvoice_voices.py`
- Modify: `tests/test_find_cosyvoice_voices.py`

**Interfaces:**
- Consumes: `VoiceCandidate` and `parse_service_time` from Task 1.
- Produces: `ApiError`, `http_post_json(url, api_key, payload, timeout)`, and `CosyVoiceClient.list_candidates(prefix=None)`.

- [ ] **Step 1: Write failing client tests with an injected transport**

Create a fake callable with signature:

```python
def fake_transport(url: str, api_key: str, payload: dict, timeout: float) -> dict:
    ...
```

Test that `list_voice` sends `page_index` values `0`, then `1`, stops on a short page, removes duplicate `voice_id` values, ignores list entries whose status is not `OK`, calls `query_voice` for remaining entries, and returns candidates only when the detail response is also `OK` and contains a non-empty `target_model`. Add tests for an empty first page, a repeated full page, and all detail requests failing.

- [ ] **Step 2: Run focused tests and verify failure**

Run the discovery command. Expected: failures for undefined `CosyVoiceClient` and `ApiError`.

- [ ] **Step 3: Implement request validation and pagination**

Implement:

```python
class ApiError(RuntimeError):
    pass

class CosyVoiceClient:
    def __init__(self, endpoint, api_key, region="beijing", timeout=30.0,
                 transport=http_post_json): ...

    def list_candidates(self, prefix=None, page_size=100,
                        max_pages=100) -> list[VoiceCandidate]: ...
```

Every request body uses `"model": "voice-enrollment"`. List requests use `"action": "list_voice"`; detail requests use `"action": "query_voice"`. Validate `output` and `voice_list` shapes, track seen IDs and page signatures, skip a voice whose detail cannot be verified, and raise `ApiError` when at least one eligible list item existed but no detail request could be verified.

`http_post_json` uses `urllib.request` with `Authorization: Bearer ...`, decodes JSON, converts `HTTPError`, `URLError`, timeout, malformed JSON, and a top-level service `code`/`message` error into sanitized `ApiError` messages.

- [ ] **Step 4: Run tests and verify pass**

Run the discovery command. Expected: all Task 1 and Task 2 tests pass.

- [ ] **Step 5: Commit Task 2**

```powershell
git add scripts/find_cosyvoice_voices.py tests/test_find_cosyvoice_voices.py
git commit -m "feat: query callable CosyVoice voices"
```

### Task 3: Safe runtime configuration update

**Files:**
- Modify: `scripts/find_cosyvoice_voices.py`
- Modify: `tests/test_find_cosyvoice_voices.py`

**Interfaces:**
- Consumes: selected `VoiceCandidate` from Tasks 1–2.
- Produces: `locate_config(explicit_path=None)` and `update_tts_config(config_path, candidate)`.

- [ ] **Step 1: Write failing temporary-directory tests**

Use `tempfile.TemporaryDirectory` to assert that:

```python
updated = json.loads(config_path.read_text(encoding="utf-8"))
self.assertEqual(updated["tts"]["provider"], "aliyun")
self.assertEqual(updated["tts"]["voice_id"], candidate.voice_id)
self.assertEqual(updated["tts"]["model"], candidate.target_model)
self.assertEqual(updated["unrelated"], {"keep": True})
self.assertTrue(config_path.with_name("config.json.bak").exists())
```

Also assert that a non-object JSON root or non-object `tts` value raises `ConfigError` without changing the original file or creating a backup. Test explicit config selection, current-directory selection, and failure when no valid path exists.

- [ ] **Step 2: Run tests and verify failure**

Run the discovery command. Expected: failures for undefined configuration functions.

- [ ] **Step 3: Implement locating, validation, backup, and replacement**

Define `ConfigError(RuntimeError)`. `locate_config` accepts an existing explicit path or checks current-directory `config.json` and then the repository-relative debug runtime config. It never searches templates or `artifacts`.

`update_tts_config` reads UTF-8 JSON, validates object shapes, applies the three fields together, serializes indented UTF-8 JSON to a closed temporary file in the same directory, copies the original to `<name>.bak`, then calls `os.replace`. Always remove a leftover temporary file in `finally`.

- [ ] **Step 4: Run tests and verify pass**

Run the discovery command. Expected: all tests pass.

- [ ] **Step 5: Commit Task 3**

```powershell
git add scripts/find_cosyvoice_voices.py tests/test_find_cosyvoice_voices.py
git commit -m "feat: safely apply CosyVoice configuration"
```

### Task 4: Interactive CLI and end-to-end verification

**Files:**
- Modify: `scripts/find_cosyvoice_voices.py`
- Modify: `tests/test_find_cosyvoice_voices.py`
- Modify: `README.txt`

**Interfaces:**
- Consumes: all Task 1–3 APIs.
- Produces: `build_parser()`, `run(argv=None, environ=None, input_fn=input, output_fn=print)`, and executable `main()`.

- [ ] **Step 1: Write failing CLI tests**

Inject a fake client/factory, input function, output collector, and temporary config. Cover missing `DASHSCOPE_API_KEY`, zero candidates, invalid selection followed by a valid selection, cancellation at confirmation, and confirmed update. Assert cancellation does not write a backup and successful output includes both `voice_id` and `target_model`.

- [ ] **Step 2: Run tests and verify failure**

Run the discovery command. Expected: CLI tests fail because `run` and parser options do not exist.

- [ ] **Step 3: Implement parser and interactive flow**

Add `--config`, `--region`, `--workspace-id`, `--prefix`, and `--all`. Read workspace ID from the CLI first and then `DASHSCOPE_WORKSPACE_ID`; read API Key only from the injected environment. Display a numbered fixed-width table, default the selection prompt to item `1`, accept `q` to cancel, show the exact three changes, and require `y` or `yes` before calling `update_tts_config`.

Return exit code `0` on successful query with cancellation or update, `1` for API/config failures, and `2` for missing credentials or invalid CLI usage. `main()` calls `raise SystemExit(run())`.

- [ ] **Step 4: Document usage**

Add a short `README.txt` section showing:

```powershell
$env:DASHSCOPE_API_KEY = "<your-api-key>"
python scripts/find_cosyvoice_voices.py
```

Mention `--config`, `--prefix`, `--all`, the `.bak` file, and that voice/model are updated together.

- [ ] **Step 5: Run automated verification**

Run:

```powershell
python -m unittest discover -s tests -p "test_find_cosyvoice_voices.py" -v
python -m py_compile scripts/find_cosyvoice_voices.py tests/test_find_cosyvoice_voices.py
git diff --check
```

Expected: all unit tests pass, compilation exits zero, and `git diff --check` prints nothing.

- [ ] **Step 6: Run a live read-only query**

Pipe `q` into the normal selection prompt so the script performs the live query and exits before confirmation or any file write:

```powershell
"q" | python scripts/find_cosyvoice_voices.py
```

Use the existing `DASHSCOPE_API_KEY`. Verify the response contains callable voices and that morning filtering behaves as designed. Do not print the key or full raw response.

- [ ] **Step 7: Apply only after explicit selection**

Present the live candidate table to the user. After the user identifies a row, run the normal interactive path with that row and confirmation, then inspect only `tts.provider`, `tts.voice_id`, and `tts.model` plus the backup's existence.

- [ ] **Step 8: Commit Task 4**

```powershell
git add scripts/find_cosyvoice_voices.py tests/test_find_cosyvoice_voices.py README.txt
git commit -m "feat: add interactive CosyVoice voice finder"
```
