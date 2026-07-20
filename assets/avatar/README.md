# avatar 资产包

## 目录
- `sprites/`        12 张已抠底透明立绘（1254×1254，`gen_00`~`11`，画布未裁切以保证帧间对齐）
- `layered/`        v0.3 头/身分层（歪头用）
  - `body.png`      身体层：y<535 为 AI 头发填充，y≥535 为原图身体
  - `head/gen_XX.png` 12 个头层，切口 y=535（必须高于泡泡袖 y≈550）
- `stickers/`       表情包贴纸（`sweat_laugh` = gen_10 裁头）
- `dev_placeholder/` itch 免费女仆 sprite sheet（开发占位）
- `avatar.json`     状态机 + 口型 + 运动层 + 贴纸 + 分层配置
- `contact_sheet.png` 验收对照表

## avatar.json 关键字段（v0.3）

| 字段 | 含义 |
|------|------|
| `layers.enabled` | `true` 时启用头/身分层；资产缺失时渲染器回退单图层 |
| `layers.body` / `layers.head_dir` | 身体图与头层目录（头文件名复用 `states.*.file` 基名） |
| `layers.neck_pivot` | 歪头旋转支点（画布坐标，默认 627,500） |
| `motion_layer.tilt` | 头层弹簧：`max_deg` / `stiffness` / `damping` |
| `motion_layer.listening.tilt_deg` | 倾听姿态目标倾角 |
| `motion_layer.breath.scale_amp` | 呼吸纵向缩放幅度（纯 ScaleY，脚底锚定）；`amp_px` 已废弃 |
| `motion_layer.bounce.gain` | RMS→弹跳倍率（默认 4.0） |

改 `avatar.json` 后无需重启：`AvatarConfigWatcher` 防抖热重载运动参数；头层 PNG 的 mtime 变化时才会重建贴图。

## 待办
1. 对照 contact_sheet 核对 `_verify:true` 的状态名
2. 无语表情可走贴纸通道，梗图丢进 `stickers/` 并在 json 注册
3. 主立绘用平滑缩放；`dev_placeholder` 才需要 NearestNeighbor
