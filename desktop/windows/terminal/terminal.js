(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  if (root && root.document) api.start(root.document, root);
})(typeof globalThis === 'undefined' ? this : globalThis, function () {
  'use strict';
  const encoder = new TextEncoder();
  const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

  function byteLength(value) { return encoder.encode(value).byteLength; }
  function isUuid(value) { return typeof value === 'string' && UUID.test(value); }
  function isInputFrame(message) { return !!message && message.kind === 'input' && typeof message.data === 'string' && byteLength(message.data) <= 8192; }
  function isResizeFrame(message) { return !!message && message.kind === 'resize' && Number.isInteger(message.columns) && Number.isInteger(message.rows) && message.columns >= 2 && message.columns <= 500 && message.rows >= 2 && message.rows <= 300; }
  function ticketBody(paneId, generation) {
    if (!isUuid(paneId) || !isUuid(generation)) throw new TypeError('pane and generation must be UUIDs');
    return JSON.stringify({ paneId: paneId, generation: generation });
  }
  function safeText(value) { return String(value == null ? '' : value); }
  function registerBlockedOscHandlers(parser) { [0, 1, 2, 52].forEach(code => parser.registerOscHandler(code, () => true)); }
  function moveIntoSplit(tabs, activeId, paneId, orientation) {
    const active = tabs.find(tab => tab.id === activeId);
    if (!active || active.paneIds.length !== 1 || active.paneIds[0] === paneId) return null;
    const next = tabs.map(tab => tab.id === activeId ? { id: tab.id, paneIds: [tab.paneIds[0], paneId], orientation: orientation } : { id: tab.id, paneIds: tab.paneIds.filter(id => id !== paneId), orientation: tab.orientation }).filter(tab => tab.paneIds.length);
    return { tabs: next, activeId: activeId };
  }
  function unsplitTab(tabs, activeId, newTabId) {
    const active = tabs.find(tab => tab.id === activeId);
    if (!active || active.paneIds.length !== 2) return null;
    return { tabs: tabs.map(tab => tab.id === activeId ? { id: tab.id, paneIds: [tab.paneIds[0]], orientation: tab.orientation } : { id: tab.id, paneIds: tab.paneIds.slice(), orientation: tab.orientation }).concat({ id: newTabId, paneIds: [active.paneIds[1]], orientation: 'horizontal' }), activeId: activeId };
  }
  function outboundError(message, bufferedAmount) {
    if (message && message.kind === 'input' && !isInputFrame(message)) return 'input-size';
    if ((isInputFrame(message) || isResizeFrame(message)) && bufferedAmount >= 512 * 1024) return 'backpressure';
    return '';
  }
  function nextOutputBytes(pending, bytes) { return Number.isInteger(pending) && Number.isInteger(bytes) && pending >= 0 && bytes >= 0 && pending + bytes <= 512 * 1024 ? pending + bytes : -1; }
  function releaseOutputBytes(pending, bytes) { return Math.max(0, pending - bytes); }
  function advanceRefresh(current, event) {
    if (event === 'request') return current.inflight ? { state: { inflight: true, pending: true }, run: false } : { state: { inflight: true, pending: false }, run: true };
    if (event === 'complete') return current.pending ? { state: { inflight: true, pending: false }, run: true } : { state: { inflight: false, pending: false }, run: false };
    throw new TypeError('unknown refresh event');
  }
  function websocketUrl(location) { const url = new URL('/connect', location.href); url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:'; return url.href; }

  function start(document, window) {
    const state = { panes: new Map(), tabs: [], activeTab: null, maxPanes: 8, browserMode: !(window.chrome && window.chrome.webview), refresh: { inflight: false, pending: false } };
    const elements = {
      tabs: document.getElementById('tabs'), panes: document.getElementById('panes'), notice: document.getElementById('notice'), service: document.getElementById('service-state'), launch: document.getElementById('launch'), refresh: document.getElementById('refresh'), splitPane: document.getElementById('split-pane'), splitHorizontal: document.getElementById('split-horizontal'), splitVertical: document.getElementById('split-vertical'), unsplit: document.getElementById('unsplit')
    };
    function notice(message, error) { elements.notice.textContent = safeText(message); elements.notice.className = error ? 'error' : ''; }
    function nativePost(message) { if (!state.browserMode) window.chrome.webview.postMessage(message); }
    function activeTab() { return state.tabs.find(tab => tab.id === state.activeTab) || null; }
    function tabForPane(id) { return state.tabs.find(tab => tab.paneIds.includes(id)); }
    function uuid() { return window.crypto.randomUUID(); }
    function isCurrent(pane, attachment) { return state.panes.get(pane.id) === pane && pane.attachment === attachment && !attachment.cancelled; }
    function invalidateAttachment(pane) {
      if (pane.attachment) pane.attachment.cancelled = true;
      pane.attachment = null; pane.connecting = false; pane.ready = false;
      const socket = pane.socket; pane.socket = null;
      if (socket) socket.close();
      if (pane.terminal) pane.terminal.options.disableStdin = true;
    }
    function disposePane(pane) { invalidateAttachment(pane); if (pane.observer) pane.observer.disconnect(); if (pane.terminal) pane.terminal.dispose(); pane.pendingOutputBytes = 0; }
    function refreshPanes() {
      const next = advanceRefresh(state.refresh, 'request'); state.refresh = next.state;
      if (next.run) state.refresh.promise = fetchPanes();
      return state.refresh.promise || Promise.resolve();
    }
    async function fetchPanes() {
      try {
        const response = await window.fetch('/api/panes', { credentials: 'omit' });
        if (!response.ok) throw new Error('Pane list unavailable (' + response.status + ')');
        const payload = await response.json();
        if (!payload || !Array.isArray(payload.panes) || !Number.isInteger(payload.maxPanes)) throw new Error('Pane list was malformed');
        const incoming = new Map(payload.panes.filter(pane => pane && isUuid(pane.id)).map(pane => [pane.id, pane]));
        for (const [id, pane] of state.panes) if (!incoming.has(id)) { disposePane(pane); state.panes.delete(id); }
        for (const [id, pane] of incoming) { const local = Object.assign(state.panes.get(id) || {}, pane); if (local.status === 'exited') invalidateAttachment(local); state.panes.set(id, local); }
        state.maxPanes = Math.min(8, Math.max(0, payload.maxPanes));
        const assigned = new Set();
        state.tabs = state.tabs.map(tab => Object.assign(tab, { paneIds: tab.paneIds.filter(id => state.panes.has(id) && !assigned.has(id) && !!assigned.add(id)) })).filter(tab => tab.paneIds.length);
        for (const id of state.panes.keys()) if (!tabForPane(id)) state.tabs.push({ id: uuid(), paneIds: [id], orientation: 'horizontal' });
        if (!activeTab() && state.tabs[0]) state.activeTab = state.tabs[0].id;
        elements.service.textContent = payload.state === 'running' ? 'Service running' : 'Service state: ' + safeText(payload.state);
        elements.launch.disabled = state.browserMode || state.panes.size >= state.maxPanes;
        render();
      } catch (error) { elements.service.textContent = 'Terminal service unavailable'; notice(error.message, true); }
      finally {
        const next = advanceRefresh(state.refresh, 'complete'); state.refresh = next.state;
        if (next.run) state.refresh.promise = fetchPanes(); else state.refresh.promise = null;
      }
    }
    async function attach(pane) {
      if (pane.connecting || pane.ready || pane.socket) return;
      const attachment = pane.attachment = { generation: uuid(), cancelled: false };
      pane.connecting = true;
      updatePaneStatus(pane, 'Connecting…');
      try {
        const response = await window.fetch('/api/ticket', { method: 'POST', credentials: 'omit', headers: { 'Content-Type': 'application/json' }, body: ticketBody(pane.id, attachment.generation) });
        if (!isCurrent(pane, attachment)) return;
        if (!response.ok) throw new Error('Connection ticket unavailable (' + response.status + ')');
        const ticket = await response.json();
        if (!isCurrent(pane, attachment)) return;
        if (!ticket || typeof ticket.ticket !== 'string' || !Number.isFinite(ticket.expiresInSeconds)) throw new Error('Connection ticket was malformed');
        const socket = pane.socket = new window.WebSocket(websocketUrl(window.location));
        socket.binaryType = 'arraybuffer';
        socket.onopen = () => { if (isCurrent(pane, attachment) && pane.socket === socket) socket.send(JSON.stringify({ paneId: pane.id, generation: attachment.generation, ticket: ticket.ticket })); else socket.close(); };
        socket.onmessage = event => {
          if (!isCurrent(pane, attachment) || pane.socket !== socket) return;
          if (typeof event.data === 'string') {
            let message; try { message = JSON.parse(event.data); } catch (_) { return; }
            if (message.kind === 'ready') { pane.ready = true; pane.connecting = false; pane.terminal.options.disableStdin = false; fitPane(pane); updatePaneStatus(pane, 'Connected'); if (message.truncated || message.historyUnavailable) notice('Reattached terminal history is bounded and cannot reconstruct an arbitrary TUI screen.'); }
            return;
          }
          if (event.data instanceof ArrayBuffer) {
            const bytes = new Uint8Array(event.data); const pending = nextOutputBytes(pane.pendingOutputBytes || 0, bytes.byteLength);
            if (pending < 0) { invalidateAttachment(pane); updatePaneStatus(pane, 'Detached — renderer is too slow'); notice('Renderer fell behind, so this terminal was detached. Its bounded history may be truncated; reconnect manually.', true); return; }
            pane.pendingOutputBytes = pending;
            const terminal = pane.terminal;
            terminal.write(bytes, () => { if (state.panes.get(pane.id) === pane && pane.terminal === terminal) pane.pendingOutputBytes = releaseOutputBytes(pane.pendingOutputBytes, bytes.byteLength); });
          }
        };
        socket.onclose = () => { if (!isCurrent(pane, attachment) || pane.socket !== socket) return; pane.socket = null; pane.ready = false; pane.connecting = false; pane.attachment = null; if (pane.terminal) pane.terminal.options.disableStdin = true; updatePaneStatus(pane, 'Detached — reconnect manually'); };
        socket.onerror = () => { if (isCurrent(pane, attachment) && pane.socket === socket) notice('Terminal connection failed. Reconnect manually; input is never retried.', true); };
      } catch (error) { if (isCurrent(pane, attachment)) { pane.connecting = false; pane.attachment = null; updatePaneStatus(pane, 'Detached — reconnect manually'); notice(error.message, true); } }
    }
    function send(pane, message) {
      const problem = outboundError(message, pane.socket ? pane.socket.bufferedAmount : 0);
      if (problem) { if (message.kind === 'input') notice(problem === 'backpressure' ? 'Input was not sent: terminal connection is busy.' : 'Input was not sent: it exceeds 8 KiB.', true); return; }
      if (pane.ready && pane.socket && pane.socket.readyState === window.WebSocket.OPEN && (isInputFrame(message) || isResizeFrame(message))) pane.socket.send(JSON.stringify(message));
    }
    function updatePaneStatus(pane, value) { pane.connectionLabel = value; const label = document.getElementById('pane-state-' + pane.id); if (label) label.textContent = value; }
    function fitPane(pane) {
      if (!pane.fit || !pane.terminal || !pane.mount || !pane.ready || !pane.mount.clientWidth || !pane.mount.clientHeight) return;
      pane.fit.fit();
      const size = { columns: pane.terminal.cols, rows: pane.terminal.rows };
      if (pane.lastSize && pane.lastSize.columns === size.columns && pane.lastSize.rows === size.rows) return;
      pane.lastSize = size; send(pane, Object.assign({ kind: 'resize' }, size));
    }
    function mountPane(pane, mount) {
      if (pane.observer) pane.observer.disconnect();
      pane.mount = mount;
      if (window.ResizeObserver) { pane.observer = new window.ResizeObserver(() => fitPane(pane)); pane.observer.observe(mount); }
      if (pane.terminal) mount.appendChild(pane.terminal.element);
    }
    function makeTerminal(pane, mount) {
      if (pane.terminal) { mountPane(pane, mount); fitPane(pane); return; }
      const terminal = pane.terminal = new window.Terminal({ cursorBlink: true, disableStdin: true, scrollback: 5000, theme: { background: '#13141f', foreground: '#e9e9ed', cursor: '#9184d9' }, linkHandler: { activate: function () {} } });
      const fit = pane.fit = new window.FitAddon.FitAddon();
      terminal.loadAddon(fit); terminal.open(mount); registerBlockedOscHandlers(terminal.parser); terminal.onTitleChange(function () {});
      terminal.onData(data => send(pane, { kind: 'input', data: data }));
      terminal.onResize(size => { if (pane.ready) { pane.lastSize = { columns: size.cols, rows: size.rows }; send(pane, { kind: 'resize', columns: size.cols, rows: size.rows }); } });
      mountPane(pane, mount);
      if (pane.status === 'running') attach(pane);
    }
    function renderTabs() {
      elements.tabs.replaceChildren();
      for (const tab of state.tabs) {
        const pane = state.panes.get(tab.paneIds[0]); if (!pane) continue;
        const button = document.createElement('button'); button.type = 'button'; button.className = 'tab'; button.role = 'tab'; button.id = 'tab-' + tab.id; button.setAttribute('aria-controls', 'tab-panel-' + tab.id); button.setAttribute('aria-selected', String(tab.id === state.activeTab)); button.tabIndex = tab.id === state.activeTab ? 0 : -1; button.textContent = safeText(pane.projectName || pane.profileName || 'Terminal');
        button.addEventListener('click', () => { state.activeTab = tab.id; render(); });
        button.addEventListener('keydown', event => {
          const keys = ['ArrowLeft', 'ArrowRight', 'Home', 'End']; if (!keys.includes(event.key)) return;
          event.preventDefault(); const index = state.tabs.findIndex(candidate => candidate.id === tab.id); const next = event.key === 'Home' ? 0 : event.key === 'End' ? state.tabs.length - 1 : (index + (event.key === 'ArrowRight' ? 1 : -1) + state.tabs.length) % state.tabs.length;
          state.activeTab = state.tabs[next].id; render(); document.getElementById('tab-' + state.activeTab).focus();
        });
        elements.tabs.appendChild(button);
      }
    }
    function renderSplitOptions(tab) {
      elements.splitPane.replaceChildren();
      const placeholder = document.createElement('option'); placeholder.value = ''; placeholder.textContent = 'Choose another pane'; elements.splitPane.appendChild(placeholder);
      const canSplit = !!tab && tab.paneIds.length === 1;
      elements.splitPane.disabled = !canSplit; elements.splitHorizontal.disabled = !canSplit; elements.splitVertical.disabled = !canSplit; elements.unsplit.disabled = !tab || tab.paneIds.length !== 2;
      if (!canSplit) return;
      for (const [id, pane] of state.panes) if (id !== tab.paneIds[0]) { const option = document.createElement('option'); option.value = id; option.textContent = safeText(pane.projectName || pane.profileName || id); elements.splitPane.appendChild(option); }
    }
    function renderPanes(focusedPaneId) {
      const tab = activeTab();
      for (const pane of state.panes.values()) if (pane.observer) { pane.observer.disconnect(); pane.observer = null; pane.mount = null; }
      elements.panes.replaceChildren(); elements.panes.className = tab && tab.orientation === 'vertical' ? 'split-vertical' : ''; if (tab) { elements.panes.id = 'tab-panel-' + tab.id; elements.panes.setAttribute('role', 'tabpanel'); elements.panes.setAttribute('aria-labelledby', 'tab-' + tab.id); }
      if (!tab) { notice(state.browserMode ? 'Browser fallback: no native launch bridge. Existing owned panes can be viewed if the terminal service is running.' : 'Use New terminal to request native enrollment and confirmation.'); return; }
      for (const id of tab.paneIds) {
        const pane = state.panes.get(id); if (!pane) continue;
        const card = document.createElement('article'); card.className = 'pane';
        const head = document.createElement('div'); head.className = 'pane-head';
        const meta = document.createElement('span'); meta.className = 'pane-meta'; meta.textContent = [pane.profileName, pane.projectName, pane.workingDirectory, pane.sessionId || (pane.sourceSessionId ? 'source ' + pane.sourceSessionId : 'session unknown')].filter(Boolean).map(safeText).join(' · ');
        const status = document.createElement('span'); status.className = 'pane-state'; status.id = 'pane-state-' + pane.id; status.textContent = pane.status === 'exited' ? 'Exited' + (pane.exitCode == null ? '' : ' (' + safeText(pane.exitCode) + ')') : (pane.connectionLabel || 'Connecting…');
        const reconnect = document.createElement('button'); reconnect.type = 'button'; reconnect.textContent = 'Reconnect'; reconnect.addEventListener('click', () => { if (!pane.socket) attach(pane); });
        const restart = document.createElement('button'); restart.type = 'button'; restart.textContent = 'Restart'; restart.disabled = state.browserMode; restart.addEventListener('click', () => nativePost({ kind: 'restartPane', paneId: pane.id }));
        const close = document.createElement('button'); close.type = 'button'; close.textContent = 'Close'; close.addEventListener('click', async () => { if (!window.confirm('Stop and remove this terminal pane?')) return; invalidateAttachment(pane); try { const response = await window.fetch('/api/panes/' + encodeURIComponent(pane.id) + '/close', { method: 'POST', credentials: 'omit', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ confirmed: true }) }); if (!response.ok) throw new Error('Close request failed (' + response.status + ')'); await refreshPanes(); } catch (error) { updatePaneStatus(pane, 'Detached — reconnect manually'); notice(error.message, true); } });
        head.append(meta, status, reconnect, restart, close); const mount = document.createElement('div'); mount.className = 'terminal'; card.append(head, mount); elements.panes.appendChild(card); makeTerminal(pane, mount);
        if (pane.id === focusedPaneId) window.requestAnimationFrame(() => pane.terminal.focus());
      }
    }
    function focusedPaneId() { for (const pane of state.panes.values()) if (pane.terminal && pane.terminal.element && pane.terminal.element.contains(document.activeElement)) return pane.id; return null; }
    function render() { const focused = focusedPaneId(); renderTabs(); renderSplitOptions(activeTab()); renderPanes(focused); }
    function split(orientation) { const id = elements.splitPane.value; if (!state.panes.has(id)) return; const result = moveIntoSplit(state.tabs, state.activeTab, id, orientation); if (!result) return; state.tabs = result.tabs; state.activeTab = result.activeId; render(); }
    function unsplit() { const result = unsplitTab(state.tabs, state.activeTab, uuid()); if (!result) return; state.tabs = result.tabs; state.activeTab = result.activeId; render(); }
    elements.launch.addEventListener('click', () => nativePost({ kind: 'chooseLaunch' }));
    elements.refresh.addEventListener('click', refreshPanes); elements.splitHorizontal.addEventListener('click', () => split('horizontal')); elements.splitVertical.addEventListener('click', () => split('vertical')); elements.unsplit.addEventListener('click', unsplit);
    if (!state.browserMode) window.chrome.webview.addEventListener('message', event => { if (event.data && event.data.kind === 'panesChanged') refreshPanes(); });
    refreshPanes();
  }
  return { byteLength: byteLength, isInputFrame: isInputFrame, isResizeFrame: isResizeFrame, ticketBody: ticketBody, registerBlockedOscHandlers: registerBlockedOscHandlers, moveIntoSplit: moveIntoSplit, unsplitTab: unsplitTab, outboundError: outboundError, nextOutputBytes: nextOutputBytes, releaseOutputBytes: releaseOutputBytes, advanceRefresh: advanceRefresh, safeText: safeText, start: start };
});
