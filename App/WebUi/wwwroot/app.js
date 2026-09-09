(() => {
  const $ = (id) => document.getElementById(id);
  const hasWebView = !!(window.chrome && chrome.webview);

  let emotionRows = [];
  let actionRows = [];
  let suppressPatch = false;

  function post(msg) {
    if (hasWebView) chrome.webview.postMessage(msg);
  }

  function send(name, data) {
    const msg = { type: "command", name };
    if (data !== undefined) msg.data = data;
    post(msg);
  }

  function setPill(el, on, label) {
    el.classList.toggle("on", !!on);
    el.textContent = `${label} ${on ? "已连接" : "未连接"}`;
  }

  function dash(v) {
    return v && String(v).trim() ? v : "—";
  }

  function ms(v) {
    return v == null ? "—" : String(v);
  }

  function esc(s) {
    return String(s)
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;")
      .replaceAll('"', "&quot;");
  }

  function getByPath(obj, path) {
    return path.split(".").reduce((o, k) => (o == null ? undefined : o[k]), obj);
  }

  function nestPath(path, value) {
    const parts = path.split(".");
    const root = {};
    let cur = root;
    for (let i = 0; i < parts.length - 1; i++) {
      cur[parts[i]] = {};
      cur = cur[parts[i]];
    }
    cur[parts[parts.length - 1]] = value;
    return root;
  }

  function readField(el) {
    const type = el.dataset.type || "string";
    if (el.type === "checkbox" || type === "bool") return !!el.checked;
    if (type === "int") {
      const n = parseInt(el.value, 10);
      return Number.isFinite(n) ? n : 0;
    }
    if (type === "float") {
      const n = parseFloat(el.value);
      return Number.isFinite(n) ? n : 0;
    }
    return el.value;
  }

  function setField(el, value) {
    if (el.type === "checkbox" || el.dataset.type === "bool") {
      el.checked = !!value;
      return;
    }
    if (el.dataset.path === "interaction.wakeKeywords" && Array.isArray(value)) {
      el.value = value.join(", ");
      return;
    }
    if (value == null) {
      el.value = "";
      return;
    }
    el.value = String(value);
  }

  function fillSelect(el, items, selected, valueKey, labelKey) {
    if (!el) return;
    const opts = items || [];
    el.innerHTML = opts
      .map((it, i) => {
        if (typeof it === "string") {
          const sel = it === selected || i === selected ? " selected" : "";
          return `<option value="${esc(it)}"${sel}>${esc(it)}</option>`;
        }
        const val = valueKey ? it[valueKey] : it.value ?? it.processName ?? it;
        const lab = labelKey ? it[labelKey] : it.displayName ?? it.label ?? val;
        const sel = String(val) === String(selected) ? " selected" : "";
        return `<option value="${esc(String(val ?? ""))}"${sel}>${esc(String(lab ?? ""))}</option>`;
      })
      .join("");
  }

  function renderMaps() {
    const eList = $("emotionList");
    const aList = $("actionList");
    eList.innerHTML = emotionRows
      .map(
        (r, i) =>
          `<div class="map-row" data-kind="emotion" data-index="${i}">` +
          `<input data-k="key" value="${esc(r.key || r.emotion || "")}" placeholder="情绪别名" />` +
          `<input data-k="id" value="${esc(r.id || r.hotkeyId || "")}" placeholder="热键 ID" />` +
          `<button type="button" class="btn ghost sm" data-rm="emotion" data-i="${i}">删</button></div>`
      )
      .join("");
    aList.innerHTML = actionRows
      .map(
        (r, i) =>
          `<div class="map-row" data-kind="action" data-index="${i}">` +
          `<input data-k="key" value="${esc(r.key || r.action || "")}" placeholder="动作别名" />` +
          `<input data-k="id" value="${esc(r.id || r.hotkeyId || "")}" placeholder="热键 ID" />` +
          `<button type="button" class="btn ghost sm" data-rm="action" data-i="${i}">删</button></div>`
      )
      .join("");
  }

  function mapsFromDict(dict) {
    if (!dict) return [];
    if (Array.isArray(dict)) {
      return dict.map((r) => ({
        key: r.key ?? r.emotion ?? r.action ?? "",
        id: r.id ?? r.hotkeyId ?? "",
      }));
    }
    return Object.entries(dict).map(([key, id]) => ({ key, id: String(id) }));
  }

  function populateConfig(data) {
    suppressPatch = true;
    const draft = data.draft || data.config || data;
    const devices = data.inputDevices || draft.inputDevices || [];
    const loops = data.loopbackSources || [];
    const outputs = data.outputDevices || [];

    const inputSel = $("audio.inputDeviceIndex");
    if (devices.length) {
      inputSel.innerHTML = devices
        .map((d, i) => `<option value="${i}">${esc(typeof d === "string" ? d : d.name || String(i))}</option>`)
        .join("");
      const idx = getByPath(draft, "audio.inputDeviceIndex");
      if (idx != null) inputSel.value = String(idx);
    }

    fillSelect(
      $("audio.loopbackProcessName"),
      loops.length ? loops : [{ displayName: "全部音源（扬声器混音）", processName: "" }],
      getByPath(draft, "audio.loopbackProcessName") ?? "",
      "processName",
      "displayName"
    );
    fillSelect(
      $("audio.virtualMicDeviceName"),
      outputs,
      getByPath(draft, "audio.virtualMicDeviceName") ?? "",
      null,
      null
    );

    document.querySelectorAll("[data-path]").forEach((el) => {
      if (el.id === "audio.inputDeviceIndex" && devices.length) return;
      if (el.id === "audio.loopbackProcessName" && loops.length) return;
      if (el.id === "audio.virtualMicDeviceName" && outputs.length) return;
      let v = getByPath(draft, el.dataset.path);
      if (el.dataset.path === "interaction.isPkMode" && v == null) {
        const mode = getByPath(draft, "interaction.mode");
        v = String(mode || "").toLowerCase() === "pk";
      }
      if (v !== undefined) setField(el, v);
    });

    const vts = draft.vts || {};
    emotionRows = mapsFromDict(data.emotionMap || data.emotionRows || vts.emotionMap);
    actionRows = mapsFromDict(data.actionMap || data.actionRows || vts.actionMap);
    renderMaps();

    if (data.status) setSaveStatus(data.status, data.ok === false ? "err" : "");
    markBiliSecrets(draft.bilibili || {});
    markApiKeySecrets(draft);
    suppressPatch = false;
  }

  function markApiKeySecrets(draft) {
    [
      ["llm.apiKey", !!(draft.llm && draft.llm.apiKeySet)],
      ["asr.apiKey", !!(draft.asr && draft.asr.apiKeySet)],
      ["tts.apiKey", !!(draft.tts && draft.tts.apiKeySet)],
    ].forEach(([id, saved]) => {
      const el = $(id);
      if (!el) return;
      el.placeholder = saved ? "已保存" : "留空不修改";
      const hint = el.closest(".field")?.querySelector(".hint");
      if (hint) hint.textContent = saved ? "已保存" : "留空不修改";
    });
  }

  function markBiliSecrets(bili) {
    [
      ["bilibili.sessdata", !!(bili.sessdataSet || bili.sessdata)],
      ["bilibili.biliJct", !!(bili.biliJctSet || bili.biliJct)],
      ["bilibili.buvid3", !!(bili.buvid3Set || bili.buvid3)],
    ].forEach(([id, saved]) => {
      const el = $(id);
      if (!el) return;
      el.placeholder = saved ? "已保存" : "留空不修改";
      const hint = el.closest(".field")?.querySelector(".hint");
      if (hint) hint.textContent = saved ? "已保存" : "留空不修改";
    });
  }

  function patchFromEl(el) {
    if (suppressPatch || !el.dataset.path) return;
    let value = readField(el);
    if (el.dataset.path === "interaction.wakeKeywords") {
      value = String(el.value || "")
        .split(/[,，]/)
        .map((s) => s.trim())
        .filter(Boolean);
    }
    send("patchConfig", nestPath(el.dataset.path, value));
  }

  function setSaveStatus(text, cls) {
    const el = $("saveStatus");
    el.textContent = text || "";
    el.classList.remove("ok", "err");
    if (cls) el.classList.add(cls);
  }

  function renderBiliQr(data) {
    const status = String(data.status || "").toLowerCase();
    const box = $("biliQrBox");
    const label = $("biliQrStatus");
    const start = $("btnBiliQr");
    const cancel = $("btnBiliQrCancel");
    const busy = status === "waiting" || status === "scanned";
    start.disabled = busy;
    cancel.hidden = !busy;
    label.classList.remove("ok", "err");
    const texts = {
      waiting: "等待扫码",
      scanned: "已扫码",
      succeeded: "已登录",
      expired: "已过期",
      failed: data.error || "失败",
      cancelled: "",
      idle: "",
    };
    label.textContent = texts[status] ?? "";
    if (status === "succeeded") {
      label.classList.add("ok");
      if (data.sessdata) $("bilibili.sessdata").value = data.sessdata;
      if (data.biliJct) $("bilibili.biliJct").value = data.biliJct;
      if (data.buvid3) $("bilibili.buvid3").value = data.buvid3;
      markBiliSecrets({
        sessdata: data.sessdata,
        biliJct: data.biliJct,
        buvid3: data.buvid3,
        sessdataSet: !!data.sessdata,
        biliJctSet: !!data.biliJct,
        buvid3Set: !!data.buvid3,
      });
    }
    if (status === "expired" || status === "failed") label.classList.add("err");

    const url = data.qrUrl;
    if (busy && url && typeof qrcode === "function") {
      box.hidden = false;
      const qr = qrcode(0, "M");
      qr.addData(url);
      qr.make();
      box.innerHTML = qr.createSvgTag(4, 2);
    } else {
      box.hidden = true;
      box.innerHTML = "";
    }
  }

  function renderState(data) {
    const label = data.stateLabel || "空闲";
    const st = $("stateLabel");
    st.textContent = label;
    st.classList.toggle("on", label === "正在监听" || label === "正在说话");

    $("userText").textContent = dash(data.userText);
    $("assistantText").textContent = dash(data.assistantText);
    $("opponentText").textContent = dash(data.opponentText);
    $("emotion").textContent = dash(data.emotion);
    $("userEmotion").textContent = dash(data.userEmotion);

    const err = $("lastError");
    if (data.lastError) {
      err.hidden = false;
      err.textContent = data.lastError;
    } else {
      err.hidden = true;
      err.textContent = "";
    }

    setPill($("pillVts"), data.vtsConnected, "VTS");
    setPill($("pillObs"), data.obsConnected, "OBS");
    setPill($("pillDanmaku"), data.danmakuActive, "弹幕");
    setPill($("pillAsr"), data.localAsrActive ? !!data.localAsrReachable : false, "ASR");

    $("modeLabel").innerHTML = data.isPkMode ? "模式 <b>PK</b>" : "模式 <b>正常</b>";
    $("btnStop").disabled = !data.canStop;

    const mic = $("btnMic");
    mic.textContent = data.micMuted ? "静音中" : "麦克风";
    mic.classList.toggle("muted", !!data.micMuted);

    const pk = $("btnPk");
    pk.textContent = data.isPkMode ? "PK 模式" : "正常模式";
    pk.classList.toggle("pk", !!data.isPkMode);

    const micPct = Math.min(100, Math.round((data.micLevel || 0) * 100));
    const loopPct = Math.min(100, Math.round((data.loopbackLevel || 0) * 100));
    $("micBar").style.width = `${micPct}%`;
    $("loopBar").style.width = `${loopPct}%`;
    $("micPct").textContent = `${micPct}%`;
    $("loopPct").textContent = `${loopPct}%`;
    $("latAsr").textContent = ms(data.asrLatencyMs);
    $("latLlm").textContent = ms(data.llmLatencyMs);
    $("latTts").textContent = ms(data.ttsLatencyMs);
    $("danmakuCount").textContent = data.danmakuQueueCount
      ? `弹幕队列 ${data.danmakuQueueCount}`
      : "";

    $("eventList").innerHTML = (data.events || [])
      .map(
        (ev) =>
          `<div class="event${ev.isError ? " err" : ""}">` +
          `<span class="t">${esc(ev.time || "")}</span>` +
          `<span class="s">${esc(ev.source || "")}</span>` +
          `<span class="m">${esc(ev.message || "")}</span></div>`
      )
      .join("");
  }

  // Rail navigation
  document.querySelectorAll(".rail button[data-page]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const page = btn.dataset.page;
      document.querySelectorAll(".rail button[data-page]").forEach((b) => b.classList.remove("active"));
      btn.classList.add("active");
      document.querySelectorAll(".page").forEach((p) => p.classList.remove("active"));
      $(`page-${page}`).classList.add("active");
      if (page === "memory") send("getMemory");
      if (page === "settings") send("getConfig");
    });
  });

  // Memory page
  function renderMemory(data) {
    const d = data || {};
    $("factsLoading").hidden = !d.factsLoading;
    $("viewersLoading").hidden = !d.viewersLoading;
    $("pkLoading").hidden = !d.pkLoading;
    $("factsEmpty").hidden = !d.factsEmpty;
    $("viewersEmpty").hidden = !d.viewersEmpty;
    $("pkEmpty").hidden = !d.pkEmpty;
    $("factsError").hidden = !d.factsError;
    $("factsError").textContent = d.factsError || "";
    $("viewersError").hidden = !d.viewersError;
    $("viewersError").textContent = d.viewersError || "";
    $("pkError").hidden = !d.pkError;
    $("pkError").textContent = d.pkError || "";
    $("memStatus").textContent = d.statusMessage || "";
    $("btnMemExtract").disabled = !!d.extracting;
    if (typeof d.factSearch === "string" && document.activeElement !== $("factSearch"))
      $("factSearch").value = d.factSearch;
    if (typeof d.viewerSearch === "string" && document.activeElement !== $("viewerSearch"))
      $("viewerSearch").value = d.viewerSearch;
    if (typeof d.pkSearch === "string" && document.activeElement !== $("pkSearch"))
      $("pkSearch").value = d.pkSearch;

    $("factRows").innerHTML = (d.facts || [])
      .map(
        (f) =>
          `<tr>` +
          `<td class="fact-content">${esc(f.content || "")}</td>` +
          `<td>${esc(f.importanceStars || String(f.importance ?? ""))}</td>` +
          `<td>${esc(f.subjectUid || "—")}</td>` +
          `<td>${esc(f.lastAccessed || "—")}</td>` +
          `<td>${esc(f.expires || "—")}</td>` +
          `<td><button type="button" class="btn ghost sm" data-del-fact="${esc(f.id || "")}">删</button></td>` +
          `</tr>`
      )
      .join("");

    $("viewerRows").innerHTML = (d.viewers || [])
      .map(
        (v) =>
          `<tr>` +
          `<td>${esc(v.nickname || "—")}</td>` +
          `<td>${esc(v.uid || "")}</td>` +
          `<td>${esc(String(v.interactionCount ?? 0))}</td>` +
          `<td>${esc(v.platform || "")}</td>` +
          `<td>${esc(v.lastSeen || "—")}</td>` +
          `</tr>`
      )
      .join("");

    $("pkRows").innerHTML = (d.pkTurns || [])
      .map(
        (t) =>
          `<tr>` +
          `<td>${esc(t.opponentName || "—")}<div class="mem-uid">${esc(t.opponentUid || "")}</div></td>` +
          `<td class="fact-content">${esc(t.opponentText || "")}</td>` +
          `<td class="fact-content">${esc(t.assistantText || "")}</td>` +
          `<td>${esc(t.ts || "—")}</td>` +
          `<td><button type="button" class="btn ghost sm" data-del-pk="${esc(t.id || "")}">删</button></td>` +
          `</tr>`
      )
      .join("");
  }

  document.querySelectorAll("[data-mem-tab]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const tab = btn.dataset.memTab;
      document.querySelectorAll("[data-mem-tab]").forEach((b) => b.classList.remove("active"));
      btn.classList.add("active");
      $("mem-pk").classList.toggle("active", tab === "pk");
      $("mem-facts").classList.toggle("active", tab === "facts");
      $("mem-viewers").classList.toggle("active", tab === "viewers");
      send("memoryTab", { tab });
    });
  });
  $("btnMemRefresh").addEventListener("click", () => send("refreshMemory"));
  $("btnMemExtract").addEventListener("click", () => {
    $("memStatus").textContent = "正在提取…";
    send("extractMemory");
  });
  let factSearchTimer = 0;
  let viewerSearchTimer = 0;
  let pkSearchTimer = 0;
  $("factSearch").addEventListener("input", () => {
    clearTimeout(factSearchTimer);
    factSearchTimer = setTimeout(
      () => send("setFactSearch", { query: $("factSearch").value }),
      275
    );
  });
  $("viewerSearch").addEventListener("input", () => {
    clearTimeout(viewerSearchTimer);
    viewerSearchTimer = setTimeout(
      () => send("setViewerSearch", { query: $("viewerSearch").value }),
      275
    );
  });
  $("pkSearch").addEventListener("input", () => {
    clearTimeout(pkSearchTimer);
    pkSearchTimer = setTimeout(
      () => send("setPkSearch", { query: $("pkSearch").value }),
      275
    );
  });
  $("pkRows").addEventListener("click", (e) => {
    const btn = e.target.closest("[data-del-pk]");
    if (!btn) return;
    const id = btn.getAttribute("data-del-pk");
    if (!id) return;
    if (!confirm("删除这条对话？")) return;
    send("deletePkTurn", { id });
  });
  $("factRows").addEventListener("click", (e) => {
    const btn = e.target.closest("[data-del-fact]");
    if (!btn) return;
    const id = btn.getAttribute("data-del-fact");
    if (!id) return;
    if (!confirm("删除这条记忆？")) return;
    send("deleteFact", { id });
  });

  // Accordion single-open
  document.querySelectorAll("details.acc").forEach((d) => {
    d.addEventListener("toggle", () => {
      if (!d.open) return;
      document.querySelectorAll("details.acc").forEach((o) => {
        if (o !== d) o.open = false;
      });
    });
  });

  // Monitor commands
  $("btnStop").addEventListener("click", () => send("stopSpeaking"));
  $("btnMic").addEventListener("click", () => send("toggleMic"));
  $("btnPk").addEventListener("click", () => send("togglePk"));
  $("btnNewPk").addEventListener("click", () => send("newPk"));
  $("btnRestartAsr").addEventListener("click", () => send("restartLocalAsr"));

  // Settings dock / refresh
  $("btnSave").addEventListener("click", () => {
    setSaveStatus("保存中…");
    const patch = {};
    document.querySelectorAll("[data-path]").forEach((el) => {
      let value = readField(el);
      if (el.dataset.path === "interaction.wakeKeywords") {
        value = String(el.value || "")
          .split(/[,，]/)
          .map((s) => s.trim())
          .filter(Boolean);
      }
      const nested = nestPath(el.dataset.path, value);
      deepMerge(patch, nested);
    });
    // Include map rows as arrays the host understands.
    patch.emotionRows = emotionRows.map((r) => ({ emotion: r.key, hotkeyId: r.id }));
    patch.actionRows = actionRows.map((r) => ({ action: r.key, hotkeyId: r.id }));
    send("saveConfig", patch);
  });

  function deepMerge(target, src) {
    for (const [k, v] of Object.entries(src)) {
      if (v && typeof v === "object" && !Array.isArray(v)) {
        target[k] = target[k] || {};
        deepMerge(target[k], v);
      } else {
        target[k] = v;
      }
    }
  }
  $("btnDiscard").addEventListener("click", () => {
    setSaveStatus("已放弃");
    send("discardConfig");
  });
  $("btnRefreshLoopback").addEventListener("click", () => send("refreshLoopback"));
  $("btnRefreshOutputs").addEventListener("click", () => send("refreshOutputs"));
  $("btnQueryHotkeys").addEventListener("click", () => send("queryVtsHotkeys"));
  $("btnAddEmotion").addEventListener("click", () => send("addEmotion"));
  $("btnAddAction").addEventListener("click", () => send("addAction"));
  $("btnImportAnimations").addEventListener("click", () => send("importAnimations"));
  $("btnBiliQr").addEventListener("click", () => send("startBiliQrLogin"));
  $("btnBiliQrCancel").addEventListener("click", () => send("cancelBiliQrLogin"));

  // Field changes → patchConfig
  document.getElementById("setScroll").addEventListener("change", (e) => {
    const el = e.target.closest("[data-path]");
    if (el) patchFromEl(el);
  });
  document.getElementById("setScroll").addEventListener(
    "input",
    (e) => {
      const el = e.target.closest("[data-path]");
      if (!el || el.tagName === "SELECT" || el.type === "checkbox") return;
      patchFromEl(el);
    },
    { passive: true }
  );

  // Emotion / action map edits & removes
  $("emotionList").addEventListener("click", (e) => {
    const btn = e.target.closest("[data-rm='emotion']");
    if (!btn) return;
    send("removeEmotion", { index: Number(btn.dataset.i) });
  });
  $("actionList").addEventListener("click", (e) => {
    const btn = e.target.closest("[data-rm='action']");
    if (!btn) return;
    send("removeAction", { index: Number(btn.dataset.i) });
  });
  function onMapInput(e) {
    const row = e.target.closest(".map-row");
    if (!row || !e.target.matches("input")) return;
    const kind = row.dataset.kind;
    const index = Number(row.dataset.index);
    const key = row.querySelector('[data-k="key"]').value;
    const id = row.querySelector('[data-k="id"]').value;
    const path = kind === "emotion" ? "vts.emotionMap" : "vts.actionMap";
    const rows = kind === "emotion" ? emotionRows : actionRows;
    if (rows[index]) {
      rows[index] = { key, id };
    }
    const dict = {};
    rows.forEach((r) => {
      if (r.key && r.id) dict[r.key] = r.id;
    });
    send("patchConfig", nestPath(path, dict));
  }
  $("emotionList").addEventListener("change", onMapInput);
  $("actionList").addEventListener("change", onMapInput);

  // Host → SPA
  function onHostMessage(event) {
    const msg = event.data;
    if (!msg || !msg.type) return;
    if (msg.type === "state") renderState(msg.data || {});
    else if (msg.type === "config") populateConfig(msg.data || {});
    else if (msg.type === "memory") renderMemory(msg.data || {});
    else if (msg.type === "biliQr") renderBiliQr(msg.data || {});
    else if (msg.type === "result") {
      const d = msg.data || {};
      const ok = d.ok !== false && !d.error;
      if (d.kind === "extract") {
        $("memStatus").textContent = d.message || (ok ? "提取完成" : "提取失败");
        return;
      }
      setSaveStatus(d.message || d.status || (ok ? "已保存" : "失败"), ok ? "ok" : "err");
      if (d.config || d.draft) populateConfig(d);
    }
  }

  if (hasWebView) {
    chrome.webview.addEventListener("message", onHostMessage);
  }

  post({ type: "ready" });
  send("getConfig");
})();
