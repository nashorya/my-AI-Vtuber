package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/xifan2333/blivedm-go/client"
	"github.com/xifan2333/blivedm-go/message"
	log "github.com/sirupsen/logrus"
)

var (
	pkStartCmds = []string{"PK_BATTLE_PRE", "PK_BATTLE_PRE_NEW", "PK_BATTLE_START", "PK_BATTLE_START_NEW"}
	pkEndCmds   = []string{"PK_BATTLE_END", "PK_END", "PK_BATTLE_CRIT", "PK_BATTLE_SETTLE_NEW"}
	apiUA       = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
)

func main() {
	log.SetOutput(os.Stdout)
	log.SetLevel(log.WarnLevel)

	roomID, _ := strconv.Atoi(strings.TrimSpace(os.Getenv("ROOM_ID")))
	if roomID == 0 {
		fmt.Println("ERROR: ROOM_ID not set")
		os.Exit(1)
	}

	pushURL := envOr("PUSH_URL", "http://localhost:19876/danmaku/")
	pkPushURL := strings.TrimSpace(os.Getenv("PK_PUSH_URL"))
	pkNotice := os.Getenv("PK_NOTICE") == "1"
	cookie := buildCookie(
		os.Getenv("SESSDATA"),
		os.Getenv("BILI_JCT"),
		os.Getenv("BUVID3"),
	)

	httpClient := &http.Client{
		Timeout: 3 * time.Second,
		Transport: &http.Transport{
			Proxy: nil,
		},
	}

	c := client.NewClient(roomID)
	if cookie != "" {
		c.SetCookie(cookie)
	}

	c.OnDanmaku(func(danmaku *message.Danmaku) {
		if danmaku == nil || danmaku.Content == "" {
			return
		}
		uid := "0"
		uname := "unknown"
		if danmaku.Sender != nil {
			if id := stringifyID(danmaku.Sender.Uid); id != "" {
				uid = id
			}
			if danmaku.Sender.Uname != "" {
				uname = danmaku.Sender.Uname
			}
		}
		postJSON(httpClient, pushURL, map[string]any{
			"uid":      uid,
			"username": uname,
			"content":  danmaku.Content,
		})
	})

	if pkNotice && pkPushURL != "" {
		selfIDs := resolveRoomIDs(httpClient, roomID)
		pk := newPkTracker(60 * time.Second)
		onStart := func(raw string) {
			defer func() {
				if rec := recover(); rec != nil {
					fmt.Printf("[PK] handler error: %v\n", rec)
				}
			}()
			preview := raw
			if len(preview) > 400 {
				preview = preview[:400]
			}
			fmt.Printf("[PK] raw=%s\n", preview)
			var payload any
			if err := json.Unmarshal([]byte(raw), &payload); err != nil {
				return
			}
			hint := extractOpponentHintAny(payload, selfIDs)
			oppRoom := extractOpponentRoomIDAny(payload, selfIDs)
			if oppRoom == nil && hint != nil && hint.RoomID != 0 {
				oppRoom = intPtr(hint.RoomID)
			}
			if oppRoom == nil && hint == nil {
				fmt.Println("[PK] start event unresolved opponent")
				return
			}
			key := 0
			if oppRoom != nil {
				key = *oppRoom
			}
			now := time.Duration(time.Now().UnixNano())
			if !pk.begin(key, now) {
				return
			}
			var roomInit, master any
			if oppRoom != nil {
				roomInit, master = fetchOpponentParts(httpClient, *oppRoom)
			}
			opponent := completeOpponent(hint, roomInit, master)
			if !opponentAcceptable(opponent) {
				pk.markIncomplete(key)
				fmt.Printf("[PK] incomplete opponent room=%d\n", key)
				return
			}
			if opponent.UID == "" {
				pk.markIncomplete(key)
			} else {
				pk.markSuccess(key, now)
			}
			fmt.Printf("[PK] opponent: %s (%d fans, room %d uid=%s)\n",
				opponent.Username, opponent.Follower, opponent.RoomID, opponent.UID)
			postJSON(httpClient, pkPushURL, opponent)
		}
		onEnd := func(string) {
			pk.clear()
			fmt.Println("[PK] match ended")
			postJSON(httpClient, pkPushURL, map[string]string{"type": "end"})
		}
		for _, cmd := range pkStartCmds {
			c.RegisterCustomEventHandler(cmd, onStart)
		}
		for _, cmd := range pkEndCmds {
			c.RegisterCustomEventHandler(cmd, onEnd)
		}
		fmt.Printf("PK notice enabled, pushing to %s\n", pkPushURL)
	}

	fmt.Printf("Connecting to room %d...\n", roomID)
	if err := c.Start(); err != nil {
		fmt.Printf("ERROR: %v\n", err)
		os.Exit(1)
	}
	select {}
}

