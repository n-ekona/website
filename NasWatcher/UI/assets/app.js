'use strict';

/* ------------------------------------------------------------------ bridge */

let seq = 0;
const pending = new Map();

function call(action, payload) {
  return new Promise((resolve, reject) => {
    if (!window.chrome || !chrome.webview) {
      reject(new Error('ホストとの接続がありません (WebView2 ブリッジ不可)'));
      return;
    }
    const id = ++seq;
    pending.set(id, { resolve, reject });
    chrome.webview.postMessage({ id, action, payload: payload || {} });
  });
}

window.addEventListener('error', (event) => {
  console.error(event.error || event.message);
});

const handledEvents = new WeakSet();

function handleMessage(event) {
  if (!event || typeof event !== 'object') return;
  if (handledEvents.has(event)) return;
  handledEvents.add(event);

  let msg = event.data;
  if (typeof msg === 'string') {
    try { msg = JSON.parse(msg); } catch (e) { return; }
  }
  if (!msg || typeof msg !== 'object') return;

  if (msg.id && pending.has(msg.id)) {
    const entry = pending.get(msg.id);
    pending.delete(msg.id);
    if (msg.ok === false) {
      entry.reject(new Error(msg.error || 'エラー'));
    } else {
      entry.resolve(msg.data);
    }
    return;
  }

  switch (msg.event) {
    case 'speed':       applySpeed(msg.data); break;
    case 'hotspot':     applyHotspot(msg.data); break;
    case 'scanStatus':  setScanStatus(msg.data.text, msg.data.state); break;
    case 'scanHosts':   renderHosts(msg.data); break;
    case 'status':      toast(msg.data.text, msg.data.kind || 'info'); break;
    case 'toast':       toast(msg.data.text, msg.data.kind || 'info'); break;
  }
}

// ホストからのメッセージは chrome.webview と window のどちらでも届く
window.addEventListener('message', handleMessage);
if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', handleMessage);
}

/* ------------------------------------------------------------------ helpers */

const $ = (id) => document.getElementById(id);

function toast(text, kind = 'info') {
  const el = document.createElement('div');
  el.className = 'toast ' + kind;
  el.textContent = text;
  $('toasts').appendChild(el);
  setTimeout(() => {
    el.style.transition = 'opacity .3s, transform .3s';
    el.style.opacity = '0';
    el.style.transform = 'translateX(20px)';
    setTimeout(() => el.remove(), 320);
  }, 2800);
}

function bytes(value) {
  if (value === null || value === undefined || isNaN(value)) return '-';
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  let n = value, i = 0;
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
  return (i === 0 ? n.toFixed(0) : n.toFixed(n >= 100 ? 0 : 2)) + ' ' + units[i];
}

function speed(value) {
  if (value === null || value === undefined || isNaN(value)) return '-';
  const mbps = value * 8 / 1e6;
  return bytes(value) + '/s  (' + (mbps >= 100 ? mbps.toFixed(0) : mbps.toFixed(1)) + ' Mbps)';
}

function fillSelect(select, items, selectedValue) {
  const current = selectedValue !== undefined ? selectedValue : select.value;
  select.innerHTML = '';
  items.forEach((item) => {
    const opt = document.createElement('option');
    opt.value = item.value;
    opt.textContent = item.text;
    select.appendChild(opt);
  });
  if (current && items.some((i) => String(i.value) === String(current))) {
    select.value = current;
  }
}

/* ------------------------------------------------------------------ state */

const state = {
  adapters: [],
  hosts: [],
  selectedHost: null,
  shares: [],
  hotspot: null,
  speedAdapterId: null,
  speedAdapterName: null,
  scanning: false,
  nasIp: null,
  passVisible: false,
};

/* ------------------------------------------------------------------ adapters */

async function loadAdapters() {
  const data = await call('loadAdapters');
  state.adapters = data.adapters || [];
  state.nasIp = data.nasIp || null;
  fillSelect($('adapterSelect'), state.adapters.map((a) => ({ value: a.id, text: a.name })), data.selectedId);
  $('adapterSelect').onchange = async () => {
    await call('selectAdapter', { id: $('adapterSelect').value });
    updateAdapterInfo();
  };
  updateAdapterInfo(data.info || '');
  if (data.hotspot) applyHotspot(data.hotspot);
}

