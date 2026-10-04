# Windows 主播安装包

一个按用户安装的 Windows x64 EXE，默认安装到 `%LOCALAPPDATA%\Programs\AIVTuber`，创建桌面、开始菜单与系统卸载入口。缺少 WebView2 时运行微软签名的 Evergreen bootstrapper（需联网）；.NET 已由公开构建自带。升级与卸载保留应用运行后产生的配置、记忆及其他非随包文件；卸载删除私有档案。

使用 NSIS 3（macOS: `brew install nsis`；Windows: `choco install nsis`）。先从已通过质量门的 Actions 下载对应公开 `publish`，从微软官方下载 WebView2 bootstrapper。私有档案和成品必须放在代码仓库外，不能上传到 GitHub。

```sh
python3 installer/build_installer.py \
  --app-dir /private/path/publish \
  --profile /private/path/shared-001.json \
  --output /private/path/AIVTuber-Setup.exe \
  --webview-bootstrapper /private/path/MicrosoftEdgeWebview2Setup.exe \
  --version 0.36.0-rc.2 \
  --source-commit 9f4ebbf3ff8e3f0835d1ffcc5ac3aaaa7d9bd9f2
```

正式档案必须绑定 `shared-001`、生产鉴权域名，不填写账号，提供可用的 LLM/ASR/TTS Key 和默认音色。脚本复用既有 Packager 校验，编译后输出 EXE、SHA-256 和不含密钥的回执。不要将标记 TEST-ONLY 的 CI 测试包发给主播。

Windows CI 仅使用文档里的假 Key 构建测试安装器，验证静默安装、二进制哈希、快捷方式、卸载注册项、升级保留数据、卸载清除私有档案并保留用户数据。同时验证文件占用时拒绝安装/卸载、升级拒绝更换目录、旧版本已移除文件的清理，以及升级后的哈希和界面启动。正式 Key 仅在运营者本机封装。

安装器目前没有代码签名证书，Windows 可能显示“未知发布者”。正式分发前还需使用真实档案验收注册和音频服务。
