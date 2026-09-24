
const WebSocket = require('ws');

const PORT = process.env.PORT ? parseInt(process.env.PORT, 10) : 7777;
const BROADCAST_HZ = 60;
const STALE_MS = 5000;

const MAX_CONNECTIONS = 500;
const MAX_CONNECTIONS_PER_IP = 20;
const MAX_PAYLOAD_BYTES = 8 * 1024;
const MAX_MSGS_PER_SEC = 120; // real gameplay sends every 2 frames; this is generous headroom
const HEARTBEAT_MS = 8 * 1000;
// A Unity scene load can stall the client's network thread long enough to
// miss one ping - require a few consecutive misses before reaping.
const MAX_MISSED_HEARTBEATS = 3;
const MAX_LOBBIES = 500;
const MAX_LOBBY_MEMBERS = 16;
const MAX_CHAT_HISTORY = 50; // per lobby, so someone joining mid-conversation isn't lost

let nextId = 1;
let nextLobbyId = 1;
const clients = new Map();  // id -> { ws, name, lobbyId, ip, msgCount, msgWindowStart }
const states = new Map();   // id -> { x, y, facingRight, animState, animSpeed, isPaused, lastUpdate }
const lobbies = new Map();  // lobbyId -> { name, hostId, members: Set<id>, chatHistory: [], hostToken }
const ipCounts = new Map(); // ip -> count of open connections
const tokenToLobby = new Map(); // client-generated session token -> lobbyId, so a reconnecting host can reclaim it

function send(ws, obj) {
  if (ws.readyState !== WebSocket.OPEN) return;
  try { ws.send(JSON.stringify(obj)); } catch (e) { /* socket already gone */ }
}

function lobbySummary(lobbyId) {
  const l = lobbies.get(lobbyId);
  if (!l) return null;
  const hostClient = clients.get(l.hostId);
  return {
    id: lobbyId,
    name: l.name,
    hostName: hostClient ? hostClient.name : '?',
    hostColor: hostClient ? hostClient.nameColor : '#FFFFFF',
    count: l.members.size,
    hard: !!l.hard,
    mapHubId: l.mapHubId || null,
    mapName: l.mapName || null,
  };
}

function leaveLobby(id) {
  const c = clients.get(id);
  if (!c || c.lobbyId == null) return;
  const l = lobbies.get(c.lobbyId);
  if (l) {
    l.members.delete(id);
    if (l.members.size === 0) {
      lobbies.delete(c.lobbyId);
      if (l.hostToken) tokenToLobby.delete(l.hostToken);
    }
  }
  c.lobbyId = null;
}

function removeClient(id) {
  const c = clients.get(id);
  if (c) {
    const n = (ipCounts.get(c.ip) || 1) - 1;
    if (n <= 0) ipCounts.delete(c.ip); else ipCounts.set(c.ip, n);
  }
  leaveLobby(id);
  clients.delete(id);
  states.delete(id);
  console.log(`[-] player ${id} disconnected (${clients.size} connected)`);
}

// A connection arriving through the portal's local reverse proxy always has
// 127.0.0.1 as its immediate TCP peer, which would otherwise turn the
// per-IP cap into a global cap on all public traffic combined. Cloudflare
// injects the real origin IP into CF-Connecting-IP/X-Forwarded-For on every
// request it proxies - trust those ONLY when the peer is genuinely the
// local proxy, since a direct (non-proxied) connection could set either
// header to whatever it wants to dodge the cap.
function getClientIp(req) {
  const remote = req.socket.remoteAddress || 'unknown';
  const isLoopback = remote === '127.0.0.1' || remote === '::1' || remote === '::ffff:127.0.0.1';
  if (isLoopback) {
    const cf = req.headers['cf-connecting-ip'];
    if (typeof cf === 'string' && cf) return cf;
    const xff = req.headers['x-forwarded-for'];
    if (typeof xff === 'string' && xff) return xff.split(',')[0].trim();
  }
  return remote;
}

const wss = new WebSocket.Server({
  port: PORT,
  maxPayload: MAX_PAYLOAD_BYTES,
  verifyClient: (info, cb) => {
    const ip = getClientIp(info.req);
    if (clients.size >= MAX_CONNECTIONS) { cb(false, 503, 'Server full'); return; }
    if ((ipCounts.get(ip) || 0) >= MAX_CONNECTIONS_PER_IP) { cb(false, 429, 'Too many connections from this address'); return; }
    cb(true);
  },
});

