# 任务:形象渲染器二期(修订版)— 呼吸修正 + 整图姿态切换

> 承接一期(`AVATAR_RENDERER_PLAN.md`,已合入 PR #5)。
> **本文档取代旧版 phase2**:旧版 Batch B 的"头身分层 + 弹簧歪头"方案**已彻底作废**(头发撕裂、切口接缝无法解决),替换为整图姿态切换。
> 资产以 **avatar_pack_v0.6** 为准,解压覆盖 `assets/avatar/`。
>
> 分两批交付,A 完成即交付验收,不要等 B。

---

## Batch A:修正与打磨(无新资产,不受方案变更影响)

### A1. 呼吸动画重做 【最高优先级 — 当前实现观感错误】

**现状问题**:验收发现呼吸把上半身向上平移再向下,导致上半身像素覆盖下半身;更早一版是整图上下平移,观感为"整个人原地上下飘"。两版都错。

**根因**:呼吸被实现成了**平移**。平移必然产生遮挡或缝隙。真人呼吸时躯干是**形变**,没有任何部位离开原位覆盖其他部位。

**唯一正确实现**:
- 呼吸 = 对整张立绘的 `ScaleTransform`。`ScaleY` 在 `1.0` ↔ `1.0 + scale_amp` 之间正弦振荡;`ScaleX` 保持 1.0(或反向微调 `1.0 - scale_amp*0.3`,可选)。
- `CenterX`/`CenterY` **必须**取自 `avatar.json` 的 `meta.pivot`(脚底中心 627,1180),换算到控件坐标。
- **禁止** `TranslateTransform` 参与呼吸。**禁止**把立绘按矩形裁成上下半身分别变换。
- `motion_layer.breath.amp_px` 字段废弃(忽略或置 0)。

**参数**:`scale_amp` 默认 **0.008**,可热重载。判定:盯着看能察觉,余光只觉得"活着"。

**验收**:呼吸周期内脚底像素行**逐帧不动**;画面无遮挡、无缝隙、无重复像素。

### A2. deltaTime 重复回调 bug

`AvatarWindow.OnRendering` 中 `deltaMs <= 0` 被替换为 16.67ms。但 WPF `CompositionTarget.Rendering` 同一帧可能多次回调且 `RenderingTime` 相同,此时 deltaMs 为 0,当前写法会让状态机/运动引擎各多推进一整帧,表现为偶发加速抖动。

**修复**:记录上次 `RenderingTime`,与上次**相同则直接 return**(跳过整帧),不补时长。仅 deltaMs 为负时钳制。250ms 上限保留。

### A3. 弹跳增益外置

`MotionEngine` 中 `_smoothedRms * MaxPx * 4f` 常数 `4f` 写死,导致 RMS>0.25 弹跳即饱和,正常说话大部分时间顶满。

**修复**:提取为 `motion_layer.bounce.gain`(默认 4.0)可配置热重载;包络平滑由线性 `dt/attack` 改为帧率无关的指数 `1 - exp(-dt/τ)`。

### A4. 参数热重载 【调试效率关键】

`FileSystemWatcher` 监听 avatar.json(防抖 300ms,注意编辑器保存触发多次事件),重载后**仅热更新 `motion_layer`/`mouth_sync`/`stickers`/`poses` 的参数值**,不重建已预加载位图、不重置当前状态。解析失败保留旧配置 + 警告,禁止崩溃黑屏。参考已有 `TtsHotReloadTests` 模式,加单测。

---

## Batch B:整图姿态切换(替代作废的分层歪头)

> ⚠️ 设计前提:歪头/侧身**不做任何分层或旋转**。每个姿态是一张**完整独立立绘**,姿态变化 = 整图交叉淡化切换。这是本方案的核心,不要退回分层旋转。

### B0. 资产(avatar_pack_v0.6,已就绪)

```
assets/avatar/poses/
  front.png        正脸(= sprites/gen_00,挂完整表情系统)
  tilt_right.png   向右歪头
  tilt_left.png    向左歪头
  side_right.png   右侧身
  side_left.png    左侧身
```
- 五张画布尺寸一致(1254×1254),脚底已对齐到基线 y≈1188。
- `avatar.json` 的 `poses` 节定义全部映射、过渡、触发参数。
- `layers` 节已标记 `deprecated`,`layered/` 目录资产保留仅供参考——**渲染器不得使用 layers,不得做头身分层旋转**。

### B1. 姿态切换器

- 读 `poses.list`,预加载五张为 Freeze 后的 BitmapImage。
- 切换 = 交叉淡化(`poses.transition`,默认 fade 180ms),旧姿态淡出、新姿态淡入,复用一期已有的双 Image 淡化机制。禁止硬跳。
- 当前姿态叠加运动层(呼吸/漂移/摆动/弹跳)照常作用于整图——姿态图是静态立绘,运动层让它"活着"。

### B2. 姿态与表情系统的互斥(方向3)

- **仅 `front` 姿态**挂完整系统:12 表情变体、三档口型、眨眼。
- **非 front 姿态**:显示该姿态的固定整图,`full_expression=false`,`mouth="none"`——即**口型不动、情绪表情不生效、眨眼不生效**。运动层保留。
- 姿态**切回 front 时**,完整表情/口型/眨眼系统恢复。
- 实现上:进入非 front 姿态时挂起 mouth/emotion/blink 的图源切换逻辑(但运动层继续);切回 front 恢复。注意 RMS 仍在更新弹跳,只是不再驱动口型图切换。

### B3. 触发逻辑

- **待机随机**(`triggers.idle_random`,默认开):front 停留期间,每 `interval_ms`(12~30s)随机切到一个非 front 姿态,停留 2~4s 后回 front。说话中(RMS 高于口型第一档持续一段时间)应**抑制**随机切换,避免说话说到一半歪头把嘴锁住——说话时优先留在 front。
- **手动**:Monitor 页加五个姿态按钮,点击即切,供 Windows 验收测试。
- **情绪绑定**(`triggers.emotion_hint`,默认关):预留接口,允许将情绪映射到姿态(如 shy→tilt_left),本期不启用,留空实现即可。

### B4. SetListening

一期空实现。本期填上:`SetListening(true)` 在**当前为 front 且非说话**时,可选地切到一个歪头姿态表示"倾听",并将眨眼间隔乘 `blink_interval_scale`(0.7);`false` 时回 front、眨眼恢复。调用方暂无(Realtime 接入是后续任务),本期只需保证 Monitor 可手动触发验证。

---

## 验收清单

Batch A:
- [ ] 呼吸时脚底像素行逐帧不动,无遮挡/缝隙/重复像素
- [ ] 改 avatar.json 保存后,呼吸幅度/弹跳增益即时生效,无需重启,无闪烁
- [ ] 同帧重复回调不再导致加速(单测覆盖 deltaMs==0)
- [ ] 现有单测仍全绿

Batch B:
- [ ] 五姿态切换均为平滑淡化,脚底不跳
- [ ] front 姿态完整表情/口型/眨眼正常;非 front 姿态口型静止、情绪不生效、运动层仍在
- [ ] 姿态切回 front 后表情系统完全恢复
- [ ] 说话过程中不会被随机姿态切换打断(说话时留在 front)
- [ ] Monitor 页五个姿态按钮可手动触发

## 本期不做

Realtime 模型接入、SetListening 真实调用方、sleep 自动进入、OBS 采集、粒子特效、为非 front 姿态制作张嘴/表情变体(方向3明确不做)、LLM 情绪链路联调(二期后用户单独跑真实对话校准 `emotion_map`,日志若出现"未知情绪回退 neutral"只补 config 映射,不改代码)。

## 关键提醒

- **不要使用 `layers` 节,不要做头身分层或旋转歪头**。歪头效果完全由 poses 整图切换实现。
- 头层/身体层/tail 等 layered 资产是历史遗留,忽略。
- 所有观感参数(呼吸幅度、弹跳增益、姿态停留时长、过渡时长)必须可热重载,用户需开窗调手感。
