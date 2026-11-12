(() => {
  "use strict";

  const $ = (selector) => document.querySelector(selector);

  const TOKEN = document.querySelector('meta[name="lanchat-token"]')?.content || "";
  const IS_LOCAL = TOKEN.length > 0;

  const apiFetch = (url, options = {}) => {
    const headers = new Headers(options.headers || {});
    if (TOKEN) headers.set("X-LanChat-Token", TOKEN);
    return fetch(url, { ...options, headers });
  };

  const fileUrl = (id) => `/api/file/${encodeURIComponent(id)}?token=${encodeURIComponent(TOKEN)}`;

  const els = {
    selfName: $("#self-name"),
    roomList: $("#room-list"),
    peerList: $("#peer-list"),
    recentList: $("#recent-list"),
    onlineLabel: $("#online-label"),
    recentLabel: $("#recent-label"),
    searchLabel: $("#search-label"),
    searchList: $("#search-list"),
    searchInput: $("#search-input"),
    netStatus: $("#net-status"),
    netDot: $("#net-dot"),
    chatAvatar: $("#chat-avatar"),
    chatName: $("#chat-name"),
    chatSubtitle: $("#chat-subtitle"),
    lockPill: $("#lock-pill"),
    mutePill: $("#mute-pill"),
    messages: $("#messages"),
    emptyState: $("#empty-state"),
    input: $("#input"),
    sendBtn: $("#send-btn"),
    attachBtn: $("#attach-btn"),
    shotBtn: $("#shot-btn"),
    micBtn: $("#mic-btn"),
    fileInput: $("#file-input"),
    themeBtn: $("#theme-btn"),
    clearBtn: $("#clear-btn"),
    muteBtn: $("#mute-btn"),
    blockBtn: $("#block-btn"),
    addPeerBtn: $("#add-peer-btn"),
    settingsBtn: $("#settings-btn"),
    settingsModal: $("#settings-modal"),
    settingsClose: $("#settings-close"),
    setName: $("#set-name"),
    setRoom: $("#set-room"),
    setPassword: $("#set-password"),
    removePass: $("#remove-pass"),
    removePassRow: $("#remove-pass-row"),
    encHint: $("#enc-hint"),
    setNotifications: $("#set-notifications"),
    setSounds: $("#set-sounds"),
    setTray: $("#set-tray"),
    setStartup: $("#set-startup"),
    setRetention: $("#set-retention"),
    filesSize: $("#files-size"),
    cleanupBtn: $("#cleanup-btn"),
    setLanWeb: $("#set-lanweb"),
    qrRow: $("#qr-row"),
    qrImg: $("#qr-img"),
    lanUrl: $("#lan-url"),
    copyUrl: $("#copy-url"),
    setFeed: $("#set-feed"),
    versionLabel: $("#version-label"),
    checkUpdate: $("#check-update"),
    applyUpdate: $("#apply-update"),
    updateStatus: $("#update-status"),
    settingsMeta: $("#settings-meta"),
    settingsSave: $("#settings-save"),
    settingsCancel: $("#settings-cancel"),
    replyBar: $("#reply-bar"),
    replyName: $("#reply-name"),
    replyText: $("#reply-text"),
    replyCancel: $("#reply-cancel"),
    recording: $("#recording"),
    recTime: $("#rec-time"),
    recCancel: $("#rec-cancel"),
    loginModal: $("#login-modal"),
    loginPassword: $("#login-password"),
    loginError: $("#login-error"),
    loginBtn: $("#login-btn"),
    addPeerModal: $("#add-peer-modal"),
    addPeerAddress: $("#add-peer-address"),
    addPeerCancel: $("#add-peer-cancel"),
    addPeerSave: $("#add-peer-save"),
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
    muted: new Set(),
    blocked: new Set(),
    typing: new Map(),
    sse: null,
    atBottom: true,
    dragDepth: 0,
    replyTo: null,
    searchResults: [],
    searchTimer: null,
    searchQuery: "",
    typingThrottle: 0,
    recorder: null,
    recordingStarted: 0,
    recordingTimer: null,
    cancelRecording: false
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

  const fmtDuration = (seconds) => {
    const total = Math.max(0, Math.round(Number(seconds) || 0));
    return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, "0")}`;
  };

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
    if (state.config.sounds === false) return;
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

  const applyConfig = (config) => {
    if (!config) return;
    state.config = config;
    state.selfName = config.name || state.selfName;
    state.muted = new Set(config.muted || []);
    state.blocked = new Set(config.blocked || []);
    applyTheme(config.theme);
    renderSelf();
    renderSidebar();
    updateHeader();
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
    if (state.muted.has(key)) return;
    if (state.config.notifications === false) return;
    const conv = state.conversations.get(key);
    const title = conv?.name || "LAN Chat";
    let body;
    if (message.kind === "image") body = "sent an image";
    else if (message.kind === "voice") body = "sent a voice message";
    else if (message.kind === "file") body = `sent a file: ${message.fileName || "file"}`;
    else body = message.text || "New message";
    if (key === "group") body = `${message.fromName}: ${body}`;
    postToHost({ type: "notify", title, body });
    beep();
  };

  const addMessage = (key, message) => {
    if (!addRaw(key, message)) return;
    const mine = message.fromId === state.selfId;

    if (!mine) {
      state.typing.delete(key);
      if (!message.synced) {
        const focused = document.hasFocus();
        if (key === state.active && focused) {
          state.unread.delete(key);
          markRead(key);
        } else if (key === state.active) {
          notifyIncoming(key, message);
        } else {
          state.unread.set(key, (state.unread.get(key) || 0) + 1);
          notifyIncoming(key, message);
        }
      }
    }

    if (key === state.active) renderMessages();
    renderSidebar();
    updateHeader();
  };

  const conversationName = (key) => {
    if (key === "group") return state.config.room || "LAN Room";
    const conv = state.conversations.get(key);
    if (conv && conv.name) return conv.name;
    const peer = state.peers.find((p) => p.id === key);
    return peer ? peer.name : "Unknown";
  };

  const isOnline = (key) => state.peers.some((p) => p.id === key);

  const renderSelf = () => {
    els.selfName.textContent = state.selfName || "You";
  };

  const muteIcon = `<svg class="conv-mute" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" aria-label="Muted"><path d="M4 9v6h4l5 4V5L8 9H4Z"/><path d="m17 9 4 6M21 9l-4 6"/></svg>`;

  const convItem = (key, name, subtitle) => {
    const active = key === state.active ? "active" : "";
    const unread = state.unread.get(key) || 0;
    const room = key === "group";
    const hue = room ? 220 : hueFor(key);
    const muted = state.muted.has(key) ? muteIcon : "";
    return `
      <div class="conv ${active}" data-key="${esc(key)}" title="${esc(name)}" role="button" tabindex="0">
        <div class="avatar ${room ? "room" : ""}" style="--hue:${hue}">${room ? "#" : esc(initials(name))}</div>
        <div class="conv-info">
          <div class="conv-name">${esc(name)}${muted}</div>
          <div class="conv-sub">${subtitle}</div>
        </div>
        ${unread ? `<span class="badge">${unread > 99 ? "99+" : unread}</span>` : ""}
      </div>`;
  };

  const subtitleFor = (peer) =>
    peer.keyOk === false
      ? '<span class="status-dot error"></span>Wrong password'
      : '<span class="status-dot online"></span>Online';

  const renderSidebar = () => {
    const onlineText = state.peers.length === 0
      ? "Only you online"
      : `${state.peers.length} online`;

    els.roomList.innerHTML = convItem(
      "group",
      state.config.room || "LAN Room",
      `<span class="status-dot online"></span>${onlineText}`
    );

    els.onlineLabel.textContent = `Online${state.peers.length ? " — " + state.peers.length : ""}`;
    els.peerList.innerHTML = state.peers.length
      ? state.peers
          .slice()
          .sort((a, b) => a.name.localeCompare(b.name))
          .map((peer) => convItem(peer.id, peer.name, subtitleFor(peer)))
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

  const renderSearchResults = () => {
    const query = state.searchQuery.trim();
    if (!query) {
      els.searchLabel.hidden = true;
      els.searchList.innerHTML = "";
      return;
    }

    els.searchLabel.hidden = false;
    els.searchLabel.textContent = `Results — ${state.searchResults.length}`;
    els.searchList.innerHTML = state.searchResults.length
      ? state.searchResults
          .map(
            (hit) => `
        <div class="search-hit" data-key="${esc(hit.key)}" data-id="${esc(hit.id)}" role="button" tabindex="0">
          <div class="search-hit-head">
            <span class="search-hit-name">${esc(hit.name)}</span>
            <span class="search-hit-time">${fmtTime(hit.ts)}</span>
          </div>
          <div class="search-hit-text">${esc(hit.preview || "")}</div>
        </div>`
          )
          .join("")
      : '<div class="section-empty">Nothing matched</div>';
  };

  const emptyStateHtml = () => {
    if (state.active === "group") {
      return `
        <div class="empty-state">
          <div class="empty-art">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.2"><path d="M4 6.5A2.5 2.5 0 0 1 6.5 4h11A2.5 2.5 0 0 1 20 6.5v7a2.5 2.5 0 0 1-2.5 2.5H9.6L5.2 19.6A1 1 0 0 1 3.6 18.8V15"/><path d="M8 9h8M8 12h5"/></svg>
          </div>
          <h3>Your LAN room is ready</h3>
          <p>Anyone running LAN Chat in this room will appear here automatically.</p>
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
  const replyGlyph = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9"><path d="M9 7 4 12l5 5"/><path d="M4 12h9a6 6 0 0 1 6 6v1"/></svg>`;

  const tickFor = (message) => {
    if (message.fromId !== state.selfId || !message.state) return "";
    const glyph = message.state === "queued"
      ? "◷"
      : message.state === "failed"
        ? "!"
        : message.state === "read" || message.state === "delivered"
          ? "✓✓"
          : "✓";
    const label = {
      queued: "Waiting to send",
      sent: "Sent",
      delivered: "Delivered",
      read: "Read",
      failed: "Not delivered"
    }[message.state] || "";
    return `<span class="tick ${esc(message.state)}" title="${label}">${glyph}</span>`;
  };

  const quoteHtml = (message) =>
    message.replyToId
      ? `<div class="quote quote-inline">
           <span class="quote-name">${esc(message.replyToName || "")}</span>
           <span class="quote-text">${esc(message.replyToText || "")}</span>
         </div>`
      : "";

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
      const url = fileUrl(message.fileId);
      bubble = `
        <div class="bubble image-bubble media-bubble">
          ${quoteHtml(message)}
          <img class="msg-image" src="${url}" data-full="${url}" alt="${esc(message.fileName || "image")}" loading="lazy">
          <div class="image-caption">${esc(message.fileName || "image")}</div>
        </div>`;
    } else if (message.kind === "voice" && message.fileId) {
      bubble = `
        <div class="bubble voice-bubble media-bubble">
          ${quoteHtml(message)}
          <audio controls preload="metadata" src="${fileUrl(message.fileId)}"></audio>
          <span class="voice-time">${fmtDuration(message.duration)}</span>
        </div>`;
    } else if (message.kind === "file" && message.fileId) {
      bubble = `
        <div class="bubble media-bubble">
          ${quoteHtml(message)}
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
      bubble = `<div class="bubble">${quoteHtml(message)}${linkify(message.text || "")}</div>`;
    }

    return `
      <div class="msg-row ${mine ? "mine" : ""}" data-id="${esc(message.id)}">
        ${avatar}
        <div class="msg-stack">
          ${showSender ? `<div class="msg-sender">${esc(message.fromName)}</div>` : ""}
          ${bubble}
          <div class="msg-time">${fmtTime(message.ts)}${tickFor(message)}</div>
        </div>
        <button class="msg-reply" data-reply="${esc(message.id)}" title="Reply" aria-label="Reply">${replyGlyph}</button>
      </div>`;
  };

  const renderMessages = (forceBottom = false, highlightId = null) => {
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

    if (highlightId) {
      const target = els.messages.querySelector(`[data-id="${CSS.escape(highlightId)}"]`);
      if (target) {
        target.scrollIntoView({ block: "center" });
        target.classList.add("flash");
        setTimeout(() => target.classList.remove("flash"), 1600);
        return;
      }
    }

    if (shouldStick) {
      els.messages.scrollTop = els.messages.scrollHeight;
      state.atBottom = true;
    }
  };

  const typingFor = (key) => {
    const entry = state.typing.get(key);
    if (!entry) return null;
    if (entry.until < Date.now()) {
      state.typing.delete(key);
      return null;
    }
    return entry.name;
  };

  const updateHeader = () => {
    const name = conversationName(state.active);
    const peer = state.peers.find((p) => p.id === state.active);
    const online = state.active === "group" ? state.peers.length > 0 : Boolean(peer);
    const typingName = typingFor(state.active);

    els.chatName.textContent = name;
    els.lockPill.hidden = !state.config.passwordSet;
    els.mutePill.hidden = !state.muted.has(state.active);
    els.muteBtn.classList.toggle("active", state.muted.has(state.active));
    els.blockBtn.hidden = state.active === "group" || !IS_LOCAL;

    if (state.active === "group") {
      els.chatAvatar.className = "avatar room";
      els.chatAvatar.textContent = "#";
      els.chatSubtitle.textContent = typingName
        ? `${typingName} is typing…`
        : state.peers.length === 0
          ? "Only you online — waiting for others"
          : `${state.peers.length} online in ${state.config.room || "this room"}`;
    } else {
      els.chatAvatar.className = "avatar";
      els.chatAvatar.style.setProperty("--hue", hueFor(state.active));
      els.chatAvatar.textContent = initials(name);
      if (typingName) els.chatSubtitle.textContent = "typing…";
      else if (peer && peer.keyOk === false) els.chatSubtitle.textContent = "Wrong room password";
      else els.chatSubtitle.textContent = online ? "Online" : "Offline";
    }

    els.input.placeholder = `Message ${name}`;
    postToHost({ type: "title", text: `LAN Chat — ${name}` });
  };

  const markRead = (key) => {
    if (key === "group" || !IS_LOCAL) return;
    apiFetch(`/api/read/${encodeURIComponent(key)}`, { method: "POST" }).catch(() => {});
  };

  const selectConversation = (key) => {
    if (!key) return;
    state.active = key;
    state.unread.delete(key);
    state.typing.delete(key);
    try {
      localStorage.setItem("lanchat.active", key);
    } catch {
      /* ignore */
    }
    ensureConversation(key);
    renderSidebar();
    renderMessages(true);
    updateHeader();
    markRead(key);
    els.input.focus();
  };

  const updateNet = (connected) => {
    els.netDot.className = "status-dot " + (connected ? "online" : "error");
    els.netStatus.textContent = connected
      ? state.peers.length === 0
        ? "Searching…"
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
      case "state": {
        const conv = state.conversations.get(state.active);
        const message = conv?.messages.find((m) => m.id === event.id);
        if (message) {
          message.state = event.state;
          renderMessages();
        }
        break;
      }
      case "peers":
        state.peers = event.peers || [];
        renderSidebar();
        updateHeader();
        updateNet(true);
        break;
      case "config":
        applyConfig(event.config);
        break;
      case "typing": {
        if (event.key && event.key !== state.selfId) {
          state.typing.set(event.key, { name: event.name || "Someone", until: Date.now() + 5000 });
          updateHeader();
        }
        break;
      }
      case "error":
        toast(event.message || "Something went wrong.", "error");
        break;
      case "refresh":
        loadHistory();
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

  const loadHistory = async () => {
    try {
      const response = await apiFetch("/api/history");
      if (!response.ok) return;
      const history = await response.json();
      state.conversations.clear();
      state.seen.clear();
      for (const conv of history.conversations || []) {
        ensureConversation(conv.id, conv.name);
        for (const message of conv.messages || []) addRaw(conv.id, message);
      }
      renderSidebar();
      renderMessages(true);
      updateHeader();
    } catch {
      /* ignore */
    }
  };

  const refreshState = async () => {
    try {
      const response = await apiFetch("/api/state");
      if (!response.ok) return;
      const data = await response.json();
      state.peers = data.peers || [];
      state.selfName = data.self?.name || state.selfName;
      if (data.config) applyConfig(data.config);
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
    const replyToId = state.replyTo?.id || null;

    try {
      const response = await apiFetch("/api/send", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ to, text, replyToId })
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) {
        toast(data.error || "Could not send the message.", "error");
        return;
      }
      els.input.value = "";
      clearReply();
      autoResize();
      if (data.message) addMessage(to, data.message);
    } catch {
      toast("Could not reach the local chat service.", "error");
    }
  };

  const uploadOne = (file, to, index, total, duration = 0) =>
    new Promise((resolve) => {
      const name = file.name || "file";
      const mime = file.type || "application/octet-stream";
      const progress = toast(`Sending ${name}…`, "", 0);
      const xhr = new XMLHttpRequest();
      xhr.open(
        "POST",
        `/api/upload?to=${encodeURIComponent(to)}&name=${encodeURIComponent(name)}&mime=${encodeURIComponent(mime)}&duration=${duration}`
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

  const sendFiles = async (files, duration = 0) => {
    if (!files || files.length === 0) return;
    const to = state.active;
    let index = 0;
    for (const file of files) {
      index++;
      await uploadOne(file, to, index, files.length, duration);
    }
  };

  const walkEntry = async (entry, prefix, out) => {
    if (!entry) return;
    if (entry.isFile) {
      const file = await new Promise((resolve) => entry.file(resolve, () => resolve(null)));
      if (file) out.push({ file, path: prefix + entry.name });
      return;
    }

    if (entry.isDirectory) {
      const reader = entry.createReader();
      for (;;) {
        const batch = await new Promise((resolve) => reader.readEntries(resolve, () => resolve([])));
        if (!batch.length) break;
        for (const child of batch) {
          await walkEntry(child, prefix + entry.name + "/", out);
        }
      }
    }
  };

  const collectDropped = async (dataTransfer) => {
    const out = [];
    const items = [...(dataTransfer?.items || [])];
    const entries = items
      .map((item) => (item.kind === "file" ? item.webkitGetAsEntry?.() : null))
      .filter(Boolean);

    if (entries.length) {
      for (const entry of entries) await walkEntry(entry, "", out);
      if (out.length) {
        return out.map((item) => {
          const file = item.file;
          return new File([file], item.path.split("/").pop() || file.name, { type: file.type });
        });
      }
    }

    return [...(dataTransfer?.files || [])];
  };

  const autoResize = () => {
    els.input.style.height = "auto";
    els.input.style.height = Math.min(els.input.scrollHeight, 160) + "px";
  };

  const setReply = (message) => {
    if (!message) return;
    state.replyTo = {
      id: message.id,
      name: message.fromId === state.selfId ? state.selfName : message.fromName,
      text: message.kind === "text"
        ? (message.text || "").slice(0, 140)
        : message.kind === "image"
          ? "Photo"
          : message.kind === "voice"
            ? "Voice message"
            : `File: ${message.fileName || "file"}`
    };
    els.replyName.textContent = state.replyTo.name;
    els.replyText.textContent = state.replyTo.text;
    els.replyBar.hidden = false;
    els.input.focus();
  };

  const clearReply = () => {
    state.replyTo = null;
    els.replyBar.hidden = true;
    els.replyName.textContent = "";
    els.replyText.textContent = "";
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
      if (response.ok) applyConfig(await response.json());
    } catch {
      /* ignore */
    }
  };

  const toggleMute = async () => {
    const key = state.active;
    if (!key) return;
    if (state.muted.has(key)) state.muted.delete(key);
    else state.muted.add(key);
    renderSidebar();
    updateHeader();
    try {
      await apiFetch(`/api/mute/${encodeURIComponent(key)}`, { method: "POST" });
    } catch {
      /* ignore */
    }
  };

  const toggleBlock = async () => {
    if (state.active === "group") return;
    const name = conversationName(state.active);
    const blocked = state.blocked.has(state.active);
    if (!blocked && !confirm(`Block ${name}? Their messages will be ignored.`)) return;
    try {
      const response = await apiFetch(`/api/block/${encodeURIComponent(state.active)}`, { method: "POST" });
      if (!response.ok) {
        toast("Could not update the block list.", "error");
        return;
      }
      toast(blocked ? `${name} unblocked.` : `${name} blocked.`);
      if (!blocked) selectConversation("group");
    } catch {
      toast("Could not update the block list.", "error");
    }
  };

  const updateQrRow = () => {
    const show = els.setLanWeb.checked && Boolean(state.config.lanUrl);
    els.qrRow.hidden = !show;
    if (!show) return;
    els.lanUrl.textContent = state.config.lanUrl || "";
    if (IS_LOCAL) {
      els.qrImg.src = `/api/lanweb/qr?token=${encodeURIComponent(TOKEN)}&t=${Date.now()}`;
    }
  };

  const openSettings = () => {
    const config = state.config;
    els.setName.value = state.selfName || "";
    els.setRoom.value = config.room || "";
    els.setPassword.value = "";
    els.removePass.checked = false;
    els.removePassRow.hidden = !config.passwordSet;
    els.encHint.textContent = config.passwordSet
      ? "Encryption is ON. Peers must prove they know this password. Enter a new one to change it, or remove it below."
      : "Encryption is OFF. Set a room password to encrypt messages and files, and to authenticate peers.";
    els.setNotifications.checked = config.notifications !== false;
    els.setSounds.checked = config.sounds !== false;
    els.setTray.checked = config.minimizeToTray !== false;
    els.setStartup.checked = Boolean(config.startWithWindows);
    els.setRetention.value = Number(config.retentionDays ?? 7);
    els.setLanWeb.checked = Boolean(config.allowLanWeb);
    els.setFeed.value = config.updateFeed || "";
    els.filesSize.textContent = `${fmtSize(config.filesSize)} of received files stored`;
    els.versionLabel.textContent = `Version ${config.version || "?"}`;
    els.updateStatus.textContent = "";
    els.applyUpdate.hidden = true;
    updateQrRow();
    els.settingsMeta.textContent =
      `This computer: chat port ${config.port ?? "?"} · discovery port ${config.udpPort ?? "?"} · ` +
      `${state.peers.length} peer(s) online · data in ${config.dataDir || "app data"}` +
      (config.portable ? " (portable)" : "");
    els.settingsModal.hidden = false;
    setTimeout(() => els.setName.focus(), 40);
  };

  const closeSettings = () => {
    els.settingsModal.hidden = true;
  };

  const saveSettings = async () => {
    const body = {
      name: els.setName.value.trim() || state.selfName,
      theme: state.config.theme,
      room: els.setRoom.value.trim() || state.config.room,
      notifications: els.setNotifications.checked,
      sounds: els.setSounds.checked,
      minimizeToTray: els.setTray.checked,
      startWithWindows: els.setStartup.checked,
      allowLanWeb: els.setLanWeb.checked,
      fileRetentionDays: Number(els.setRetention.value || 0),
      updateFeed: els.setFeed.value.trim()
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
      applyConfig(data);
      closeSettings();
      toast("Settings saved.");
      refreshState();
    } catch {
      toast("Could not save settings.", "error");
    }
  };

  const cleanupFiles = async () => {
    try {
      const response = await apiFetch("/api/files/cleanup", { method: "POST" });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) {
        toast(data.error || "Cleanup failed.", "error");
        return;
      }
      if (!data.removed) toast("Nothing to clean up yet.");
      else toast(`Removed ${data.removed} old item(s).`);
      els.filesSize.textContent = `${fmtSize(data.filesSize)} of received files stored`;
      state.config.filesSize = data.filesSize;
      loadHistory();
    } catch {
      toast("Cleanup failed.", "error");
    }
  };

  const checkUpdates = async () => {
    els.updateStatus.textContent = "Checking…";
    els.applyUpdate.hidden = true;
    try {
      const feed = els.setFeed.value.trim();
      if (feed !== (state.config.updateFeed || "")) {
        const saved = await apiFetch("/api/config", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ updateFeed: feed })
        });
        if (saved.ok) applyConfig(await saved.json());
      }

      const response = await apiFetch("/api/update/check", { method: "POST" });
      const data = await response.json().catch(() => ({}));
      if (data.error) {
        els.updateStatus.textContent = data.error;
        return;
      }
      if (data.available) {
        els.updateStatus.textContent = `Version ${data.version} is available.${data.notes ? " " + data.notes : ""}`;
        els.applyUpdate.hidden = false;
      } else {
        els.updateStatus.textContent =
          `You are on the latest version (${data.version || state.config.version}).`;
      }
    } catch {
      els.updateStatus.textContent = "Could not check for updates.";
    }
  };

  const applyUpdate = async () => {
    els.updateStatus.textContent = "Downloading…";
    try {
      const response = await apiFetch("/api/update/apply", { method: "POST" });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) {
        els.updateStatus.textContent = data.error || "Update failed.";
        return;
      }
      els.updateStatus.textContent = "Restarting to install…";
    } catch {
      els.updateStatus.textContent = "Update failed.";
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

  const openAddPeer = () => {
    els.addPeerAddress.value = "";
    els.addPeerModal.hidden = false;
    setTimeout(() => els.addPeerAddress.focus(), 40);
  };

  const submitAddPeer = async () => {
    const address = els.addPeerAddress.value.trim();
    if (!address) return;
    try {
      const response = await apiFetch("/api/peers/add", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ address })
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) {
        toast(data.error || "Could not reach that address.", "error");
        return;
      }
      els.addPeerModal.hidden = true;
      toast(data.warning || "Computer added.");
      refreshState();
    } catch {
      toast("Could not reach that address.", "error");
    }
  };

  const runSearch = async () => {
    const query = els.searchInput.value.trim();
    state.searchQuery = query;
    if (!query) {
      state.searchResults = [];
      renderSearchResults();
      return;
    }

    try {
      const response = await apiFetch(`/api/search?q=${encodeURIComponent(query)}`);
      const data = await response.json().catch(() => ({}));
      if (state.searchQuery !== query) return;
      state.searchResults = data.results || [];
      renderSearchResults();
    } catch {
      state.searchResults = [];
      renderSearchResults();
    }
  };

  const jumpToHit = (key, id) => {
    selectConversation(key);
    renderMessages(true, id);
  };

  const notifyTyping = () => {
    if (!IS_LOCAL || state.active === "group") return;
    const now = Date.now();
    if (now - state.typingThrottle < 2000) return;
    state.typingThrottle = now;
    apiFetch("/api/typing", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ to: state.active })
    }).catch(() => {});
  };

  const startRecording = async () => {
    if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === "undefined") {
      toast("Recording is not available here.", "error");
      return;
    }

    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      const mime = MediaRecorder.isTypeSupported("audio/webm;codecs=opus")
        ? "audio/webm;codecs=opus"
        : "audio/webm";
      const recorder = new MediaRecorder(stream, { mimeType: mime });
      const chunks = [];
      state.cancelRecording = false;
      state.recordingStarted = Date.now();
      state.recorder = recorder;

      recorder.ondataavailable = (event) => {
        if (event.data && event.data.size) chunks.push(event.data);
      };

      recorder.onstop = () => {
        stream.getTracks().forEach((track) => track.stop());
        clearInterval(state.recordingTimer);
        els.recording.hidden = true;
        els.micBtn.classList.remove("recording");
        const seconds = Math.round((Date.now() - state.recordingStarted) / 1000);
        if (state.cancelRecording) return;
        const blob = new Blob(chunks, { type: "audio/webm" });
        if (blob.size < 900 || seconds < 1) {
          toast("Recording was too short.");
          return;
        }
        const file = new File([blob], `voice-${Date.now()}.webm`, { type: "audio/webm" });
        sendFiles([file], seconds);
      };

      recorder.start();
      els.recording.hidden = false;
      els.micBtn.classList.add("recording");
      const started = Date.now();
      els.recTime.textContent = "0:00";
      state.recordingTimer = setInterval(() => {
        els.recTime.textContent = fmtDuration((Date.now() - started) / 1000);
      }, 500);
    } catch {
      toast("Microphone access was refused.", "error");
    }
  };

  const stopRecording = (cancel = false) => {
    if (!state.recorder) return;
    state.cancelRecording = cancel;
    try {
      state.recorder.stop();
    } catch {
      /* ignore */
    }
    state.recorder = null;
  };

  const bindEvents = () => {
    const sidebar = $(".conv-scroll");
    const activate = (target) => {
      const conv = target.closest(".conv");
      if (conv) {
        selectConversation(conv.dataset.key);
        return;
      }
      const hit = target.closest(".search-hit");
      if (hit) jumpToHit(hit.dataset.key, hit.dataset.id);
    };

    sidebar.addEventListener("click", (event) => activate(event.target));
    sidebar.addEventListener("keydown", (event) => {
      if (event.key !== "Enter" && event.key !== " ") return;
      const target = event.target.closest(".conv, .search-hit");
      if (!target) return;
      event.preventDefault();
      activate(target);
    });

    els.searchInput.addEventListener("input", () => {
      clearTimeout(state.searchTimer);
      state.searchTimer = setTimeout(runSearch, 280);
    });

    els.sendBtn.addEventListener("click", sendText);

    els.input.addEventListener("keydown", (event) => {
      if (event.key === "Enter" && !event.shiftKey) {
        event.preventDefault();
        sendText();
      }
    });
    els.input.addEventListener("input", () => {
      autoResize();
      notifyTyping();
    });

    els.attachBtn.addEventListener("click", () => els.fileInput.click());
    els.fileInput.addEventListener("change", () => {
      const files = [...els.fileInput.files];
      els.fileInput.value = "";
      sendFiles(files);
    });

    els.shotBtn.hidden = !IS_LOCAL;
    els.shotBtn.addEventListener("click", () => {
      postToHost({ type: "capture", to: state.active });
      toast("Capturing this screen…");
    });

    els.micBtn.addEventListener("click", () => {
      if (state.recorder) stopRecording(false);
      else startRecording();
    });
    els.recCancel.addEventListener("click", () => stopRecording(true));

    els.themeBtn.addEventListener("click", toggleTheme);
    els.muteBtn.addEventListener("click", toggleMute);
    els.blockBtn.addEventListener("click", toggleBlock);
    els.replyCancel.addEventListener("click", clearReply);
    els.addPeerBtn.addEventListener("click", openAddPeer);
    els.addPeerCancel.addEventListener("click", () => {
      els.addPeerModal.hidden = true;
    });
    els.addPeerSave.addEventListener("click", submitAddPeer);
    els.addPeerAddress.addEventListener("keydown", (event) => {
      if (event.key === "Enter") submitAddPeer();
    });

    els.settingsBtn.addEventListener("click", openSettings);
    els.settingsCancel.addEventListener("click", closeSettings);
    els.settingsClose.addEventListener("click", closeSettings);
    els.settingsSave.addEventListener("click", saveSettings);
    els.settingsModal.addEventListener("click", (event) => {
      if (event.target === els.settingsModal) closeSettings();
    });
    els.setLanWeb.addEventListener("change", updateQrRow);
    els.cleanupBtn.addEventListener("click", cleanupFiles);
    els.checkUpdate.addEventListener("click", checkUpdates);
    els.applyUpdate.addEventListener("click", applyUpdate);
    els.copyUrl.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(state.config.lanUrl || "");
        toast("Link copied.");
      } catch {
        toast("Could not copy the link.", "error");
      }
    });

    els.loginBtn.addEventListener("click", async () => {
      els.loginError.hidden = true;
      try {
        const response = await fetch("/api/login", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ password: els.loginPassword.value })
        });
        if (!response.ok) {
          const data = await response.json().catch(() => ({}));
          els.loginError.textContent = data.error || "Could not sign in.";
          els.loginError.hidden = false;
          return;
        }
        location.reload();
      } catch {
        els.loginError.textContent = "Could not reach the host computer.";
        els.loginError.hidden = false;
      }
    });
    els.loginPassword.addEventListener("keydown", (event) => {
      if (event.key === "Enter") els.loginBtn.click();
    });

    els.clearBtn.addEventListener("click", clearConversation);

    els.messages.addEventListener("click", (event) => {
      const image = event.target.closest(".msg-image");
      if (image) {
        openLightbox(image.dataset.full || image.src);
        return;
      }

      const reply = event.target.closest("[data-reply]");
      if (reply) {
        const conv = state.conversations.get(state.active);
        const message = conv?.messages.find((m) => m.id === reply.dataset.reply);
        setReply(message);
        return;
      }

      const open = event.target.closest("[data-open]");
      if (open) {
        apiFetch(`/api/open/${encodeURIComponent(open.dataset.open)}`, { method: "POST" }).catch(() => {});
        return;
      }

      const reveal = event.target.closest("[data-reveal]");
      if (reveal) {
        apiFetch(`/api/reveal/${encodeURIComponent(reveal.dataset.reveal)}`, { method: "POST" }).catch(() => {});
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
        } else if (!els.addPeerModal.hidden) {
          els.addPeerModal.hidden = true;
        } else if (!els.replyBar.hidden) {
          clearReply();
        }
      }
    });

    window.addEventListener("focus", () => {
      state.unread.delete(state.active);
      renderSidebar();
      markRead(state.active);
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

    const chat = $(".chat");

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
    chat.addEventListener("drop", async (event) => {
      event.preventDefault();
      state.dragDepth = 0;
      els.dropOverlay.classList.remove("visible");
      const files = await collectDropped(event.dataTransfer);
      if (files.length) sendFiles(files);
    });

    window.addEventListener("dragover", (event) => event.preventDefault());
    window.addEventListener("drop", (event) => event.preventDefault());

    window.chrome?.webview?.addEventListener?.("message", (event) => {
      if (event.data?.type === "open-settings") openSettings();
    });
  };

  const showLogin = async () => {
    els.loginModal.hidden = false;
    setTimeout(() => els.loginPassword.focus(), 60);
    try {
      const info = await fetch("/api/lanweb/info").then((r) => r.json());
      if (info?.room) {
        const heading = els.loginModal.querySelector("h2");
        if (heading) heading.textContent = `Join ${info.room}`;
      }
    } catch {
      /* ignore */
    }
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
      if (response.status === 401) {
        showLogin();
        return;
      }

      const data = await response.json();
      state.selfId = data.self?.id || "";
      state.selfName = data.self?.name || "";
      state.peers = data.peers || [];
      if (data.config) applyConfig(data.config);
      else applyTheme(savedTheme);
      renderSelf();
    } catch {
      updateNet(false);
    }

    await loadHistory();

    if (state.active !== "group" && !state.conversations.has(state.active)) {
      state.active = "group";
    }

    renderSidebar();
    renderMessages(true);
    updateHeader();
    updateNet(true);
    connectEvents();
    markRead(state.active);
    setInterval(refreshState, 30000);
    setInterval(() => {
      let changed = false;
      for (const [key, entry] of state.typing) {
        if (entry.until < Date.now()) {
          state.typing.delete(key);
          changed = true;
        }
      }
      if (changed) updateHeader();
    }, 1200);
  };

  init();
})();
