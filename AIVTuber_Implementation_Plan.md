# AI VTuber 后端实现计划

> 目标：一个可分发给普通主播的 AI VTuber 后台程序，主播配置自己的 API key，接入 VTube Studio 控制 Live2D，云端 ASR+LLM+TTS 全流式，首次出声 ≤ 1.5s。
> 语言：C# (.NET 8)
> 预计总量：~2500 行

---

## 项目结构

```
AIVTuber/
├── AIVTuber.sln
├── Core/
│   ├── Audio/
│   │   ├── MicrophoneCapture.cs       # 麦克风采集
│   │   ├── LoopbackCapture.cs         # WASAPI loopback（抓系统声音）
│   │   ├── VadDetector.cs             # VAD 静音检测
│   │   └── AudioPlayer.cs             # TTS 音频播放 + RMS 计算
│   ├── Pipeline/
│   │   ├── AsrClient.cs               # 流式 ASR
│   │   ├── LlmClient.cs               # 流式 LLM
│   │   └── TtsClient.cs               # 流式 TTS
│   ├── Memory/
│   │   ├── MemoryDb.cs                # SQLite 主库
│   │   ├── EmbeddingEngine.cs         # 本地 bge-small ONNX
│   │   ├── ViewerRepository.cs        # 观众档案 CRUD
│   │   ├── FactRepository.cs          # 事实记忆 CRUD
│   │   └── MemoryExtractor.cs         # 调 LLM 提取事实的异步任务
│   ├── Vts/
│   │   └── VtsClient.cs               # VTube Studio WebSocket Plugin API
│   ├── LiveStream/
│   │   ├── BilibiliDanmakuClient.cs   # B站弹幕 WebSocket
│   │   └── DanmakuSelector.cs         # 弹幕选择策略
│   ├── Obs/
│   │   └── ObsClient.cs               # OBS WebSocket 字幕控制
│   ├── Bot/
│   │   ├── ConversationManager.cs     # 工作记忆（上下文窗口 + 滚动摘要）
│   │   └── BotOrchestrator.cs         # 主流水线协调
│   └── Config/
│       ├── AppConfig.cs               # 配置模型（Pydantic 同等）
│       └── ConfigManager.cs           # 读写 config.json
├── App/
│   ├── Program.cs                     # 入口，首次启动引导
│   └── TrayIcon.cs                    # 系统托盘（可选）
├── Tests/
│   └── ...
└── config.json                        # 用户配置文件（不打进包）
```

---

## 阶段划分

### Phase 1：音频基础（无 AI，先跑通声音）
**目标**：能采集麦克风/系统声音，VAD 检测说话，播放音频文件。
**完成标志**：对着麦克风说话 → 控制台打印"speech detected"；播放一个 wav 文件同时输出实时 RMS 值。

#### Task 1.1 — MicrophoneCapture
- 使用 `NAudio.Wave.WaveInEvent`
- 支持指定 `deviceIndex`（配置文件读取）
- 输出 `IObservable<byte[]>` 或事件回调，帧大小 30ms，16kHz 单声道
- 提供 `Start()` / `Stop()` / `ListDevices()` 静态方法

#### Task 1.2 — LoopbackCapture
- 使用 `NAudio.CoreAudioApi.WasapiLoopbackCapture`
- 自动重采样到 16kHz 单声道（使用 `NAudio.Wave.MediaFoundationResampler`）
- 接口与 MicrophoneCapture 保持一致，可互换

#### Task 1.3 — VadDetector
- 封装 `WebRtcVadSharp`（NuGet：WebRtcVadSharp）
- 输入：连续音频帧流；输出：`SpeechSegment`（byte[] + 时间戳）
- 参数：aggressiveness(0-3)、pre-speech padding(200ms)、post-speech silence(500ms)

#### Task 1.4 — AudioPlayer
- 使用 `NAudio.Wave.WaveOutEvent`
- 播放 wav/mp3 byte[]（流式：支持边追加边播放）
- 每 30ms 计算一次 RMS，通过事件暴露出去（供 VTS 嘴型用）
- 提供 `PlayAsync(Stream audioStream)` 支持 streaming TTS