function updateAdapterInfo(text) {
  const el = $('adapterInfo');
  if (text) { el.textContent = text; return; }
  const a = state.adapters.find((x) => x.id === $('adapterSelect').value);
  if (!a) { el.textContent = '有線LANアダプターが見つかりません。'; return; }
  el.textContent = a.description + '  /  IP: ' + (a.ip || '未接続') + '  /  ゲートウェイ: ' + (a.gateway || 'なし') + '  /  ' + (a.up ? '接続中' : '切断');
}

/* ------------------------------------------------------------------ scan */

async function startScan() {
  if (state.scanning) return;
  state.scanning = true;
  $('scanBtn').disabled = true;
  $('cancelScanBtn').hidden = false;
  setScanStatus('スキャン中...', 'busy');
  $('hostTable').querySelector('tbody').innerHTML = '<tr class="empty"><td colspan="4">スキャンしています…</td></tr>';

  try {
    const extra = $('extraIp').value.trim();
    await call('scan', { extraIp: extra });
  } catch (err) {
    toast(err.message, 'err');
    setScanStatus('スキャンに失敗しました: ' + err.message, 'error');
  } finally {
    state.scanning = false;
    $('scanBtn').disabled = false;
    $('cancelScanBtn').hidden = true;
  }
}

function setScanStatus(text, kind) {
  $('scanStatus').textContent = text;
  const pulse = $('scanPulse');
  pulse.className = 'pulse' + (kind === 'busy' ? ' active' : kind === 'error' ? ' err' : kind === 'ok' ? ' ok' : '');
}

function renderHosts(hosts) {
  state.hosts = hosts || [];
  const tbody = $('hostTable').querySelector('tbody');
  tbody.innerHTML = '';

  if (state.hosts.length === 0) {
    tbody.innerHTML = '<tr class="empty"><td colspan="4">NAS 候補は見つかりませんでした</td></tr>';
    return;
  }

  state.hosts.forEach((h) => {
    const tr = document.createElement('tr');
    tr.dataset.ip = h.ip;

    const tdIp = document.createElement('td');
    tdIp.textContent = h.ip;
    tr.appendChild(tdIp);

    const tdName = document.createElement('td');
    tdName.textContent = h.hostName || '—';
    tr.appendChild(tdName);

    const tdKind = document.createElement('td');
    const tag = document.createElement('span');
    const kindClass = h.kind.indexOf('NAS') === 0 ? 'nas' : (h.kind.indexOf('Web') === 0 ? 'web' : 'other');
    tag.className = 'tag ' + kindClass;
    tag.textContent = h.kind;
    tdKind.appendChild(tag);
    tr.appendChild(tdKind);

    const tdLat = document.createElement('td');
    tdLat.className = 'num';
    tdLat.textContent = h.latency >= 0 ? h.latency.toFixed(0) + ' ms' : '—';
    tr.appendChild(tdLat);

    tr.onclick = async () => {
      tbody.querySelectorAll('tr').forEach((r) => r.classList.remove('selected'));
      tr.classList.add('selected');
      state.selectedHost = h;
      state.nasIp = h.ip;
      await call('selectHost', { ip: h.ip, ports: h.ports });
      loadLanInfo();
      await loadShares();
    };

    tbody.appendChild(tr);
  });
}

/* ------------------------------------------------------------------ shares / capacity */

async function loadShares() {
  if (!state.selectedHost) return;
  const data = await call('listShares');
  state.shares = data.shares || [];

  const list = $('shareOptions');
  list.innerHTML = '';
  state.shares.forEach((s) => {
    const opt = document.createElement('option');
    opt.value = s;
    list.appendChild(opt);
  });

  const current = $('shareSelect').value;
  if (data.selected) {
    $('shareSelect').value = data.selected;
  } else if (!current && state.shares.length > 0) {
    $('shareSelect').value = state.shares[0];
  }

  if (state.shares.length > 0) {
    await refreshCapacity();
  } else {
    setUsage(null);
    $('capacityDetail').textContent = data.error || '共有を自動取得できませんでした。共有名を入力して［更新］を押してください。';
  }
  updateSmbPath();
}

