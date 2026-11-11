(() => {
  "use strict";

  const $ = (selector) => document.querySelector(selector);

  const TOKEN = document.querySelector('meta[name="lanchat-token"]')?.content || "";

  const apiFetch = (url, options = {}) => {
    const headers = new Headers(options.headers || {});
    if (TOKEN) headers.set("X-LanChat-Token", TOKEN);
    return fetch(url, { ...options, headers });
  };

  const els = {
    selfName: $("#self-name"),
    roomList: $("#room-list"),
    peerList: $("#peer-list"),
    recentList: $("#recent-list"),
    onlineLabel: $("#online-label"),
    recentLabel: $("#recent-label"),
    netStatus: $("#net-status"),
    netDot: $("#net-dot"),
    chatAvatar: $("#chat-avatar"),
    chatName: $("#chat-name"),
    chatSubtitle: $("#chat-subtitle"),
    lockPill: $("#lock-pill"),
    messages: $("#messages"),
    emptyState: $("#empty-state"),
    input: $("#input"),
    sendBtn: $("#send-btn"),
    attachBtn: $("#attach-btn"),
    fileInput: $("#file-input"),
    themeBtn: $("#theme-btn"),
    clearBtn: $("#clear-btn"),
    settingsBtn: $("#settings-btn"),
    settingsModal: $("#settings-modal"),
    setName: $("#set-name"),
    setPassword: $("#set-password"),
    removePass: $("#remove-pass"),
    removePassRow: $("#remove-pass-row"),
    encHint: $("#enc-hint"),
    setTheme: $("#set-theme"),
    settingsMeta: $("#settings-meta"),
    settingsSave: $("#settings-save"),
    settingsCancel: $("#settings-cancel"),
    lightbox: $("#lightbox"),
    lightboxImg: $("#lightbox-img"),
    toasts: $("#toasts"),
    dropOverlay: $("#drop-overlay")
  };

  const state = {
    selfId: "",
    selfName: "",
    config: {},
    peers: [],
    conversations: new Map(),
    active: "group",
    unread: new Map(),
    seen: new Set(),
    sse: null,
    atBottom: true,
    dragDepth: 0
  };

  const esc = (value) =>
    String(value ?? "").replace(/[&<>"']/g, (c) => ({
      "&": "&amp;",
      "<": "&lt;",
      ">": "&gt;",
      '"': "&quot;",
      "'": "&#39;"
    }[c]));

  const linkify = (text) =>
    esc(text).replace(/(https?:\/\/[^\s<]+)/g, '<a href="$1" rel="noreferrer">$1</a>');

  const initials = (name) => {
    const clean = String(name || "?").trim();
    if (!clean) return "?";
    const parts = clean.split(/\s+/);
    if (parts.length > 1) return (parts[0][0] + parts[1][0]).toUpperCase();
    return clean.slice(0, 2).toUpperCase();
  };

  const hueFor = (id) => {
    let hash = 0;
    const text = String(id || "");
    for (let i = 0; i < text.length; i++) hash = (hash * 31 + text.charCodeAt(i)) | 0;
    return Math.abs(hash) % 360;
  };

  const fmtTime = (ts) =>
    new Date(ts).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });

  const dayKey = (ts) => {
    const d = new Date(ts);
    return `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;
  };

  const fmtDay = (ts) => {
    const now = new Date();
    const yesterday = new Date(now);
    yesterday.setDate(now.getDate() - 1);
    if (dayKey(ts) === dayKey(now.getTime())) return "Today";
    if (dayKey(ts) === dayKey(yesterday.getTime())) return "Yesterday";
    return new Date(ts).toLocaleDateString([], { weekday: "long", month: "short", day: "numeric" });
  };

  const fmtSize = (bytes) => {
    const n = Number(bytes) || 0;
    if (n < 1024) return `${n} B`;
    if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
    if (n < 1024 * 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`;
    return `${(n / 1024 / 1024 / 1024).toFixed(2)} GB`;
  };

  const postToHost = (message) => {
    try {
      window.chrome?.webview?.postMessage(message);
    } catch {
      /* running in a plain browser */
    }
  };

  const toast = (text, type = "", timeout = 4200) => {
    const el = document.createElement("div");
    el.className = "toast" + (type ? " " + type : "");
    el.textContent = text;
    els.toasts.appendChild(el);
    if (timeout) setTimeout(() => el.remove(), timeout);
    return el;
  };

  const setToast = (el, text, isError = false) => {
    el.textContent = text;
    el.classList.toggle("error", isError);
  };

  const beep = () => {
    try {
      const ctx = new (window.AudioContext || window.webkitAudioContext)();
      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.type = "sine";
      osc.frequency.value = 740;
      gain.gain.value = 0.035;
      osc.connect(gain).connect(ctx.destination);
      osc.start();
      gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.16);
      osc.stop(ctx.currentTime + 0.17);
      setTimeout(() => ctx.close(), 400);
    } catch {
      /* ignore */
    }
  };

  const applyTheme = (theme) => {
    const value = theme === "light" ? "light" : "dark";
    document.documentElement.dataset.theme = value;
    state.config.theme = value;
    try {
      localStorage.setItem("lanchat.theme", value);
    } catch {
      /* ignore */
    }
    postToHost({ type: "theme", theme: value });
  };

  const ensureConversation = (key, name) => {
    let conv = state.conversations.get(key);
    if (!conv) {
      conv = { id: key, name: name || (key === "group" ? "LAN Room" : key), messages: [] };
      state.conversations.set(key, conv);
    } else if (name) {
      conv.name = name;
    }
    return conv;
  };

  const addRaw = (key, message) => {
    if (!message || !message.id || state.seen.has(message.id)) return false;
    state.seen.add(message.id);
    const conv = ensureConversation(key);
    if (key !== "group") {
      if (message.fromId !== state.selfId && message.fromName) conv.name = message.fromName;
      else if (message.toName) conv.name = message.toName;
    }
    conv.messages.push(message);
    return true;
  };

  const notifyIncoming = (key, message) => {
    const conv = state.conversations.get(key);
    const title = conv?.name || "LAN Chat";
    let body;
    if (message.kind === "image") body = "sent an image";
    else if (message.kind === "file") body = `sent a file: ${message.fileName || "file"}`;
    else body = message.text || "New message";
    if (key === "group") body = `${message.fromName}: ${body}`;
    postToHost({ type: "notify", title, body });
    beep();
  };

  const addMessage = (key, message) => {
    if (!addRaw(key, message)) return;
    if (message.fromId !== state.selfId) {
      const focused = document.hasFocus();
      if (key === state.active && focused) {
        state.unread.delete(key);
      } else if (key === state.active && !focused) {
        notifyIncoming(key, message);
      } else {
        state.unread.set(key, (state.unread.get(key) || 0) + 1);
        notifyIncoming(key, message);
      }
    }

    if (key === state.active) {
      renderMessages();
    }
    renderSidebar();
    updateHeader();
  };

  const conversationName = (key) => {
    if (key === "group") return "LAN Room";
    const conv = state.conversations.get(key);
    if (conv && conv.name) return conv.name;
    const peer = state.peers.find((p) => p.id === key);
    return peer ? peer.name : "Unknown";
  };

  const isOnline = (key) => state.peers.some((p) => p.id === key);

  const renderSelf = () => {
    els.selfName.textContent = state.selfName || "You";
  };

  const convItem = (key, name, subtitle, options = {}) => {
    const active = key === state.active ? "active" : "";
    const unread = state.unread.get(key) || 0;
    const room = key === "group";
    const hue = room ? 220 : hueFor(key);
    return `
      <div class="conv ${active}" data-key="${esc(key)}" title="${esc(name)}">
        <div class="avatar ${room ? "room" : ""}" style="--hue:${hue}">${room ? "#" : esc(initials(name))}</div>
        <div class="conv-info">
          <div class="conv-name">${esc(name)}</div>
          <div class="conv-sub">${subtitle}</div>
        </div>
        ${unread ? `<span class="badge">${unread > 99 ? "99+" : unread}</span>` : ""}
      </div>`;
  };

  const renderSidebar = () => {
    const onlineText = state.peers.length === 0
      ? "Only you online"
      : `${state.peers.length} online`;

    els.roomList.innerHTML = convItem(
      "group",
      "LAN Room",
      `<span class="status-dot online"></span>${onlineText}`
    );

    els.onlineLabel.textContent = `Online${state.peers.length ? " — " + state.peers.length : ""}`;
    els.peerList.innerHTML = state.peers.length
      ? state.peers
          .slice()
          .sort((a, b) => a.name.localeCompare(b.name))
          .map((peer) =>
            convItem(peer.id, peer.name, '<span class="status-dot online"></span>Online')
          )
          .join("")
      : '<div class="section-empty">No one else is online yet</div>';

    const onlineIds = new Set(state.peers.map((p) => p.id));
    const recents = [...state.conversations.values()].filter(
      (c) => c.id !== "group" && !onlineIds.has(c.id) && c.messages.length > 0
    );
    recents.sort((a, b) => (b.messages.at(-1)?.ts || 0) - (a.messages.at(-1)?.ts || 0));
    els.recentLabel.hidden = recents.length === 0;
    els.recentList.innerHTML = recents
      .map((c) => convItem(c.id, c.name, '<span class="status-dot offline"></span>Offline'))
      .join("");
  };

  const emptyStateHtml = () => {
    if (state.active === "group") {
      return `
        <div class="empty-state">
          <div class="empty-art">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.2"><path d="M4 6.5A2.5 2.5 0 0 1 6.5 4h11A2.5 2.5 0 0 1 20 6.5v7a2.5 2.5 0 0 1-2.5 2.5H9.6L5.2 19.6A1 1 0 0 1 3.6 18.8V15"/><path d="M8 9h8M8 12h5"/></svg>
          </div>
          <h3>Your LAN room is ready</h3>
          <p>Anyone running LAN Chat on this network will appear here automatically.</p>
        </div>`;
    }

    const name = esc(conversationName(state.active));
    const online = isOnline(state.active);
    return `
      <div class="empty-state">
        <div class="empty-art">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.2"><path d="M12 21a9 9 0 1 0-9-9"/><path d="M3 21v-5h5"/><path d="M8.5 12h.01M12 12h.01M15.5 12h.01"/></svg>
        </div>
        <h3>No messages yet</h3>
        <p>Say hi to ${name}. ${online ? "They're online right now." : "They're offline, but your message history is kept here."}</p>
      </div>`;
  };

  const fileIcon = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8l-5-5Z"/><path d="M14 3v5h5"/></svg>`;

  const messageHtml = (message) => {
    if (message.kind === "system") {
      return `<div class="system-row"><div class="system-pill">${esc(message.text)}</div></div>`;
    }

    const mine = message.fromId === state.selfId;
    const showSender = state.active === "group" && !mine;
    const hue = hueFor(message.fromId);
    const avatar = mine
      ? ""
      : `<div class="avatar sm msg-avatar" style="--hue:${hue}">${esc(initials(message.fromName))}</div>`;

    let bubble;
    if (message.kind === "image" && message.fileId) {
      const url = `/api/file/${encodeURIComponent(message.fileId)}`;
      bubble = `
        <div class="bubble image-bubble">
          <img class="msg-image" src="${url}" data-full="${url}" alt="${esc(message.fileName || "image")}" loading="lazy">
          <div class="image-caption">${esc(message.fileName || "image")}</div>
        </div>`;
    } else if (message.kind === "file" && message.fileId) {
      bubble = `
        <div class="bubble">
          <div class="file-card">
            <div class="file-icon">${fileIcon}</div>
            <div class="file-info">
              <div class="file-name">${esc(message.fileName || "file")}</div>
              <div class="file-size">${fmtSize(message.size)}</div>
              <div class="file-actions">
                <button class="file-btn" data-open="${esc(message.fileId)}">Open</button>
                <button class="file-btn" data-reveal="${esc(message.fileId)}">Show in folder</button>
              </div>
            </div>
          </div>
        </div>`;
    } else {
      bubble = `<div class="bubble">${linkify(message.text || "")}</div>`;
    }

    return `
      <div class="msg-row ${mine ? "mine" : ""}">
        ${avatar}
        <div class="msg-stack">
          ${showSender ? `<div class="msg-sender">${esc(message.fromName)}</div>` : ""}
          ${bubble}
          <div class="msg-time">${fmtTime(message.ts)}</div>
        </div>
      </div>`;
  };

  const renderMessages = (forceBottom = false) => {
    const conv = state.conversations.get(state.active);
    const shouldStick = forceBottom || state.atBottom;

    if (!conv || conv.messages.length === 0) {
      els.messages.innerHTML = emptyStateHtml();
      state.atBottom = true;
      return;
    }

    let html = "";
    let lastDay = null;
    for (const message of conv.messages) {
      const day = dayKey(message.ts);
      if (day !== lastDay) {
        html += `<div class="date-divider">${esc(fmtDay(message.ts))}</div>`;
        lastDay = day;
      }
      html += messageHtml(message);
    }

    els.messages.innerHTML = html;
    if (shouldStick) {
      els.messages.scrollTop = els.messages.scrollHeight;
      state.atBottom = true;
    }
  };

  const updateHeader = () => {
    const name = conversationName(state.active);
    const online = state.active === "group" ? state.peers.length > 0 : isOnline(state.active);
    els.chatName.textContent = name;
    els.lockPill.hidden = !state.config.passwordSet;

    if (state.active === "group") {
      els.chatAvatar.className = "avatar room";
      els.chatAvatar.textContent = "#";
      els.chatSubtitle.textContent = state.peers.length === 0
        ? "Only you online — waiting for others"
        : `${state.peers.length} online on your network`;
    } else {
      els.chatAvatar.className = "avatar";
      els.chatAvatar.style.setProperty("--hue", hueFor(state.active));
      els.chatAvatar.textContent = initials(name);
      els.chatSubtitle.textContent = online ? "Online" : "Offline";
    }

    els.input.placeholder = `Message ${name}`;
    postToHost({ type: "title", text: `LAN Chat — ${name}` });
  };

  const selectConversation = (key) => {
    if (!key) return;
    state.active = key;
    state.unread.delete(key);
    try {
      localStorage.setItem("lanchat.active", key);
    } catch {
      /* ignore */
    }
    ensureConversation(key);
    renderSidebar();
    renderMessages(true);
    updateHeader();
    els.input.focus();
  };

  const updateNet = (connected) => {
    els.netDot.className = "status-dot " + (connected ? "online" : "error");
    els.netStatus.textContent = connected
      ? state.peers.length === 0
        ? "Searching the network…"
        : `${state.peers.length} online`
      : "Reconnecting…";
  };

  const connectEvents = () => {
    try {
      state.sse?.close();
    } catch {
      /* ignore */
    }

    const source = new EventSource(`/api/events?token=${encodeURIComponent(TOKEN)}`);
    state.sse = source;
    source.onopen = () => updateNet(true);
    source.onerror = () => updateNet(false);
    source.onmessage = (event) => {
      try {
        handleEvent(JSON.parse(event.data));
      } catch {
        /* ignore malformed events */
      }
    };
  };

  const handleEvent = (event) => {
    switch (event.type) {
      case "message":
        addMessage(event.key, event.message);
        break;
      case "peers":
        state.peers = event.peers || [];
        renderSidebar();
        updateHeader();
        updateNet(true);
        break;
      case "config":
        state.config = event.config || state.config;
        state.selfName = state.config.name || state.selfName;
        applyTheme(state.config.theme);
        renderSelf();
        renderSidebar();
        updateHeader();
        break;
      case "error":
        toast(event.message || "Something went wrong.", "error");
        break;
      case "cleared": {
        const conv = state.conversations.get(event.key);
        if (conv) {
          for (const message of conv.messages) state.seen.delete(message.id);
          conv.messages = [];
        }
        if (event.key === state.active) renderMessages(true);
        renderSidebar();
        break;
      }
    }
  };

  const refreshState = async () => {
    try {
      const response = await apiFetch("/api/state");
      if (!response.ok) return;
      const data = await response.json();
      state.peers = data.peers || [];
      state.selfName = data.self?.name || state.selfName;
      state.config = data.config || state.config;
      renderSelf();
      renderSidebar();
      updateHeader();
      updateNet(true);
    } catch {
      updateNet(false);
    }
  };

  const sendText = async () => {
    const text = els.input.value.trim();
    if (!text) return;
    const to = state.active;

    try {
      const response = await apiFetch("/api/send", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ to, text })
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) {
        toast(data.error || "Could not send the message.", "error");
        return;
      }
      els.input.value = "";
      autoResize();
      if (data.message) addMessage(to, data.message);
    } catch {
      toast("Could not reach the local chat service.", "error");
    }
  };

  const uploadOne = (file, to, index, total) =>
    new Promise((resolve) => {
      const name = file.name || "file";
      const mime = file.type || "application/octet-stream";
      const progress = toast(`Sending ${name}…`, "", 0);
      const xhr = new XMLHttpRequest();
      xhr.open(
        "POST",
        `/api/upload?to=${encodeURIComponent(to)}&name=${encodeURIComponent(name)}&mime=${encodeURIComponent(mime)}`
      );
      if (TOKEN) xhr.setRequestHeader("X-LanChat-Token", TOKEN);
      xhr.upload.onprogress = (event) => {
        if (event.lengthComputable) {
          const pct = Math.round((event.loaded / event.total) * 100);
          setToast(progress, `Sending ${name} — ${pct}%${total > 1 ? ` (${index}/${total})` : ""}`);
        }
      };
      xhr.onload = () => {
        try {
          const data = JSON.parse(xhr.responseText || "{}");
          if (xhr.status >= 200 && xhr.status < 300) {
            progress.remove();
            if (data.message) addMessage(to, data.message);
          } else {
            setToast(progress, data.error || "Upload failed.", true);
            setTimeout(() => progress.remove(), 5000);
          }
        } catch {
          setToast(progress, "Upload failed.", true);
          setTimeout(() => progress.remove(), 5000);
        }
        resolve();
      };
      xhr.onerror = () => {
        setToast(progress, "Upload failed — connection lost.", true);
        setTimeout(() => progress.remove(), 5000);
        resolve();
      };
      xhr.send(file);
    });

  const sendFiles = async (files) => {
    if (!files || files.length === 0) return;
    const to = state.active;
    let index = 0;
    for (const file of files) {
      index++;
      await uploadOne(file, to, index, files.length);
    }
  };

  const autoResize = () => {
    els.input.style.height = "auto";
    els.input.style.height = Math.min(els.input.scrollHeight, 160) + "px";
  };

  const toggleTheme = async () => {
    const next = state.config.theme === "light" ? "dark" : "light";
    applyTheme(next);
    try {
      const response = await apiFetch("/api/config", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ theme: next })
      });
      if (response.ok) {
        state.config = await response.json();
        applyTheme(state.config.theme);
        renderSidebar();
        updateHeader();
      }
    } catch {
      /* ignore */
    }
  };

  const openSettings = () => {
    els.setName.value = state.selfName || "";
    els.setPassword.value = "";
    els.removePass.checked = false;
    els.removePassRow.hidden = !state.config.passwordSet;
    els.encHint.textContent = state.config.passwordSet
      ? "Encryption is ON. Messages and files use this password. Enter a new one to change it, or remove it below."
      : "Encryption is OFF. Set a room password to encrypt messages and files on the network.";
    els.setTheme.value = state.config.theme || "dark";
    els.settingsMeta.textContent =
      `This computer: chat port ${state.config.port ?? "?"} Â· discovery port ${state.config.udpPort ?? "?"} Â· ${state.peers.length} peer(s) online`;
    els.settingsModal.hidden = false;
    setTimeout(() => els.setName.focus(), 40);
  };

  const closeSettings = () => {
    els.settingsModal.hidden = true;
  };

  const saveSettings = async () => {
    const body = {
      name: els.setName.value.trim() || state.selfName,
      theme: els.setTheme.value
    };
    if (els.setPassword.value) body.password = els.setPassword.value;
    else if (els.removePass.checked) body.password = "";

    try {
      const response = await apiFetch("/api/config", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) {
        toast(data.error || "Could not save settings.", "error");
        return;
      }
      state.config = data;
      state.selfName = data.name;
      applyTheme(data.theme);
      renderSelf();
      renderSidebar();
      updateHeader();
      closeSettings();
      toast("Settings saved.");
    } catch {
      toast("Could not save settings.", "error");
    }
  };

  const clearConversation = async () => {
    if (!confirm("Clear this conversation on this computer?")) return;
    try {
      const response = await apiFetch(`/api/history/${encodeURIComponent(state.active)}`, {
        method: "DELETE"
      });
      if (!response.ok) {
        toast("Could not clear the conversation.", "error");
        return;
      }
      const conv = state.conversations.get(state.active);
      if (conv) {
        for (const message of conv.messages) state.seen.delete(message.id);
        conv.messages = [];
      }
      renderMessages(true);
      renderSidebar();
    } catch {
      toast("Could not clear the conversation.", "error");
    }
  };

  const openLightbox = (url) => {
    els.lightboxImg.src = url;
    els.lightbox.hidden = false;
  };

  const bindEvents = () => {
    document.querySelector(".conv-scroll").addEventListener("click", (event) => {
      const item = event.target.closest(".conv");
      if (item) selectConversation(item.dataset.key);
    });

    els.sendBtn.addEventListener("click", sendText);

    els.input.addEventListener("keydown", (event) => {
      if (event.key === "Enter" && !event.shiftKey) {
        event.preventDefault();
        sendText();
      }
    });
    els.input.addEventListener("input", autoResize);

    els.attachBtn.addEventListener("click", () => els.fileInput.click());
    els.fileInput.addEventListener("change", () => {
      const files = [...els.fileInput.files];
      els.fileInput.value = "";
      sendFiles(files);
    });

    els.themeBtn.addEventListener("click", toggleTheme);
    els.settingsBtn.addEventListener("click", openSettings);
    els.settingsCancel.addEventListener("click", closeSettings);
    els.settingsSave.addEventListener("click", saveSettings);
    els.settingsModal.addEventListener("click", (event) => {
      if (event.target === els.settingsModal) closeSettings();
    });
    els.clearBtn.addEventListener("click", clearConversation);

    els.messages.addEventListener("click", (event) => {
      const image = event.target.closest(".msg-image");
      if (image) {
        openLightbox(image.dataset.full || image.src);
        return;
      }

      const open = event.target.closest("[data-open]");
      if (open) {
        apiFetch(`/api/open/${encodeURIComponent(open.dataset.open)}`, { method: "POST" });
        return;
      }

      const reveal = event.target.closest("[data-reveal]");
      if (reveal) {
        apiFetch(`/api/reveal/${encodeURIComponent(reveal.dataset.reveal)}`, { method: "POST" });
        return;
      }

      const link = event.target.closest("a[href]");
      if (link) {
        event.preventDefault();
        postToHost({ type: "open-external", url: link.href });
      }
    });

    els.messages.addEventListener("scroll", () => {
      state.atBottom =
        els.messages.scrollHeight - els.messages.scrollTop - els.messages.clientHeight < 80;
    });

    els.lightbox.addEventListener("click", () => {
      els.lightbox.hidden = true;
      els.lightboxImg.src = "";
    });

    window.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        if (!els.lightbox.hidden) {
          els.lightbox.hidden = true;
          els.lightboxImg.src = "";
        } else if (!els.settingsModal.hidden) {
          closeSettings();
        }
      }
    });

    window.addEventListener("focus", () => {
      state.unread.delete(state.active);
      renderSidebar();
    });

    document.addEventListener("paste", (event) => {
      const items = event.clipboardData?.items;
      if (!items) return;
      const files = [];
      for (const item of items) {
        if (item.type && item.type.startsWith("image/")) {
          const file = item.getAsFile();
          if (file) {
            const ext = (file.type.split("/")[1] || "png").replace("jpeg", "jpg");
            files.push(new File([file], `pasted-${Date.now()}.${ext}`, { type: file.type }));
          }
        }
      }
      if (files.length) {
        event.preventDefault();
        sendFiles(files);
      }
    });

    const hasFiles = (event) =>
      Array.from(event.dataTransfer?.types || []).includes("Files");

    const chat = document.querySelector(".chat");

    chat.addEventListener("dragenter", (event) => {
      if (!hasFiles(event)) return;
      event.preventDefault();
      state.dragDepth++;
      els.dropOverlay.classList.add("visible");
    });
    chat.addEventListener("dragover", (event) => {
      if (!hasFiles(event)) return;
      event.preventDefault();
      event.dataTransfer.dropEffect = "copy";
    });
    chat.addEventListener("dragleave", () => {
      state.dragDepth = Math.max(0, state.dragDepth - 1);
      if (state.dragDepth === 0) els.dropOverlay.classList.remove("visible");
    });
    chat.addEventListener("drop", (event) => {
      event.preventDefault();
      state.dragDepth = 0;
      els.dropOverlay.classList.remove("visible");
      const files = [...(event.dataTransfer?.files || [])];
      if (files.length) sendFiles(files);
    });

    window.addEventListener("dragover", (event) => event.preventDefault());
    window.addEventListener("drop", (event) => event.preventDefault());
  };

  const init = async () => {
    let savedTheme = "dark";
    let savedActive = "group";
    try {
      savedTheme = localStorage.getItem("lanchat.theme") || "dark";
      savedActive = localStorage.getItem("lanchat.active") || "group";
    } catch {
      /* ignore */
    }

    applyTheme(savedTheme);
    state.active = savedActive;
    bindEvents();

    try {
      const response = await apiFetch("/api/state");
      const data = await response.json();
      state.selfId = data.self?.id || "";
      state.selfName = data.self?.name || "";
      state.config = data.config || {};
      state.peers = data.peers || [];
      applyTheme(state.config.theme || savedTheme);
      renderSelf();
    } catch {
      updateNet(false);
    }

    try {
      const history = await apiFetch("/api/history").then((response) => response.json());
      for (const conv of history.conversations || []) {
        ensureConversation(conv.id, conv.name);
        for (const message of conv.messages || []) addRaw(conv.id, message);
      }
    } catch {
      /* ignore */
    }

    if (state.active !== "group" && !state.conversations.has(state.active)) {
      state.active = "group";
    }

    renderSidebar();
    renderMessages(true);
    updateHeader();
    updateNet(true);
    connectEvents();
    setInterval(refreshState, 30000);
  };

  init();
})();