---

### Phase 2：云端 Pipeline（全流式）
**目标**：说一句话 → ASR → LLM → TTS → 播放，首次出声 ≤ 1.5s。
**完成标志**：对着麦克风说「你好」→ 1.5s 内听到 AI 回复。

#### Task 2.1 — AsrClient
- 接口：`Task<string> RecognizeAsync(byte[] pcm16k)`
- 默认实现：阿里云 ASR（或科大讯飞）HTTP API
- 配置项：`provider`、`api_key`、`app_id`

#### Task 2.2 — LlmClient
- 接口：`IAsyncEnumerable<string> StreamAsync(List<Message> history, string userInput)`
- 返回 token 流，每收到一个句子边界（句号/问号/感叹号/换行）触发 `OnSentenceReady` 事件
- 支持 OpenAI 格式（兼容 DeepSeek/Kimi/豆包等）
- 配置项：`base_url`、`api_key`、`model`、`system_prompt`、`max_history_tokens`

#### Task 2.3 — TtsClient
- 接口：`IAsyncEnumerable<byte[]> StreamAsync(string text, string voiceId)`
- 每收到音频块立即推给 AudioPlayer（不等全部生成完）
- 支持 fish-audio 或火山引擎 TTS（可配置）
- 配置项：`provider`、`api_key`、`voice_id`

#### Task 2.4 — BotOrchestrator（基础版，无记忆）

串联上述组件：
```
VAD.OnSpeech
  → AsrClient.RecognizeAsync()
  → ConversationManager.BuildMessages()
  → LlmClient.StreamAsync()
      → 每句话: TtsClient.StreamAsync() → AudioPlayer.PlayAsync()
```

**⚠️ 最容易出错的地方：流式管线的并发模型**

错误做法（常见）：等 LLM 全部输出完再送 TTS，或者每个 token 都触发一次 TTS——前者慢，后者会生成几百个"的""了""吗"的音频片段。

正确做法：LLM token 流 → 在内存中拼句子 → 句子完整了才送 TTS，LLM 继续生成下一句。用两个 Channel 分离"LLM 生产"和"TTS 消费"：

```csharp
// BotOrchestrator.cs 核心逻辑伪代码

async Task RunAsync(string userInput)
{
    // Channel 容量设 3：最多预合成 3 句，防止 LLM 跑太快堆满内存
    var sentenceChannel = Channel.CreateBounded<string>(3);

    // 任务 A：LLM 流式输出 → 按句子切分 → 写入 Channel
    var producerTask = Task.Run(async () =>
    {
        var buffer = new StringBuilder();
        await foreach (var token in _llm.StreamAsync(messages, userInput))
        {
            buffer.Append(token);

            // 句子边界检测：中文句末标点 + 英文句末标点
            // ⚠️ 注意：不能只判断最后一个字符，token 可能包含多个字符
            if (ContainsSentenceBoundary(buffer.ToString(), out var sentence, out var remainder))
            {
                await sentenceChannel.Writer.WriteAsync(sentence.Trim());
                buffer.Clear();
                buffer.Append(remainder); // 边界后的内容留给下一句
            }
        }
        // 循环结束后 buffer 里可能还有剩余（最后一句没有标点）
        if (buffer.Length > 0)
            await sentenceChannel.Writer.WriteAsync(buffer.ToString().Trim());

        sentenceChannel.Writer.Complete();
    });

    // 任务 B：从 Channel 取句子 → TTS → 播放（串行，保证音频顺序）
    var consumerTask = Task.Run(async () =>
    {
        await foreach (var sentence in sentenceChannel.Reader.ReadAllAsync())
        {
            if (string.IsNullOrWhiteSpace(sentence)) continue;

            // TTS 流式：边合成边播放
            var audioStream = _tts.StreamAsync(sentence, _config.Tts.VoiceId);
            await _player.PlayAsync(audioStream); // 阻塞直到这句播完
        }
    });

    await Task.WhenAll(producerTask, consumerTask);
}

// 句子边界检测
bool ContainsSentenceBoundary(string text, out string sentence, out string remainder)
{
    // 中文：。！？；  英文：. ! ?  换行：\n
    var pattern = @"[。！？；.!?\n]";
    var match = Regex.Match(text, pattern);
    if (match.Success)
    {
        sentence  = text[..(match.Index + 1)];
        remainder = text[(match.Index + 1)..];
        return true;
    }
    sentence = remainder = string.Empty;
    return false;
}
```

