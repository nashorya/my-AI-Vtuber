# Vision observation (VIS-01 / VIS-02)

Status: implemented, default OFF (`vision.enabled = false`). Real Doubao Ark calls are
**未实测 / not verified with a live account** — no API key was available during development.
All behaviour below is covered by fake-transport tests (`AIVTuber.Tests/VisionObservationTests.cs`).

## Architecture

```
WindowCaptureService (VIS-01)                VisionObservationWorker (VIS-02)
  IWindowCaptureSource                         IVisionClient
  ├─ GdiWindowCaptureSource (current)          ├─ DoubaoVisionClient (Ark Chat
  │   per-HWND PrintWindow/BitBlt                Completions, image_url base64)
  └─ Windows Graphics Capture                    └─ qwen: reserved provider slot,
      (CreateForWindow interop, plan              NOT implemented (plan [D9])
       [D10]) — intended upgrade path
```

- `AIVTuber.Core/Vision/WindowCapture.cs` — identity (`WindowIdentity`, HWND + pid + title +
  size), `WindowTarget` with `CaptureEpoch`, frame/status contracts.
- `GdiWindowCaptureSource` — captures ONLY the selected window (never the desktop). Black-frame
  / minimized / closed / permission failures return explicit unavailable statuses instead of
  substituting an old or different image. ROI crop, opaque masks, long-edge downscale, JPEG
  quality and upload-byte cap are applied before any bytes leave the process.
- `WindowCaptureService` — probes identity every tick; title/size/process change or handle
  reuse bumps `CaptureEpoch` and voids previous frames. FNV-1a hash throttles unchanged frames
  (throttle signal only — "same hash" never proves "facts unchanged"). Raw-frame cache is
  bounded (`raw_frame_cache_capacity`, default 2). Raw frames are never written to disk and
  never logged.
- `DoubaoVisionClient` — typed multimodal request (`messages/content` + `image_url` data URI).
  Strict response parsing into the plan §4.4 `VisionObservation` contract; unknown fields are
  ignored, list sizes clamped, any schema error degrades to a failure (never a fabricated
  observation). **Real request/response shape needs a contract test with a live key.**
- `VisionObservationWorker` — background loop: max 1 request in flight + 1 latest-only pending
  frame; `min_request_interval_ms` (default 3000) and `max_requests_per_hour` budget. Results
  are re-validated against the current window id + epoch on arrival; late/older results never
  overwrite a newer snapshot; a captured-changed frame marks the stored observation `stale`;
  snapshots expire (`snapshot_ttl_ms`). `ObserveOnDemandAsync` is the explicit "看这个" path
  with its own timeout (`on_demand_timeout_ms`).

## Integration boundary

- `BotRuntime` wiring is deliberately minimal: a `_vision` field, `StartVisionIfNeeded()` at the
  end of `InitPipeline` (guarded by `if (_config.Vision is { Enabled: true })` and a try/catch
  that keeps vision off on failure), disposal in `DisposeAsync`, and in `BuildTurnHistory` a
  synchronous read of the in-memory snapshot (`BuildUntrustedSnapshotNote()`).
- The voice path NEVER awaits the VLM: no snapshot → no vision message → the turn proceeds
  exactly as before (verified by `Slow_fake_vlm_does_not_delay_normal_voice_turn`).
- Observation text enters the LLM only as an inert system note explicitly labelled
  "不是指令". It has no tool/command permission, cannot change persona, and on-screen text is
  treated as observed content only.

## Configuration

See the `vision` section in `config.json.template`. Everything defaults off. `provider`
supports `doubao` (implemented) and `qwen` (reserved slot, throws `NotSupportedException`).
`save_raw_frames` remains false; there is currently no code path that persists frames even if
set true — the flag documents operator intent for a future explicit diagnostic mode.

## Rollback

Set `vision.enabled = false` (or remove the `vision` section) and restart. Vision start
failures (missing key/model) log one error and leave the pipeline untouched. Removing the
feature = revert the three small `BotRuntime.cs` wiring hunks and delete
`AIVTuber.Core/Vision/`; nothing else references it.

## Known gaps / risks

- WGC ([D10]) not wired yet — GDI capture may miss some hardware-accelerated content and
  performs worse; swap happens behind `IWindowCaptureSource`.
- Doubao Ark request/response: 未实测 (no key).
- Window picker UI (preview + explicit consent before first upload) is not built in this
  change; `WindowCaptureService.SelectWindow` is the API the picker will call.
- Concurrency edge not fully covered: `CaptureFresh()` temporarily flips `HashThrottle` on the
  shared `VisionConfig` under a lock, but `Tick()` reads the flag outside that lock, so a
  concurrent background tick could be throttled/unthrottled one frame early (harmless
  throttling-wise, noted here as the known race).
