# 鉴权服务部署记录与升级步骤（腾讯云轻量服务器）

2026-10-02 已部署到现有的腾讯云轻量服务器（Ubuntu 22.04 x86_64，服务器上本来就有 nginx 和 certbot）。地址：`https://auth.eatconfusion.online/`。

## 现状

| 项 | 内容 |
|---|---|
| 程序 | `/opt/aivtuber-auth/`（框架依赖发布，约 1 MB） |
| .NET 运行时 | `/opt/dotnet`（只装了 ASP.NET Core 运行时，没有动系统自带的 .NET） |
| 服务 | systemd：`aivtuber-auth`，只监听 `127.0.0.1:5080`，用专用账号 `aivtuber-auth` 运行 |
| 数据库 | `/var/lib/aivtuber-auth/auth.db`（SQLite） |
| 对外入口 | nginx 站点 `/etc/nginx/sites-available/aivtuber-auth`，反向代理到 5080 |
| 证书 | Let's Encrypt（certbot，nginx 方式），自动续期 |
| DNS | 腾讯云 DNSPod：`auth` → `A` → 服务器公网 IP |
| 备份 | `/usr/local/bin/backup-auth-db.sh`，每天 05:00，保留 14 天，只有 root 可读 |

## 2026-10-03 邀请码注册升级

已将邀请码注册服务部署到上述服务器（代码修复提交 `38219fe`）。升级前备份在 `/var/backups/aivtuber-auth/releases/invite-20261003T080951Z/`，含数据库快照和旧程序。程序 DLL SHA-256：`168721bed09cf51baa020ff0fffadd282e9d90d509e38ecb0c202c37aa40627b`。

服务与 nginx 正常，已通过公网 HTTPS 验证注册、登录、心跳、退出、邀请码不可重用和停用账号拒绝访问。专用验收账号已停用。测试与剩余客户端验收边界见 [接续记录](2026-10-03-invite-handoff.md)。

## 为什么用框架依赖发布

自带运行时的单文件版本约 100 MB。从本地电脑上传到这台服务器的速度只有每秒约 20 KB，要传一个多小时；框架依赖发布只有 1 MB，服务器自己从微软下载运行时只要一分钟。

## 管理命令（在服务器上）

```bash
cd /opt/aivtuber-auth
sudo -u aivtuber-auth DOTNET_ROOT=/opt/dotnet /opt/dotnet/dotnet AIVTuber.AuthServer.dll admin --db /var/lib/aivtuber-auth/auth.db list
```

后面接 `create`、`extend`、`disable`、`set-daily`、`add-today`、`usage`、`invite create` 等，见 `docs/auth/README.md`。建账号示例：

```bash
echo '主播的密码' | sudo -u aivtuber-auth DOTNET_ROOT=/opt/dotnet /opt/dotnet/dotnet AIVTuber.AuthServer.dll admin \
  --db /var/lib/aivtuber-auth/auth.db create --username alice --profile streamer-017 --days 30 --password-stdin
```

## 升级程序

在本地电脑上：

```bash
dotnet publish AIVTuber.AuthServer -c Release -r linux-x64 --self-contained false -o /tmp/auth-fd
tar -C /tmp/auth-fd -czf /tmp/auth-fd.tgz .
scp /tmp/auth-fd.tgz <用户>@<服务器>:/tmp/
```

在服务器上（先手动备份一次）：

```bash
sudo /usr/local/bin/backup-auth-db.sh
sudo systemctl stop aivtuber-auth
sudo tar -xzf /tmp/auth-fd.tgz -C /opt/aivtuber-auth && rm /tmp/auth-fd.tgz
sudo chown -R aivtuber-auth: /opt/aivtuber-auth
sudo systemctl start aivtuber-auth
systemctl is-active aivtuber-auth
```

数据库结构的升级在服务启动时自动完成（含邀请码表）。

## 验证

```bash
curl -s -X POST https://auth.eatconfusion.online/v1/auth/login -H 'content-type: application/json' \
  -d '{"username":"x","password":"y","profile_id":"p","app_version":"0","credential_revision":1}'
```

应返回 `{"status":"invalid_credentials",...}`。

## 部署到另一台新服务器时

1. 服务器上装 ASP.NET Core 10 运行时到 `/opt/dotnet`（从微软下载 `aspnetcore-runtime-10.0.x-linux-x64.tar.gz` 解压）。
2. 建系统账号 `aivtuber-auth`，把发布包解压到 `/opt/aivtuber-auth`，建 `/var/lib/aivtuber-auth`，权限给该账号。
3. 把 `docs/auth/deploy/aivtuber-auth.service` 放到 `/etc/systemd/system/`，`systemctl enable --now aivtuber-auth`。
4. DNS 加子域名的 A 记录，按 `docs/auth/deploy/nginx-auth.conf` 的注释申请证书并加 nginx 站点。
5. 安装 `docs/auth/deploy/backup-auth-db.sh` 并加到 cron（`/etc/cron.d/aivtuber-auth-backup`：`0 5 * * * root /usr/local/bin/backup-auth-db.sh`）。需要 `sqlite3` 命令行工具。

## 常见问题

- 服务起不来：`sudo journalctl -u aivtuber-auth -n 50`。常见原因是 `/opt/dotnet` 缺少 ASP.NET Core 运行时，或目录权限不对。
- 服务日志只记账号 ID、状态、profile 和版本，不记密码、token 和请求体。
- 本地电脑如果开了会劫持 DNS 的代理（Clash 的 fake-ip 模式），`curl` 解析出的会是 `198.18.x.x`，可以加 `--resolve auth.eatconfusion.online:443:<服务器IP>` 绕过。