async function refreshCapacity() {
  const share = $('shareSelect').value.trim();
  if (!state.selectedHost && !state.nasIp) {
    setUsage(null);
    $('capacityDetail').textContent = 'NAS を選択するか、IP を設定してください。';
    return;
  }
  $('capacityDetail').textContent = '取得中...';
  const data = await call('getCapacity', { share });
  if (!data) {
    setUsage(null);
    $('capacityDetail').textContent = '取得できませんでした。';
    return;
  }
  if (data.ok === false) {
    setUsage(null);
    $('capacityDetail').textContent = (data.error || '取得できませんでした。') + (data.hint ? ' ' + data.hint : '');
    return;
  }
  setUsage(data);
  $('capacityDetail').textContent = data.detail;
}

function setUsage(data) {
  const fill = $('usageFill');
  if (!data) {
    fill.style.width = '0%';
    fill.className = 'usage-fill';
    $('usageText').textContent = 'NAS と共有を選択してください';
    $('usagePercent').textContent = '—';
    return;
  }
  const pct = Math.max(0, Math.min(100, data.percent));
  fill.style.width = pct + '%';
  fill.className = 'usage-fill' + (pct >= 90 ? ' danger' : pct >= 75 ? ' warn' : '');
  $('usageText').textContent = data.used + ' / ' + data.total;
  $('usagePercent').textContent = pct + '%';
}

/* ------------------------------------------------------------------ smb path */

function updateSmbPath() {
  const ip = state.selectedHost ? state.selectedHost.ip : '';
  const share = $('shareSelect').value.trim();
  $('smbPath').value = !ip ? '' : (!share ? '\\\\' + ip : '\\\\' + ip + '\\' + share);
  $('smbBadge').textContent = ip ? ip : '未選択';
}

async function copyPath() {
  const path = $('smbPath').value;
  if (!path) { toast('コピーする IP を選択してください', 'warn'); return; }
  try {
    await call('copy', { text: path });
    toast('クリップボードにコピーしました: ' + path, 'ok');
  } catch (err) {
    toast('コピーに失敗しました: ' + err.message, 'err');
  }
}

/* ------------------------------------------------------------------ speed */

let samples = [];
const MAX_SAMPLES = 120;

function applySpeed(data) {
  const rx = data.rx || 0;
  const tx = data.tx || 0;
  $('rxValue').textContent = speed(rx);
  $('txValue').textContent = speed(tx);

  samples.push({ rx, tx });
  if (samples.length > MAX_SAMPLES) samples.shift();
  drawChart();

  const opts = data.adapters || [];
  fillSelect($('speedSelect'), opts.map((a) => ({ value: a.id, text: a.name })), data.id);
  $('speedSelect').onchange = () => {
    samples = [];
    drawChart();
    call('selectSpeedAdapter', { id: $('speedSelect').value });
  };
}

function drawChart() {
  const canvas = $('chart');
  const dpr = window.devicePixelRatio || 1;
  const rect = canvas.parentElement.getBoundingClientRect();
  const w = Math.max(1, Math.floor(rect.width));
  const h = Math.max(1, Math.floor(rect.height));
  if (canvas.width !== w * dpr || canvas.height !== h * dpr) {
    canvas.width = w * dpr;
    canvas.height = h * dpr;
  }

  const ctx = canvas.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);

  const padL = 62, padR = 8, padT = 10, padB = 18;
  const cw = w - padL - padR;
  const ch = h - padT - padB;

  let max = 0;
  samples.forEach((s) => { max = Math.max(max, s.rx, s.tx); });
  const scaleMax = Math.max(max * 1.2, 1024 * 1024);

  ctx.font = '10px "Segoe UI", sans-serif';
  ctx.textBaseline = 'middle';
  for (let i = 0; i <= 4; i++) {
    const y = padT + ch * i / 4;
    ctx.strokeStyle = 'rgba(255,255,255,0.07)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(padL, y + 0.5);
    ctx.lineTo(padL + cw, y + 0.5);
    ctx.stroke();

    ctx.fillStyle = 'rgba(139,152,169,0.9)';
    ctx.textAlign = 'right';
    ctx.fillText(bytes(scaleMax * (4 - i) / 4), padL - 8, y);
  }

  if (samples.length < 2) {
    ctx.fillStyle = 'rgba(139,152,169,0.7)';
    ctx.textAlign = 'left';
    ctx.fillText('データ待ち...', padL + 10, padT + 14);
    return;
  }

  drawSeries(ctx, samples.map((s) => s.rx), 'rx', cw, ch, padL, padT, scaleMax);
  drawSeries(ctx, samples.map((s) => s.tx), 'tx', cw, ch, padL, padT, scaleMax);
}

