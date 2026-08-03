"""
Bilibili danmaku bridge script.
Connects to Bilibili live danmaku stream and pushes messages
to the C# AIVTuber backend via local HTTP POST.

Dependencies: pip install bilibili-api-python httpx
All configuration is injected via environment variables by the C# host process.

`bilibili_api` is imported lazily inside main() so the pure helpers below stay
importable (and unit-testable) without the live-stream dependency installed.
"""
import os
import asyncio
import time
import httpx

ROOM_ID  = int(os.environ.get("ROOM_ID", "0"))
SESSDATA = os.environ.get("SESSDATA", "")
BILI_JCT = os.environ.get("BILI_JCT", "")
BUVID3   = os.environ.get("BUVID3", "")
PUSH_URL = os.environ.get("PUSH_URL", "http://localhost:19876/danmaku/")
PK_PUSH_URL = os.environ.get("PK_PUSH_URL", "")
PK_NOTICE = os.environ.get("PK_NOTICE", "0") == "1"

API_UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
          "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

# PRE fires during the pre-match countdown, START once the battle actually opens.
# Both come in a plain and a _NEW variant; Dedupe collapses the duplicates.
PK_START_CMDS = ("PK_BATTLE_PRE", "PK_BATTLE_PRE_NEW",
                 "PK_BATTLE_START", "PK_BATTLE_START_NEW")
PK_END_CMDS = ("PK_BATTLE_END", "PK_END",
               "PK_BATTLE_CRIT", "PK_BATTLE_SETTLE_NEW")

# One shared client: reuses connections instead of re-handshaking TLS per event.
_client: httpx.AsyncClient | None = None


def _as_room_id(value) -> int:
    """Coerce a room id to int. Returns 0 for missing/garbage values."""
    try:
        return int(value)
    except (TypeError, ValueError):
        return 0


def extract_opponent_room_id(payload, self_room_id: int) -> int | None:
    """Pull the opponent's room id out of a PK payload.

    START carries both sides in init_info/match_info — whichever isn't ours is the
    opponent. PRE appears to be pushed per-room with the opponent inline, so a flat
    data.room_id is accepted as a fallback. Returns None when nothing usable is found;
    callers must treat that as "skip this event", never as an error.
    """
    if not isinstance(payload, dict):
        return None
    data = payload.get("data")
    if not isinstance(data, dict):
        return None

    init_room = _as_room_id((data.get("init_info") or {}).get("room_id")) \
        if isinstance(data.get("init_info"), dict) else 0
    match_room = _as_room_id((data.get("match_info") or {}).get("room_id")) \
        if isinstance(data.get("match_info"), dict) else 0

    if init_room and match_room:
        if init_room == self_room_id and match_room == self_room_id:
            return None
        return match_room if init_room == self_room_id else init_room

    # Only one of the pair present, or the flat PRE shape.
    for candidate in (init_room, match_room, _as_room_id(data.get("room_id"))):
        if candidate and candidate != self_room_id:
            return candidate
    return None


class Dedupe:
    """Collapses the PRE/START/_NEW burst that one PK match produces.

    The window is 60s rather than the few seconds a single cmd pair spans, because
    PRE and START can be tens of seconds apart and both must map to one announcement.
    """

    def __init__(self, window_sec: float = 60.0):
        self._window = window_sec
        self._seen: dict[int, float] = {}

    def should_process(self, key: int, now: float | None = None) -> bool:
        now = time.monotonic() if now is None else now
        self._seen = {k: v for k, v in self._seen.items() if now - v < self._window}
        if key in self._seen:
            return False
        self._seen[key] = now
        return True

    def clear(self) -> None:
        self._seen.clear()


def build_pk_payload(room_id: int, master_info) -> dict | None:
    """Shape a Master/info response into the JSON the C# side expects."""
    if not isinstance(master_info, dict) or master_info.get("code") != 0:
        return None
    data = master_info.get("data")
    if not isinstance(data, dict):
        return None
    info = data.get("info")
    if not isinstance(info, dict) or not info.get("uid"):
        return None
    return {
        "uid": str(info.get("uid")),
        "username": info.get("uname") or "",
        "follower": data.get("follower_num") or 0,
        "roomid": room_id,
    }


async def fetch_opponent(room_id: int) -> dict | None:
    """room_id -> uid -> uname/follower_num. Two hops, unavoidably serial:
    follower counts live on the user, not the room, and PK only gives us a room id.
    Returns None on any failure — a missed announcement is acceptable, a raised
    exception here is not.
    """
    assert _client is not None
    headers = {"user-agent": API_UA}
    try:
        r = await _client.get(
            "https://api.live.bilibili.com/room/v1/Room/room_init",
            params={"id": room_id}, headers=headers)
        room = r.json()
        if room.get("code") != 0:
            print(f"[PK] room_init failed for {room_id}: code={room.get('code')}", flush=True)
            return None
        uid = (room.get("data") or {}).get("uid")
        if not uid:
            return None

        r = await _client.get(
            "https://api.live.bilibili.com/live_user/v1/Master/info",
            params={"uid": uid}, headers=headers)
        return build_pk_payload(room_id, r.json())
    except Exception as e:
        print(f"[PK] fetch failed for room {room_id}: {e}", flush=True)
        return None


async def _post(url: str, payload: dict) -> None:
    assert _client is not None
    try:
        await _client.post(url, json=payload)
    except Exception:
        pass  # Backend not ready, ignore silently


async def main():
    global _client

    if ROOM_ID == 0:
        print("ERROR: ROOM_ID not set")
        return

    from bilibili_api import Credential
    from bilibili_api.live import LiveDanmaku

    _client = httpx.AsyncClient(timeout=3)
    credential = Credential(sessdata=SESSDATA, bili_jct=BILI_JCT, buvid3=BUVID3)
    monitor = LiveDanmaku(ROOM_ID, credential=credential)
    pk_dedupe = Dedupe()

    @monitor.on("DANMU_MSG")
    async def on_danmaku(event):
        info = event["data"]["info"]
        await _post(PUSH_URL, {
            "uid": str(info[2][0]),
            "username": info[2][1],
            "content": info[1],
        })

    async def on_pk_start(event):
        # Every failure path here must stay contained: PK is a nice-to-have, and the
        # danmaku pipeline shares this event loop.
        try:
            room_id = extract_opponent_room_id(event.get("data"), ROOM_ID)
            if room_id is None:
                return
            if not pk_dedupe.should_process(room_id):
                return
            opponent = await fetch_opponent(room_id)
            if opponent is None:
                return
            print(f"[PK] opponent: {opponent['username']} "
                  f"({opponent['follower']} fans, room {room_id})", flush=True)
            await _post(PK_PUSH_URL, opponent)
        except Exception as e:
            print(f"[PK] handler error: {e}", flush=True)

    async def on_pk_end(event):
        pk_dedupe.clear()

    if PK_NOTICE and PK_PUSH_URL:
        for cmd in PK_START_CMDS:
            monitor.on(cmd)(on_pk_start)
        for cmd in PK_END_CMDS:
            monitor.on(cmd)(on_pk_end)
        print(f"PK notice enabled, pushing to {PK_PUSH_URL}", flush=True)

    print(f"Connecting to room {ROOM_ID}...")
    try:
        await monitor.connect()
    finally:
        await _client.aclose()


if __name__ == "__main__":
    asyncio.run(main())