**⚠️ 打断处理**：用户说话时正在播放 TTS，必须能打断。

```csharp
// 收到新的 VAD 事件时：
_player.Stop();                    // 立即停止当前播放
_currentCts?.Cancel();             // 取消当前 LLM/TTS 任务
_currentCts = new CancellationTokenSource();
// 开启新一轮对话
await RunAsync(newInput, _currentCts.Token);
```

`CancellationToken` 要传入 LlmClient 和 TtsClient 的每一个 await，否则取消信号传不进去。

**⚠️ Channel 背压**：容量设为 3，当 TTS 来不及消费时，LLM 的 `WriteAsync` 会自动等待，不会无限堆积。不要用 `Unbounded` Channel。

---

### Phase 3：VTube Studio 接入
**目标**：TTS 播放时嘴巴动，LLM 输出情绪时切换表情。
**完成标志**：AI 说话时 VTS 模型嘴型同步；收到"(happy)"标签时触发对应热键。

#### Task 3.1 — VtsClient
- 使用 `System.Net.WebSockets.ClientWebSocket` 连接 `ws://localhost:8001`
- 实现 VTS Plugin API 握手认证流程（发送 plugin 名称/开发者 → 等待用户在 VTS 点允许）
- 封装三个接口：
  - `InjectParameterAsync(string paramId, float value)` — 嘴型
  - `TriggerHotkeyAsync(string hotkeyId)` — 表情切换
  - `GetHotkeyListAsync()` — 启动时获取可用热键列表写入配置

#### Task 3.2 — 嘴型同步
- AudioPlayer 的 RMS 事件 → `VtsClient.InjectParameterAsync("ParamMouthOpenY", rms * scale)`
- `scale` 可配置（不同模型嘴型幅度不同）
- RMS 推送频率 30fps（33ms timer）

#### Task 3.3 — 情绪标签解析
- LLM system prompt 中约定输出格式：正文 + `[emotion:happy]` 这样的标签
- `LlmClient` 解析标签，触发 `OnEmotionDetected(string emotion)` 事件
- `BotOrchestrator` 订阅 → 查配置表（emotion → hotkeyId）→ `VtsClient.TriggerHotkeyAsync()`

---

### Phase 4：记忆系统
**目标**：认识老观众，记住稳定事实，下播后不失忆。
**完成标志**：用同一个用户 ID 发两次弹幕，第二次 AI 能接上第一次说的内容。

#### Task 4.1 — 数据库 Schema
使用 `Microsoft.Data.Sqlite`，启动时自动建表：

```sql
CREATE TABLE IF NOT EXISTS viewers (
    uid TEXT NOT NULL,
    platform TEXT NOT NULL,
    nickname TEXT,
    first_seen TEXT,
    last_seen TEXT,
    interaction_count INTEGER DEFAULT 0,
    notes TEXT,
    PRIMARY KEY (uid, platform)
);

CREATE TABLE IF NOT EXISTS facts (
    id TEXT PRIMARY KEY,
    subject_uid TEXT,          -- NULL 表示关于主播自己
    content TEXT NOT NULL,
    importance INTEGER DEFAULT 3,
    expires TEXT DEFAULT 'stable',  -- stable | temporary
    embedding BLOB,
    created_at TEXT,
    last_accessed TEXT,
    access_count INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS sessions (
    id TEXT PRIMARY KEY,
    started_at TEXT,
    ended_at TEXT,
    summary TEXT
);

CREATE TABLE IF NOT EXISTS conversations (
    id TEXT PRIMARY KEY,
    session_id TEXT,
    role TEXT,
    content TEXT,
    speaker_uid TEXT,
    timestamp TEXT
);
```

