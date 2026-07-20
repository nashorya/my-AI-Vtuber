# 形象渲染器二期 — Batch A 收尾 + Batch B

> 更新 2026-07-20：P0–P3 已在本分支落地。下文保留原规格作为验收对照。

## 已完成

- ✅ A1 呼吸重做 / A3 bounce gain / Spring1D / MotionEngine tilt / B3 SetListening / 配置模型 / v0.3 资产
- ✅ **P0 Step 5**：`AvatarWindow` 分层渲染 + 头层位移 + 歪头（`layers.enabled` 回退单图层）
- ✅ **P1 Step 8**：`AvatarConfigWatcher` 热重载 + `UpdateConfig` / `UpdatePack` / `ReloadConfig` + `TryLoad`
- ✅ **P2 Step 7**：Monitor「倾听 ON/OFF」按钮
- ✅ **P3 Step 9+10**：`Tick_WithZeroDelta` 等单测 + README v0.3 字段说明

## 未完成（可选）

- Bugbot 指出的 ASR streaming 问题（与形象渲染无关，可另开）

---

## 验收要点

- `layers.enabled=false` 或资产缺失 → 完整回退单图层
- 表情切换只换头层，身体不动；歪头与呼吸/bounce 同时作用
- 改 `avatar.json` 的 `scale_amp` / `gain` / `tilt.max_deg` 保存后热重载生效
- Monitor 点「倾听 ON」→ 头歪至 `listening.tilt_deg`，眨眼变勤

## 风险（手测）

- 头层位移：`HeadTranslate.Y = -(canvasH - neckPivotY) * (scaleY - 1)`，WPF Y 向下，务必在 Windows 上肉眼确认无穿帮
