package bililogin

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

type Credentials struct {
	Sessdata   string
	BiliJct    string
	Buvid3     string
	DedeUserID string
}

type PollStatus int

const (
	PollWaiting PollStatus = iota
	PollScanned
	PollExpired
	PollOK
	PollError
)

func ParseCookies(cookies []*http.Cookie) Credentials {
	var creds Credentials
	for _, cookie := range cookies {
		if cookie == nil {
			continue
		}
		switch strings.ToLower(cookie.Name) {
		case "sessdata":
			creds.Sessdata = cookie.Value
		case "bili_jct":
			creds.BiliJct = cookie.Value
		case "buvid3":
			creds.Buvid3 = cookie.Value
		case "dedeuserid":
			creds.DedeUserID = cookie.Value
		}
	}
	return creds
}

func MergeCredentials(base, extra Credentials) Credentials {
	if extra.Sessdata != "" {
		base.Sessdata = extra.Sessdata
	}
	if extra.BiliJct != "" {
		base.BiliJct = extra.BiliJct
	}
	if extra.Buvid3 != "" {
		base.Buvid3 = extra.Buvid3
	}
	if extra.DedeUserID != "" {
		base.DedeUserID = extra.DedeUserID
	}
	return base
}

func ParsePollStatus(body []byte) (PollStatus, error) {
	var payload struct {
		Code    int    `json:"code"`
		Message string `json:"message"`
		Data    struct {
			Code int `json:"code"`
		} `json:"data"`
	}
	if err := json.Unmarshal(body, &payload); err != nil {
		return PollError, err
	}
	if payload.Code != 0 {
		if payload.Message == "" {
			payload.Message = "passport error"
		}
		return PollError, fmt.Errorf("%s", payload.Message)
	}
	switch payload.Data.Code {
	case 0:
		return PollOK, nil
	case 86101:
		return PollWaiting, nil
	case 86090:
		return PollScanned, nil
	case 86038:
		return PollExpired, nil
	default:
		return PollError, fmt.Errorf("unexpected poll code %d", payload.Data.Code)
	}
}

func CredentialsFromCrossDomainURL(raw string) Credentials {
	parsed, err := url.Parse(raw)
	if err != nil {
		return Credentials{}
	}
	query := parsed.Query()
	return Credentials{
		Sessdata:   query.Get("SESSDATA"),
		BiliJct:    query.Get("bili_jct"),
		DedeUserID: query.Get("DedeUserID"),
		Buvid3:     query.Get("buvid3"),
	}
}

func ExtractRoomID(body []byte) (int, error) {
	var payload struct {
		Code int            `json:"code"`
		Data map[string]any `json:"data"`
	}
	if err := json.Unmarshal(body, &payload); err != nil {
		return 0, err
	}
	if payload.Code != 0 || payload.Data == nil {
		return 0, nil
	}
	for _, key := range []string{"room_id", "roomid"} {
		if room := asRoomID(payload.Data[key]); room > 0 {
			return room, nil
		}
	}
	return 0, nil
}

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
		n, _ := typed.Int64()
		return int(n)
	case string:
		n, _ := strconv.Atoi(strings.TrimSpace(typed))
		return n
	default:
		return 0
	}
}

func LocateConfig(explicit, cwd, repoRoot string) (string, error) {
	if strings.TrimSpace(explicit) != "" {
		path := explicit
		if !filepath.IsAbs(path) {
			path = filepath.Join(cwd, path)
		}
		info, err := os.Stat(path)
		if err != nil || info.IsDir() {
			return "", fmt.Errorf("配置文件不存在: %s", path)
		}
		return path, nil
	}

	candidates := []string{
		filepath.Join(cwd, "config.json"),
		filepath.Join(repoRoot, "config.json"),
		filepath.Join(repoRoot, "artifacts", "local-test", "publish", "config.json"),
		filepath.Join(repoRoot, "App", "bin", "Debug", "net10.0-windows", "win-x64", "config.json"),
	}
	for _, candidate := range candidates {
		info, err := os.Stat(candidate)
		if err == nil && !info.IsDir() {
			return candidate, nil
		}
	}
	return "", fmt.Errorf("未找到运行配置；请使用 --config 指定现有 config.json")
}

func PatchConfig(path string, creds Credentials, roomID int) (string, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return "", fmt.Errorf("无法读取配置文件: %w", err)
	}

	var root map[string]any
	if err := json.Unmarshal(raw, &root); err != nil {
		return "", fmt.Errorf("无法读取配置文件: %w", err)
	}
	if root == nil {
		return "", fmt.Errorf("配置文件根节点必须是 JSON 对象")
	}

	bili, _ := root["bilibili"].(map[string]any)
	if bili == nil {
		bili = map[string]any{}
		root["bilibili"] = bili
	}
	bili["sessdata"] = creds.Sessdata
	bili["bili_jct"] = creds.BiliJct
	bili["buvid3"] = creds.Buvid3
	if roomID > 0 {
		bili["room_id"] = roomID
	}

	encoded, err := json.MarshalIndent(root, "", "  ")
	if err != nil {
		return "", err
	}
	encoded = append(encoded, '\n')

	backup := path + ".bak"
	if err := os.WriteFile(backup, raw, 0o644); err != nil {
		return "", fmt.Errorf("写入配置失败: %w", err)
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, encoded, 0o644); err != nil {
		return "", fmt.Errorf("写入配置失败: %w", err)
	}
	if err := os.Rename(tmp, path); err != nil {
		return "", fmt.Errorf("写入配置失败: %w", err)
	}
	return backup, nil
}