#### Task 4.2 — EmbeddingEngine
- 加载 `bge-small-zh-v1.5` ONNX 模型（随程序分发，约 90MB）
- 使用 `Microsoft.ML.OnnxRuntime`
- 接口：`float[] Encode(string text)`
- 余弦相似度计算写成静态方法
- 向量存为 BLOB（float32 数组序列化）

#### Task 4.3 — FactRepository

**⚠️ 最容易出错的地方：InsertOrMerge 的判断逻辑**

`relation_to_old` 字段由 LLM 填写，但不能无条件信任——LLM 可能填错 ID 或漏报冲突。正确做法是 LLM 的判断作为提示，自己再做一次向量兜底校验：

```csharp
async Task InsertOrMergeAsync(Fact newFact)
{
    // 1. 先用向量搜同一 subject 下的相似事实
    var candidates = await SearchAsync(
        query:      newFact.Content,
        subjectUid: newFact.SubjectUid,
        topK:       5
    );

    Fact? duplicate = null;
    Fact? conflict  = null;

    foreach (var c in candidates)
    {
        var sim = CosineSimilarity(newFact.Embedding, c.Embedding);

        if (sim > 0.92f)
        {
            // 高度相似 → duplicate，不管 LLM 怎么说
            duplicate = c;
            break;
        }

        // LLM 标记了 conflict，且向量相似度合理（> 0.6，说明在讲同一件事）
        if (newFact.RelationToOld == $"conflict:{c.Id}" && sim > 0.6f)
        {
            conflict = c;
        }
    }

    if (duplicate != null)
    {
        // 重复：不插入新记录，只给旧记录加权
        await UpdateWeightAsync(duplicate.Id, delta: +1);
        return;
    }

    if (conflict != null)
    {
        // 冲突：用新事实替换旧事实（保留旧 ID，只更新内容和时间）
        await UpdateContentAsync(conflict.Id, newFact.Content, newFact.Importance);
        return;
    }

    // 全新事实：直接插入
    await InsertAsync(newFact);
}
```

**⚠️ embedding 存取**：SQLite 的 BLOB 存 float32 数组，序列化/反序列化要用固定字节序：

```csharp
// 序列化
byte[] EmbeddingToBytes(float[] embedding)
{
    var bytes = new byte[embedding.Length * 4];
    Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
    return bytes;
}

// 反序列化
float[] BytesToEmbedding(byte[] bytes)
{
    var floats = new float[bytes.Length / 4];
    Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
    return floats;
}
```

**⚠️ 检索打分公式**（综合相似度 + 时间衰减 + 访问频次）：

```csharp
float Score(float similarity, Fact fact)
{
    var daysSince = (DateTime.UtcNow - fact.LastAccessed).TotalDays;
    var decay     = (float)Math.Exp(-0.01 * daysSince);   // 半衰期约 70 天
    var freq      = (float)Math.Log(fact.AccessCount + 1);
    return similarity * 0.6f + decay * 0.2f + freq * 0.2f;
}
// 检索时按 Score 降序取 topK，不要只按相似度排
```

#### Task 4.4 — MemoryExtractor（异步后台任务）
- 每 N 轮对话（可配置，默认 5 轮）触发一次
- 取最近 N 轮对话 + 相关旧事实喂给 LLM
- Prompt（见下方）约束 JSON 输出
- 解析结果写入 FactRepository

**MemoryExtractor Prompt**：
```
你是直播间AI的记忆整理员。从对话片段中提取值得长期记住的事实。

【值得记住】关于具体人的稳定信息：身份、喜好、经历、关系、约定、纠正过的错误
【不要记住】寒暄客套、玩梗刷屏、一次性话题、AI自己说的话、礼物感谢

规则：
1. 每条事实独立成立，不依赖上下文
2. 第三人称，主语写明 UID，不写"他/她"
3. 时效性信息标注日期
4. 没有值得记的返回空数组，宁缺毋滥

对话片段：{conversation}
相关旧记忆：{existing_facts}

输出JSON：
{"facts":[{"subject_uid":"...","content":"...","importance":1-5,"expires":"stable|temporary","relation_to_old":"new|duplicate:<id>|conflict:<id>"}]}
```

