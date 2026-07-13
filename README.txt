AIVTuber - AI虚拟主播后端程序
====================================

快速开始
--------
1. 双击 AIVTuber.exe 启动程序
2. 首次启动会进入配置向导，按提示填写：
   - LLM API 地址和密钥（如 DeepSeek）
   - ASR API 密钥
   - TTS API 密钥和 Voice ID（如 fish-audio）
   - VTube Studio 连接地址
   - 麦克风设备选择
   - B站弹幕配置（可选）
   - OBS 字幕配置（可选）
3. 配置完成后重启程序即可使用

本地 ASR Sidecar
----------------
本地 ASR 使用发布包内的托管 Python 运行时，不读取系统 PATH。发布布局为：
- sidecar/python/python.exe
- sidecar/asr_server.py
- sidecar/requirements.lock
- sidecar/asr-sidecar.manifest.json

发布前运行：
  powershell -File scripts/Test-SidecarPackage.ps1 -PackageRoot . -RequireRuntime

若运行时未随包提供，验证固定返回 ASR-SIDECAR-001；不得将该包描述为自包含本地 ASR 版本。
无 CUDA 的机器使用 device=auto，由 CPU 兼容的已暂存 wheels 执行；模型或设备初始化失败固定归类为
ASR-SIDECAR-005，/health 状态为 failed，不会被误判为 ready。

配置文件
--------
启动后在程序同目录生成 config.json，可手动编辑。
参考 config.json.template 了解所有配置项。

VTube Studio 设置
------------------
1. 打开 VTube Studio
2. 菜单 → 插件 → 允许插件连接
3. 程序首次连接时需在 VTS 中点击"允许"
4. 在 config.json 的 vts.emotion_map 中配置表情热键映射

OBS 字幕设置
------------
1. OBS → 工具 → WebSocket 服务器设置 → 启用 → 设置密码
2. 场景中添加两个"文本 (GDI+)"源，分别命名为 AssistantText 和 UserText
3. 在 config.json 中设置 obs.enable = true 并填入密码

B站弹幕设置
-----------
1. 浏览器登录B站 → F12 → Application → Cookies
2. 复制 SESSDATA、bili_jct、buvid3 的值
3. 需要安装 Python 及 bilibili-api-python 包:
   pip install bilibili-api-python httpx
4. 在 config.json 中设置 bilibili.enable = true 并填入房间号和 Cookie

向量记忆（可选）
--------------
如需向量语义检索功能，下载 bge-small-zh-v1.5 到 models/bge-small-zh/ 目录:
- 下载地址: https://huggingface.co/BAAI/bge-small-zh-v1.5
- 需要文件: model.onnx 和 vocab.txt（两个都必需）
- vocab.txt 用于 WordPiece 分词，缺失则无法加载向量引擎
- 不配置(或缺文件)时使用字符串相似度兜底

依赖
----
- .NET 10 运行时（自包含发布时无需额外安装）
- VTube Studio（嘴型和表情控制）
- OBS Studio + WebSocket 插件（字幕显示，可选）
- 本地 ASR 不依赖系统 Python；需要发布包内 sidecar/python/python.exe
- Python 3.8+（仅 B站弹幕桥接，可选）

文件说明
--------
AIVTuber.exe      - 主程序
config.json       - 用户配置（启动后自动生成）
config.json.template - 配置模板
danmaku_bridge.py - B站弹幕桥接脚本
sidecar/          - 本地 ASR 托管运行时、服务脚本和完整性 manifest
models/bge-small-zh/ - 向量模型目录（可选）
memory.db         - 记忆数据库（自动生成）

技术支持
--------
详见 AIVTuber_Implementation_Plan.md
