# 形象渲染器二期 — 剩余工作(Batch A 收尾 + Batch B)

> 本文档承接已合入的 Batch A 前半部分(呼吸重做 + bounce gain 外置 + Spring1D + tilt 引擎 + SetListening)。
> 已完成项见 commit 历史;本文档列出**尚未实现**的部分,按优先级排序。

## 已完成(本次提交)

- ✅ **A1 呼吸重做**:MotionEngine 呼吸改为纯 `ScaleY` 振荡,`breathY` 强制为 0(废弃 `amp_px` 位移)。锚点由 AvatarWindow 的 `RenderTransformOrigin=(pivotX, 1.0)` 保证脚底不动。
- ✅ **A3 bounce gain 外置**:`BounceConfig.Gain`(默认 4.0)替代硬编码 `4f`;包络平滑从线性 `dt/attack` 改为帧率无关的指数 `1 - exp(-dt/τ)`。
- ✅ **Spring1D 弹簧**(新文件 `AIVTuber.Core/Avatar/Spring1D.cs`):二阶弹簧,半隐式 Euler,目标变化不重置速度(允许过冲)。
- ✅ **MotionEngine tilt 集成**:`SetListeningTilt` / emotion override `tilt_deg` / `ClearPersistentOverride`;`MotionFrame` 加 `TiltDeg` 字段(默认 0,向后兼容)。优先级:emotion > listening > 0。
- ✅ **B3 SetListening 实装**:`PixelAvatarDriver.SetListening` 调 `_motion.SetListeningTilt(tiltDeg)` + `_sm.SetBlinkIntervalScale(scale)`。
- ✅ **配置模型扩展**:`LayersConfig` / `TiltConfig` / `BounceConfig.Gain` / `MotionOverrideDef.TiltDeg`;`BreathConfig.AmpPx` 标记 deprecated。
- ✅ **v0.3 资产**:解压 `avatar_pack_v0.3.zip` 到 `assets/avatar/`(含 `layered/body.png` + `layered/head/gen_00~11.png` + 新 avatar.json)。
- ✅ **测试**:323 全绿(+16 新增:Spring1D 7 个、tilt 5 个、bounce gain/指数平滑 2 个、breath ScaleY/no-translation 2 个)。

---

## 未完成 — 按优先级

### 🔴 P0:Step 5 — AvatarWindow 分层渲染 + 头层位移 + 歪头(Batch B 核心)

**为什么没做**:这是最大的一块(XAML 结构改造 + code-behind PreloadAssets/ApplyBody/ApplyMotion 重写),需要手动 WPF 验证,不能盲写。

**要做什么**:

1. **`App/Views/AvatarWindow.xaml`**:在 `BodyLayer` 内部加一个 `HeadLayer` Grid(独立 `TransformGroup`:RotateTransform `HeadTilt` + TranslateTransform `HeadTranslate`),含 `HeadImageA`/`HeadImageB` 两个 Image(用于头层 cross-fade)。默认 `Visibility=Collapsed`。

2. **`App/Views/AvatarWindow.xaml.cs`**:
   - `PreloadAssets`:`pack.Layers?.Enabled == true` 且 `layered/body.png` + `layered/head/*.png` 都存在时:
     - 加载 body.png 到 `_bodyBitmap`(单张,所有表情共用)
     - 加载 head sprites 到 `_headFrames: Dictionary<string, ImageSource>`(key=state 名,文件名复用 `states[*].file` 的基名,如 `gen_00.png` → 从 `layered/head/gen_00.png` 取)
     - `HeadLayer.Visibility = Visible`
     - `BodyImageA.Source = _bodyBitmap`(身体始终不变)
   - 否则:走原单图层逻辑,`HeadLayer.Visibility = Collapsed`
   - `ApplyBody`:分层模式下 HeadImageA/B 按状态切换(含 cross-fade),BodyImageA 不变;单图层模式走原逻辑
   - `ApplyMotion`(新增头层部分):
     - `HeadTilt.Angle = m.TiltDeg`(歪头)
     - `HeadTranslate.Y = -(canvasH - neckPivotY) * (m.ScaleY - 1)`(身体缩放时头层跟随上移;负值=WPF Y 向上)
     - `HeadLayer.RenderTransformOrigin = (neckPivotX/canvasW, neckPivotY/canvasH)`(脖子支点)

3. **回退保证**:`layers.enabled=false` 或资产缺失 → `HeadLayer.Visibility=Collapsed` + 走原 BodyImageA/B 单图层路径,一期功能全部正常。

**关键约束**:
- 表情切换只换头层图源,身体层纹丝不动
- 头层文件名复用 `states.file` 基名(如 `sprites/gen_00.png` → `layered/head/gen_00.png`)
- TransformGroup 顺序:Scale → Rotate → Translate(身体层);Rotate → Translate(头层)

**验收**:
- `layers.enabled=false` 时完整回退单图层,不崩溃
- 表情切换时身体层不动,只有头层变化
- 歪头与呼吸、sway、bounce 同时作用无抖动/穿帮