function drawSeries(ctx, values, kind, cw, ch, padL, padT, scaleMax) {
  const color = kind === 'rx' ? [56, 189, 248] : [251, 146, 60];
  const rgb = color.join(',');
  const step = cw / (values.length - 1);

  const pts = values.map((v, i) => {
    const ratio = Math.max(0, Math.min(1, v / scaleMax));
    return [padL + i * step, padT + ch - ch * ratio];
  });

  // 滑らかな曲線 (Catmull-Rom)
  ctx.beginPath();
  ctx.moveTo(pts[0][0], pts[0][1]);
  for (let i = 0; i < pts.length - 1; i++) {
    const p0 = pts[Math.max(0, i - 1)];
    const p1 = pts[i];
    const p2 = pts[i + 1];
    const p3 = pts[Math.min(pts.length - 1, i + 2)];
    const c1x = p1[0] + (p2[0] - p0[0]) / 6;
    const c1y = p1[1] + (p2[1] - p0[1]) / 6;
    const c2x = p2[0] - (p3[0] - p1[0]) / 6;
    const c2y = p2[1] - (p3[1] - p1[1]) / 6;
    ctx.bezierCurveTo(c1x, c1y, c2x, c2y, p2[0], p2[1]);
  }

  const line = new Path2D();
  // 塗りつぶし
  ctx.save();
  ctx.lineTo(pts[pts.length - 1][0], padT + ch);
  ctx.lineTo(pts[0][0], padT + ch);
  ctx.closePath();
  const grad = ctx.createLinearGradient(0, padT, 0, padT + ch);
  grad.addColorStop(0, 'rgba(' + rgb + ',0.34)');
  grad.addColorStop(1, 'rgba(' + rgb + ',0.02)');
  ctx.fillStyle = grad;
  ctx.fill();
  ctx.restore();

  ctx.beginPath();
  ctx.moveTo(pts[0][0], pts[0][1]);
  for (let i = 0; i < pts.length - 1; i++) {
    const p0 = pts[Math.max(0, i - 1)];
    const p1 = pts[i];
    const p2 = pts[i + 1];
    const p3 = pts[Math.min(pts.length - 1, i + 2)];
    ctx.bezierCurveTo(
      p1[0] + (p2[0] - p0[0]) / 6, p1[1] + (p2[1] - p0[1]) / 6,
      p2[0] - (p3[0] - p1[0]) / 6, p2[1] - (p3[1] - p1[1]) / 6,
      p2[0], p2[1]);
  }
  ctx.strokeStyle = 'rgb(' + rgb + ')';
  ctx.lineWidth = 1.8;
  ctx.lineJoin = 'round';
  ctx.stroke();
}

/* ------------------------------------------------------------------ hotspot */

function applyHotspot(data) {
  const wasOn = state.hotspot ? state.hotspot.on : null;
  state.hotspot = data;
  const toggle = $('hotspotToggle');
  toggle.disabled = data.busy === true;
  toggle.checked = data.on === true;

  const st = $('hsState');
  st.textContent = data.stateText || '—';
  st.className = data.on ? 'state-on' : (data.busy ? 'state-busy' : 'state-off');

  $('hsSsid').textContent = data.ssid || '—';
  updatePassphrase(data.passphrase || '—');
  $('hsClients').textContent = data.clientCount + ' / ' + data.maxClientCount + ' 台  ' + (data.clientsText || '');
  $('hsGateway').textContent = data.gatewayIp || '—';
  $('hsMessage').textContent = data.message || '';

  // ホットスポットの ON / OFF が切り替わったときにモバイル端末向けの情報を更新する
  if (wasOn !== null && wasOn !== data.on) loadLanInfo();
}

/* パスフレーズの表示 / 非表示 */
function updatePassphrase(pass) {
  const el = $('hsPass');
  const button = $('togglePass');
  if (!el) return;

  if (!pass || pass === '—') {
    el.textContent = '—';
    el.classList.remove('masked');
  } else if (state.passVisible) {
    el.textContent = pass;
    el.classList.remove('masked');
  } else {
    el.textContent = '•'.repeat(Math.min(pass.length, 14));
    el.classList.add('masked');
  }

  if (button) button.classList.toggle('revealed', state.passVisible);
}

