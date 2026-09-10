package main

import (
	"encoding/json"
	"testing"
	"time"
)

const (
	selfRoom     = 12345
	opponentRoom = 67890
)

func parseJSON(t *testing.T, raw string) any {
	t.Helper()
	var payload any
	if err := json.Unmarshal([]byte(raw), &payload); err != nil {
		t.Fatal(err)
	}
	return payload
}

func TestExtractOpponentRoomID(t *testing.T) {
	t.Run("init_info is self", func(t *testing.T) {
		payload := parseJSON(t, `{"data":{"init_info":{"room_id":12345},"match_info":{"room_id":67890}}}`)
		got := extractOpponentRoomID(payload, selfRoom)
		if got == nil || *got != opponentRoom {
			t.Fatalf("got %v", got)
		}
	})
	t.Run("match_info is self", func(t *testing.T) {
		payload := parseJSON(t, `{"data":{"init_info":{"room_id":67890},"match_info":{"room_id":12345}}}`)
		got := extractOpponentRoomID(payload, selfRoom)
		if got == nil || *got != opponentRoom {
			t.Fatalf("got %v", got)
		}
	})
	t.Run("neither is self is unresolved", func(t *testing.T) {
		payload := parseJSON(t, `{"data":{"init_info":{"room_id":11111},"match_info":{"room_id":22222}}}`)
		if extractOpponentRoomIDAny(payload, []int{selfRoom}) != nil {
			t.Fatal("expected unresolved when neither side is self")
		}
	})
	t.Run("short or long self id matches", func(t *testing.T) {
		payload := parseJSON(t, `{"data":{"init_info":{"init_id":21347320,"uid":1,"uname":"自己"},"match_info":{"match_id":545068,"uid":2,"uname":"对面"}}}`)
		got := extractOpponentRoomIDAny(payload, []int{12345, 21347320})
		if got == nil || *got != 545068 {
			t.Fatalf("got %v", got)
		}
	})
	t.Run("both are self returns nil", func(t *testing.T) {
		payload := parseJSON(t, `{"data":{"init_info":{"room_id":12345},"match_info":{"room_id":12345}}}`)
		if extractOpponentRoomID(payload, selfRoom) != nil {
			t.Fatal("expected nil")
		}
	})
	t.Run("missing fields", func(t *testing.T) {
		if extractOpponentRoomID(parseJSON(t, `{"data":{}}`), selfRoom) != nil {
			t.Fatal("expected nil")
		}
	})
	t.Run("empty payload", func(t *testing.T) {
		if extractOpponentRoomID(parseJSON(t, `{}`), selfRoom) != nil {
			t.Fatal("expected nil")
		}
	})
	t.Run("nil payload", func(t *testing.T) {
		if extractOpponentRoomID(nil, selfRoom) != nil {
			t.Fatal("expected nil")
		}
	})
	t.Run("zero room id treated as missing", func(t *testing.T) {
		payload := parseJSON(t, `{"data":{"init_info":{"room_id":0},"match_info":{"room_id":67890}}}`)
		got := extractOpponentRoomID(payload, selfRoom)
		if got == nil || *got != opponentRoom {
			t.Fatalf("got %v", got)
		}
	})
	t.Run("string room id is coerced", func(t *testing.T) {
		payload := parseJSON(t, `{"data":{"init_info":{"room_id":"12345"},"match_info":{"room_id":"67890"}}}`)
		got := extractOpponentRoomID(payload, selfRoom)
		if got == nil || *got != opponentRoom {
			t.Fatalf("got %v", got)
		}
	})
	t.Run("flat room id pre shape", func(t *testing.T) {
		payload := parseJSON(t, `{"data":{"room_id":67890,"uname":"someone"}}`)
		got := extractOpponentRoomID(payload, selfRoom)
		if got == nil || *got != opponentRoom {
			t.Fatalf("got %v", got)
		}
	})
	t.Run("flat room id equal to self", func(t *testing.T) {
		payload := parseJSON(t, `{"data":{"room_id":12345}}`)
		if extractOpponentRoomID(payload, selfRoom) != nil {
			t.Fatal("expected nil")
		}
	})
	t.Run("nested shape wins over flat", func(t *testing.T) {
		payload := parseJSON(t, `{"data":{"room_id":12345,"init_info":{"room_id":12345},"match_info":{"room_id":67890}}}`)
		got := extractOpponentRoomID(payload, selfRoom)
		if got == nil || *got != opponentRoom {
			t.Fatalf("got %v", got)
		}
	})
	t.Run("init_id and match_id are room ids", func(t *testing.T) {
		payload := parseJSON(t, `{"data":{"init_info":{"init_id":12345,"uid":1},"match_info":{"match_id":67890,"uid":2,"uname":"对面"}}}`)
		got := extractOpponentRoomID(payload, selfRoom)
		if got == nil || *got != opponentRoom {
			t.Fatalf("got %v", got)
		}
	})
}

func TestExtractOpponentHint(t *testing.T) {
	t.Run("reads match_info as opponent", func(t *testing.T) {
		payload := parseJSON(t, `{"data":{"init_info":{"init_id":12345,"uid":11,"uname":"自己"},"match_info":{"match_id":67890,"uid":22,"uname":"对面主播乙"}}}`)
		got := extractOpponentHint(payload, selfRoom)
		if got == nil || got.UID != "22" || got.Username != "对面主播乙" || got.RoomID != opponentRoom {
			t.Fatalf("got %+v", got)
		}
	})
}