wss.on('connection', (ws, req) => {
  const ip = getClientIp(req);
  ipCounts.set(ip, (ipCounts.get(ip) || 0) + 1);

  // Disable Nagle's algorithm - it delays these small, frequent (60Hz) writes.
  if (req.socket && typeof req.socket.setNoDelay === 'function') req.socket.setNoDelay(true);

  const id = nextId++;
  ws.isAlive = true;
  ws.on('pong', () => { ws.isAlive = true; });

  clients.set(id, {
    ws, name: 'Player' + id, nameColor: '#FFFFFF', dotColor: '#3399FF',
    lobbyId: null, ip, msgCount: 0, msgWindowStart: Date.now(),
  });
  send(ws, { type: 'welcome', id });
  console.log(`[+] player ${id} connected from ${ip} (${clients.size} connected)`);

  ws.on('message', (data) => {
    const c = clients.get(id);
    if (!c) return;

    const now = Date.now();
    if (now - c.msgWindowStart >= 1000) {
      c.msgWindowStart = now;
      c.msgCount = 0;
    }
    c.msgCount++;
    if (c.msgCount > MAX_MSGS_PER_SEC) {
      ws.close(1008, 'rate limit');
      return;
    }

    let msg;
    try { msg = JSON.parse(data.toString('utf8')); } catch (e) { return; }
    if (!msg || typeof msg !== 'object') return;

    try { handleMessage(id, msg); } catch (e) { console.error(`[!] handleMessage error (player ${id}):`, e); }
  });

  const cleanup = () => removeClient(id);
  ws.on('close', cleanup);
  ws.on('error', cleanup);
});

// strips control/newline characters from player-supplied names before they're
// stored - otherwise they end up verbatim in console.log lines (log injection)
// and can wrap the lobby list/chat UI oddly on the client
function sanitizeName(s, maxLen) {
  return s.replace(/[\x00-\x1F\x7F]/g, '').slice(0, maxLen);
}

function clampNum(v, fallback, min, max) {
  if (!Number.isFinite(v)) return fallback;
  if (v < min) return min;
  if (v > max) return max;
  return v;
}

function handleMessage(id, msg) {
  const c = clients.get(id);
  if (!c) return;

  switch (msg.type) {
    case 'state': {
      if (typeof msg.name === 'string' && msg.name.length > 0) c.name = sanitizeName(msg.name, 24);
      // per-client, not per-frame, so a snapshot still includes it even without a fresh state line this tick
      const hexRe = /^#[0-9a-fA-F]{6}$/;
      if (hexRe.test(msg.nameColor)) c.nameColor = msg.nameColor;
      if (hexRe.test(msg.dotColor)) c.dotColor = msg.dotColor;
      states.set(id, {
        x: clampNum(msg.x, 0, -1e6, 1e6),
        y: clampNum(msg.y, 0, -1e6, 1e6),
        facingRight: !!msg.facingRight,
        animState: Number.isFinite(msg.animState) ? clampNum(msg.animState | 0, 0, 0, 63) : 0,
        animSpeed: clampNum(msg.animSpeed, 1, -10, 10),
        isPaused: !!msg.isPaused,
        lastUpdate: Date.now(),
      });
      break;
    }
    case 'host': {
      if (typeof msg.playerName === 'string' && msg.playerName.trim().length > 0) c.name = sanitizeName(msg.playerName, 24);
      const token = typeof msg.token === 'string' && /^[a-zA-Z0-9-]{1,64}$/.test(msg.token) ? msg.token : null;
      const reclaimId = token ? tokenToLobby.get(token) : null;
      const reclaim = reclaimId != null ? lobbies.get(reclaimId) : null;
      if (reclaim) {
        leaveLobby(id);
        reclaim.hostId = id;
        reclaim.members.add(id);
        c.lobbyId = reclaimId;
        send(c.ws, { type: 'hosted', lobbyId: reclaimId, name: reclaim.name, mapHubId: reclaim.mapHubId, mapName: reclaim.mapName, hard: reclaim.hard });
        console.log(`[lobby] ${id} reclaimed host of "${reclaim.name}" (#${reclaimId})`);
        break;
      }
      if (lobbies.size >= MAX_LOBBIES) {
        send(c.ws, { type: 'join_failed', reason: 'Server is full' });
        break;
      }
      leaveLobby(id);
      const lobbyId = nextLobbyId++;
      const name = (typeof msg.name === 'string' && msg.name.trim().length > 0) ? sanitizeName(msg.name, 32) : (c.name + "'s lobby");
      const mapHubId = typeof msg.mapHubId === 'string' && /^[a-zA-Z0-9-]{1,64}$/.test(msg.mapHubId) ? msg.mapHubId : null;
      const mapName = mapHubId && typeof msg.mapName === 'string' ? sanitizeName(msg.mapName, 48) : null;
      const hard = !!msg.hard;
      lobbies.set(lobbyId, { name, hostId: id, members: new Set([id]), chatHistory: [], mapHubId, mapName, hard, hostToken: token });
      if (token) tokenToLobby.set(token, lobbyId);
      c.lobbyId = lobbyId;
      send(c.ws, { type: 'hosted', lobbyId, name, mapHubId, mapName, hard });
      console.log(`[lobby] ${id} hosted "${name}" (#${lobbyId})${mapHubId ? ` map=${mapHubId}` : ''}`);
      break;
    }
    case 'list_lobbies': {
      const list = [...lobbies.keys()].map(lobbySummary).filter(Boolean);
      send(c.ws, { type: 'lobby_list', lobbies: list });
      break;
    }
    case 'join_lobby': {
      if (typeof msg.playerName === 'string' && msg.playerName.trim().length > 0) c.name = sanitizeName(msg.playerName, 24);
      const lobbyId = msg.lobbyId | 0;
      const l = lobbies.get(lobbyId);
      if (!l) {
        send(c.ws, { type: 'join_failed', reason: 'Lobby not found' });
        break;
      }
      if (l.members.size >= MAX_LOBBY_MEMBERS) {
        send(c.ws, { type: 'join_failed', reason: 'Lobby is full' });
        break;
      }
      leaveLobby(id);
      l.members.add(id);
      c.lobbyId = lobbyId;
      send(c.ws, { type: 'joined', lobbyId, name: l.name, history: l.chatHistory, mapHubId: l.mapHubId || null, mapName: l.mapName || null, hard: !!l.hard });
      console.log(`[lobby] ${id} joined "${l.name}" (#${lobbyId})`);
      break;
    }
    case 'leave_lobby': {
      leaveLobby(id);
      send(c.ws, { type: 'left' });
      break;
    }
    // Generic passthrough for anything built on top of DOTnet (game modes,
    // addon mods, etc.) - the relay never needs to understand the payload.
    case 'game_msg': {
      if (c.lobbyId == null) break;
      const l = lobbies.get(c.lobbyId);
      if (!l) break;
      for (const memberId of l.members) {
        const mc = clients.get(memberId);
        if (mc) send(mc.ws, { type: 'game_msg', from: id, payload: msg.payload });
      }
      break;
    }
    case 'chat': {
      if (c.lobbyId == null) break;
      const l = lobbies.get(c.lobbyId);
      if (!l) break;
      const text = typeof msg.text === 'string' ? msg.text.slice(0, 240).trim() : '';
      if (!text) break;
      const entry = { from: c.name, fromColor: c.nameColor, text };
      l.chatHistory.push(entry);
      if (l.chatHistory.length > MAX_CHAT_HISTORY) l.chatHistory.shift();
      for (const memberId of l.members) {
        const mc = clients.get(memberId);
        if (mc) send(mc.ws, { type: 'chat', ...entry });
      }
      console.log(`[chat] #${c.lobbyId} ${c.name}: ${text}`);
      break;
    }
  }
}

