namespace RtBot.Controllers;

/// <summary>The companion dashboard: one static page that polls the call's state and renders it with textContent only.</summary>
internal static class LivePage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<title>Aria — Live Meeting Intelligence</title>
<style>
:root{--bg:#0a0e17;--card:#121826;--sub:#172033;--ink:#f8fafc;--body:#cbd5e1;--mute:#94a3b8;--dim:#64748b;--line:rgba(255,255,255,.09);--wiq:#38bdf8;--skill:#a78bfa;--ok:#34d399;--warn:#fbbf24;--bad:#fb7185;--dec:#60a5fa;--pace:#818cf8}
body.light{--bg:#f8fafc;--card:#ffffff;--sub:#f1f5f9;--ink:#0f172a;--body:#334155;--mute:#64748b;--dim:#94a3b8;--line:#e2e8f0;--wiq:#0284c7;--skill:#7c3aed;--ok:#059669;--warn:#d97706;--bad:#e11d48;--dec:#2563eb;--pace:#4f46e5}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:14.5px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI",Inter,system-ui,sans-serif;-webkit-font-smoothing:antialiased}
.shell{max-width:1440px;margin:0 auto;padding:16px 20px 24px}
.topbar{display:flex;align-items:center;gap:14px;padding:12px 18px;background:var(--card);border:1px solid var(--line);border-radius:16px}
.orb{width:12px;height:12px;border-radius:50%;background:var(--ok);box-shadow:0 0 0 4px rgba(52,211,153,.16)}
.brand{font-weight:600;font-size:17px;letter-spacing:-.01em}
.pill{display:inline-flex;align-items:center;gap:6px;font-size:12px;font-weight:600;padding:3px 10px;border-radius:999px;border:1px solid var(--line);background:var(--sub);color:var(--body)}
.sep{width:1px;height:22px;background:var(--line)}
.subj{font-weight:600;font-size:16px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;flex:1}
.clock{font-size:20px;font-weight:600;font-variant-numeric:tabular-nums;text-align:right;line-height:1.1}
.rem{font-size:11.5px;color:var(--mute);text-align:right}
.theme{border:1px solid var(--line);background:var(--sub);color:var(--mute);border-radius:10px;padding:6px 10px;cursor:pointer;font:inherit;font-size:12px}
.strip{display:flex;align-items:center;justify-content:space-between;gap:12px;margin:12px 0;flex-wrap:wrap}
.skills-row{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.micro{font-size:11px;font-weight:600;letter-spacing:.08em;text-transform:uppercase;color:var(--dim)}
.sk{display:inline-flex;align-items:center;gap:6px;padding:4px 12px;border-radius:999px;font-size:12.5px;font-weight:500;background:var(--card);border:1px solid rgba(167,139,250,.35);color:var(--body)}
.sk.dyn{background:rgba(167,139,250,.14);border-color:rgba(167,139,250,.65);color:var(--ink)}
.pulse{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.mbar{width:42px;height:6px;background:rgba(148,163,184,.22);border-radius:4px;overflow:hidden;display:inline-block}.mbar i{display:block;height:100%}
.grid{display:grid;grid-template-columns:1.35fr .9fr .85fr;gap:14px}
.panel{background:var(--card);border:1px solid var(--line);border-radius:16px;display:flex;flex-direction:column;min-height:540px;max-height:calc(100vh - 156px)}
.ph{display:flex;align-items:center;justify-content:space-between;padding:13px 18px;border-bottom:1px solid var(--line);gap:8px}
.tabs{display:flex;gap:5px;flex-wrap:wrap}
.tab{border:1px solid transparent;background:transparent;border-radius:8px;padding:4px 10px;cursor:pointer;font:inherit;font-size:12.5px;font-weight:500;color:var(--mute)}
.tab.on{background:var(--sub);color:var(--ink);border-color:var(--line)}
.scroll{overflow:auto;padding:14px 16px;flex:1}
.icard{background:var(--sub);border:1px solid var(--line);border-left:3px solid var(--pace);border-radius:12px;padding:11px 14px;margin:0 0 10px}
.icard.search{background:rgba(56,189,248,.1);border-color:rgba(56,189,248,.4)}
.ihead{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.tag{font-size:10.5px;font-weight:700;letter-spacing:.06em;padding:2px 8px;border-radius:6px;text-transform:uppercase}
.ititle{font-weight:600;font-size:15px;color:var(--ink);flex:1}
.ibody{margin-top:5px;color:var(--body);font-size:14px}
.isrc{margin-top:5px;color:var(--mute);font-size:12px}
.osec{padding:0 0 14px;margin:0 0 14px;border-bottom:1px solid var(--line)}.osec:last-child{border-bottom:0;margin-bottom:0}
.oh{display:flex;justify-content:space-between;align-items:center;margin-bottom:8px}
.oitem{padding:6px 0 6px 12px;border-left:2px solid var(--line);margin-bottom:6px;font-size:14px}
.ometa{font-size:12px;color:var(--mute);margin-top:2px}
.turn{margin:0 0 11px;font-size:13.5px}.turn b{font-size:12px;color:var(--dec)}.turn.agent b{color:var(--skill)}.turn time{color:var(--dim);font-size:11px;margin-left:6px}.turn div{color:var(--body);margin-top:1px}
.empty{color:var(--mute);padding:24px 6px;text-align:center;font-size:13.5px}
.dots:after{content:"";animation:d 1.2s steps(4) infinite}@keyframes d{0%{content:""}25%{content:"."}50%{content:".."}75%{content:"..."}}
@media(max-width:1100px){.grid{grid-template-columns:1fr}.panel{max-height:none;min-height:320px}}
</style>
</head>
<body>
<div class="shell">
  <header class="topbar">
    <span class="orb" id="orb"></span>
    <span class="brand" id="brand">Aria</span>
    <span class="pill" id="status">Connecting</span>
    <span class="sep"></span>
    <div class="subj" id="subject">Live workspace</div>
    <span class="pill" id="m-agenda" style="display:none"></span>
    <div><div class="clock" id="clock">00:00</div><div class="rem" id="left"></div></div>
    <button class="theme" id="themeBtn" type="button">Light</button>
  </header>

  <section class="strip">
    <div class="skills-row"><span class="micro">Skills</span><div class="skills-row" id="m-skills"></div></div>
    <div class="pulse">
      <span class="pill"><span id="m-align">Alignment —</span><span class="mbar"><i id="m-align-bar"></i></span></span>
      <span class="pill" id="m-voices">Voices —</span>
    </div>
  </section>

  <main class="grid">
    <section class="panel">
      <div class="ph">
        <span class="micro">Live intelligence</span>
        <div class="tabs">
          <button class="tab on" data-tab="all">All</button>
          <button class="tab" data-tab="knowledge">Work IQ</button>
          <button class="tab" data-tab="skill">Skills</button>
          <button class="tab" data-tab="risks">Risks</button>
        </div>
      </div>
      <div class="scroll" id="feed"></div>
    </section>

    <section class="panel">
      <div class="ph"><span class="micro">Outcomes</span><span class="rem" id="m-out-n">0 captured</span></div>
      <div class="scroll" id="outcomes"></div>
    </section>

    <section class="panel">
      <div class="ph"><span class="micro">Conversation</span><span class="rem" id="m-turns"></span></div>
      <div class="scroll" id="convo"></div>
    </section>
  </main>
</div>
<script>
const params = new URLSearchParams(location.hash.slice(1)), key = params.get('k') || '', id = decodeURIComponent(location.pathname.split('/').pop());
let tab = 'all', last = null, timer = null, busy = false;
const $ = s => document.querySelector(s);
const el = (t, c, x) => { const e = document.createElement(t); if (c) e.className = c; if (x !== undefined && x !== null) e.textContent = x; return e; };
const tones = { knowledge:'var(--wiq)', skill:'var(--skill)', risk:'var(--bad)', alignment:'var(--warn)', pace:'var(--pace)', facilitation:'var(--pace)' };
const bgs = { knowledge:'rgba(56,189,248,.15)', skill:'rgba(167,139,250,.16)', risk:'rgba(251,113,133,.16)', alignment:'rgba(251,191,36,.16)', pace:'rgba(129,140,248,.16)', facilitation:'rgba(129,140,248,.16)' };
const labels = { knowledge:'Work IQ', skill:'Skill', risk:'Risk', alignment:'Alignment', pace:'Pace', facilitation:'Facilitation' };
const toneOf = k => Object.hasOwn(tones, k) ? tones[k] : tones.facilitation;
const bgOf = k => Object.hasOwn(bgs, k) ? bgs[k] : bgs.facilitation;
const labelOf = k => Object.hasOwn(labels, k) ? labels[k] : labels.facilitation;
const clock = s => { s = Math.max(0, Math.round(s)); const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), x = s % 60; return (h ? h + ':' + String(m).padStart(2, '0') : String(m).padStart(2, '0')) + ':' + String(x).padStart(2, '0'); };
const alignTone = a => a >= 75 ? 'var(--ok)' : a >= 50 ? 'var(--warn)' : 'var(--bad)';

$('#themeBtn').onclick = () => {
  const light = document.body.classList.toggle('light');
  $('#themeBtn').textContent = light ? 'Dark' : 'Light';
};

document.querySelectorAll('.tab').forEach(b => b.onclick = () => {
  tab = b.dataset.tab;
  document.querySelectorAll('.tab').forEach(x => x.classList.toggle('on', x === b));
  if (last) renderFeed(last);
});

function insightCard(i) {
  const c = el('div', 'icard'); c.style.borderLeftColor = toneOf(i.kind);
  const head = el('div', 'ihead');
  const tagText = i.kind === 'skill' && i.citation ? 'Skill \u00B7 ' + i.citation : labelOf(i.kind);
  const chip = el('span', 'tag', tagText); chip.style.color = toneOf(i.kind); chip.style.background = bgOf(i.kind);
  head.append(chip, el('span', 'ititle', i.title));
  if (i.severity === 'high') { const hi = el('span', 'tag', 'High'); hi.style.color = 'var(--bad)'; hi.style.background = bgs.risk; head.append(hi); }
  c.append(head, el('div', 'ibody', i.text));
  if (i.citation && i.kind !== 'skill') c.append(el('div', 'isrc', i.citation));
  return c;
}

function renderFeed(d) {
  const f = $('#feed'), out = [];
  if ((tab === 'all' || tab === 'knowledge') && d.searching) {
    const c = el('div', 'icard search'); c.style.borderLeftColor = tones.knowledge;
    const h = el('div', 'ihead'); const chip = el('span', 'tag', 'Work IQ'); chip.style.color = tones.knowledge; chip.style.background = bgs.knowledge;
    const t = el('span', 'ititle'); t.append(document.createTextNode('Searching Microsoft 365'), el('span', 'dots'));
    h.append(chip, t); c.append(h, el('div', 'isrc', d.searching)); out.push(c);
  }
  let items = [...d.insights].reverse();
  if (tab === 'knowledge') items = items.filter(i => i.kind === 'knowledge');
  else if (tab === 'skill') items = items.filter(i => i.kind === 'skill');
  else if (tab === 'risks') items = items.filter(i => ['risk', 'alignment', 'pace'].includes(i.kind) || i.severity === 'high');
  if (items.length) items.forEach(i => out.push(insightCard(i)));
  else if (!out.length) out.push(el('div', 'empty', 'Listening. Specialist skills and Microsoft 365 evidence appear automatically as the conversation unfolds.'));
  f.replaceChildren(...out);
}

function renderOutcomes(d) {
  const box = $('#outcomes'), out = [];
  const sec = (title, count, color, rows) => {
    const s = el('div', 'osec'), h = el('div', 'oh');
    h.append(el('span', 'micro', title), el('span', 'rem', String(count))); s.append(h);
    if (!rows.length) s.append(el('div', 'ometa', 'None recorded yet'));
    else rows.forEach(r => { r.style.borderLeftColor = color; s.append(r); });
    out.push(s);
  };
  sec('Decisions', d.decisions.length, 'var(--dec)', [...d.decisions].reverse().map(x => el('div', 'oitem', x)));
  sec('Actions', d.actions.length, 'var(--ok)', [...d.actions].reverse().map(a => {
    const r = el('div', 'oitem', a.task);
    r.append(el('div', 'ometa', a.owner + (a.due ? ' \u00B7 due ' + a.due : '')));
    return r;
  }));
  const trackedEntries = Object.entries(d.tracked || {}).flatMap(([cat, arr]) => (arr || []).map(v => ({ cat, v })));
  if (trackedEntries.length) {
    sec('Specialist tracking', trackedEntries.length, 'var(--skill)', trackedEntries.reverse().map(t => {
      const r = el('div', 'oitem', t.v); r.append(el('div', 'ometa', t.cat)); return r;
    }));
  } else if (d.parked.length) {
    sec('Parking lot', d.parked.length, 'var(--warn)', [...d.parked].reverse().map(p => el('div', 'oitem', p)));
  }
  box.replaceChildren(...out);
}

function render(d) {
  last = d;
  $('#brand').textContent = d.agent || 'Aria';
  $('#subject').textContent = d.subject || 'Teams meeting';
  $('#status').textContent = d.restricted ? 'Guest present \u00B7 M365 off' : d.searching ? 'Searching M365' : 'Listening';
  $('#orb').style.background = d.searching ? 'var(--wiq)' : 'var(--ok)';
  $('#clock').textContent = clock(d.elapsed);
  $('#left').textContent = d.remaining == null ? 'elapsed' : d.remaining >= 0 ? clock(d.remaining) + ' left' : '+' + clock(-d.remaining) + ' over';

  const a = d.alignment;
  $('#m-align').textContent = a == null ? 'Alignment \u2014' : 'Alignment ' + a + '%' + (d.alignmentNote ? ' \u00B7 ' + d.alignmentNote : '');
  const bar = $('#m-align-bar'); bar.style.width = (a == null ? 0 : a) + '%'; bar.style.background = a == null ? 'transparent' : alignTone(a);

  $('#m-voices').textContent = d.voices.length
    ? d.voices.slice(0, 3).map(v => v.name.split(' ')[0] + ' ' + Math.round(v.share * 100) + '%').join(' \u00B7 ')
    : 'Voices \u2014';

  $('#m-out-n').textContent = (d.decisions.length + d.actions.length) + ' captured';

  const sk = $('#m-skills');
  sk.replaceChildren(...d.skills.map(s => {
    const label = (s.dynamic ? '\u2726 ' : '') + (s.label || s.name);
    const e = el('span', 'sk' + (s.dynamic ? ' dyn' : ''), label);
    e.title = s.description || '';
    return e;
  }));
  if (!d.skills.length) sk.append(el('span', 'rem', 'Watching for domain shifts\u2026'));

  const cur = d.agenda.items[d.agenda.current], ag = $('#m-agenda');
  if (cur) { ag.style.display = ''; ag.textContent = (d.agenda.current + 1) + '/' + d.agenda.items.length + ' \u00B7 ' + cur.title; }
  else { ag.style.display = 'none'; }

  $('#m-turns').textContent = d.transcript.length ? d.transcript.length + ' turns' : '';
  const cv = $('#convo'); const atEnd = cv.scrollTop + cv.clientHeight >= cv.scrollHeight - 40;
  cv.replaceChildren(...d.transcript.map(t => { const p = el('div', 'turn' + (t.agent ? ' agent' : '')); const b = el('b', '', t.speaker); const tm = el('time', '', new Date(t.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })); p.append(b, tm, el('div', '', t.text)); return p; }));
  if (!d.transcript.length) cv.append(el('div', 'empty', 'Conversation appears here as people speak.'));
  if (atEnd) cv.scrollTop = cv.scrollHeight;

  renderFeed(d);
  renderOutcomes(d);
}

async function tick() {
  if (busy) return;
  busy = true;
  try {
    const r = await fetch('/api/live/' + encodeURIComponent(id), { cache: 'no-store', headers: { 'X-Live-Key': key } });
    if (r.status === 404) { clearInterval(timer); $('#status').textContent = 'Meeting ended or invalid link'; return; }
    if (!r.ok) { $('#status').textContent = 'Reconnecting'; return; }
    render(await r.json());
  } catch { $('#status').textContent = 'Reconnecting'; } finally { busy = false; }
}
tick(); timer = setInterval(tick, 2000);
</script>
</body>
</html>
""";
}