#### Task 4.5 — ConversationManager（升级版）
- 启动时注入记忆块到 system prompt：
  - 观众档案（主键查询，精确）
  - top-5 相关事实（embedding 检索）
- 上下文超长时：最早几轮对话压缩成摘要（调 LLM），摘要保留，原文丢弃

---

### Phase 5：弹幕接入 + OBS 字幕
**目标**：读取直播间弹幕，AI 自动选择回复；OBS 同步显示字幕。
**完成标志**：B 站直播间发弹幕 → AI 回复 → OBS 字幕同步显示。

#### Task 5.1 — BilibiliDanmakuClient

**架构说明**：B 站弹幕协议维护成本高（时常变动），推荐「Python 弹幕进程 + C# 主进程」双进程方案：
- 一个独立的 Python 脚本用 `bilibili-api-python` 连接 B 站，收到弹幕后通过本地 HTTP POST 推给 C# 主程序
- C# 主程序暴露一个本地 `/danmaku` 端点接收
- 好处：B 站改协议只动 Python 那边，C# 完全不受影响；主播也不需要在 C# 里填 Cookie

Python 弹幕转发脚本（随程序一起分发，约 50 行）：
```python
# danmaku_bridge.py
# 依赖：pip install bilibili-api-python httpx
# 所有配置从环境变量读取，由 C# 主程序启动时注入，不需要手动修改本文件
import os
import asyncio
import httpx
from bilibili_api import Credential
from bilibili_api.live import LiveDanmaku

ROOM_ID  = int(os.environ["ROOM_ID"])
SESSDATA = os.environ["SESSDATA"]
BILI_JCT = os.environ["BILI_JCT"]
BUVID3   = os.environ["BUVID3"]
PUSH_URL = os.environ.get("PUSH_URL", "http://localhost:19876/danmaku")

async def main():
    credential = Credential(sessdata=SESSDATA, bili_jct=BILI_JCT, buvid3=BUVID3)
    monitor = LiveDanmaku(ROOM_ID, credential=credential)

    @monitor.on("DANMU_MSG")
    async def on_danmaku(event):
        uid      = str(event["data"]["info"][2][0])
        username = event["data"]["info"][2][1]
        content  = event["data"]["info"][1]
        try:
            async with httpx.AsyncClient(timeout=3) as client:
                await client.post(PUSH_URL, json={
                    "uid": uid, "username": username, "content": content
                })
        except Exception:
            pass  # 主程序未就绪时忽略，不崩溃

    await monitor.connect()

asyncio.run(main())
```

C# 侧 `BilibiliDanmakuClient`：
- 用 `Microsoft.AspNetCore` 起一个最小 HTTP 服务监听 `localhost:19876/danmaku`
- 收到 POST → 解析 JSON → 触发 `OnDanmaku(Danmaku)` 事件
- 启动时自动拉起 Python 子进程（`Process.Start("python danmaku_bridge.py")`），程序退出时一并关闭

**⚠️ 最容易出错的地方：Python 子进程的生命周期管理**

直接 `Process.Start` 之后不管，会出现三个问题：主程序崩溃时 Python 进程变孤儿继续跑；Python 进程崩了没人重启；端口冲突（上次没关干净）。

