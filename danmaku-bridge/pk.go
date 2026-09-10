package main

import (
	"encoding/json"
	"strconv"
	"strings"
	"time"
)

func asRoomID(value any) int {
	switch typed := value.(type) {
	case nil:
		return 0
	case float64:
		return int(typed)
	case float32:
		return int(typed)
	case int:
		return typed
	case int64:
		return int(typed)
	case json.Number:
		n, err := typed.Int64()
		if err != nil {
			return 0
		}
		return int(n)
	case string:
		n, err := strconv.Atoi(typed)
		if err != nil {
			return 0
		}
		return n
	default:
		return 0
	}
}

func asMap(value any) map[string]any {
	typed, ok := value.(map[string]any)
	if !ok || typed == nil {
		return map[string]any{}
	}
	return typed
}

func containsRoom(self []int, room int) bool {
	for _, id := range self {
		if id != 0 && id == room {
			return true
		}
	}
	return false
}

func parseSelfRoomIDs(body any, configured int) []int {
	ids := make([]int, 0, 3)
	add := func(n int) {
		if n == 0 || containsRoom(ids, n) {
			return
		}
		ids = append(ids, n)
	}
	add(configured)
	root := asMap(body)
	if asRoomID(root["code"]) == 0 {
		data := asMap(root["data"])
		add(asRoomID(data["room_id"]))
		add(asRoomID(data["short_id"]))
	}
	return ids
}

// extractOpponentRoomID pulls the opponent's room id out of a PK payload.
// START carries both sides in init_info/match_info; PRE may only have data.room_id.
func extractOpponentRoomID(payload any, selfRoomID int) *int {
	return extractOpponentRoomIDAny(payload, []int{selfRoomID})
}

func extractOpponentRoomIDAny(payload any, self []int) *int {
	root := asMap(payload)
	data := asMap(root["data"])
	initInfo := asMap(data["init_info"])
	matchInfo := asMap(data["match_info"])
	initRoom := roomFromSide(initInfo, "room_id", "init_id")
	matchRoom := roomFromSide(matchInfo, "room_id", "match_id")

	if initRoom != 0 && matchRoom != 0 {
		initSelf := containsRoom(self, initRoom)
		matchSelf := containsRoom(self, matchRoom)
		if initSelf && matchSelf {
			return nil
		}
		if initSelf && !matchSelf {
			return intPtr(matchRoom)
		}
		if matchSelf && !initSelf {
			return intPtr(initRoom)
		}
		return nil
	}

	for _, candidate := range []int{initRoom, matchRoom, asRoomID(data["room_id"])} {
		if candidate != 0 && !containsRoom(self, candidate) {
			return intPtr(candidate)
		}
	}
	return nil
}

func intPtr(v int) *int { return &v }

func roomFromSide(side map[string]any, keys ...string) int {
	for _, key := range keys {
		if n := asRoomID(side[key]); n != 0 {
			return n
		}
	}
	return 0
}

// extractOpponentHint reads uid/uname from the opponent side of a START payload
// so we can still announce when room_init / Master/info is unreachable.
func extractOpponentHint(payload any, selfRoomID int) *pkPush {
	return extractOpponentHintAny(payload, []int{selfRoomID})
}

func extractOpponentHintAny(payload any, self []int) *pkPush {
	root := asMap(payload)
	data := asMap(root["data"])
	initInfo := asMap(data["init_info"])
	matchInfo := asMap(data["match_info"])
	initRoom := roomFromSide(initInfo, "room_id", "init_id")
	matchRoom := roomFromSide(matchInfo, "room_id", "match_id")

	side := map[string]any{}
	room := 0
	if initRoom != 0 && matchRoom != 0 {
		initSelf := containsRoom(self, initRoom)
		matchSelf := containsRoom(self, matchRoom)
		if initSelf && !matchSelf {
			side, room = matchInfo, matchRoom
		} else if matchSelf && !initSelf {
			side, room = initInfo, initRoom
		} else {
			return nil
		}
	} else if roomID := extractOpponentRoomIDAny(payload, self); roomID != nil {
		room = *roomID
		if room == initRoom {
			side = initInfo
		} else if room == matchRoom {
			side = matchInfo
		}
	} else {
		return nil
	}

	uid := stringifyID(side["uid"])
	uname, _ := side["uname"].(string)
	if uid == "" && uname == "" && room == 0 {
		return nil
	}
	if uname == "" {
		uname = "对面主播"
	}
	return &pkPush{UID: uid, Username: uname, RoomID: room}
}