---

### 🟡 P1:Step 8 — 参数热重载(A4,FileSystemWatcher)

**要做什么**:

1. **新文件 `AIVTuber.Core/Avatar/AvatarConfigWatcher.cs`**:
   ```csharp
   internal sealed class AvatarConfigWatcher : IDisposable
   {
       public AvatarConfigWatcher(string assetsDir, Func<Task> onReload, TimeSpan debounce);
       // FileSystemWatcher 监听 avatar.json 的 Changed/Renamed,防抖 300ms 后调 onReload
   }
   ```

2. **`MotionEngine`**:`_cfg` 从 readonly 改可写;加 `UpdateConfig(MotionLayerConfig)`(保留 `_timeSec`/`_smoothedRms`/tilt spring 状态)

3. **`AvatarStateMachine`**:`_pack` 改可写;加 `UpdatePack(AvatarPackConfig)`(保留当前状态/blink 计时)

4. **`PixelAvatarDriver`**:加 `ReloadConfig(AvatarPackConfig newPack)` — 换 `_pack`、调 `_sm.UpdatePack` + `_motion.UpdateConfig`。加 `AvatarConfigReloaded` 事件让 AvatarWindow 同步。

5. **`BotRuntime.InitPixelAvatar`**:末尾创建 `AvatarConfigWatcher`,回调里 `Load` + `ReloadConfig` + 触发事件。`DisposeAsync` 里 dispose watcher。

6. **`AvatarWindow.xaml.cs`**:订阅重载事件,更新本地 `_pack` 引用和 `_headFrames`(检测文件 mtime 变化才重建),**不动 `_bodyFrames`**(任务硬性要求)。

**测试**:`MotionEngine_UpdateConfig_ChangesBounceGain`、`AvatarStateMachine_UpdatePack_ChangesBlinkInterval`、`AvatarConfigWatcher_DebouncesMultipleEvents`

**验收**:改 avatar.json 的 `scale_amp`/`gain`/`tilt.max_deg` 保存后即时生效,无需重启,无闪烁。

---

### 🟢 P2:Step 7 — Monitor 页倾听测试按钮

**要做什么**:

1. **`App/Views/MonitorView.xaml`**:在"表情"区下加按钮
   ```xml
   <ui:Button Content="倾听 ON" Appearance="Secondary" Click="OnAvatarListening" Tag="listening"
              ToolTip="SetListening(true) — 头部歪 + 眨眼变勤" />
   ```

2. **`App/Views/MonitorView.xaml.cs`**:`OnAvatarListening` 切换 ON/OFF 状态,调 `vm.TriggerAvatarListening(bool)`

3. **`AIVTuber.Core/ViewModels/MonitorViewModel.cs`**:加 `TriggerAvatarListening(bool on)` → `_runtime.PixelAvatar?.SetListening(on)`

**验收**:Monitor 页点"倾听 ON",头部歪至 `listening.tilt_deg`(默认 5°)有过冲,松开后弹簧回正;眨眼间隔缩短。

---

### 🟢 P3:Step 9+10 — A2 单测 + 文档收尾

1. **A2 单测固化**:`AvatarStateMachineTests` 加 `Tick_WithZeroDelta_DoesNotAdvanceBlinkTimer`(deltaMs==0 时 blink 计时不推进)。注:AvatarWindow.OnRendering 的 RenderingTime 去重是 UI 层逻辑,单测只覆盖到 SM 层的 deltaMs==0 语义。

2. **`assets/avatar/README.md` 更新**:说明 v0.3 配置(layers/tilt/gain/scale_amp 字段含义)。

---

## 实施建议顺序

1. **Step 5**(分层渲染)— 最关键,做完 Batch B 就能验收。需要手动跑 App 看 WPF 效果。
2. **Step 8**(热重载)— 调参效率,做完后 Step 5 的手感调整会快很多。
3. **Step 7**(Monitor 按钮)— 小,但依赖 Step 5(否则看不到歪头效果)。
4. **Step 9+10**(收尾)— 最后。

## 风险点

- **头层位移方向**:WPF Y 轴向下,身体 `ScaleY>1` 时身体顶端上移(像素 Y 变小),头层要保持对齐需 `HeadTranslate.Y` 为负。公式 `-(canvasH - neckPivotY) * (scaleY - 1)`。务必手动验证。
- **MotionFrame 已加 `TiltDeg`**:Step 5 的 ApplyMotion 直接读 `m.TiltDeg` 即可,无需再改 MotionEngine。
- **AvatarConfigLoader 现有 fallback 行为**:解析失败返回 placeholder pack(非 null)。热重载路径需要区分"真的解析成功"vs"fallback",否则会把 placeholder 覆盖掉真实配置。建议 Loader 加 `TryLoad` 重载或在 ReloadConfig 里比对 `pack.Meta.Name != "dev_placeholder"`。
