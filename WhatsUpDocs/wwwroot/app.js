const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const f3 = x => x == null ? '–' : x.toFixed(3);
const f4 = x => x == null ? '–' : x.toFixed(4);
const fmtTime = s => s ? new Date(s).toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'short' }) : '';
const CFG_KEYS = ['targetWords', 'maxWords', 'minSentences', 'maxSentences', 'temperature', 'introTokens', 'endTokens', 'middleTokens'];

async function call(url, options) {
  const res = await fetch(url, options);
  const text = await res.text();
  if (!res.ok) throw new Error(text.replace(/^"|"$/g, '') || res.statusText);
  return text ? JSON.parse(text) : null;
}
const send = (url, method, body) => call(url, { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });

// ---- Highlight shared bigrams (same tokenizer as ROUGE-2: lowercase, keep a-z0-9 only) ----

function tokens(text) {
  const out = [], lower = String(text ?? '').toLowerCase(), re = /[a-z0-9]+/g;
  let m;
  while ((m = re.exec(lower))) out.push({ t: m[0], s: m.index, e: m.index + m[0].length });
  return out;
}
const bigramSet = toks => new Set(toks.slice(1).map((x, i) => toks[i].t + ' ' + x.t));

// Mark whole matching runs, including spaces/punctuation between the two words of a matching bigram
function markText(text, toks, other) {
  const spans = [];
  for (let i = 0; i + 1 < toks.length; i++) {
    if (!other.has(toks[i].t + ' ' + toks[i + 1].t)) continue;
    const last = spans[spans.length - 1];
    if (last && toks[i].s <= last[1]) last[1] = toks[i + 1].e;
    else spans.push([toks[i].s, toks[i + 1].e]);
  }
  let html = '', pos = 0;
  for (const [s, e] of spans) { html += esc(text.slice(pos, s)) + '<mark>' + esc(text.slice(s, e)) + '</mark>'; pos = e; }
  return html + esc(text.slice(pos));
}

function highlight(pred, ref) {
  const tp = tokens(pred), tr = tokens(ref);
  return { pred: markText(pred ?? '', tp, bigramSet(tr)), ref: markText(ref ?? '', tr, bigramSet(tp)) };
}

function compareHtml(summary, reference) {
  if (!reference) return `<div class="two"><div><h4>Output</h4><div class="txt">${esc(summary)}</div></div><div><h4>Real abstract</h4><p class="note">Test papers have no abstract.</p></div></div>`;
  const h = highlight(summary, reference);
  return `<div class="two"><div><h4>Output</h4><div class="txt">${h.pred}</div></div>
    <div><h4>Real abstract</h4><div class="txt">${h.ref}</div></div></div>`;
}

// ---- Tab ----

for (const tab of document.querySelectorAll('.tab')) {
  tab.addEventListener('click', () => {
    for (const t of document.querySelectorAll('.tab')) t.classList.toggle('active', t === tab);
    for (const p of document.querySelectorAll('.panel')) p.hidden = p.id !== `tab-${tab.dataset.tab}`;
    if (tab.dataset.tab === 'prompts') loadPrompts();
    if (tab.dataset.tab === 'runs') loadRuns();
    if (tab.dataset.tab === 'docs') loadDocsTab();
    if (tab.dataset.tab === 'submit') loadSubmitTab();
  });
}

// ---- Prompt ----

let prompts = [], current = null, saved = '';
const editorState = () => JSON.stringify({ c: $('prompt-text').value, n: $('prompt-note').value, cfg: readCfg() });
const dirty = () => editorState() !== saved;

function readCfg() {
  const cfg = { strategy: 'select' };
  for (const k of CFG_KEYS) cfg[k] = Number($(`cfg-${k}`).value);
  cfg.examples = $('cfg-examples').value || null;
  cfg.exampleCount = cfg.examples ? Number($('cfg-exampleCount').value) : 0;
  return cfg;
}

function showPrompt(p) {
  current = p;
  $('prompt-title').textContent = `Version #${p.id}`;
  $('prompt-text').value = p.content;
  $('prompt-note').value = p.note ?? '';
  for (const k of CFG_KEYS) $(`cfg-${k}`).value = p.config[k];
  $('cfg-examples').value = p.config.examples ?? '';
  $('cfg-exampleCount').value = p.config.exampleCount ?? 0;
  $('examples-text').hidden = true;
  $('examples-info').textContent = '';
  saved = editorState();
  refreshDirty();
  renderPromptList();
}

function refreshDirty() { $('prompt-dirty').textContent = dirty() ? 'unsaved' : ''; }