type dedupe struct {
	window time.Duration
	seen   map[int]time.Duration
}

func newDedupe(window time.Duration) *dedupe {
	return &dedupe{window: window, seen: map[int]time.Duration{}}
}

func (d *dedupe) shouldProcess(key int, now time.Duration) bool {
	fresh := make(map[int]time.Duration, len(d.seen))
	for k, at := range d.seen {
		if now-at < d.window {
			fresh[k] = at
		}
	}
	d.seen = fresh
	if _, ok := d.seen[key]; ok {
		return false
	}
	d.seen[key] = now
	return true
}

func (d *dedupe) clear() {
	d.seen = map[int]time.Duration{}
}

type pkTracker struct {
	window      time.Duration
	lastSuccess map[int]time.Duration
	inflight    map[int]bool
}

func newPkTracker(window time.Duration) *pkTracker {
	return &pkTracker{
		window:      window,
		lastSuccess: map[int]time.Duration{},
		inflight:    map[int]bool{},
	}
}

func (t *pkTracker) evict(now time.Duration) {
	fresh := make(map[int]time.Duration, len(t.lastSuccess))
	for k, at := range t.lastSuccess {
		if now-at < t.window {
			fresh[k] = at
		}
	}
	t.lastSuccess = fresh
}

func (t *pkTracker) begin(key int, now time.Duration) bool {
	t.evict(now)
	if _, ok := t.lastSuccess[key]; ok {
		return false
	}
	if t.inflight[key] {
		return false
	}
	t.inflight[key] = true
	return true
}

func (t *pkTracker) markIncomplete(key int) {
	delete(t.inflight, key)
}

func (t *pkTracker) markSuccess(key int, now time.Duration) {
	delete(t.inflight, key)
	t.lastSuccess[key] = now
}

func (t *pkTracker) clear() {
	t.lastSuccess = map[int]time.Duration{}
	t.inflight = map[int]bool{}
}

func opponentAcceptable(p *pkPush) bool {
	if p == nil {
		return false
	}
	return p.UID != "" || strings.TrimSpace(p.Username) != "" || p.RoomID != 0
}

func completeOpponent(hint *pkPush, roomInit any, master any) *pkPush {
	out := &pkPush{}
	if hint != nil {
		*out = *hint
	}
	if roomInit != nil {
		root := asMap(roomInit)
		if asRoomID(root["code"]) == 0 {
			data := asMap(root["data"])
			if uid := stringifyID(data["uid"]); uid != "" {
				out.UID = uid
			}
			if rid := asRoomID(data["room_id"]); rid != 0 && out.RoomID == 0 {
				out.RoomID = rid
			}
		}
	}
	if master != nil {
		room := out.RoomID
		if filled := buildPkPayload(room, master); filled != nil {
			if filled.UID != "" {
				out.UID = filled.UID
			}
			if filled.Username != "" {
				out.Username = filled.Username
			}
			if filled.Follower != 0 {
				out.Follower = filled.Follower
			}
			if filled.RoomID != 0 {
				out.RoomID = filled.RoomID
			}
		}
	}
	if strings.TrimSpace(out.Username) == "" && (out.UID != "" || out.RoomID != 0) {
		out.Username = "对面主播"
	}
	if !opponentAcceptable(out) {
		return nil
	}
	return out
}

type pkPush struct {
	UID      string `json:"uid"`
	Username string `json:"username"`
	Follower int64  `json:"follower"`
	RoomID   int    `json:"roomid"`
}

func buildPkPayload(roomID int, masterInfo any) *pkPush {
	root := asMap(masterInfo)
	if root == nil || asRoomID(root["code"]) != 0 {
		return nil
	}
	data := asMap(root["data"])
	if data == nil {
		return nil
	}
	info := asMap(data["info"])
	if info == nil {
		return nil
	}
	uid := stringifyID(info["uid"])
	if uid == "" {
		return nil
	}
	uname, _ := info["uname"].(string)
	return &pkPush{
		UID:      uid,
		Username: uname,
		Follower: int64(asRoomID(data["follower_num"])),
		RoomID:   roomID,
	}
}

func stringifyID(value any) string {
	switch typed := value.(type) {
	case nil:
		return ""
	case string:
		return typed
	case float64:
		return strconv.FormatInt(int64(typed), 10)
	case int:
		return strconv.Itoa(typed)
	case int64:
		return strconv.FormatInt(typed, 10)
	case json.Number:
		return typed.String()
	default:
		return ""
	}
}