```csharp
// BilibiliDanmakuClient.cs

private Process? _pythonProcess;
private CancellationTokenSource _cts = new();

public async Task StartAsync()
{
    // 启动前先检查端口是否被占用，被占用说明上次没关干净，先 kill
    KillProcessOnPort(19876);

    _pythonProcess = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName  = "python",
            Arguments = "danmaku_bridge.py",
            // 把 config 通过环境变量传给 Python，不要让主播手动改脚本
            Environment =
            {
                ["ROOM_ID"]  = _config.RoomId.ToString(),
                ["SESSDATA"] = _config.Sessdata,
                ["BILI_JCT"] = _config.BiliJct,
                ["BUVID3"]   = _config.Buvid3,
                ["PUSH_URL"] = "http://localhost:19876/danmaku"
            },
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        },
        EnableRaisingEvents = true
    };

    // Python 进程崩了自动重启（限 3 次，防止配置错误导致无限重启）
    // ⚠️ 注意：不能在 Exited 回调里 .Start() 同一个 Process 对象
    //    Process 退出后不可复用，必须 new 一个新的
    int restartCount = 0;
    _pythonProcess.Exited += async (_, _) =>
    {
        if (_cts.IsCancellationRequested) return; // 主动关闭，不重启
        if (++restartCount > 3)
        {
            _logger.LogError("弹幕进程连续崩溃 3 次，停止重启");
            return;
        }
        _logger.LogWarning($"弹幕进程崩溃，5 秒后重启（第 {restartCount} 次）");
        await Task.Delay(5000);
        // 重新创建 Process 对象再启动
        _pythonProcess = BuildPythonProcess();
        _pythonProcess.Start();
    };

    _pythonProcess.Start();

    // BuildPythonProcess() 把 ProcessStartInfo 构建逻辑抽成独立方法，
    // 供初次启动和重启时复用，避免重复代码。

    // 主程序退出时确保 Python 进程被关掉
    // ⚠️ ProcessExit 回调是同步的，.Wait() 在此处不可避免，但只在退出时调用，不会造成死锁
    AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAsync().GetAwaiter().GetResult();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; StopAsync().GetAwaiter().GetResult(); };
}

public Task StopAsync()
{
    _cts.Cancel();
    if (_pythonProcess is { HasExited: false })
    {
        _pythonProcess.Kill(entireProcessTree: true); // 连同子进程一起杀
        _pythonProcess.WaitForExit(3000);
    }
    return Task.CompletedTask;
}

// 杀占用指定端口的进程（Windows）
void KillProcessOnPort(int port)
{
    var result = Process.Start(new ProcessStartInfo
    {
        FileName  = "netstat",
        Arguments = $"-ano",
        RedirectStandardOutput = true,
        UseShellExecute = false
    });
    // 解析 netstat 输出找 PID，再 taskkill /F /PID <pid>
    // 省略具体实现，agent 自行补全
}
```

**⚠️ Python 环境问题**：分发时不能假设主播装了 Python。解决方案：

选项 A（推荐）：在发布目录里附带一个 Python embeddable 包（`python-3.11-embed-amd64.zip` 解压约 15MB），`ProcessStartInfo.FileName` 改为相对路径 `python_embed\\python.exe`。

选项 B：把 danmaku_bridge.py 用 PyInstaller 打成一个独立 exe（`danmaku_bridge.exe`），主程序直接启动这个 exe，完全不依赖 Python 环境。

配置项新增：
```json
"bilibili": {
  "enable": false,
  "room_id": 0,
  "sessdata": "",
  "bili_jct": "",
  "buvid3": ""
}
```

> Cookie 获取方式：浏览器登录 B 站 → F12 → Application → Cookies → 找 SESSDATA / bili_jct / buvid3

#### Task 5.2 — 弹幕选择策略（DanmakuSelector）
- 维护一个待回复 `Queue<Danmaku>`，每 8 秒从中选一条（避免高并发连续回复）
- 选择优先级：老观众（interaction_count > 5）> 含问号 > 随机
- 正在说话时暂停选取，说完再继续
- 同时更新 ViewerRepository（last_seen、interaction_count）

#### Task 5.3 — ObsClient

OBS 字幕使用标准 OBS WebSocket 协议（obswebsocket v5），认证握手如下：
1. 连接 `ws://localhost:4455`
2. 收到 op=0（Hello），取出 `challenge` 和 `salt`
3. 计算 `auth = Base64(SHA256(Base64(SHA256(password + salt)) + challenge))`
4. 发送 op=1（Identify）带上 auth
5. 收到 op=2（Identified）表示认证成功

字幕更新发 op=6（Request）：
```json
{
  "op": 6,
  "d": {
    "requestType": "SetInputSettings",
    "requestId": "<uuid>",
    "requestData": {
      "inputName": "AssistantText",
      "inputSettings": { "text": "AI说的话" }
    }
  }
}
```

`ObsClient` 实现：
- 使用 `Websocket.Client` 保持长连接，自动重连
- 接口：`SetSubtitleAsync(string text, string component = "AssistantText")`
- 流式打字机效果：按字符拆分，`duration / text.Length` 间隔定时推送
- 断连时静默降级（字幕功能不影响主流程）

