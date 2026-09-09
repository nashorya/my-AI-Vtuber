package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net/http"
	"net/http/cookiejar"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"time"

	"aivtuber/danmaku-bridge/bililogin"
	"github.com/skip2/go-qrcode"
)

const (
	ua           = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
	generateURL  = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate?source=main-fe-header"
	pollURL      = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll"
	spiURL       = "https://api.bilibili.com/x/frontend/finger/spi"
	liveInfoURL  = "https://api.live.bilibili.com/xlive/web-ucenter/user/live_info"
	myInfoURL    = "https://api.bilibili.com/x/space/myinfo"
	roomInfoURL  = "https://api.live.bilibili.com/room/v1/Room/getRoomInfoOld"
	pollInterval = 1500 * time.Millisecond
	pollTimeout  = 180 * time.Second
)

func main() {
	configFlag := flag.String("config", "", "要写入的 config.json")
	noWrite := flag.Bool("no-write", false, "只登录，不写配置")
	flag.Parse()

	jar, err := cookiejar.New(nil)
	if err != nil {
		fatal(err)
	}
	client := &http.Client{
		Timeout: 15 * time.Second,
		Jar:     jar,
		Transport: &http.Transport{
			Proxy: nil,
		},
	}

	creds := bililogin.Credentials{}
	if buvid, err := fetchBuvid3(client); err == nil && buvid != "" {
		creds.Buvid3 = buvid
	}

	qrURL, qrKey, err := generateQR(client)
	if err != nil {
		fatal(err)
	}

	pngPath := filepath.Join(os.TempDir(), "aivtuber-bili-login.png")
	if err := qrcode.WriteFile(qrURL, qrcode.Medium, 320, pngPath); err != nil {
		fatal(fmt.Errorf("生成二维码失败: %w", err))
	}
	code, err := qrcode.New(qrURL, qrcode.Medium)
	if err != nil {
		fatal(err)
	}

	fmt.Println("用哔哩哔哩 App 扫码，并在手机上确认登录。")
	fmt.Println(code.ToSmallString(false))
	fmt.Println("二维码图片:", pngPath)
	openFile(pngPath)

	loginURL, err := waitForLogin(client, qrKey)
	if err != nil {
		fatal(err)
	}

	creds = bililogin.MergeCredentials(creds, cookiesFromJar(jar))
	creds = bililogin.MergeCredentials(creds, bililogin.CredentialsFromCrossDomainURL(loginURL))
	if creds.Sessdata == "" || creds.BiliJct == "" {
		fatal(fmt.Errorf("登录成功但未拿到 SESSDATA / bili_jct"))
	}
	if creds.Buvid3 == "" {
		if buvid, err := fetchBuvid3(client); err == nil {
			creds.Buvid3 = buvid
		}
	}

	roomID, _ := fetchRoomID(client)
	fmt.Printf("登录成功 uid=%s room=%d\n", displayUID(creds.DedeUserID), roomID)

	if *noWrite {
		fmt.Println("未写入配置（--no-write）。把房间号和 Cookie 填进控制台直播集成即可。")
		return
	}

	cwd, _ := os.Getwd()
	repoRoot := findRepoRoot(cwd)
	configPath, err := bililogin.LocateConfig(*configFlag, cwd, repoRoot)
	if err != nil {
		fatal(err)
	}
	backup, err := bililogin.PatchConfig(configPath, creds, roomID)
	if err != nil {
		fatal(err)
	}
	fmt.Printf("已写入 %s\n", configPath)
	if backup != "" {
		fmt.Printf("备份 %s\n", backup)
	}
	if roomID == 0 {
		fmt.Println("未查到房间号，请在控制台里手填。")
	}
	fmt.Println("打开控制台：勾选启用 B 站弹幕，保存并应用。")
}

func generateQR(client *http.Client) (string, string, error) {
	body, err := get(client, generateURL)
	if err != nil {
		return "", "", err
	}
	var payload struct {
		Code    int    `json:"code"`
		Message string `json:"message"`
		Data    struct {
			URL       string `json:"url"`
			QrcodeKey string `json:"qrcode_key"`
		} `json:"data"`
	}
	if err := json.Unmarshal(body, &payload); err != nil {
		return "", "", err
	}
	if payload.Code != 0 || payload.Data.URL == "" || payload.Data.QrcodeKey == "" {
		if payload.Message == "" {
			payload.Message = "无法申请登录二维码"
		}
		return "", "", fmt.Errorf("%s", payload.Message)
	}
	return payload.Data.URL, payload.Data.QrcodeKey, nil
}