function renderPromptList() {
  $('prompt-rows').replaceChildren(...prompts.map(p => {
    const el = document.createElement('div');
    el.className = 'version' + (current?.id === p.id ? ' active' : '');
    const scores = Object.entries(p.scores).map(([set, s]) => `${set} ${f3(s)}`).join(' · ') || 'not run yet';
    el.innerHTML = `<div class="top"><b>Version #${p.id}</b><span class="spacer"></span><span class="scores">${fmtTime(p.createdAt)}</span></div>
      <div class="note-text">${esc(p.note ?? '')}</div><div class="scores">${esc(scores)}</div>`;
    el.addEventListener('click', () => { if (!dirty() || confirm('Discard unsaved changes?')) showPrompt(p); });
    return el;
  }));
}

async function loadPrompts(selectId) {
  prompts = await call('/api/prompts');
  const pick = prompts.find(p => p.id === (selectId ?? current?.id)) ?? prompts[0];
  if (pick && (selectId || !current || !dirty())) showPrompt(pick); else renderPromptList();
}

const promptBody = () => ({ content: $('prompt-text').value, note: $('prompt-note').value, config: readCfg() });

$('prompt-save').addEventListener('click', async () => {
  if (!current) return;
  try { await send(`/api/prompts/${current.id}`, 'PUT', promptBody()); await loadPrompts(current.id); }
  catch (e) { alert(e.message); }
});
$('prompt-fork').addEventListener('click', async () => {
  try { const { id } = await send('/api/prompts', 'POST', promptBody()); await loadPrompts(id); }
  catch (e) { alert(e.message); }
});
for (const id of ['prompt-text', 'prompt-note', 'cfg-examples', 'cfg-exampleCount', ...CFG_KEYS.map(k => `cfg-${k}`)]) $(id).addEventListener('input', refreshDirty);

// The example block that replaces {{examples}}: the first call scores 750 pool papers, so it takes a few seconds
$('examples-show').addEventListener('click', async () => {
  const pre = $('examples-text');
  if (!pre.hidden) { pre.hidden = true; return; }
  $('examples-info').textContent = 'picking examples…';
  try {
    const r = await send('/api/examples', 'POST', readCfg());
    $('examples-info').textContent = `${r.docs.length} papers · ~${r.approxTokens} tokens`;
    pre.textContent = r.text;
    pre.hidden = false;
  } catch (e) { $('examples-info').textContent = e.message; }
});
window.addEventListener('beforeunload', e => { if (current && dirty()) e.preventDefault(); });

// Try the version in the editor (saved or not)
async function tryDocs(ids) {
  const buttons = [$('try-one'), $('try-five')];
  buttons.forEach(b => b.disabled = true);
  $('try-summary').innerHTML = `<span class="spin"></span> Running ${ids.length} papers…`;
  $('try-results').replaceChildren();
  try {
    const r = await send('/api/try', 'POST', { ...promptBody(), paperIds: ids });
    const ok = r.results.filter(x => x.score != null);
    const mean = ok.length ? ok.reduce((a, x) => a + x.score, 0) / ok.length : null;
    $('try-summary').textContent = r.results.length > 1 ? `mean F1 ${f4(mean)}` : '';
    $('try-results').innerHTML = r.results.map(resultCard).join('');
  } catch (e) {
    $('try-summary').textContent = 'Error: ' + e.message;
  } finally {
    buttons.forEach(b => b.disabled = false);
  }
}

function resultCard(x) {
  const head = `<div class="res-h"><span class="id">#${x.paperId}</span><b>${f3(x.score)}</b><span>${x.words} words</span></div>`;
  const body = x.error ? `<div class="res-err">${esc(x.error)}</div>` : compareHtml(x.summary, x.reference);
  return `<div class="res">${head}${body}
    <details class="raw"><summary>Raw model output</summary><pre>${esc(x.raw ?? '')}</pre></details>
    <details class="raw"><summary>Prompt sent</summary><pre>${esc(x.prompt)}</pre></details></div>`;
}

const sample = (arr, n) => [...arr].sort(() => Math.random() - .5).slice(0, n);
let sets = null;
const getSets = async () => sets ??= await call('/api/sets');

// Empty id box: pick a random paper from the quick set
$('try-one').addEventListener('click', async () => {
  const v = $('try-id').value.trim();
  tryDocs([v ? Number(v) : sample((await getSets()).quick, 1)[0]]);
});
$('try-five').addEventListener('click', async () => tryDocs(sample((await getSets()).quick, 5)));

// ---- Experiments ----

let runs = [], selectedRun = null, outputs = [], sortKey = 'score', sortDesc = false;
const streams = new Map();
const checked = new Set();

