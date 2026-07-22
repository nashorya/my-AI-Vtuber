# 任务:形象渲染器二期 — 呼吸修正、头身分层歪头、倾听姿态

> 承接一期(`AVATAR_RENDERER_PLAN.md`,已合入 PR #5)。本期分两批交付:
> **Batch A(立即可做,无新资产依赖)**:呼吸修正 + 一期遗留 bug + 参数热重载
> **Batch B(依赖新资产)**:头身分层、歪头、倾听姿态
> Batch A 完成即可交付验收,不要等 Batch B。
>
> **勘误 (v0.5)**: B1 头层跟随公式以切口为准 — `(pivot_y - cut_y) × (scaleY - 1)`;
> 旧文「身体层顶端位移 / `canvasH - neckPivotY`」为错误。头层羽化在 PNG(y535~545),代码不做边缘处理。

---

## Batch A:修正与打磨(无新资产)

### A1. 呼吸动画重做 【最高优先级 — 当前实现观感错误】

**现状问题**:用户验收发现,现版本将立绘上半身向上平移后再向下平移,导致上半身像素覆盖下半身,画面诡异。此前一版为整图 Y 位移,观感为"整个人原地上下飘"。两版都是错的。

**根因**:呼吸被实现成了**平移**。平移必然产生遮挡或缝隙。真人呼吸时躯干是**形变**,没有任何部位离开原位去覆盖其他部位。

**唯一正确实现**:

- 呼吸 = 对整张立绘的 `ScaleTransform`,`ScaleY` 在 `1.0` ↔ `1.0 + scale_amp` 之间正弦振荡;`ScaleX` 保持 1.0(或反向微调 `1.0 - scale_amp*0.3` 模拟体积守恒,可选)。
- `CenterX` / `CenterY` **必须**取自 `avatar.json` 的 `meta.pivot`(脚底中心 627,1180),换算为控件坐标系。
- **禁止** `TranslateTransform` 参与呼吸。
- **禁止** 将立绘按矩形裁切成上下半身分别变换。单图层结构下不存在合法的分层呼吸实现。
- `motion_layer.breath.amp_px` 字段废弃(保留字段但强制忽略,或置 0 并在 README 注明)。

**参数**:`scale_amp` 默认从 0.012 下调为 **0.008**,并确保可热重载调整。判定标准:盯着看能察觉,余光看只觉得"她是活的"。

**验收**:脚底位置在整个呼吸周期内**逐帧不动**(可用固定截图对比脚底像素行);画面无任何遮挡、无缝隙、无重复像素。

### A2. deltaTime 重复回调 bug 【正确性】

`AvatarWindow.OnRendering` 中 `deltaMs <= 0` 时被替换为 16.67ms。但 WPF `CompositionTarget.Rendering` 在同一帧可能回调多次且 `RenderingTime` 相同,此时 deltaMs 为 0,当前写法会让状态机与运动引擎各多推进一整帧,表现为偶发动画加速/抖动。

**修复**:记录上次 `RenderingTime`,若本次与上次**相同则直接 return**(跳过整帧更新),不要补时长。仅在 deltaMs 为负(时钟异常)时才钳制。上限 250ms 钳制保留。

### A3. 弹跳增益外置 【手感】

`MotionEngine` 中 `_smoothedRms * MaxPx * 4f` 的常数 `4f` 写死。因口型"张嘴"档阈值为 0.35,而 RMS 超过 0.25 弹跳即饱和,正常说话音量下弹跳大部分时间顶满,动态感损失。

**修复**:提取为 `motion_layer.bounce.gain`(默认 4.0)可配置;并将包络平滑由线性步进 `dt/attack` 改为帧率无关的指数形式 `1 - exp(-dt/τ)`。

### A4. 参数热重载 【调试效率,本期关键】

监听 `avatar.json` 文件变更(`FileSystemWatcher` + 防抖 300ms,注意编辑器保存会触发多次事件),重新加载后**仅热更新 `motion_layer` / `mouth_sync` / `stickers` 的参数值**,不重建已预加载的位图、不重置当前状态。解析失败时保留旧配置并记录警告,禁止崩溃或黑屏。

参考项目已有的 `TtsHotReloadTests` 模式。加对应单测。

**目的**:用户需开着窗口反复调呼吸幅度、弹跳增益、摆动角度等手感参数,重启一次调一个参数不可接受。

---

## Batch B:头身分层与歪头(依赖新资产)

> ⚠️ 开工前置条件:`assets/avatar/layered/` 资产就绪。**资产未到位时不要开始 B,先交付 A。**
> 资产以 **avatar_pack v0.5** 为准。

### B0. 资产结构(由用户提供,此处为契约)

```
assets/avatar/layered/
  body.png            # 身体层(单张,共用)
  head/gen_00.png     # 头层 12 个变体:头+全部头发,透明底,画布尺寸与原图一致(1254x1254)
  head/gen_01.png ...
```

- 头层与身体层画布尺寸相同、坐标对齐,直接叠加即还原原立绘。
- 头层 PNG 在 `cut_y`~`feather_to_y`(535~545)已带 alpha 羽化;渲染器**禁止**再做边缘处理。
- `avatar.json` 的 `layers` 节含 `enabled` / `body` / `head_dir` / `cut_y` / `neck_pivot` / `feather_to_y`(文档字段)。
- **`layers.enabled` 为 false 或资产缺失时,自动回退到一期单图层模式**,全部功能(除歪头)正常工作。这是硬性要求。

### B1. 分层渲染

- 渲染改为身体层 + 头层两个 Image 叠加(贴纸 Canvas 仍在最上)。
- 表情切换只换**头层**图源;身体层始终不变。交叉淡化(fade)同样只作用于头层。
- 呼吸改为:身体层 ScaleY 缩放(锚点脚底 `meta.pivot`,同 A1 规则)+ 头层跟随一个**由缩放推导出的纵向位移**(胸腔膨胀顶起头部)。
  - **正确公式**:头层跟随位移 = `(pivot_y - cut_y) × (scaleY - 1)`(WPF:`HeadTranslate.Y = -(pivot_y - cut_y) × (scaleY - 1)`)。
  - **按切口高度 `layers.cut_y` 算**,不是按身体层顶端,也不是 `canvasH - neckPivotY`(文档旧句错误)。
  - 头层本身不缩放;接缝软边以 PNG 为准。

### B2. 歪头

- 歪头 = 头层绕 `neck_pivot` 的 `RotateTransform`,角度范围 ±`tilt_deg`(默认 8,上限 12)。
- **过渡必须用弹簧插值**,不是线性:二阶弹簧(stiffness / damping 可配),允许轻微过冲后稳定。目标角度变化时不重置速度,保证连续。
- 新增配置 `motion_layer.tilt`: `{ "max_deg": 8, "stiffness": 120, "damping": 14 }`,全部热重载可调。
- 触发来源:
  - `SetListening(true)` → 倾斜至 `listening.tilt_deg`(见 B3)
  - 情绪 `motion_override` 可指定 `tilt_deg`(如 shy 轻歪、surprised 短暂正回)
  - 待机随机:低概率(每 15~40s)歪一次,保持 2~4s 后回正
- 摆动(`sway`)保持作用于整体;歪头作用于头层,两者叠加不冲突。

### B3. SetListening 实装

一期为空实现。本期填上:`SetListening(true)` → 头部倾斜 `listening.tilt_deg`(默认 5)+ 眨眼间隔乘以 `blink_interval_scale`(默认 0.7,眨得更勤,读作"专注倾听");`false` 时弹簧回正、眨眼恢复。

调用方暂无(Realtime 接入是后续任务),本期**只需保证接口行为正确并可通过 Monitor 页手动触发测试**。

---

## 验收清单

Batch A:
- [ ] 呼吸时脚底像素行逐帧不动,无遮挡/缝隙/重复像素
- [ ] 修改 avatar.json 保存后,呼吸幅度/弹跳增益即时生效,无需重启,无闪烁
- [ ] 同帧重复回调不再导致动画加速(单测覆盖 deltaMs==0 场景)
- [ ] 现有单测仍全绿

Batch B:
- [ ] `layers.enabled=false` 或资产缺失时完整回退单图层,不崩溃
- [ ] 表情切换时身体层纹丝不动,只有头层变化
- [ ] Monitor 页触发 listening,头部平滑歪至目标角度并有轻微过冲,松开后回正
- [ ] 歪头与呼吸、摆动、弹跳同时作用时无抖动、无接缝穿帮(羽化在 PNG,代码无边缘处理)
- [ ] 呼吸时头层跟随按 `(pivot_y - cut_y) × (scaleY - 1)`,切口不离缝

## 本期仍不做

Realtime 模型接入、`SetListening` 的真实调用方、sleep 自动进入、OBS 采集配置、粒子特效、LLM 情绪链路联调(用户将在二期后单独跑一次真实对话校准 `emotion_map`,届时如日志出现"未知情绪回退 neutral",只需补 config 映射表,不改代码)。