async function toggleHotspot(checked) {
  const toggle = $('hotspotToggle');
  toggle.disabled = true;
  try {
    const data = await call('hotspot', { on: checked });
    applyHotspot(data);
    toast('モバイルホットスポットを ' + data.stateText + ' にしました', 'ok');
  } catch (err) {
    toast(err.message, 'err');
    applyHotspot(state.hotspot || {});
  } finally {
    toggle.disabled = false;
  }
}

/* ------------------------------------------------------------------ settings */

async function openSettings() {
  const s = await call('getSettings');
  $('setIp').value = s.nasIp || '';
  $('setShare').value = s.shareName || '';
  $('setWebPort').value = s.webPort;
  $('setAuthMode').value = s.authMode;
  $('setUser').value = s.username || '';
  $('setPassword').value = s.password || '';
  $('setTimeout').value = s.probeTimeoutMs;
  $('setSnmpCommunity').value = s.snmpCommunity || 'public';
  $('setSnmpPort').value = s.snmpPort || 161;
  $('setSaveCred').checked = s.saveCredentials;
  $('setAutoScan').checked = s.autoScanOnStart;
  $('setUseSnmp').checked = s.useSnmp !== false;
  $('settingsModal').classList.remove('hidden');
}

async function saveSettings() {
  await call('saveSettings', {
    nasIp: $('setIp').value,
    shareName: $('setShare').value,
    webPort: parseInt($('setWebPort').value, 10) || 5000,
    authMode: $('setAuthMode').value,
    username: $('setUser').value,
    password: $('setPassword').value,
    probeTimeoutMs: parseInt($('setTimeout').value, 10) || 400,
    snmpCommunity: $('setSnmpCommunity').value || 'public',
    snmpPort: parseInt($('setSnmpPort').value, 10) || 161,
    saveCredentials: $('setSaveCred').checked,
    autoScanOnStart: $('setAutoScan').checked,
    useSnmp: $('setUseSnmp').checked
  });
  $('settingsModal').classList.add('hidden');
  toast('設定を保存しました', 'ok');
}

/* ------------------------------------------------------------------ LAN (モバイル端末から) */

async function loadLanInfo() {
  try {
    const data = await call('lanInfo');
    $('lanGateway').textContent = data.gateway || '—';
    $('lanWebUrl').textContent = data.webUrl || '—';
    $('lanSmbUrl').textContent = data.smbUrl || '—';

    const toggle = $('lanToggle');
    toggle.textContent = data.enabled ? 'LAN アクセスを解除' : 'LAN アクセスを許可';
  } catch (err) {
    /* 失敗しても致命的ではない */
  }
}

async function testLan() {
  const el = $('lanResult');
  el.className = 'muted';
  el.textContent = 'テスト中...';

  try {
    const data = await call('lanTest', { ip: state.nasIp || '', port: 445 });
    el.className = 'muted ' + (data.ok ? 'ok' : 'err');
    el.textContent = data.message + (data.hint ? ' → ' + data.hint : '');
  } catch (err) {
    el.className = 'muted err';
    el.textContent = err.message;
  }
}

async function toggleLan() {
  const el = $('lanResult');
  el.className = 'muted';
  el.textContent = '適用中... (管理者権限の確認ダイアログ可能会出ます)';

  try {
    const enabled = $('lanToggle').textContent.indexOf('解除') >= 0;
    const data = await call(enabled ? 'lanDisable' : 'lanEnable');
    el.className = 'muted ' + (data.ok ? 'ok' : 'err');
    el.textContent = data.message;
    await loadLanInfo();
  } catch (err) {
    el.className = 'muted err';
    el.textContent = err.message;
  }
}

/* ------------------------------------------------------------------ リレー (TCP 中継) */

let relayRunning = false;

function relayUrl(data) {
  if (!data) return '—';
  if (data.targetPort === 445) return data.smbTarget || data.url || '—';
  return data.url || '—';
}

async function loadRelayInfo() {
  try {
    const data = await call('relayStatus');
    relayRunning = data.running === true;
    $('relayToggle').textContent = relayRunning ? 'リレーを停止' : 'リレーを開始';
    const url = relayUrl(data);
    $('relayUrl').textContent = url;
    $('relayUrlHint').textContent = url;

    const el = $('relayResult');
    if (relayRunning) {
      el.className = 'muted ok';
      el.textContent = `中継中: 待受 ${data.gateway}:${data.port} → NAS ${data.nasIp}:${data.targetPort}  (接続 ${data.connections} 件)`;
    } else {
      el.className = 'muted';
      el.textContent = '停止中です。';
    }
  } catch (err) {
    /* 失敗しても致命的ではない */
  }
}

