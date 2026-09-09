# PNG Avatar Head-Only Breathing Design

## Goal

Change the periodic idle breathing motion so only the avatar head moves vertically. Preserve whole-character motion for speech bounce, emotion shake, jump, sink, and any future body actions.

The implementation must be reversible through avatar pack configuration so the current whole-body breathing behavior can be restored without reverting unrelated code.

## Scope

- Add a configurable breath target: `body` for the existing behavior or `head` for the new behavior.
- Configure the current avatar pack to use head-only breathing with approximately 2 px vertical amplitude.
- Split the flattened sprite at render time; do not generate or manually maintain additional PNG files.
- Keep expression transitions and cross-fades synchronized across the head and body layers.
- Leave sleep-state priority and wake-up behavior unchanged.
- Leave LLM emotion routing and vocabulary unchanged.

## Rendering Architecture

`AvatarWindow` keeps one outer character container for non-breath motion. Inside it, the existing current/previous image pair becomes two synchronized pairs:

1. The body pair renders the lower sprite region and remains stationary during idle breathing.
2. The head pair renders the upper sprite region and receives only the periodic vertical breath offset.

The outer character container continues to receive speech bounce, shake, jump, sink, drift, and rotation. Both inner layers therefore follow deliberate whole-character actions together.

Because the source sprites are flattened, the split uses two overlapping rectangular clip regions. The current 1254 x 1254 pack starts the body near canvas Y 480 and ends the head near canvas Y 520. The 40 px overlap hides the neck boundary during a small head movement. These values live in the pack configuration and can be tuned after visual inspection.

## Motion Data Flow

`MotionEngine` reports the breath component separately from whole-character motion:

- Whole-character `OffsetX`, `OffsetY`, rotation, and scale exclude the periodic breath component.
- A dedicated breath Y offset carries the sinusoidal head movement.
- The current pack disables breath scaling and lowers `amp_px` from 5 to about 2.

`AvatarWindow.ApplyMotion` applies whole-character values to the outer container. When `breath.target` is `head`, it applies the separate breath offset to the head layer. When the target is `body`, it combines the breath offset with the outer body transform to preserve the existing rendering mode.

## Configuration

Extend the breath configuration with:

- `target`: `body` or `head`; defaults to `body` for existing packs.
- `head_cut_y`: bottom of the moving upper region in source-canvas pixels.
- `head_overlap_px`: overlap between the upper and lower clip regions.

The bundled avatar uses `target: head`, `amp_px: 2`, `scale_amp: 0`, `head_cut_y: 520`, and `head_overlap_px: 40` as initial inspection values.

Invalid target values fall back to `body`. Invalid cut or overlap values are clamped to the source canvas bounds. Missing values retain backward-compatible defaults.

## State Transitions

Both head and body image pairs use the same state source, previous-state source, fade progress, and opacity. A state change therefore cannot leave the head on one expression while the body uses another.

The special sleep state remains higher priority than emotions. Users still return to the awake/default state before selecting another expression.

## Verification

- Unit tests verify that breath offset oscillates independently while quiet body offset remains stable.
- Existing bounce, shake, jump, sink, fade, and state-priority tests continue to pass.
- Configuration loader tests cover new fields and backward-compatible defaults.
- Build and run the WPF app, then inspect neutral, mouth, emotion, fade, and sleep frames at the configured window size.
- Confirm the body, hands, skirt, and legs stay fixed during idle breathing and that the head moves about 2 px without a visible neck seam.
- Confirm switching `target` back to `body` restores the previous whole-body behavior.

## Rollback

Set `motion_layer.breath.target` to `body` and restore the previous amplitude/scale values. The implementation will be isolated in its own commit so it can also be reverted cleanly if the runtime split is unsuitable.