配置项新增：
```json
"obs": {
  "enable": false,
  "host": "localhost",
  "port": 4455,
  "password": "",
  "assistant_text_component": "AssistantText",
  "user_text_component": "UserText"
}
```

OBS 侧准备（告知主播）：
1. OBS → 工具 → WebSocket 服务器设置 → 启用 → 设密码
2. 场景中添加两个「文本 GDI+」组件，分别命名 `AssistantText` 和 `UserText`

---

### Phase 6：配置与分发
**目标**：主播能自助完成配置，exe 双击启动。

#### Task 6.1 — AppConfig
```json
{
  "audio": {
    "input_device_index": 0,
    "use_loopback": false,
    "loopback_device_name": ""
  },
  "asr": {
    "provider": "aliyun",
    "api_key": "",
    "app_id": ""
  },
  "llm": {
    "base_url": "https://api.deepseek.com",
    "api_key": "",
    "model": "deepseek-chat",
    "system_prompt": "你是一个VTuber..."
  },
  "tts": {
    "provider": "fish-audio",
    "api_key": "",
    "voice_id": ""
  },
  "vts": {
    "host": "localhost",
    "port": 8001,
    "mouth_scale": 1.5,
    "emotion_map": { "happy": "hotkey_xxx", "sad": "hotkey_yyy" }
  },
  "obs": {
    "enable": false,
    "host": "localhost",
    "port": 4455,
    "password": "",
    "assistant_text_component": "AssistantText",
    "user_text_component": "UserText"
  },
  "memory": {
    "extract_every_n_turns": 5
  },
  "bilibili": {
    "enable": false,
    "room_id": 0,
    "sessdata": "",
    "bili_jct": "",
    "buvid3": ""
  }
}
```

#### Task 6.2 — 首次启动引导
- 检测 config.json 不存在 → 控制台逐步引导填写（或生成模板让用户编辑）
- 检测 VTS 未连接 → 提示用户在 VTS 点允许
- 检测麦克风列表 → 打印序号让用户选择
- 全部通过后写入 config.json，重启生效

#### Task 6.3 — 打包
- `dotnet publish -r win-x64 --self-contained true -p:PublishSingleFile=true`
- 附带 bge-small ONNX 模型文件（同目录）
- 附带 `config.json.template`
- 最终交付物：一个目录（exe + 模型文件 + 配置模板 + README.txt）

---

## 依赖清单（NuGet）

| 包 | 用途 |
|---|---|
| NAudio | 音频采集/播放/WASAPI |
| WebRtcVadSharp | VAD |
| Microsoft.Data.Sqlite | SQLite |
| Microsoft.ML.OnnxRuntime | ONNX embedding |
| System.Text.Json | JSON 序列化 |
| Websocket.Client | VTS / OBS WebSocket |
| Microsoft.AspNetCore | 接收弹幕推送的本地 HTTP 端点 |

---

## 给 Agent 的执行指令

**按 Phase 顺序执行，每个 Task 完成后写一个对应的单元测试再继续。**

- Phase 1 完成标准：`dotnet test` 全绿，控制台能看到 VAD 触发日志
- Phase 2 完成标准：对话一轮，计时首次出声 ≤ 1.5s
- Phase 3 完成标准：VTS 模型嘴型随播放音频动
- Phase 4 完成标准：重启程序后能检索到上次存入的事实
- Phase 5 完成标准：B 站弹幕触发 AI 回复，OBS 字幕同步更新
- Phase 6 完成标准：`dotnet publish` 产物在全新 Windows 机器上双击可运行

**编码规范**：
- 所有 IO 操作使用 async/await，禁止 `.Result` 和 `.Wait()`
- 模块间通过接口解耦，便于替换 ASR/TTS 提供商
- 配置项硬编码一律提取到 AppConfig
- 每个类不超过 200 行，超了就拆

---

## 不在范围内（后续迭代）

- GUI（当前只有控制台 + 托盘图标）
- YouTube / Twitch 弹幕
- 本地 LLM / TTS 支持
- QQ 机器人
