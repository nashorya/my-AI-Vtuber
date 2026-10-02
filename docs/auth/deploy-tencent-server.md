# 把鉴权服务部署到一台已有的 Linux 服务器

适用：腾讯云上已有一台在用的 Linux 服务器（例如小程序后端那台）。鉴权服务很轻，几十个主播每分钟一次心跳，和原有服务放在一起不会有明显压力。数据库是一个 SQLite 文件，需要自己备份。以后想改用云函数，数据搬走即可。

## 0. 先确认

在服务器上执行：

```bash
uname -m
```

- 输出 `x86_64`：用 `linux-x64` 构建（下面的默认情况）。
- 输出 `aarch64`：把构建命令里的 `linux-x64` 换成 `linux-arm64`，其余不变。

同时确认服务器上已有的 nginx（或其他网站服务）占用了 443 端口。下面的步骤按"已有 nginx"来写。

## 1. 构建（在你自己的电脑上）

```bash
dotnet publish AIVTuber.AuthServer -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/auth-server-linux-x64
```

输出目录里需要上传的是两个文件：`AIVTuber.AuthServer`（约 100 MB，已带 .NET 运行时，服务器上不用再装）和 `libe_sqlite3.so`（必须和它放在同一目录）。

## 2. 上传并建服务账号

```bash
# 在你的电脑上
scp artifacts/auth-server-linux-x64/AIVTuber.AuthServer artifacts/auth-server-linux-x64/libe_sqlite3.so <用户>@<服务器>:/tmp/
```

```bash
# 在服务器上
sudo useradd --system --home /opt/aivtuber-auth --shell /usr/sbin/nologin aivtuber-auth
sudo mkdir -p /opt/aivtuber-auth /var/lib/aivtuber-auth
sudo mv /tmp/AIVTuber.AuthServer /tmp/libe_sqlite3.so /opt/aivtuber-auth/
sudo chmod +x /opt/aivtuber-auth/AIVTuber.AuthServer
sudo chown -R aivtuber-auth: /opt/aivtuber-auth /var/lib/aivtuber-auth
```

## 3. 开机自启

把 `docs/auth/deploy/aivtuber-auth.service` 放到服务器的 `/etc/systemd/system/`，然后：

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now aivtuber-auth
sudo systemctl status aivtuber-auth     # 应显示 active (running)
```

服务只监听本机 5080 端口，不对外。

## 4. 域名和 HTTPS

1. 在域名解析里给鉴权服务加一条子域名记录（例如 `auth.你的域名`），指向这台服务器的公网 IP。
2. 在腾讯云「SSL 证书」里为这个子域名申请免费证书，下载 nginx 版本，放到 `/etc/nginx/ssl/`。
3. 参考 `docs/auth/deploy/nginx-auth.conf` 加一个 `server` 块（改域名和证书路径），然后：

```bash
sudo nginx -t && sudo systemctl reload nginx
```

4. 在腾讯云控制台的安全组里确认 443 端口已放行。

## 5. 验证（在你自己的电脑上）

```bash
curl -s -X POST https://auth.你的域名/v1/auth/login -H 'content-type: application/json' \
  -d '{"username":"x","password":"y","profile_id":"p","app_version":"0","credential_revision":1}'
```

应返回 `{"status":"invalid_credentials",...}`。返回这个就说明服务、HTTPS、反向代理全通了；连不上或返回 HTML 页面，说明还没通。

## 6. 建账号和设额度

管理命令在服务器上运行，使用服务的同一个程序和数据库：

```bash
cd /opt/aivtuber-auth
echo '主播的密码' | sudo -u aivtuber-auth ./AIVTuber.AuthServer admin --db /var/lib/aivtuber-auth/auth.db \
  create --username alice --profile streamer-017 --days 30 --password-stdin
sudo -u aivtuber-auth ./AIVTuber.AuthServer admin --db /var/lib/aivtuber-auth/auth.db set-default-daily --minutes 60
sudo -u aivtuber-auth ./AIVTuber.AuthServer admin --db /var/lib/aivtuber-auth/auth.db list
```

其余命令（续期、停用、加时、查用量等）见 `docs/auth/README.md`。

## 7. 把地址写进主播档案

主播专属档案里的 `auth_server` 填 `https://auth.你的域名/`，然后按 `docs/auth/README.md` 的「生成主播专属包」打包。

## 8. 备份

```bash
sudo apt install sqlite3        # 或 yum install sqlite
sudo cp docs/auth/deploy/backup-auth-db.sh /usr/local/bin/backup-auth-db.sh
sudo chmod +x /usr/local/bin/backup-auth-db.sh
echo '0 5 * * * root /usr/local/bin/backup-auth-db.sh' | sudo tee /etc/cron.d/aivtuber-auth-backup
```

备份保存在 `/var/backups/aivtuber-auth/`，保留 14 天。建议再开腾讯云服务器的自动快照，或定期把备份目录拷到别处。

## 升级

重新构建，上传两个文件，覆盖 `/opt/aivtuber-auth/` 里的同名文件，然后 `sudo systemctl restart aivtuber-auth`。数据库结构的升级是启动时自动做的。升级前先手动跑一次备份脚本。

## 常见问题

- `systemctl status` 显示失败：用 `sudo journalctl -u aivtuber-auth -n 50` 看日志。常见原因是 `libe_sqlite3.so` 没和程序放在一起，或目录权限不对。
- 服务日志只记账号 ID、状态、profile 和版本，不记密码、token 和请求体。
