/**
 * The longest time upstream may legitimately hold a ready piece before asking to play it.
 * Upstream keeps these constants module-private; tests/timing.test.ts pins the mirror to the
 * vendored source so a re-vendor that changes them fails loudly.
 */
import type { PerformancePack } from './upstream/pack.ts';

export const UPSTREAM_TIMING = {
 GAP_CAP_MS: 1200,
 GAP_JITTER: 0.1,
 SAME_BEAT_GESTURE_DELAY_MS: 300,
} as const;

/** One beat: the boundary pause (capped, with jitter) or a blocking gesture, whichever is longer. */
export function maxHoldMs(pack: PerformancePack): number {
 const longestPulse = Math.max(0, ...Object.values(pack.pulse).map(clip => clip.durationMs));
 const gap = UPSTREAM_TIMING.GAP_CAP_MS * (1 + UPSTREAM_TIMING.GAP_JITTER);
 return Math.ceil(Math.max(gap, UPSTREAM_TIMING.SAME_BEAT_GESTURE_DELAY_MS + longestPulse));
}
