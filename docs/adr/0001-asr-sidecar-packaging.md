# ADR 0001: ASR Sidecar Packaging

## Decision

The Windows application uses a managed embedded-Python sidecar layout under `sidecar/`.
The application does not search `PATH` for Python in the release configuration. The
runtime executable is `sidecar/python/python.exe`, and `asr_server.py` is launched with
the application directory as its working directory.

The repository publishes the versioned script, pinned requirements, manifest, and
validator. The heavyweight Python runtime, native wheels, and model cache are staged by
a distributor only for artifacts explicitly promoted as local-ASR builds because they
are platform artifacts, not source dependencies. Standard GitHub releases use API ASR
and omit this runtime. `scripts/Test-SidecarPackage.ps1 -RequireRuntime` is the promotion
gate for a local-ASR artifact.

## Contract

- Platform: Windows x64.
- Protocol version: `1`; `/health` must report `loading`, `ready`, or `failed`.
- Only `ready` is online. `loading` and `failed` never accept recognition work.
- Model source defaults to `Qwen/Qwen3-ASR-0.6B`; device defaults to `auto` so CPU-only
  hosts remain supported when the staged wheels support CPU execution.
- The manifest hashes every source-controlled payload file.
- No system Python fallback is permitted for a promoted release artifact.

## Fixed Errors

- `ASR-SIDECAR-001`: managed runtime missing.
- `ASR-SIDECAR-002`: manifest payload missing.
- `ASR-SIDECAR-003`: manifest payload hash mismatch.
- `ASR-SIDECAR-004`: manifest invalid or unsupported.
- `ASR-SIDECAR-005`: model or device initialization failed.

An API-ASR artifact may omit the staged runtime and must not be described as a
self-contained local-ASR release. Any artifact that is promoted as a local-ASR build
must pass the `-RequireRuntime` gate; a missing runtime fails with `ASR-SIDECAR-001`.