func envOr(key, fallback string) string {
	if v := strings.TrimSpace(os.Getenv(key)); v != "" {
		return v
	}
	return fallback
}

func buildCookie(sessdata, biliJct, buvid3 string) string {
	parts := make([]string, 0, 3)
	if sessdata != "" {
		parts = append(parts, "SESSDATA="+sessdata)
	}
	if biliJct != "" {
		parts = append(parts, "bili_jct="+biliJct)
	}
	if buvid3 != "" {
		parts = append(parts, "buvid3="+buvid3)
	}
	return strings.Join(parts, "; ")
}

func resolveRoomIDs(httpClient *http.Client, configured int) []int {
	headers := map[string]string{"User-Agent": apiUA}
	body, err := getJSON(httpClient, "https://api.live.bilibili.com/room/v1/Room/room_init", map[string]string{
		"id": strconv.Itoa(configured),
	}, headers)
	if err != nil {
		fmt.Printf("[PK] self room_init failed: %v\n", err)
		return []int{configured}
	}
	ids := parseSelfRoomIDs(body, configured)
	fmt.Printf("[PK] self rooms %v\n", ids)
	return ids
}

func fetchOpponentParts(httpClient *http.Client, roomID int) (any, any) {
	headers := map[string]string{"User-Agent": apiUA}
	room, err := getJSON(httpClient, "https://api.live.bilibili.com/room/v1/Room/room_init", map[string]string{
		"id": strconv.Itoa(roomID),
	}, headers)
	if err != nil {
		fmt.Printf("[PK] room_init error room=%d: %v\n", roomID, err)
		return nil, nil
	}
	root := asMap(room)
	if asRoomID(root["code"]) != 0 {
		fmt.Printf("[PK] room_init failed room=%d code=%v\n", roomID, root["code"])
		return room, nil
	}
	uid := stringifyID(asMap(root["data"])["uid"])
	if uid == "" {
		return room, nil
	}
	master, err := getJSON(httpClient, "https://api.live.bilibili.com/live_user/v1/Master/info", map[string]string{
		"uid": uid,
	}, headers)
	if err != nil {
		fmt.Printf("[PK] Master/info error uid=%s: %v\n", uid, err)
		return room, nil
	}
	return room, master
}

func getJSON(httpClient *http.Client, rawURL string, query, headers map[string]string) (any, error) {
	req, err := http.NewRequest(http.MethodGet, rawURL, nil)
	if err != nil {
		return nil, err
	}
	q := req.URL.Query()
	for k, v := range query {
		q.Set(k, v)
	}
	req.URL.RawQuery = q.Encode()
	for k, v := range headers {
		req.Header.Set(k, v)
	}
	resp, err := httpClient.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, err
	}
	var parsed any
	if err := json.Unmarshal(body, &parsed); err != nil {
		return nil, err
	}
	return parsed, nil
}

func postJSON(httpClient *http.Client, url string, payload any) {
	if url == "" {
		return
	}
	body, err := json.Marshal(payload)
	if err != nil {
		return
	}
	req, err := http.NewRequest(http.MethodPost, url, bytes.NewReader(body))
	if err != nil {
		return
	}
	req.Header.Set("Content-Type", "application/json")
	resp, err := httpClient.Do(req)
	if err != nil {
		return
	}
	io.Copy(io.Discard, resp.Body)
	resp.Body.Close()
}
