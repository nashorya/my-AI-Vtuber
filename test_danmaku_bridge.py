"""Unit tests for danmaku_bridge PK parsing helpers.

Pure-function tests only — no network, no bilibili_api dependency.
Run: python -m pytest test_danmaku_bridge.py -v
"""
import danmaku_bridge as bridge

SELF_ROOM = 12345
OPPONENT_ROOM = 67890


class TestExtractOpponentRoomId:
    """PK payloads carry two room ids; the one that isn't ours is the opponent."""

    def test_init_info_is_self(self):
        payload = {"data": {
            "init_info": {"room_id": SELF_ROOM},
            "match_info": {"room_id": OPPONENT_ROOM},
        }}
        assert bridge.extract_opponent_room_id(payload, SELF_ROOM) == OPPONENT_ROOM

    def test_match_info_is_self(self):
        payload = {"data": {
            "init_info": {"room_id": OPPONENT_ROOM},
            "match_info": {"room_id": SELF_ROOM},
        }}
        assert bridge.extract_opponent_room_id(payload, SELF_ROOM) == OPPONENT_ROOM

    def test_neither_is_self_falls_back_to_init_info(self):
        # Shouldn't happen, but must not crash or return our own id.
        payload = {"data": {
            "init_info": {"room_id": 11111},
            "match_info": {"room_id": 22222},
        }}
        assert bridge.extract_opponent_room_id(payload, SELF_ROOM) == 11111

    def test_both_are_self_returns_none(self):
        payload = {"data": {
            "init_info": {"room_id": SELF_ROOM},
            "match_info": {"room_id": SELF_ROOM},
        }}
        assert bridge.extract_opponent_room_id(payload, SELF_ROOM) is None

    def test_missing_fields_returns_none(self):
        assert bridge.extract_opponent_room_id({"data": {}}, SELF_ROOM) is None

    def test_empty_payload_returns_none(self):
        assert bridge.extract_opponent_room_id({}, SELF_ROOM) is None

    def test_none_payload_returns_none(self):
        assert bridge.extract_opponent_room_id(None, SELF_ROOM) is None

    def test_zero_room_id_treated_as_missing(self):
        payload = {"data": {
            "init_info": {"room_id": 0},
            "match_info": {"room_id": OPPONENT_ROOM},
        }}
        assert bridge.extract_opponent_room_id(payload, SELF_ROOM) == OPPONENT_ROOM

    def test_string_room_id_is_coerced(self):
        payload = {"data": {
            "init_info": {"room_id": str(SELF_ROOM)},
            "match_info": {"room_id": str(OPPONENT_ROOM)},
        }}
        assert bridge.extract_opponent_room_id(payload, SELF_ROOM) == OPPONENT_ROOM

    def test_flat_room_id_pre_shape(self):
        # PK_BATTLE_PRE is pushed per-room and may carry the opponent directly.
        payload = {"data": {"room_id": OPPONENT_ROOM, "uname": "someone"}}
        assert bridge.extract_opponent_room_id(payload, SELF_ROOM) == OPPONENT_ROOM

    def test_flat_room_id_equal_to_self_returns_none(self):
        payload = {"data": {"room_id": SELF_ROOM}}
        assert bridge.extract_opponent_room_id(payload, SELF_ROOM) is None

    def test_nested_shape_wins_over_flat(self):
        payload = {"data": {
            "room_id": SELF_ROOM,
            "init_info": {"room_id": SELF_ROOM},
            "match_info": {"room_id": OPPONENT_ROOM},
        }}
        assert bridge.extract_opponent_room_id(payload, SELF_ROOM) == OPPONENT_ROOM

    def test_init_id_and_match_id_are_room_ids(self):
        payload = {"data": {
            "init_info": {"init_id": SELF_ROOM, "uid": 1},
            "match_info": {"match_id": OPPONENT_ROOM, "uid": 2, "uname": "对面"},
        }}
        assert bridge.extract_opponent_room_id(payload, SELF_ROOM) == OPPONENT_ROOM

    def test_hint_reads_match_info(self):
        payload = {"data": {
            "init_info": {"init_id": SELF_ROOM, "uid": 11, "uname": "自己"},
            "match_info": {"match_id": OPPONENT_ROOM, "uid": 22, "uname": "对面主播乙"},
        }}
        hint = bridge.extract_opponent_hint(payload, SELF_ROOM)
        assert hint == {
            "uid": "22",
            "username": "对面主播乙",
            "follower": 0,
            "roomid": OPPONENT_ROOM,
        }


class TestDedupe:
    """PRE and START (and their _NEW variants) all fire for one PK match."""

    def test_first_call_passes(self):
        d = bridge.Dedupe(window_sec=60)
        assert d.should_process(OPPONENT_ROOM, now=1000.0) is True

    def test_repeat_within_window_blocked(self):
        d = bridge.Dedupe(window_sec=60)
        d.should_process(OPPONENT_ROOM, now=1000.0)
        assert d.should_process(OPPONENT_ROOM, now=1030.0) is False

    def test_repeat_after_window_passes(self):
        d = bridge.Dedupe(window_sec=60)
        d.should_process(OPPONENT_ROOM, now=1000.0)
        assert d.should_process(OPPONENT_ROOM, now=1061.0) is True

    def test_different_keys_independent(self):
        d = bridge.Dedupe(window_sec=60)
        assert d.should_process(OPPONENT_ROOM, now=1000.0) is True
        assert d.should_process(99999, now=1000.0) is True

    def test_clear_allows_immediate_reprocess(self):
        d = bridge.Dedupe(window_sec=60)
        d.should_process(OPPONENT_ROOM, now=1000.0)
        d.clear()
        assert d.should_process(OPPONENT_ROOM, now=1001.0) is True

    def test_expired_entries_are_evicted(self):
        d = bridge.Dedupe(window_sec=60)
        for room in range(100):
            d.should_process(room, now=1000.0)
        d.should_process(999, now=2000.0)
        assert len(d._seen) == 1


class TestBuildPkPayload:
    def test_maps_api_responses_to_push_payload(self):
        master = {"code": 0, "data": {
            "info": {"uid": 8739477, "uname": "老实憨厚的笑笑"},
            "follower_num": 2821200,
        }}
        got = bridge.build_pk_payload(OPPONENT_ROOM, master)
        assert got == {
            "uid": "8739477",
            "username": "老实憨厚的笑笑",
            "follower": 2821200,
            "roomid": OPPONENT_ROOM,
        }

    def test_returns_none_on_error_code(self):
        assert bridge.build_pk_payload(OPPONENT_ROOM, {"code": -352}) is None

    def test_returns_none_on_missing_data(self):
        assert bridge.build_pk_payload(OPPONENT_ROOM, {"code": 0}) is None

    def test_missing_follower_defaults_to_zero(self):
        master = {"code": 0, "data": {"info": {"uid": 1, "uname": "x"}}}
        assert bridge.build_pk_payload(OPPONENT_ROOM, master)["follower"] == 0
