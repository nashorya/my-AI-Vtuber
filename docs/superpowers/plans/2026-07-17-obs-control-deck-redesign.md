# OBS Control Deck Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the generic card-and-form visual language with a compact OBS-inspired control deck while preserving existing navigation, behavior, accessibility, and themes.

**Architecture:** Keep all runtime/ViewModel behavior unchanged. Build the visual grammar from shared resource keys (near-square tool buttons, dock surfaces, state accents), then apply it to shell, monitor, memory, configuration, and first-run views. Existing WPF layout smoke tests remain the regression boundary.

**Tech Stack:** WPF, WPF-UI 3.1.1, existing XAML resource dictionaries, xUnit STA layout smoke tests.

---

### Task 1: Establish OBS control-deck tokens

**Files:**
- Modify: `App/Resources/Tokens.xaml`
- Modify: `App/Resources/Themes/Dark.xaml`
- Modify: `App/Resources/Themes/Light.xaml`
- Test: `App.Tests/LayoutSmokeTests.cs`

- [ ] **Step 1: Add a failing resource smoke assertion**

Extend the STA test to resolve `DeckPanelBrush`, `DeckPanelBorderBrush`, and `DeckAccentBrush` from `Application.Current.Resources` after each theme transition.

- [ ] **Step 2: Run the smoke test and verify it fails**

Run: `dotnet test App.Tests/App.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~LayoutSmokeTests`

Expected: FAIL because the deck resource keys are absent.

- [ ] **Step 3: Add shared resource keys**

Add 2px tool-button corners, 4px dock-panel corners, a 40px tool-button height, and semantic panel/accent brushes for light and dark themes. Do not use raw colors in views.

- [ ] **Step 4: Verify the smoke test passes**

Run the Step 2 command. Expected: PASS.

### Task 2: Rework shell and Monitor into the primary control deck

**Files:**
- Modify: `App/MainWindow.xaml`
- Modify: `App/Views/MonitorView.xaml`
- Test: `App.Tests/LayoutSmokeTests.cs`

- [ ] **Step 1: Extend the smoke test with tool-button sizing assertions**

Name Monitor mute/interrupt controls and assert that each arranged control has a minimum 40px target height.

- [ ] **Step 2: Run the test and verify the expected failure**

Run the App.Tests command above. Expected: FAIL until the named controls and deck spacing exist.

- [ ] **Step 3: Implement the deck layout**

Make the shell theme control a compact icon-led utility action. In Monitor, use a thin status strip, square-corner action buttons with icon + concise label, and dock-like bordered event/data panels. Preserve `StopSpeakingButton`, `MicMuteButton`, commands, bindings, tooltips, and automation names.

- [ ] **Step 4: Verify layout smoke and Release build**

Run: `dotnet test App.Tests/App.Tests.csproj --no-restore -c Release`

Run: `dotnet build App/App.csproj --no-restore -c Release`

Expected: both commands pass with zero build warnings/errors.

### Task 3: Apply the visual grammar to Memory, Configuration, and onboarding

**Files:**
- Modify: `App/Views/MemoryView.xaml`
- Modify: `App/Views/ConfigView.xaml`
- Modify: `App/Views/FirstRunView.xaml`
- Test: `App.Tests/LayoutSmokeTests.cs`

- [ ] **Step 1: Extend the smoke test to resolve all three views in both themes**

Keep the 760×520, 980×680, and 1440×900 coverage and assert nonzero arranged size for each root view.

- [ ] **Step 2: Run the smoke test before the XAML change**

Run the App.Tests command. Expected: current test passes; use it as behavior-preserving regression coverage before visual-only edits.

- [ ] **Step 3: Replace generic cards with dock panels and toolbars**

Use thin panel borders, compact section headers, transparent/secondary utility actions, and square danger icons. Keep all binding paths, handlers, key bindings, `Tooltip`, and `AutomationProperties.Name` values unchanged.

- [ ] **Step 4: Verify tests and audit XAML**

Run App.Tests and `git diff --check`. Expected: tests pass and no whitespace errors.

### Task 4: Final regression verification

**Files:**
- Test: `AIVTuber.Tests/AIVTuber.Tests.csproj`
- Test: `App.Tests/App.Tests.csproj`

- [ ] **Step 1: Run Core tests**

Run: `dotnet test AIVTuber.Tests/AIVTuber.Tests.csproj --no-restore --verbosity minimal`

Expected: all tests pass, including `VadDetectorTests` with copied `WebRtcVad.dll`.

- [ ] **Step 2: Run WPF layout smoke and Release build**

Run: `dotnet test App.Tests/App.Tests.csproj --no-restore -c Release --verbosity minimal`

Run: `dotnet build App/App.csproj --no-restore -c Release --verbosity minimal`

Expected: smoke passes; build has zero warnings/errors.
