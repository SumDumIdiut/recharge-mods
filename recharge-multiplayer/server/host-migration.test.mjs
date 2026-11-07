import { spawn } from 'child_process';
import { fileURLToPath } from 'url';
import path from 'path';
import WebSocket from 'ws';
const SRV = path.dirname(fileURLToPath(import.meta.url));
const srv = spawn('node', ['server.js'], { cwd: SRV, env: { ...process.env, PORT: '7799' }, stdio: ['ignore', 'pipe', 'pipe'] });
const fail = (m) => { console.log('FAIL', m); srv.kill('SIGKILL'); process.exit(1); };
await new Promise((r) => setTimeout(r, 800));
const conn = () => new WebSocket('ws://127.0.0.1:7799');
const once = (ws, type, ms = 3000) => new Promise((res, rej) => {
  const t = setTimeout(() => rej(new Error('timeout waiting for ' + type)), ms);
  const h = (d) => { const m = JSON.parse(d); if (m.type === type) { clearTimeout(t); ws.off('message', h); res(m); } };
  ws.on('message', h);
});
const list = async (ws) => { ws.send(JSON.stringify({ type: 'list_lobbies' })); return (await once(ws, 'lobby_list')).lobbies; };
try {
  const host = conn(); await once(host, 'welcome');
  host.send(JSON.stringify({ type: 'host', name: 'T lobby', playerName: 'HostGuy', token: 'tok1' }));
  const hosted = await once(host, 'hosted');
  const j1 = conn(); await once(j1, 'welcome');
  j1.send(JSON.stringify({ type: 'join_lobby', lobbyId: hosted.lobbyId, playerName: 'First' })); await once(j1, 'joined');
  const j2 = conn(); await once(j2, 'welcome');
  j2.send(JSON.stringify({ type: 'join_lobby', lobbyId: hosted.lobbyId, playerName: 'Second' })); await once(j2, 'joined');
  const obs = conn(); await once(obs, 'welcome');
  let l = await list(obs);
  if (l.length !== 1 || l[0].hostName !== 'HostGuy' || l[0].count !== 3) fail('before: ' + JSON.stringify(l));

  const promo = once(j1, 'hosted'); const note = once(j2, 'chat');
  host.terminate();
  const p = await promo;
  if (!p.promoted || p.lobbyId !== hosted.lobbyId || p.name !== 'T lobby') fail('bad promote msg ' + JSON.stringify(p));
  const n = await note;
  if (!/First is now the host/.test(n.text)) fail('bad notice ' + n.text);
  l = await list(obs);
  if (l.length !== 1 || l[0].hostName !== 'First' || l[0].count !== 2) fail('after first migration: ' + JSON.stringify(l));

  // new host leaves too -> next member promoted
  const promo2 = once(j2, 'hosted');
  j1.terminate();
  await promo2;
  l = await list(obs);
  if (l.length !== 1 || l[0].hostName !== 'Second' || l[0].count !== 1) fail('after second migration: ' + JSON.stringify(l));

  // last member leaves -> lobby deleted
  j2.terminate();
  await new Promise((r) => setTimeout(r, 300));
  l = await list(obs);
  if (l.length !== 0) fail('lobby should be gone: ' + JSON.stringify(l));
  console.log('PASS host passes First -> Second, promoted client notified, others told, lobby deleted only when empty');
  srv.kill('SIGKILL'); process.exit(0);
} catch (e) { fail(e.message); }