async function toggleRelay() {
  const el = $('relayResult');
  el.className = 'muted';
  el.textContent = '処理中...';

  try {
    const targetPort = parseInt($('relayTarget').value, 10) || 80;

    if (!relayRunning && targetPort === 445) {
      el.textContent = 'SMB (445) を確保しています... 管理者権限の確認が表示されたら［はい］を押してください。';
    }

    const data = relayRunning
      ? await call('relayStop')
      : await call('relayStart', { targetPort });

    relayRunning = data.running === true;
    $('relayToggle').textContent = relayRunning ? 'リレーを停止' : 'リレーを開始';
    $('relayUrl').textContent = relayUrl(data);
    $('relayUrlHint').textContent = relayUrl(data);
    el.className = 'muted ' + (relayRunning ? 'ok' : '');
    el.textContent = relayRunning
      ? `中継を開始しました (待受ポート ${data.port} → NAS ${data.nasIp}:${data.targetPort})。` + (data.hint ? ' ' + data.hint : '')
      : 'リレーを停止しました。';
  } catch (err) {
    el.className = 'muted err';
    el.textContent = err.message;
  }
}

/* ------------------------------------------------------------------ init */

function bind() {
  $('scanBtn').onclick = startScan;
  $('cancelScanBtn').onclick = () => call('cancelScan');
  $('copyBtn').onclick = copyPath;
  $('openSmbBtn').onclick = () => call('openSmb');
  $('openWebBtn').onclick = () => call('openWeb');
  $('refreshShares').onclick = () => loadShares();
  $('refreshCapacity').onclick = () => refreshCapacity();
  $('shareSelect').onchange = () => { updateSmbPath(); refreshCapacity(); };
  $('refreshAdapters').onclick = () => loadAdapters();
  $('hotspotToggle').onchange = (e) => toggleHotspot(e.target.checked);
  $('togglePass').onclick = () => {
    state.passVisible = !state.passVisible;
    updatePassphrase(state.hotspot ? (state.hotspot.passphrase || '—') : '—');
  };
  $('openSettings').onclick = () => openSettings();
  $('lanTest').onclick = () => testLan();
  $('lanToggle').onclick = () => toggleLan();
  $('relayToggle').onclick = () => toggleRelay();
  $('copyRelayUrl').onclick = async () => {
    const url = $('relayUrl').textContent;
    if (!url || url === '—') return;
    try {
      await call('copy', { text: url });
      toast('URL をコピーしました: ' + url, 'ok');
    } catch (err) {
      toast(err.message, 'err');
    }
  };
  $('closeSettings').onclick = () => $('settingsModal').classList.add('hidden');
  $('cancelSettings').onclick = () => $('settingsModal').classList.add('hidden');
  $('saveSettings').onclick = () => saveSettings();
  $('openLog').onclick = () => call('openLog');
  $('settingsModal').onclick = (e) => { if (e.target === $('settingsModal')) $('settingsModal').classList.add('hidden'); };
  window.addEventListener('resize', drawChart);
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') $('settingsModal').classList.add('hidden'); });
}

async function init() {
  bind();
  fillSelect($('webPort'), ['80', '443', '5000', '5001', '8080'].map((v) => ({ value: v, text: v })), '5000');
  fillSelect($('relayTarget'), [
    { value: '445', text: 'SMB (445) - iPhone の「ファイル」向け' },
    { value: '5005', text: 'WebDAV (5005) - Files アプリ向け' },
    { value: '5006', text: 'WebDAV (5006) - HTTPS' },
    { value: '80', text: 'NAS の Web (80)' },
    { value: '443', text: 'NAS の Web (443)' },
    { value: '5000', text: 'NAS の Web (5000)' },
    { value: '5001', text: 'NAS の Web (5001)' },
  ], '445');
  await loadAdapters();
  updateSmbPath();
  await loadLanInfo();
  await loadRelayInfo();

  // Chromium のフォーム復元で前回の値が再適用されることがあるため、
  // 既定値 (SMB 445) を少し遅らせて明示し直す
  setTimeout(() => {
    if (!relayRunning) $('relayTarget').value = '445';
  }, 500);
}

init();