func waitForLogin(client *http.Client, qrKey string) (string, error) {
	deadline := time.Now().Add(pollTimeout)
	announcedScan := false
	for time.Now().Before(deadline) {
		body, err := get(client, pollURL+"?"+url.Values{
			"qrcode_key": {qrKey},
			"source":     {"main-fe-header"},
		}.Encode())
		if err != nil {
			return "", err
		}
		status, err := bililogin.ParsePollStatus(body)
		if err != nil {
			return "", err
		}
		switch status {
		case bililogin.PollOK:
			return pollLoginURL(body), nil
		case bililogin.PollScanned:
			if !announcedScan {
				fmt.Println("已扫码，等待手机确认…")
				announcedScan = true
			}
		case bililogin.PollExpired:
			return "", fmt.Errorf("二维码已过期，请重新运行")
		}
		time.Sleep(pollInterval)
	}
	return "", fmt.Errorf("等待扫码超时，请重新运行")
}

func pollLoginURL(body []byte) string {
	var payload struct {
		Data struct {
			URL string `json:"url"`
		} `json:"data"`
	}
	_ = json.Unmarshal(body, &payload)
	return payload.Data.URL
}

func fetchBuvid3(client *http.Client) (string, error) {
	body, err := get(client, spiURL)
	if err != nil {
		return "", err
	}
	var payload struct {
		Code int `json:"code"`
		Data struct {
			B3 string `json:"b_3"`
		} `json:"data"`
	}
	if err := json.Unmarshal(body, &payload); err != nil {
		return "", err
	}
	if payload.Code != 0 {
		return "", fmt.Errorf("buvid3 请求失败")
	}
	return payload.Data.B3, nil
}

func fetchRoomID(client *http.Client) (int, error) {
	if room, err := roomFromURL(client, liveInfoURL); err == nil && room > 0 {
		return room, nil
	}
	uid, err := fetchUID(client)
	if err != nil || uid == "" {
		return 0, err
	}
	return roomFromURL(client, roomInfoURL+"?mid="+url.QueryEscape(uid))
}

func fetchUID(client *http.Client) (string, error) {
	body, err := get(client, myInfoURL)
	if err != nil {
		return "", err
	}
	var payload struct {
		Code int `json:"code"`
		Data struct {
			Mid json.Number `json:"mid"`
		} `json:"data"`
	}
	if err := json.Unmarshal(body, &payload); err != nil {
		return "", err
	}
	if payload.Code != 0 {
		return "", nil
	}
	return payload.Data.Mid.String(), nil
}

func roomFromURL(client *http.Client, rawURL string) (int, error) {
	body, err := get(client, rawURL)
	if err != nil {
		return 0, err
	}
	return bililogin.ExtractRoomID(body)
}

func cookiesFromJar(jar http.CookieJar) bililogin.Credentials {
	hosts := []string{
		"https://www.bilibili.com/",
		"https://passport.bilibili.com/",
		"https://api.bilibili.com/",
		"https://api.live.bilibili.com/",
	}
	var creds bililogin.Credentials
	for _, host := range hosts {
		u, err := url.Parse(host)
		if err != nil {
			continue
		}
		creds = bililogin.MergeCredentials(creds, bililogin.ParseCookies(jar.Cookies(u)))
	}
	return creds
}

func get(client *http.Client, rawURL string) ([]byte, error) {
	body, _, err := getWithCookies(client, rawURL)
	return body, err
}

func getWithCookies(client *http.Client, rawURL string) ([]byte, []*http.Cookie, error) {
	req, err := http.NewRequest(http.MethodGet, rawURL, nil)
	if err != nil {
		return nil, nil, err
	}
	req.Header.Set("User-Agent", ua)
	req.Header.Set("Referer", "https://www.bilibili.com/")
	req.Header.Set("Origin", "https://www.bilibili.com")
	resp, err := client.Do(req)
	if err != nil {
		return nil, nil, err
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return nil, nil, err
	}
	if resp.StatusCode >= 400 {
		return nil, nil, fmt.Errorf("%s: HTTP %d", rawURL, resp.StatusCode)
	}
	return body, resp.Cookies(), nil
}

func findRepoRoot(cwd string) string {
	dir := cwd
	for i := 0; i < 6; i++ {
		if fileExists(filepath.Join(dir, "danmaku-bridge")) && fileExists(filepath.Join(dir, "AIVTuber.Core")) {
			return dir
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			break
		}
		dir = parent
	}
	return cwd
}

func fileExists(path string) bool {
	info, err := os.Stat(path)
	return err == nil && info.IsDir()
}

func openFile(path string) {
	var cmd *exec.Cmd
	switch runtime.GOOS {
	case "windows":
		cmd = exec.Command("cmd", "/c", "start", "", path)
	case "darwin":
		cmd = exec.Command("open", path)
	default:
		cmd = exec.Command("xdg-open", path)
	}
	_ = cmd.Start()
}

func displayUID(uid string) string {
	if strings.TrimSpace(uid) == "" {
		return "?"
	}
	return uid
}

func fatal(err error) {
	fmt.Fprintln(os.Stderr, err.Error())
	os.Exit(1)
}
