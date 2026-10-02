#!/usr/bin/env bash
# 每天备份一次鉴权数据库，保留 14 天。需要 sqlite3 命令行工具。
set -euo pipefail
dir=/var/backups/aivtuber-auth
mkdir -p "$dir"
sqlite3 /var/lib/aivtuber-auth/auth.db ".backup '$dir/auth-$(date +%F).db'"
find "$dir" -name 'auth-*.db' -mtime +14 -delete
