package main

import (
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestPostJSON_DanmakuShape(t *testing.T) {
	var got map[string]any
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/danmaku/" {
			t.Errorf("path=%s", r.URL.Path)
		}
		body, _ := io.ReadAll(r.Body)
		if err := json.Unmarshal(body, &got); err != nil {
			t.Error(err)
		}
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte(`{"status":"ok"}`))
	}))
	defer srv.Close()

	client := &http.Client{Timeout: 3 * time.Second}
	postJSON(client, srv.URL+"/danmaku/", map[string]any{
		"uid":      "123",
		"username": "测试用户",
		"content":  "你好",
	})

	if got["uid"] != "123" || got["username"] != "测试用户" || got["content"] != "你好" {
		t.Fatalf("payload=%v", got)
	}
}

func TestPostJSON_PkEndShape(t *testing.T) {
	var got map[string]any
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		_ = json.Unmarshal(body, &got)
		w.WriteHeader(http.StatusOK)
	}))
	defer srv.Close()

	postJSON(&http.Client{Timeout: 3 * time.Second}, srv.URL+"/pk/", map[string]string{"type": "end"})
	if got["type"] != "end" {
		t.Fatalf("payload=%v", got)
	}
}

func TestRoomInit_PublicAPI(t *testing.T) {
	client := &http.Client{Timeout: 8 * time.Second, Transport: &http.Transport{Proxy: nil}}
	room, err := getJSON(client, "https://api.live.bilibili.com/room/v1/Room/room_init", map[string]string{
		"id": "25902599",
	}, map[string]string{"User-Agent": apiUA})
	if err != nil {
		t.Fatal(err)
	}
	root := asMap(room)
	if asRoomID(root["code"]) != 0 {
		t.Fatalf("room_init code=%v", root["code"])
	}
	uid := stringifyID(asMap(root["data"])["uid"])
	if uid == "" {
		t.Fatal("missing uid")
	}
}