async function loadRuns() {
  const [r, p] = await Promise.all([call('/api/runs'), call('/api/prompts')]);
  runs = r;
  prompts = p;
  const keep = $('run-prompt').value;
  $('run-prompt').replaceChildren(...p.map(x => new Option(`#${x.id} ${x.note ?? ''}`, x.id)));
  if (keep) $('run-prompt').value = keep;
  renderRuns();
  for (const run of runs) if (run.status === 'running') watch(run.id);
}

// Only shows something unusual: running (with progress), errors, cancelled/interrupted, prompt edited after the run
const STATUS_TEXT = { running: 'running', cancelled: 'cancelled', failed: 'failed', interrupted: 'interrupted' };
function statusCell(r) {
  const p = r.progress, out = [];
  if (r.status !== 'done') out.push(`<span class="st ${r.status}">${STATUS_TEXT[r.status] ?? r.status}</span>`);
  if (p) out.push(`<span class="prog"><i style="width:${(100 * p.done / Math.max(p.total, 1)).toFixed(0)}%"></i></span> ${p.done}/${p.total}`);
  else if (r.status !== 'running' && r.done < r.total) out.push(`${r.done}/${r.total} papers`);
  if (r.errors) out.push(`<span class="bad-text">${r.errors} errors</span>`);
  if (r.stale) out.push(`<span class="stale">${r.stale}</span>`);
  return out.join(' ');
}

function duration(r) {
  if (!r.finishedAt) return '';
  const s = (new Date(r.finishedAt) - new Date(r.startedAt)) / 1000;
  return s < 90 ? `${s.toFixed(0)} s` : `${(s / 60).toFixed(1)} min`;
}

function renderRuns() {
  $('run-rows').replaceChildren(...runs.map(r => {
    const tr = document.createElement('tr');
    tr.classList.toggle('active', selectedRun?.id === r.id);
    const live = r.progress?.score;
    tr.innerHTML = `<td><input type="checkbox" ${checked.has(r.id) ? 'checked' : ''} aria-label="Select to compare"></td>
      <td class="num">${r.id}</td><td class="clip">${esc(r.name)}</td><td>#${r.promptId ?? ''}</td><td>${r.setName}</td>
      <td class="num f1">${f4(r.score ?? live)}</td><td class="num">${r.avgWords?.toFixed(0) ?? '–'}</td>
      <td>${duration(r)}</td><td>${statusCell(r)}</td>
      <td><div class="acts">${r.status === 'running' ? '<button data-a="cancel">Cancel</button>' : `${r.errors || r.done < r.total ? '<button data-a="retry">Retry failed</button>' : ''}<button data-a="delete" class="danger">Delete</button>`}</div></td>`;
    const box = tr.querySelector('input');
    box.addEventListener('click', e => {
      e.stopPropagation();
      if (box.checked) checked.add(r.id); else checked.delete(r.id);
      $('cmp-go').disabled = checked.size !== 2;
    });
    for (const b of tr.querySelectorAll('button')) b.addEventListener('click', e => { e.stopPropagation(); runAction(r, b.dataset.a); });
    tr.addEventListener('click', () => openRun(r.id));
    return tr;
  }));
  $('cmp-go').disabled = checked.size !== 2;
}

async function runAction(r, action) {
  try {
    if (action === 'cancel') await call(`/api/runs/${r.id}/cancel`, { method: 'POST' });
    if (action === 'retry') { await call(`/api/runs/${r.id}/retry`, { method: 'POST' }); }
    if (action === 'delete') {
      if (!confirm(`Delete run #${r.id} "${r.name}"?`)) return;
      await call(`/api/runs/${r.id}`, { method: 'DELETE' });
      checked.delete(r.id);
      if (selectedRun?.id === r.id) { selectedRun = null; $('run-detail').hidden = true; }
    }
  } catch (e) { alert(e.message); }
  await loadRuns();
}

// EventSource reconnects on its own; close it when the server says done
function watch(id) {
  if (streams.has(id)) return;
  const es = new EventSource(`/api/runs/${id}/events`);
  streams.set(id, es);
  es.addEventListener('progress', e => {
    const p = JSON.parse(e.data), r = runs.find(x => x.id === id);
    if (r) { r.progress = p; r.done = Math.max(r.done, p.done); r.errors = p.errors; renderRuns(); }
  });
  es.addEventListener('done', async () => {
    es.close();
    streams.delete(id);
    await loadRuns();
    if (selectedRun?.id === id) openRun(id);
  });
}

$('run-start').addEventListener('click', async () => {
  $('run-msg').textContent = '';
  try {
    const { id } = await send('/api/runs', 'POST', { promptId: Number($('run-prompt').value), setName: $('run-set').value, parallel: 8 });
    await loadRuns();
    watch(id);
  } catch (e) { $('run-msg').textContent = 'Error: ' + e.message; }
});