setInterval(() => {
  const now = Date.now();
  for (const [id, s] of states) {
    if (now - s.lastUpdate > STALE_MS) states.delete(id);
  }

  for (const l of lobbies.values()) {
    const members = [...l.members];
    const all = members
      .filter((id) => states.has(id))
      .map((id) => ({
        id,
        name: clients.get(id).name,
        nameColor: clients.get(id).nameColor,
        dotColor: clients.get(id).dotColor,
        ...states.get(id),
      }));

    for (const id of members) {
      const c = clients.get(id);
      if (!c) continue;
      send(c.ws, {
        type: 'snapshot',
        players: all.filter((p) => p.id !== id).map(({ lastUpdate, ...rest }) => rest),
      });
    }
  }
}, 1000 / BROADCAST_HZ);

// ws has no built-in idle timeout (unlike a raw socket's setTimeout) -- a
// standard ping/pong heartbeat instead, terminating anything that didn't
// answer the previous ping.
setInterval(() => {
  wss.clients.forEach((ws) => {
    if (ws.isAlive === false) {
      ws.missedPings = (ws.missedPings || 0) + 1;
      if (ws.missedPings >= MAX_MISSED_HEARTBEATS) { ws.terminate(); return; }
    } else {
      ws.missedPings = 0;
    }
    ws.isAlive = false;
    try { ws.ping(); } catch (e) { /* already gone */ }
  });
}, HEARTBEAT_MS);

process.on('uncaughtException', (e) => console.error('[!] uncaught exception:', e));
process.on('unhandledRejection', (e) => console.error('[!] unhandled rejection:', e));

for (const sig of ['SIGTERM', 'SIGINT']) {
  process.on(sig, () => {
    console.log(`[i] ${sig} received, shutting down`);
    wss.close(() => process.exit(0));
    setTimeout(() => process.exit(0), 2000).unref();
  });
}

console.log(`IGTAP multiplayer relay (WebSocket) listening on :${PORT}`);