func TestDedupe(t *testing.T) {
	t.Run("first call passes", func(t *testing.T) {
		d := newDedupe(60 * time.Second)
		if !d.shouldProcess(opponentRoom, 1000*time.Second) {
			t.Fatal("expected pass")
		}
	})
	t.Run("repeat within window blocked", func(t *testing.T) {
		d := newDedupe(60 * time.Second)
		d.shouldProcess(opponentRoom, 1000*time.Second)
		if d.shouldProcess(opponentRoom, 1030*time.Second) {
			t.Fatal("expected block")
		}
	})
	t.Run("repeat after window passes", func(t *testing.T) {
		d := newDedupe(60 * time.Second)
		d.shouldProcess(opponentRoom, 1000*time.Second)
		if !d.shouldProcess(opponentRoom, 1061*time.Second) {
			t.Fatal("expected pass")
		}
	})
	t.Run("different keys independent", func(t *testing.T) {
		d := newDedupe(60 * time.Second)
		if !d.shouldProcess(opponentRoom, 1000*time.Second) || !d.shouldProcess(99999, 1000*time.Second) {
			t.Fatal("expected both to pass")
		}
	})
	t.Run("clear allows immediate reprocess", func(t *testing.T) {
		d := newDedupe(60 * time.Second)
		d.shouldProcess(opponentRoom, 1000*time.Second)
		d.clear()
		if !d.shouldProcess(opponentRoom, 1001*time.Second) {
			t.Fatal("expected pass")
		}
	})
	t.Run("expired entries are evicted", func(t *testing.T) {
		d := newDedupe(60 * time.Second)
		for room := 0; room < 100; room++ {
			d.shouldProcess(room, 1000*time.Second)
		}
		d.shouldProcess(999, 2000*time.Second)
		if len(d.seen) != 1 {
			t.Fatalf("len=%d", len(d.seen))
		}
	})
}

func TestBuildPkPayload(t *testing.T) {
	t.Run("maps api responses", func(t *testing.T) {
		master := parseJSON(t, `{"code":0,"data":{"info":{"uid":8739477,"uname":"老实憨厚的笑笑"},"follower_num":2821200}}`)
		got := buildPkPayload(opponentRoom, master)
		if got == nil || got.UID != "8739477" || got.Username != "老实憨厚的笑笑" || got.Follower != 2821200 || got.RoomID != opponentRoom {
			t.Fatalf("got %+v", got)
		}
	})
	t.Run("error code", func(t *testing.T) {
		if buildPkPayload(opponentRoom, parseJSON(t, `{"code":-352}`)) != nil {
			t.Fatal("expected nil")
		}
	})
	t.Run("missing data", func(t *testing.T) {
		if buildPkPayload(opponentRoom, parseJSON(t, `{"code":0}`)) != nil {
			t.Fatal("expected nil")
		}
	})
	t.Run("missing follower defaults to zero", func(t *testing.T) {
		master := parseJSON(t, `{"code":0,"data":{"info":{"uid":1,"uname":"x"}}}`)
		got := buildPkPayload(opponentRoom, master)
		if got == nil || got.Follower != 0 {
			t.Fatalf("got %+v", got)
		}
	})
}

func TestCompleteOpponent_KeepsUidWhenMasterFails(t *testing.T) {
	hint := &pkPush{Username: "笑笑", RoomID: opponentRoom}
	roomInit := parseJSON(t, `{"code":0,"data":{"uid":8739477,"room_id":67890}}`)
	got := completeOpponent(hint, roomInit, nil)
	if got == nil || got.UID != "8739477" || got.Username != "笑笑" || got.RoomID != opponentRoom {
		t.Fatalf("got %+v", got)
	}
}

func TestCompleteOpponent_HintOnlyIsAcceptable(t *testing.T) {
	got := completeOpponent(&pkPush{Username: "沅依utatte", RoomID: 1907447144}, nil, nil)
	if !opponentAcceptable(got) || got.UID != "" || got.Username != "沅依utatte" {
		t.Fatalf("got %+v", got)
	}
}

func TestPkTracker_FailedPreAllowsStart(t *testing.T) {
	tr := newPkTracker(60 * time.Second)
	if !tr.begin(opponentRoom, 1000*time.Second) {
		t.Fatal("first PRE should begin")
	}
	tr.markIncomplete(opponentRoom)
	if !tr.begin(opponentRoom, 1001*time.Second) {
		t.Fatal("START after incomplete PRE must retry")
	}
	tr.markSuccess(opponentRoom, 1001*time.Second)
	if tr.begin(opponentRoom, 1002*time.Second) {
		t.Fatal("success must block duplicate announce")
	}
}

func TestParseSelfRoomIDs(t *testing.T) {
	body := parseJSON(t, `{"code":0,"data":{"room_id":21347320,"short_id":12345}}`)
	got := parseSelfRoomIDs(body, 12345)
	if !containsRoom(got, 12345) || !containsRoom(got, 21347320) {
		t.Fatalf("got %v", got)
	}
}