async function openRun(id) {
  const d = await call(`/api/runs/${id}`);
  selectedRun = d.run;
  outputs = d.outputs;
  $('run-detail').hidden = false;
  $('detail-title').textContent = `Run #${d.run.id} · ${d.run.name}`;
  $('out-view').innerHTML = '<p class="empty">Pick a paper.</p>';
  renderOutputs();
  renderRuns();
}

function renderOutputs() {
  const rows = [...outputs].sort((a, b) => {
    const x = a[sortKey] ?? -1, y = b[sortKey] ?? -1;
    return sortDesc ? y - x : x - y;
  });
  for (const th of document.querySelectorAll('.outs th.sortable')) th.classList.toggle('on', th.dataset.k === sortKey);
  $('out-rows').replaceChildren(...rows.map(o => {
    const tr = document.createElement('tr');
    tr.innerHTML = `<td class="num">${o.paperId}</td><td class="num f1">${f3(o.score)}</td><td class="num">${o.words ?? ''}</td>
      <td class="err" title="${esc(o.error ?? '')}">${esc(o.error ?? '')}</td>`;
    tr.addEventListener('click', () => {
      for (const x of $('out-rows').children) x.classList.toggle('active', x === tr);
      openOutput(selectedRun.id, o.paperId);
    });
    return tr;
  }));
}

for (const th of document.querySelectorAll('.outs th.sortable')) {
  th.addEventListener('click', () => {
    if (sortKey === th.dataset.k) sortDesc = !sortDesc; else { sortKey = th.dataset.k; sortDesc = false; }
    renderOutputs();
  });
}

async function openOutput(runId, paperId) {
  const { output: o, reference } = await call(`/api/runs/${runId}/outputs/${paperId}`);
  $('out-view').innerHTML = `<div class="res"><div class="res-h"><span class="id">#${o.paperId}</span><b>${f3(o.score)}</b><span>${o.words ?? 0} words</span></div>
    ${o.error ? `<div class="res-err">${esc(o.error)}</div>` : compareHtml(o.summary, reference)}
    <details class="raw"><summary>Raw model output</summary><pre>${esc(o.rawOutput ?? '')}</pre></details>
    <details class="raw"><summary>Context sent to the LLM</summary><pre>${esc(o.contextText ?? '')}</pre></details>
    <p class="pad0"><button class="small" id="to-docs">Analyse in the Papers tab</button></p></div>`;
  $('to-docs').addEventListener('click', async () => {
    docOpen = paperId;
    openTab('docs');
    await new Promise(r => setTimeout(r, 300));
    $('doc-run').value = String(runId);
    await loadDocRun();
  });
}

// ---- Compare two runs: B − A on the papers both runs scored ----

$('cmp-go').addEventListener('click', async () => {
  const [a, b] = [...checked].sort((x, y) => x - y);
  const c = await call(`/api/compare?a=${a}&b=${b}`);
  const ra = runs.find(r => r.id === a), rb = runs.find(r => r.id === b);
  const delta = c.meanA != null ? c.meanB - c.meanA : null;
  $('cmp').hidden = false;
  $('cmp').innerHTML = `<div class="ch0"><h2>Compare #${a} (A) with #${b} (B)</h2><button class="small" id="cmp-close">Close</button></div>
    <div class="cmp-kpis">
      <div class="kpi"><span>A · ${esc(ra?.name)}</span><b>${f4(c.meanA)}</b></div>
      <div class="kpi"><span>B · ${esc(rb?.name)}</span><b>${f4(c.meanB)}</b></div>
      <div class="kpi hero"><span>B − A</span><b class="${delta > 0 ? 'up' : delta < 0 ? 'down' : ''}">${delta == null ? '–' : (delta >= 0 ? '+' : '') + delta.toFixed(4)}</b></div>
      <div class="kpi"><span>Papers B better / worse / same</span><b>${c.better} / ${c.worse} / ${c.same}</b></div>
      ${confidenceKpi(c.rows.map(r => r.delta))}
    </div>
    <div class="cmp-table"><table><thead><tr><th class="num">Paper</th><th class="num">A</th><th class="num">B</th><th class="num">B − A</th></tr></thead>
      <tbody>${c.rows.map(r => `<tr data-id="${r.paperId}"><td class="num">${r.paperId}</td><td class="num">${f3(r.a)}</td><td class="num">${f3(r.b)}</td>
        <td class="num ${r.delta > 0.005 ? 'up' : r.delta < -0.005 ? 'down' : ''}">${(r.delta >= 0 ? '+' : '') + r.delta.toFixed(3)}</td></tr>`).join('')}</tbody></table></div>
    <div id="cmp-view" class="results"></div>`;
  $('cmp-close').addEventListener('click', () => { $('cmp').hidden = true; });
  for (const tr of $('cmp').querySelectorAll('tbody tr')) {
    tr.addEventListener('click', async () => {
      const id = Number(tr.dataset.id);
      const [x, y] = await Promise.all([call(`/api/runs/${a}/outputs/${id}`), call(`/api/runs/${b}/outputs/${id}`)]);
      $('cmp-view').innerHTML = [['A', a, x], ['B', b, y]].map(([label, runId, d]) =>
        `<div class="res"><div class="res-h"><span class="id">${label} · run #${runId} · paper #${id}</span><b>${f3(d.output.score)}</b><span>${d.output.words ?? 0} words</span></div>
         ${d.output.error ? `<div class="res-err">${esc(d.output.error)}</div>` : compareHtml(d.output.summary, d.reference)}</div>`).join('');
    });
  }
  $('cmp').scrollIntoView({ behavior: 'smooth' });
});

