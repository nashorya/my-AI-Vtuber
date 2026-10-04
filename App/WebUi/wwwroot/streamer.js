(() => {
  "use strict";
  const $ = (id) => document.getElementById(id);
  const hasWebView = !!(window.chrome && chrome.webview);

  let settings = null;          // last settings projection from the host
  let state = null;             // last home state
  let saveSeq = 0;              // request ids for saveSettings
  const latestSave = {};        // kind -> latest request id (older replies are ignored)
  const pendingKind = {};       // request id -> kind
  let personaDirty = false;
  let liveDirty = false;
  let pendingIdentity = null;   // name/title the persona assistant filled in; saved with the persona
  let previewRequest = 0;       // newest preview request id seen
  let previewActive = false;

  function post(msg) { if (hasWebView) chrome.webview.postMessage(msg); }
  function send(name, data) {
    const msg = { type: "command", name };
    if (data !== undefined) msg.data = data;
    post(msg);
  }

  function text(el, value) { if (el) el.textContent = value == null ? "" : String(value); }

  function saveSettings(kind, patch) {
    const requestId = ++saveSeq;
    latestSave[kind] = requestId;
    pendingKind[requestId] = kind;
    send("saveSettings", { requestId, patch });
    return requestId;
  }

  // ── Navigation ─────────────────────────────────────────────────────────
  function showPage(page, focusId) {
    document.querySelectorAll(".tabs [data-page]").forEach((b) => {
      const on = b.dataset.page === page;
      b.classList.toggle("active", on);
      b.setAttribute("aria-selected", on ? "true" : "false");
    });
    document.querySelectorAll(".page").forEach((p) => p.classList.toggle("active", p.id === `page-${page}`));
    if (page !== "home") send("getSettings");
    if (focusId) setTimeout(() => $(focusId)?.focus(), 30);
  }
  document.querySelectorAll(".tabs [data-page]").forEach((b) => b.addEventListener("click", () => showPage(b.dataset.page)));
  document.querySelectorAll("[data-goto]").forEach((b) =>
    b.addEventListener("click", () => showPage(b.dataset.goto, b.dataset.focus)));

  // ── Home ───────────────────────────────────────────────────────────────
  const serviceText = {
    off: "未启用",
    connected: "已连接",
    connecting: "连接中",
    notConnected: "未连接",
  };

  let quotaTimer = null;
  function renderQuota(q, c) {
    const tag = $("quotaTag");
    clearInterval(quotaTimer);
    if (!q || !q.managed || !c.signedIn) { tag.hidden = true; return; }
    let remaining = q.remainingSeconds;
    const paint = () => {
      const out = q.exhausted || remaining <= 0;
      const minutes = Math.ceil(remaining / 60);
      tag.hidden = false;
      tag.className = "quota" + (out ? " out" : remaining <= 600 ? " low" : "");
      tag.textContent = out ? "今日时长已用完 · 明早 6:00 恢复"
        : remaining < 60 ? "今日剩余不到 1 分钟"
        : remaining <= 600 ? `今日剩余 ${minutes} 分钟，快用完了`
        : `今日剩余 ${minutes} 分钟`;
    };
    paint();
    // Between pushes the page counts down on its own while the companion runs; every push re-syncs it.
    if (c.running && !q.exhausted) quotaTimer = setInterval(() => { remaining = Math.max(0, remaining - 1); paint(); }, 1000);
  }

  function renderState(s) {
    state = s;
    const c = s.companion || {};
    text($("activity"), c.activity || "空闲");
    $("activityDot").className = "dot " + (!c.signedIn ? "off" : c.paused ? "paused" : "on");
    text($("activitySub"), !c.signedIn ? "登录后开始陪播"
      : c.paused ? "已暂停：不识别、不说话。设备电平仍会显示。"
      : "陪播进行中");
    const btn = $("btnCompanion");
    btn.hidden = !c.signedIn;
    const assistBtn = $("btnPersonaAssist");
    if (assistBtn.textContent === "帮我写人设") assistBtn.disabled = !c.signedIn;
    assistBtn.title = c.signedIn ? "" : "登录后才能使用";
    btn.textContent = c.paused ? "继续陪播" : "暂停陪播";
    btn.classList.toggle("primary", !!c.paused);
    $("btnStop").disabled = !c.canStop;
    $("btnSignIn").hidden = !!c.signedIn;
    const q = s.quota || {};
    renderQuota(q, c);
    if (q.managed && q.exhausted) {
      btn.disabled = true;
      text($("activitySub"), "今天的陪播时长用完了，明早 6:00 恢复。");
    } else {
      btn.disabled = false;
    }

    const l = s.listening || {};
    text($("micName"), l.micName || "");
    $("micBar").style.width = `${Math.min(100, Math.round((l.micLevel || 0) * 100))}%`;
    $("loopBar").style.width = `${Math.min(100, Math.round((l.loopbackLevel || 0) * 100))}%`;
    text($("loopState"), l.loopbackEnabled ? "已开启" : "未开启");
    $("btnMic").textContent = l.micEnabled ? "关闭麦克风监听" : "打开麦克风监听";
    $("btnMic").classList.toggle("warn", !l.micEnabled);
    text($("chipMode"), l.isPkMode ? `PK 模式${l.pkOpponent ? " · " + l.pkOpponent : ""}` : "正常模式");

    const sp = s.speech || {};
    const chip = $("chipSpeech");
    text(chip, `语音识别：${sp.label || "—"}`);
    chip.className = "chip " + (sp.health === "unavailable" ? "bad" : sp.health === "ready" || sp.health === "recognizing" ? "good" : "");
    const sv = s.services || {};
    [["chipDanmaku", "弹幕", sv.danmaku], ["chipObs", "OBS 字幕", sv.obs], ["chipAvatar", "形象连接", sv.avatar]]
      .forEach(([id, label, v]) => {
        text($(id), `${label}：${serviceText[v] || "—"}`);
        $(id).className = "chip " + (v === "connected" ? "good" : v === "notConnected" ? "warn" : "");
      });

    text($("homeVoice"), (s.voice && s.voice.name) || "—");
    renderAccount(s.account || {}, s.quota || {});
    renderIssues(s.issues || []);
    renderConversation(s.conversation || []);
  }

  function renderIssues(issues) {
    const box = $("issues");
    box.innerHTML = "";
    issues.forEach((i) => {
      const div = document.createElement("div");
      div.className = "issue";
      const msg = document.createElement("p");
      msg.textContent = i.message;
      div.appendChild(msg);
      const meta = document.createElement("p");
      meta.className = "issue-meta";
      meta.textContent = [i.action, i.diagnosticId ? `诊断编号 ${i.diagnosticId}` : ""].filter(Boolean).join(" · ");
      div.appendChild(meta);
      if (i.area === "account") {
        const b = document.createElement("button");
        b.type = "button"; b.className = "btn sm"; b.textContent = "登录";
        b.addEventListener("click", () => send("signIn"));
        div.appendChild(b);
      } else {
        const b = document.createElement("button");
        b.type = "button"; b.className = "btn sm ghost"; b.textContent = "知道了";
        b.addEventListener("click", () => send("dismissIssue", { code: i.code }));
        div.appendChild(b);
      }
      box.appendChild(div);
    });
  }

  function renderConversation(items) {
    const list = $("conversation");
    list.innerHTML = "";
    if (!items.length) {
      const li = document.createElement("li");
      li.className = "empty";
      li.textContent = "开始说话后，这里会显示 AI 听到和说出的内容。";
      list.appendChild(li);
      return;
    }
    const who = { ai: "AI", opponent: "对面", input: "听到" };
    items.forEach((it) => {
      const li = document.createElement("li");
      li.className = it.who;
      const tag = document.createElement("span");
      tag.className = "who";
      tag.textContent = who[it.who] || "";
      const body = document.createElement("span");
      body.textContent = it.text;
      const t = document.createElement("time");
      t.textContent = it.time;
      li.append(tag, body, t);
      list.appendChild(li);
    });
  }

  function renderAccount(a, aq) {
    text($("homeAccount"), a.managed ? (a.signedIn ? `${a.username} · ${a.validUntil || "已登录"}` : "未登录") : "公开版");
    text($("accName"), a.username || "—");
    text($("accValid"), a.validUntil || "—");
    text($("accQuota"), aq.managed
      ? `${Math.floor((aq.totalSeconds - aq.remainingSeconds) / 60)} 分钟 / 共 ${Math.round(aq.totalSeconds / 60)} 分钟`
      : "—");
    const msg = $("accMessage");
    msg.hidden = !a.message;
    text(msg, a.message);
    $("btnSignOut").hidden = !a.signedIn;
    $("btnSignIn2").hidden = !!a.signedIn || !a.managed;
    text($("version"), a.version ? `版本 ${a.version}` : "");
  }

  $("btnCompanion").addEventListener("click", () =>
    send(state && state.companion && state.companion.paused ? "resumeCompanion" : "pauseCompanion"));
  $("btnStop").addEventListener("click", () => send("stopSpeaking"));
  $("btnMic").addEventListener("click", () => send("toggleMic"));
  $("btnPk").addEventListener("click", () => send("togglePk"));
  $("btnNewPk").addEventListener("click", () => send("newPk"));
  $("btnSignIn").addEventListener("click", () => send("signIn"));
  $("btnSignIn2").addEventListener("click", () => send("signIn"));
  $("btnSignOut").addEventListener("click", () => {
    if (confirm("退出登录后陪播会停止，确定吗？")) send("signOut");
  });

  // ── 我的 AI: persona ───────────────────────────────────────────────────
  const personaEl = $("persona");
  const EXAMPLE = "你叫小鱼，是陪主播一起直播的 AI 搭档。\n说话轻松、爱接梗，偶尔吐槽主播但不刻薄。\n回答尽量短，一两句话就好。\n遇到不懂的事就大方承认。";

  function setPersonaState(t, cls) {
    const el = $("personaState");
    text(el, t);
    el.className = "save-state " + (cls || "");
  }

  personaEl.addEventListener("input", () => {
    personaDirty = settings ? personaEl.value !== settings.persona.effectiveSystemPrompt : true;
    setPersonaState(personaDirty ? "未保存" : "已生效", personaDirty ? "warn" : "ok");
  });
  $("btnSavePersona").addEventListener("click", () => {
    setPersonaState("保存中…");
    const patch = { persona: { systemPrompt: personaEl.value } };
    if (pendingIdentity) patch.live = { selfName: pendingIdentity.selfName, wakeKeywords: pendingIdentity.wakeKeywords };
    saveSettings("persona", patch);
  });
  $("btnUndoPersona").addEventListener("click", () => {
    if (!settings) return;
    personaEl.value = settings.persona.effectiveSystemPrompt;
    personaDirty = false;
    setPersonaState("已生效", "ok");
    dropPendingIdentity();
  });
  $("btnExample").addEventListener("click", () => {
    // Never replaces what the streamer wrote: appended at the end.
    personaEl.value = personaEl.value.trim() ? `${personaEl.value.trimEnd()}\n\n${EXAMPLE}` : EXAMPLE;
    personaEl.dispatchEvent(new Event("input"));
    personaEl.focus();
  });
  $("btnDefaultPersona").addEventListener("click", () => {
    if (!settings) return;
    if (!confirm("恢复默认会替换编辑框里的人设（保存后才生效），确定吗？")) return;
    personaEl.value = settings.persona.defaultSystemPrompt;
    personaEl.dispatchEvent(new Event("input"));
    dropPendingIdentity();
  });

  // ── 我的 AI: persona assistant ─────────────────────────────────────────
  // Check the streamer's description, ask only what is missing, then preview a short persona.
  // Taking it changes the editor (and the name/title fields) only; saving stays with the streamer.
  let assistSeq = 0;
  let assistRequest = 0;        // the request whose answer the panel waits for
  let assistQuestions = [];
  let assistDescription = "";
  let assistResult = null;

  function assistBusy(label) {
    const btn = $("btnPersonaAssist");
    btn.disabled = !!label || !canUseAssistant();
    btn.textContent = label || "帮我写人设";
    $("btnAssistCompose").disabled = !!label;
    $("btnAssistAgain").disabled = !!label;
  }
  function canUseAssistant() { return !state || !state.companion || state.companion.signedIn !== false; }
  function assistStatus(t, bad) {
    const el = $("assistStatus");
    text(el, t);
    el.className = "assist-status" + (bad ? " bad" : "");
  }
  function closeAssist() {
    assistRequest = 0;
    $("personaAssist").hidden = true;
    assistBusy("");
  }
  function sendAssist(name, data) {
    assistRequest = ++assistSeq;
    send(name, Object.assign({ requestId: assistRequest }, data));
  }
  function answers() {
    return assistQuestions.map((q) => {
      const box = document.querySelector(`[data-question="${q.id}"]`);
      const typed = box.querySelector("input").value.trim();
      const chosen = box.querySelector('.assist-chip[aria-pressed="true"]');
      return { questionId: q.id, text: typed || (chosen ? chosen.textContent : null) };
    });
  }
  function compose() {
    $("assistPreview").hidden = true;
    assistStatus("正在写人设…");
    assistBusy("正在写…");
    sendAssist("personaCompose", { description: assistDescription, answers: answers() });
  }

  $("btnPersonaAssist").addEventListener("click", () => {
    assistDescription = personaEl.value;
    assistQuestions = [];
    $("assistQuestions").innerHTML = "";
    $("assistAskRow").hidden = true;
    $("assistPreview").hidden = true;
    $("personaAssist").hidden = false;
    assistStatus("正在看你写的描述…");
    assistBusy("正在看…");
    sendAssist("personaCheck", { description: assistDescription });
  });
  $("btnAssistCompose").addEventListener("click", compose);
  $("btnAssistAgain").addEventListener("click", compose);
  $("btnAssistCancel").addEventListener("click", closeAssist);
  $("btnAssistClose").addEventListener("click", closeAssist);
  $("btnAssistUse").addEventListener("click", () => {
    if (!assistResult) return;
    personaEl.value = assistResult.persona;
    personaEl.dispatchEvent(new Event("input"));
    pendingIdentity = { selfName: assistResult.streamerTitle, wakeKeywords: assistResult.aiNames.join(", ") };
    $("selfName").value = pendingIdentity.selfName;
    $("wakeWords").value = pendingIdentity.wakeKeywords;
    closeAssist();
    setPersonaState("未保存（名字和称呼会随「保存人设」一起保存）", "warn");
  });

  function dropPendingIdentity() {
    if (!pendingIdentity) return;
    pendingIdentity = null;
    if (settings && !liveDirty) renderLive();
  }

  function renderQuestions(questions) {
    const host = $("assistQuestions");
    host.innerHTML = "";
    questions.forEach((q) => {
      const box = document.createElement("div");
      box.className = "assist-q";
      box.dataset.question = q.id;
      const h = document.createElement("h3");
      h.textContent = q.text;
      const chips = document.createElement("div");
      chips.className = "assist-chips";
      q.suggestions.forEach((s) => {
        const chip = document.createElement("button");
        chip.type = "button";
        chip.className = "assist-chip";
        chip.textContent = s;
        chip.setAttribute("aria-pressed", "false");
        chip.addEventListener("click", () => {
          const on = chip.getAttribute("aria-pressed") !== "true";
          chips.querySelectorAll(".assist-chip").forEach((c) => c.setAttribute("aria-pressed", "false"));
          chip.setAttribute("aria-pressed", on ? "true" : "false");
        });
        chips.appendChild(chip);
      });
      const own = document.createElement("input");
      own.type = "text";
      own.placeholder = "自己填（不填就跳过）";
      own.setAttribute("aria-label", q.text);
      chips.appendChild(own);
      box.append(h, chips);
      host.appendChild(box);
    });
  }

  function renderPersonaPreview(d) {
    assistResult = { persona: d.persona, aiNames: d.aiNames || [], streamerTitle: d.streamerTitle || "", supplemented: d.supplemented || [] };
    const box = $("assistPersona");
    box.innerHTML = "";
    const added = new Set(assistResult.supplemented.map((s) => s.trim()));
    assistResult.persona.split("\n").forEach((line) => {
      const span = document.createElement("span");
      span.textContent = line;
      if (added.has(line.trim())) span.className = "added";
      box.append(span, document.createTextNode("\n"));
    });
    text($("assistNames"), assistResult.aiNames.map((n) => (added.has(n) ? `${n}（AI 起的）` : n)).join("、"));
    text($("assistTitle"), assistResult.streamerTitle);
    $("assistPreview").hidden = false;
  }

  function onPersonaAssistant(d) {
    if (d.requestId !== assistRequest) return; // cancelled or superseded
    assistBusy("");
    if (d.stage === "error") {
      assistStatus(d.message, true);
      return;
    }
    if (d.stage === "questions") {
      assistQuestions = d.questions || [];
      if (!assistQuestions.length) { compose(); return; }
      renderQuestions(assistQuestions);
      $("assistAskRow").hidden = false;
      assistStatus("还差几样信息，选一个或自己填，也可以跳过：");
      return;
    }
    if (d.stage === "preview") {
      $("assistQuestions").innerHTML = "";
      $("assistAskRow").hidden = true;
      assistStatus("看看合不合适，可以「用这个」后再在编辑框里改：");
      renderPersonaPreview(d);
    }
  }

  // ── 我的 AI: voice ─────────────────────────────────────────────────────
  const voiceSel = $("voiceSelect");
  const availabilityText = { available: "", unavailable: "（当前不可用）", unknown: "" };

  function selectedChoice() {
    return settings && settings.voice.choices.find((c) => c.choiceId === voiceSel.value);
  }
  function renderVoiceDesc() {
    const c = selectedChoice();
    text($("voiceDesc"), c ? c.description || "" : "");
    const effective = settings && settings.voice.effectiveChoiceId;
    $("btnApplyVoice").disabled = !c || c.choiceId === effective || c.availability === "unavailable";
  }
  voiceSel.addEventListener("change", renderVoiceDesc);

  $("btnPreview").addEventListener("click", () => {
    if (previewActive) { send("stopPreview"); return; }
    if (!voiceSel.value) return;
    text($("previewStatus"), "正在准备试听…");
    send("previewVoice", { choiceId: voiceSel.value });
  });
  $("btnApplyVoice").addEventListener("click", () => {
    if (!voiceSel.value) return;
    text($("voiceState"), "正在应用…");
    saveSettings("voice", { voice: { choiceId: voiceSel.value } });
  });
  $("btnRefreshVoices").addEventListener("click", () => {
    text($("voiceState"), "正在检查…");
    send("refreshVoices");
  });
  const speedEl = $("speed");
  speedEl.addEventListener("input", () => text($("speedValue"), Number(speedEl.value).toFixed(1)));
  speedEl.addEventListener("change", () => {
    text($("voiceState"), "正在应用语速…");
    saveSettings("speed", { voice: { speed: Number(speedEl.value) } });
  });

  function renderPreview(p) {
    if (p.requestId && p.requestId < previewRequest) return; // a newer preview replaced this one
    if (p.requestId) previewRequest = p.requestId;
    previewActive = p.state === "preparing" || p.state === "playing";
    $("btnPreview").textContent = previewActive ? "停止试听" : "试听";
    const msg = p.state === "finished" || p.state === "stopped" ? "" : p.message;
    text($("previewStatus"), [msg, p.diagnosticId ? `诊断编号 ${p.diagnosticId}` : ""].filter(Boolean).join(" · "));
  }

  // ── 直播设置 ───────────────────────────────────────────────────────────
  function fillSelect(el, items, selected, valueOf, labelOf) {
    el.innerHTML = "";
    items.forEach((it) => {
      const o = document.createElement("option");
      o.value = String(valueOf(it));
      o.textContent = labelOf(it);
      if (String(valueOf(it)) === String(selected)) o.selected = true;
      el.appendChild(o);
    });
  }

  function valueAt(key) {
    const [group, field] = key.split(".");
    return settings && settings[group] ? settings[group][field] : undefined;
  }

  function renderLive() {
    const a = settings.audio;
    fillSelect($("inputDevice"), a.inputDevices, a.inputDeviceIndex, (d) => d.index, (d) => d.name);
    fillSelect($("loopSource"), a.loopbackSources.length ? a.loopbackSources : [{ displayName: "全部电脑声音", processName: "" }],
      a.loopbackProcessName, (s) => s.processName, (s) => s.displayName);
    const outs = a.outputDevices.includes(a.virtualMicDeviceName) || !a.virtualMicDeviceName
      ? a.outputDevices : [a.virtualMicDeviceName, ...a.outputDevices];
    fillSelect($("vmicDevice"), [""].concat(outs), a.virtualMicDeviceName, (d) => d, (d) => d || "（未选择）");
    document.querySelectorAll("#page-live [data-key]").forEach((el) => {
      if (el.tagName === "SELECT" || el.type === "password") return;
      const v = valueAt(el.dataset.key);
      if (el.type === "checkbox") el.checked = !!v;
      else el.value = v == null ? "" : String(v);
    });
    $("obsPassword").placeholder = settings.obs.passwordSet ? "已保存（留空不修改）" : "未设置";
    $("avatarCard").hidden = !settings.avatar.usesVts;
    text($("biliQrStatus"), settings.live.bilibiliLoggedIn ? "已绑定" : "");
  }

  $("page-live").addEventListener("input", (e) => {
    if (e.target.closest("[data-key]")) { liveDirty = true; text($("liveState"), "有未保存的修改"); }
  });
  $("page-live").addEventListener("change", (e) => {
    if (e.target.closest("[data-key]")) { liveDirty = true; text($("liveState"), "有未保存的修改"); }
  });
  $("btnSaveLive").addEventListener("click", () => {
    const patch = {};
    document.querySelectorAll("#page-live [data-key]").forEach((el) => {
      const [group, field] = el.dataset.key.split(".");
      let v;
      if (el.type === "checkbox") v = el.checked;
      else if (el.dataset.type === "int") v = parseInt(el.value, 10) || 0;
      else v = el.value;
      if (el.type === "password" && !v) return;
      (patch[group] = patch[group] || {})[field] = v;
    });
    text($("liveState"), "保存中…");
    saveSettings("live", patch);
    $("obsPassword").value = "";
  });
  $("btnDiscardLive").addEventListener("click", () => {
    liveDirty = false;
    text($("liveState"), "");
    send("discardSettings");
  });
  $("btnRefreshDevices").addEventListener("click", () => send("refreshDevices"));
  $("btnBiliQr").addEventListener("click", () => send("startBiliQrLogin"));
  $("btnBiliQrCancel").addEventListener("click", () => send("cancelBiliQrLogin"));

  function renderBiliQr(d) {
    const status = String(d.status || "").toLowerCase();
    const busy = status === "waiting" || status === "scanned";
    $("btnBiliQr").disabled = busy;
    $("btnBiliQrCancel").hidden = !busy;
    const labels = { waiting: "请用 B 站 App 扫码", scanned: "已扫码，请在手机上确认", succeeded: "已绑定", expired: "二维码已过期，请重试", failed: "绑定没有完成，请重试" };
    text($("biliQrStatus"), labels[status] || "");
    const box = $("biliQrBox");
    if (busy && d.qrUrl && typeof qrcode === "function") {
      const qr = qrcode(0, "M");
      qr.addData(d.qrUrl);
      qr.make();
      box.innerHTML = qr.createSvgTag(4, 2);
      box.hidden = false;
    } else {
      box.hidden = true;
      box.innerHTML = "";
    }
  }

  // ── Help ───────────────────────────────────────────────────────────────
  $("btnDiagnostics").addEventListener("click", () => send("copyDiagnostics"));

  // ── Settings projection ────────────────────────────────────────────────
  function renderSettings(s) {
    settings = s;
    if (!personaDirty) {
      personaEl.value = s.persona.systemPrompt;
      personaDirty = personaEl.value !== s.persona.effectiveSystemPrompt;
      if (!personaDirty && $("personaState").textContent === "未修改") setPersonaState("已生效", "ok");
    }
    const current = voiceSel.value || s.voice.selectedChoiceId;
    fillSelect(voiceSel, s.voice.choices, current, (c) => c.choiceId,
      (c) => `${c.displayName}${availabilityText[c.availability] || ""}${c.choiceId === s.voice.effectiveChoiceId ? "（正在使用）" : ""}`);
    if (!s.voice.choices.length) {
      text($("voiceDesc"), "这个安装包没有可选音色，请联系发放者更新配置。");
    } else renderVoiceDesc();
    const eff = s.voice.choices.find((c) => c.choiceId === s.voice.effectiveChoiceId);
    text($("effectiveVoice"), eff ? eff.displayName : "默认音色");
    const notice = $("voiceNotice");
    notice.hidden = !s.voice.notice;
    text(notice, s.voice.notice);
    if (document.activeElement !== speedEl) {
      speedEl.value = s.voice.speed;
      text($("speedValue"), Number(s.voice.speed).toFixed(1));
    }
    if (!liveDirty) renderLive();
  }

  function onSaveResult(r) {
    const kind = pendingKind[r.requestId];
    delete pendingKind[r.requestId];
    if (!kind || latestSave[kind] !== r.requestId) return; // an older reply; a newer save is in flight or done
    const msg = r.ok ? "已生效" : r.message || r.stateText || "没有保存";
    if (kind === "persona") {
      if (r.ok) { personaDirty = false; pendingIdentity = null; }
      setPersonaState(r.ok ? "已生效（下一句开始使用）" : `应用失败：${msg}`, r.ok ? "ok" : "bad");
    } else if (kind === "voice" || kind === "speed") {
      text($("voiceState"), r.ok ? "已生效，下一句开始使用" : msg);
    } else if (kind === "live") {
      if (r.ok) liveDirty = false;
      text($("liveState"), r.ok ? "已保存并生效" : msg);
    }
  }

  // ── Host → page ────────────────────────────────────────────────────────
  function onHostMessage(event) {
    const msg = event.data;
    if (!msg || !msg.type) return;
    const d = msg.data || {};
    switch (msg.type) {
      case "state": renderState(d); break;
      case "settings": renderSettings(d); break;
      case "saveResult": onSaveResult(d); break;
      case "preview": renderPreview(d); break;
      case "biliQr": renderBiliQr(d); break;
      case "personaAssistant": onPersonaAssistant(d); break;
      case "result":
        if (d.kind === "voices") text($("voiceState"), [d.message, d.diagnosticId ? `诊断编号 ${d.diagnosticId}` : ""].filter(Boolean).join(" · "));
        else if (d.kind === "diagnostics") text($("diagStatus"), d.message);
        else if (d.kind === "error") alertBox(d);
        break;
    }
  }

  function alertBox(d) {
    renderIssues([{ area: d.area || "app", message: d.message, action: d.action, diagnosticId: d.diagnosticId }]
      .concat((state && state.issues) || []));
  }

  if (hasWebView) chrome.webview.addEventListener("message", onHostMessage);
  else window.__streamerHostMessage = (msg) => onHostMessage({ data: msg }); // browser preview only
  post({ type: "ready" });
  send("getSettings");
})();
