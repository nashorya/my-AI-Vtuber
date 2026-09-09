package bililogin

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestParseCookies(t *testing.T) {
	got := ParseCookies([]*http.Cookie{
		{Name: "SESSDATA", Value: "sess%2Cdata"},
		{Name: "bili_jct", Value: "jct-token"},
		{Name: "buvid3", Value: "BUVID3-1"},
		{Name: "DedeUserID", Value: "4242"},
		{Name: "other", Value: "ignore"},
	})
	if got.Sessdata != "sess%2Cdata" || got.BiliJct != "jct-token" || got.Buvid3 != "BUVID3-1" || got.DedeUserID != "4242" {
		t.Fatalf("ParseCookies = %+v", got)
	}
}

func TestMergeCookiesKeepsExistingBuvid(t *testing.T) {
	base := Credentials{Buvid3: "keep-me"}
	got := MergeCredentials(base, ParseCookies([]*http.Cookie{
		{Name: "SESSDATA", Value: "s"},
		{Name: "bili_jct", Value: "j"},
	}))
	if got.Sessdata != "s" || got.BiliJct != "j" || got.Buvid3 != "keep-me" {
		t.Fatalf("MergeCredentials = %+v", got)
	}
}

func TestParsePollStatus(t *testing.T) {
	cases := []struct {
		name string
		body string
		want PollStatus
	}{
		{"waiting", `{"code":0,"data":{"code":86101,"message":"未扫码"}}`, PollWaiting},
		{"scanned", `{"code":0,"data":{"code":86090,"message":"已扫码未确认"}}`, PollScanned},
		{"expired", `{"code":0,"data":{"code":86038,"message":"二维码已失效"}}`, PollExpired},
		{"ok", `{"code":0,"data":{"code":0,"url":"https://example.com"}}`, PollOK},
		{"api error", `{"code":-1,"message":"nope"}`, PollError},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, err := ParsePollStatus([]byte(tc.body))
			if tc.want == PollError {
				if err == nil {
					t.Fatal("expected error")
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			if got != tc.want {
				t.Fatalf("got %v want %v", got, tc.want)
			}
		})
	}
}

func TestCredentialsFromCrossDomainURL(t *testing.T) {
	got := CredentialsFromCrossDomainURL("https://passport.biligame.com/crossDomain?DedeUserID=9&SESSDATA=aa%2Cbb&bili_jct=jj&gourl=https%3A%2F%2Fpassport.bilibili.com")
	if got.Sessdata != "aa,bb" || got.BiliJct != "jj" || got.DedeUserID != "9" {
		t.Fatalf("CredentialsFromCrossDomainURL = %+v", got)
	}
}

func TestExtractRoomID(t *testing.T) {
	cases := []struct {
		name string
		body string
		want int
	}{
		{"room_id", `{"code":0,"data":{"room_id":25902599}}`, 25902599},
		{"roomid", `{"code":0,"data":{"roomid":123}}`, 123},
		{"string", `{"code":0,"data":{"room_id":"456"}}`, 456},
		{"missing", `{"code":0,"data":{}}`, 0},
		{"api fail", `{"code":-101,"data":{}}`, 0},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, err := ExtractRoomID([]byte(tc.body))
			if err != nil {
				t.Fatal(err)
			}
			if got != tc.want {
				t.Fatalf("got %d want %d", got, tc.want)
			}
		})
	}
}

func TestPatchConfigWritesBilibiliOnly(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "config.json")
	original := `{
  "tts": { "provider": "aliyun" },
  "bilibili": {
    "enable": false,
    "room_id": 0,
    "sessdata": "",
    "bili_jct": "",
    "buvid3": "",
    "push_port": 19876
  }
}`
	if err := os.WriteFile(path, []byte(original), 0o644); err != nil {
		t.Fatal(err)
	}

	backup, err := PatchConfig(path, Credentials{
		Sessdata: "S",
		BiliJct:  "J",
		Buvid3:   "B",
	}, 25902599)
	if err != nil {
		t.Fatal(err)
	}
	if backup == "" {
		t.Fatal("expected backup path")
	}

	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var cfg map[string]any
	if err := json.Unmarshal(raw, &cfg); err != nil {
		t.Fatal(err)
	}
	bili := cfg["bilibili"].(map[string]any)
	if bili["sessdata"] != "S" || bili["bili_jct"] != "J" || bili["buvid3"] != "B" {
		t.Fatalf("cookies not written: %#v", bili)
	}
	if int(bili["room_id"].(float64)) != 25902599 {
		t.Fatalf("room_id = %v", bili["room_id"])
	}
	if bili["enable"] != false {
		t.Fatal("enable should stay unchanged")
	}
	if int(bili["push_port"].(float64)) != 19876 {
		t.Fatal("push_port should stay unchanged")
	}
	if cfg["tts"].(map[string]any)["provider"] != "aliyun" {
		t.Fatal("unrelated keys were rewritten incorrectly")
	}
}

func TestLocateConfigPrefersExplicitThenCwd(t *testing.T) {
	dir := t.TempDir()
	cwdFile := filepath.Join(dir, "config.json")
	other := filepath.Join(dir, "other.json")
	if err := os.WriteFile(cwdFile, []byte(`{}`), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(other, []byte(`{}`), 0o644); err != nil {
		t.Fatal(err)
	}

	got, err := LocateConfig(other, dir, filepath.Join(dir, "missing-repo"))
	if err != nil {
		t.Fatal(err)
	}
	if got != other {
		t.Fatalf("explicit path: got %s", got)
	}

	got, err = LocateConfig("", dir, filepath.Join(dir, "missing-repo"))
	if err != nil {
		t.Fatal(err)
	}
	if got != cwdFile {
		t.Fatalf("cwd path: got %s", got)
	}
}

func TestLocateConfigErrorWhenMissing(t *testing.T) {
	dir := t.TempDir()
	_, err := LocateConfig("", dir, dir)
	if err == nil || !strings.Contains(err.Error(), "未找到") {
		t.Fatalf("expected missing-config error, got %v", err)
	}
}