// Paired bootstrap: resample papers and count how often the mean B − A stays positive
function confidenceKpi(deltas) {
  if (deltas.length < 10) return '';
  const n = deltas.length, means = [];
  for (let i = 0; i < 4000; i++) {
    let sum = 0;
    for (let j = 0; j < n; j++) sum += deltas[(Math.random() * n) | 0];
    means.push(sum / n);
  }
  means.sort((x, y) => x - y);
  const lo = means[100], hi = means[3899], pBetter = means.filter(x => x > 0).length / means.length;
  const verdict = lo > 0 ? 'B is really better' : hi < 0 ? 'B is really worse' : 'not sure, could be luck';
  return `<div class="kpi"><span>Confidence B beats A</span><b>${(100 * pBetter).toFixed(0)}%</b><small>${verdict}</small></div>`;
}

// ---- Papers ----

let docList = null, docRunOutputs = new Map(), docSort = { k: 'score', desc: false }, docOpen = null;
const SENT_SPLIT = /(?<=[.!?])\s+(?=[A-Z("'])/;
const wordCount = s => s.trim().split(/\s+/).filter(Boolean).length;
const sentencesOf = text => text.replace(/\s+/g, ' ').split(SENT_SPLIT).map(x => x.trim()).filter(x => wordCount(x) >= 6 && wordCount(x) <= 90);
const gramsOf = text => bigramSet(tokens(text));
const share = (set, other) => { if (!set.size) return 0; let n = 0; for (const g of set) if (other.has(g)) n++; return n / set.size; };
const pct = x => (100 * x).toFixed(0) + '%';

function openTab(name) {
  document.querySelector(`.tab[data-tab="${name}"]`).click();
}

async function loadDocsTab() {
  const [list, runList] = await Promise.all([docList ?? call('/api/docs'), call('/api/runs')]);
  docList = list;
  const sel = $('doc-run'), keep = sel.value;
  sel.replaceChildren(new Option('(no run)', ''), ...runList.filter(r => r.kind === 'real')
    .map(r => new Option(`#${r.id} ${r.name} · ${r.setName} · ${f3(r.score)}`, r.id)));
  sel.value = keep && [...sel.options].some(o => o.value === keep) ? keep : (runList[0]?.id ?? '');
  await loadDocRun();
}

async function loadDocRun() {
  const id = $('doc-run').value;
  docRunOutputs = new Map();
  $('diag').hidden = true;
  if (id) {
    const d = await call(`/api/runs/${id}`);
    for (const o of d.outputs) docRunOutputs.set(o.paperId, o);
    if (!$('doc-set').dataset.touched) $('doc-set').value = d.run.setName;
    loadDiagnosis(id);
  }
  renderDocList();
  if (docOpen != null) openDoc(docOpen);
}

// Whole-run diagnosis: score ceiling with perfect sentence picks from the context / from the whole paper
async function loadDiagnosis(id) {
  $('diag').hidden = false;
  $('diag').innerHTML = '<div class="kpi"><span><span class="spin"></span> Computing…</span></div>';
  try {
    const g = await call(`/api/runs/${id}/diagnose`);
    if ($('doc-run').value !== String(id)) return;
    $('diag').innerHTML = `
      <div class="kpi hero"><span>F1</span><b>${f4(g.score)}</b></div>
      <div class="kpi"><span>Ceiling: perfect picks from the context</span><b>${f3(g.oracleContext)}</b></div>
      <div class="kpi"><span>Ceiling: perfect picks from the whole paper</span><b>${f3(g.oracleFull)}</b></div>
      <div class="kpi"><span>Abstract bigrams found in: paper → context → output</span><b>${pct(g.refInPaper)} → ${pct(g.refInContext)} → ${pct(g.refInOutput)}</b></div>`;
  } catch (e) { $('diag').innerHTML = `<div class="kpi"><span>Diagnosis failed: ${esc(e.message)}</span></div>`; }
}

function renderDocList() {
  const set = $('doc-set').value, q = $('doc-search').value.trim();
  let rows = docList.filter(d => (!set || d.set === set || (set === 'dev' && d.set === 'quick')) && (!q || String(d.paperId).startsWith(q)))
    .map(d => ({ ...d, score: docRunOutputs.get(d.paperId)?.score ?? null }));
  const { k, desc } = docSort;
  rows.sort((a, b) => ((a[k] ?? -1) - (b[k] ?? -1)) * (desc ? -1 : 1));
  for (const th of document.querySelectorAll('.doclist th.sortable')) th.classList.toggle('on', th.dataset.k === k);
  $('doc-rows').replaceChildren(...rows.slice(0, 400).map(d => {
    const tr = document.createElement('tr');
    tr.classList.toggle('active', d.paperId === docOpen);
    tr.innerHTML = `<td class="num">${d.paperId}</td><td>${d.set}</td><td class="num">${d.summaryWords ?? ''}</td><td class="num f1">${f3(d.score)}</td>`;
    tr.addEventListener('click', () => { docOpen = d.paperId; renderDocList(); openDoc(d.paperId); });
    return tr;
  }));
}

for (const th of document.querySelectorAll('.doclist th.sortable')) {
  th.addEventListener('click', () => {
    docSort = docSort.k === th.dataset.k ? { k: th.dataset.k, desc: !docSort.desc } : { k: th.dataset.k, desc: false };
    renderDocList();
  });
}
$('doc-run').addEventListener('change', loadDocRun);
$('doc-set').addEventListener('change', () => { $('doc-set').dataset.touched = '1'; renderDocList(); });
$('doc-search').addEventListener('input', renderDocList);

// Which paper sentences share many bigrams with the real abstract, and what happened to them: picked / in context but skipped / not in context
function classify(sentence, refSet, outSet, ctxSet) {
  const g = gramsOf(sentence);
  let hits = 0;
  for (const x of g) if (refSet.has(x)) hits++;
  const used = outSet && share(g, outSet) >= 0.8;
  const inCtx = ctxSet && share(g, ctxSet) >= 0.8;
  return { hits, ratio: g.size ? hits / g.size : 0, status: used ? 'used' : inCtx ? 'miss' : 'out' };
}

let docViewMode = 'context';

// Only the latest call renders: switching run/paper quickly must not let an older, slower response win
let openDocSeq = 0;

async function openDoc(id) {
  const runId = $('doc-run').value, seq = ++openDocSeq;
  $('doc-view').innerHTML = '<p class="empty"><span class="spin"></span></p>';
  const [clean, raw, out] = await Promise.all([
    call(`/api/docs/${id}?view=clean`), call(`/api/docs/${id}?view=raw`),
    runId ? call(`/api/runs/${runId}/outputs/${id}`).catch(() => null) : null,
  ]);
  const context = out?.output?.contextText ?? (await call(`/api/docs/${id}?view=context&mode=numbered`)).text;
  if (seq !== openDocSeq) return;
  const ref = clean.summary, o = out?.output;
  const outSet = o?.summary ? gramsOf(o.summary) : null, ctxSet = gramsOf(context);
  let html = `<div class="dv-head"><span class="id">#${id}</span><span>${docList.find(d => d.paperId === id)?.set ?? ''}</span>
    ${o ? `<b>${f3(o.score)}</b><span>${o.words ?? 0} words</span>` : ''}</div>`;

  if (ref) {
    const refSet = gramsOf(ref), paperSet = gramsOf(clean.text);
    html += `<div class="cov">
      <div class="cov-row"><span>Abstract bigrams in the paper</span><span class="meter"><i style="width:${pct(share(refSet, paperSet))}"></i></span><span>${pct(share(refSet, paperSet))}</span></div>
      <div class="cov-row"><span>… in the context sent to the model</span><span class="meter"><i style="width:${pct(share(refSet, ctxSet))}"></i></span><span>${pct(share(refSet, ctxSet))}</span></div>
      ${outSet ? `<div class="cov-row"><span>… in the output</span><span class="meter"><i style="width:${pct(share(refSet, outSet))}"></i></span><span>${pct(share(refSet, outSet))}</span></div>` : ''}</div>`;

    const top = sentencesOf(clean.text.split('\n').filter(l => !l.startsWith('#')).join('\n'))
      .map(s => ({ s, ...classify(s, refSet, outSet, ctxSet) }))
      .filter(x => x.hits >= 3 && x.ratio >= 0.15).sort((a, b) => b.hits - a.hits).slice(0, 10);
    const label = { used: 'picked', miss: 'skipped by model', out: 'not in context' };
    html += `<div><h4 class="sub">Sentences closest to the abstract</h4>
      <div class="top-sents">${top.length ? top.map(x => `<div class="ts ${outSet || x.status === 'out' ? x.status : 'miss'}"><span class="chip">${outSet || x.status === 'out' ? label[x.status] : 'in context'}</span><span>${esc(x.s)}</span><span class="k" title="bigrams shared with the abstract">${x.hits}</span></div>`).join('') : '<p class="note">No sentence overlaps much with the abstract.</p>'}</div></div>`;
  }

  html += o?.error ? `<div class="res-err">${esc(o.error)}</div>` : o ? compareHtml(o.summary, ref) : ref ? `<div><h4 class="sub">Real abstract</h4><div class="txt">${esc(ref)}</div></div>` : '';
  html += `<div><div class="ch0"><h4 class="sub">Paper</h4><span class="seg" id="paper-seg">
      <button data-m="context">Context</button><button data-m="clean">Cleaned</button><button data-m="raw">Raw</button></span></div>
    <div id="paper" class="paper"></div></div>`;
  if (o?.rawOutput) html += `<details class="raw"><summary>Raw model output</summary><pre>${esc(o.rawOutput)}</pre></details>`;
  $('doc-view').innerHTML = html;

  const views = { context, clean: clean.text, raw: raw.text };
  const refSet = ref ? gramsOf(ref) : null;
  const show = mode => {
    docViewMode = mode;
    for (const b of document.querySelectorAll('#paper-seg button')) b.classList.toggle('on', b.dataset.m === mode);
    $('paper').innerHTML = mode === 'raw' ? esc(views.raw) : paperHtml(views[mode], refSet, outSet, mode === 'context');
  };
  for (const b of document.querySelectorAll('#paper-seg button')) b.addEventListener('click', () => show(b.dataset.m));
  show(docViewMode);
}

// Colour each sentence: green = in the output, yellow = overlaps the abstract a lot but the output missed it
function paperHtml(text, refSet, outSet, numbered) {
  return text.split('\n').map(line => {
    if (!line.trim()) return '';
    if (/^(#|===)/.test(line)) return `<span class="hd">${esc(line)}</span>`;
    const parts = numbered ? [line] : line.split(SENT_SPLIT);
    return parts.map(s => {
      const g = gramsOf(s);
      const used = outSet && g.size >= 4 && share(g, outSet) >= 0.8;
      let hits = 0;
      if (refSet) for (const x of g) if (refSet.has(x)) hits++;
      const cls = used ? 's-used' : refSet && hits >= 3 && hits / Math.max(g.size, 1) >= 0.15 ? 's-miss' : '';
      return cls ? `<span class="${cls}" title="${hits} bigrams shared with the abstract">${esc(s)}</span>` : esc(s);
    }).join(' ');
  }).join('\n');
}

// ---- Submit ----

let subTimer = null;

async function loadSubmitTab() {
  const [allRuns, subs] = await Promise.all([call('/api/runs'), call('/api/submissions')]);
  const sources = allRuns.filter(r => r.status === 'done' && r.setName !== 'test' && r.kind === 'real');
  const sel = $('sub-source'), keep = sel.value;
  sel.replaceChildren(...sources.map(r => new Option(`#${r.id} ${r.name} · ${r.setName} · F1 ${f4(r.score)}`, r.id)));
  if (keep) sel.value = keep;

  const testRuns = allRuns.filter(r => r.setName === 'test');
  const checks = await Promise.all(testRuns.map(r => r.status === 'running' ? null : call(`/api/submissions/check/${r.id}`)));
  $('sub-runs').replaceChildren(...testRuns.map((r, i) => {
    const c = checks[i];
    const tr = document.createElement('tr');
    tr.className = 'static';
    tr.innerHTML = `<td class="num">${r.id}</td><td class="clip">${esc(r.name)}</td>
      <td>${!c ? `running ${r.progress?.done ?? r.done}/${r.total}` : c.ok ? '<span class="ok-text">✓ all 345 papers</span>' : `<span class="bad-text">${esc(c.problem)}</span>`}</td>
      <td><div class="acts">${r.status === 'running' ? '<button data-a="cancel">Cancel</button>'
        : `${c && (c.errors || c.missing) ? '<button data-a="retry">Retry failed</button>' : ''}${c?.ok ? '<button data-a="make" class="primary">Build CSV</button>' : ''}`}</div></td>`;
    for (const b of tr.querySelectorAll('button')) b.addEventListener('click', () => subAction(r, b.dataset.a));
    return tr;
  }));
  if (!testRuns.length) $('sub-runs').innerHTML = '<tr class="static"><td colspan="4" class="empty">No test runs yet.</td></tr>';

  $('sub-rows').replaceChildren(...subs.map(x => {
    const tr = document.createElement('tr');
    tr.className = 'static';
    const diff = x.publicScore != null && x.sourceScore != null ? x.publicScore - x.sourceScore : null;
    tr.innerHTML = `<td class="num">${x.id}</td><td>${fmtTime(x.createdAt)}</td><td>#${x.promptId ?? ''}</td>
      <td class="num">${f4(x.sourceScore)}</td>
      <td class="num"><input class="score-in" type="text" inputmode="decimal" value="${x.publicScore ?? ''}" placeholder="0.xxxx" aria-label="Public score"></td>
      <td class="num ${diff > 0 ? 'up' : diff < 0 ? 'down' : ''}">${diff == null ? '' : (diff >= 0 ? '+' : '') + diff.toFixed(4)}</td>
      <td><input class="note-in" value="${esc(x.note ?? '')}" aria-label="Note"></td>
      <td><div class="acts"><button data-a="save">Save</button><a class="button small" href="/api/submissions/${x.id}/download">Download CSV</a></div></td>`;
    tr.querySelector('[data-a=save]').addEventListener('click', async () => {
      const v = tr.querySelector('.score-in').value.trim().replace(',', '.');
      if (v !== '' && !(Number(v) >= 0 && Number(v) <= 1)) return alert('Score must be a number between 0 and 1');
      await send(`/api/submissions/${x.id}`, 'PUT', { publicScore: v === '' ? null : Number(v), note: tr.querySelector('.note-in').value });
      loadSubmitTab();
    });
    return tr;
  }));
  if (!subs.length) $('sub-rows').innerHTML = '<tr class="static"><td colspan="8" class="empty">No submissions yet.</td></tr>';

  // While a test run is in progress, refresh every few seconds, only while this tab is open
  clearTimeout(subTimer);
  if (testRuns.some(r => r.status === 'running'))
    subTimer = setTimeout(() => { if (!$('tab-submit').hidden) loadSubmitTab(); }, 3000);
}

async function subAction(r, action) {
  try {
    if (action === 'cancel') await call(`/api/runs/${r.id}/cancel`, { method: 'POST' });
    if (action === 'retry') await call(`/api/runs/${r.id}/retry`, { method: 'POST' });
    if (action === 'make') {
      const row = await send('/api/submissions', 'POST', { runId: r.id });
      $('sub-msg').innerHTML = `Built submission #${row.id}. <a href="/api/submissions/${row.id}/download">Download CSV</a>`;
    }
  } catch (e) { alert(e.message); }
  loadSubmitTab();
}

$('sub-run').addEventListener('click', async () => {
  $('sub-msg').textContent = '';
  try {
    const { id } = await send('/api/runs/rerun', 'POST', { sourceRunId: Number($('sub-source').value), setName: 'test', parallel: 10 });
    $('sub-msg').textContent = `Running run #${id}…`;
    loadSubmitTab();
  } catch (e) { $('sub-msg').textContent = 'Error: ' + e.message; }
});

// ---- Deep links ----
// #docs?run=8&paper=637 · #runs?run=8&paper=637 · #runs?compare=3,8 · #prompts?settings · #submit
async function openFromHash() {
  const [tab, query] = location.hash.slice(1).split('?');
  if (!document.querySelector(`.tab[data-tab="${tab}"]`)) return;
  const q = new URLSearchParams(query ?? '');
  const run = q.get('run'), paper = q.get('paper') ? Number(q.get('paper')) : null;
  openTab(tab);
  if (tab === 'prompts' && q.has('settings')) document.querySelector('.cfg-box').open = true;
  if (tab === 'runs') {
    await loadRuns();
    if (q.get('compare')) {
      for (const id of q.get('compare').split(',')) checked.add(Number(id));
      renderRuns();
      $('cmp-go').click();
    }
    if (run) {
      await openRun(Number(run));
      if (paper != null) await openOutput(Number(run), paper);
    }
  }
  if (tab === 'docs') {
    docOpen = paper;
    await loadDocsTab();
    if (run) { $('doc-run').value = run; await loadDocRun(); }
  }
}

// ---- Start ----

loadPrompts();
openFromHash();
