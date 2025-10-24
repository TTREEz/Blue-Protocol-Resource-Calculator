// Single-site app that hosts both: JSON Builder + Focus Calculator.
// Shared store so edits/imports reflect live in the calculator.

// ----------------- Utilities -----------------
const $ = s => document.querySelector(s);
const $$ = s => Array.from(document.querySelectorAll(s));
const fmt2 = n => Number(n).toFixed(2);
const isFiniteNum = x => typeof x === 'number' && Number.isFinite(x);
const nz = (x, d = 0) => Number.isFinite(Number(x)) ? Number(x) : d;
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
const download = (filename, text) => {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([text], { type: 'application/json' }));
  a.download = filename;
  a.click();
  URL.revokeObjectURL(a.href);
};
const formatDuration = (seconds) => {
  seconds = Math.max(0, Math.round(seconds || 0));
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  return h >= 1 ? `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`
               : `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
};
const csvEsc = s => {
  s = String(s ?? '');
  return (s.includes(',') || s.includes('"')) ? `"${s.replace(/"/g, '""')}"` : s;
};

// ----------------- Sample data -----------------
const SAMPLE = [
  {
    "Name": "Burning Powder",
    "FocusCost": 20,
    "Yield": 15,
    "YieldMin": null,
    "YieldMax": null,
    "TimePerCraftSeconds": 5,
    "IsMineable": false,
    "Ingredients": { "Charcoal": 1 },
    "YieldOutcomes": null,
    "YieldMinChance": null,
    "YieldMaxChance": null
  },
  {
    "Name": "Charcoal",
    "FocusCost": 0,
    "Yield": 10,
    "YieldMin": null,
    "YieldMax": null,
    "TimePerCraftSeconds": 5,
    "IsMineable": false,
    "Ingredients": { "Logs": 28 },
    "YieldOutcomes": null,
    "YieldMinChance": null,
    "YieldMaxChance": null
  },
  {
    "Name": "Boru Ore",
    "FocusCost": 20,
    "Yield": 12,
    "YieldMin": 11,
    "YieldMax": 12,
    "TimePerCraftSeconds": 0,
    "IsMineable": true,
    "Ingredients": {},
    "YieldOutcomes": null,
    "YieldMinChance": null,
    "YieldMaxChance": null
  },
  {
    "Name": "Logs",
    "FocusCost": 0,
    "Yield": 1,
    "YieldMin": null,
    "YieldMax": null,
    "TimePerCraftSeconds": 0,
    "IsMineable": true,
    "Ingredients": {},
    "YieldOutcomes": null,
    "YieldMinChance": null,
    "YieldMaxChance": null
  },
  {
    "Name": "Rich Boru Ore",
    "FocusCost": 20,
    "Yield": 11.4,
    "YieldMin": 10,
    "YieldMax": 12,
    "TimePerCraftSeconds": 5,
    "IsMineable": true,
    "Ingredients": {},
    "YieldOutcomes": null,
    "YieldMinChance": null,
    "YieldMaxChance": null
  },
  {
    "Name": "Mystery Metal - Novice",
    "FocusCost": 10,
    "Yield": null,
    "YieldMin": 1,
    "YieldMax": 2,
    "TimePerCraftSeconds": 5,
    "IsMineable": false,
    "Ingredients": { "Boru Ore": 8, "Burning Powder": 1 },
    "YieldOutcomes": null,
    "YieldMinChance": 0.8,
    "YieldMaxChance": 0.2
  },
  {
    "Name": "Mystery Metal - Master",
    "FocusCost": 10,
    "Yield": null,
    "YieldMin": 1,
    "YieldMax": 3,
    "TimePerCraftSeconds": 5,
    "IsMineable": false,
    "Ingredients": { "Rich Boru Ore": 8, "Burning Powder": 1 },
    "YieldOutcomes": { "1": 70, "2": 20, "3": 10 }, // Example multi-outcome
    "YieldMinChance": null,
    "YieldMaxChance": null
  }
];

// ----------------- Recipe Store -----------------
const Store = (() => {
  const listeners = new Set();
  let map = {};
  let arrayFormat = true;

  function onChange() { listeners.forEach(fn => fn()); }
  function subscribe(fn){ listeners.add(fn); return ()=>listeners.delete(fn); }

  const toProb = v => {
    if (v === null || v === undefined || v === '') return null;
    let p = Number(v);
    if (!Number.isFinite(p)) return null;
    if (p > 1) p = p / 100;       // allow 0–100%
    return clamp(p, 0, 1);
  };

  // Convert { qty: prob } into normalized 0–1 probs; accept % or 0–1
  function normalizeOutcomes(obj) {
    if (!obj || typeof obj !== 'object') return null;
    const pairs = Object.entries(obj)
      .map(([k,v]) => [Number(k), Number(v)])
      .filter(([q,p]) => Number.isFinite(q) && q >= 0 && Number.isFinite(p) && p >= 0);
    if (!pairs.length) return null;

    // Convert >1 to percent; then normalize to sum 1
    const converted = pairs.map(([q,p]) => [q, p > 1 ? (p / 100) : p]);
    const sum = converted.reduce((s,[,p]) => s + p, 0);
    if (sum <= 0) return null;

    const norm = {};
    for (const [q,p] of converted) norm[q] = p / sum;
    return norm;
  }

  function normalizeRecipe(r) {
    // Fixed yield: allow null, but keep fixed yields > 0 if provided
    let fixedYield = r.Yield;
    const hasFixed = fixedYield !== '' && fixedYield !== null && fixedYield !== undefined;
    fixedYield = hasFixed ? Number(fixedYield) : null;

    const nr = {
      Name: String(r.Name || '').trim(),
      FocusCost: nz(r.FocusCost),
      Yield: hasFixed ? (Number.isFinite(fixedYield) && fixedYield > 0 ? fixedYield : 1) : null,
      // Min/Max: allow 0 as valid
      YieldMin: (Number.isFinite(Number(r.YieldMin)) && Number(r.YieldMin) >= 0) ? Number(r.YieldMin) : null,
      YieldMax: (Number.isFinite(Number(r.YieldMax)) && Number(r.YieldMax) >= 0) ? Number(r.YieldMax) : null,
      TimePerCraftSeconds: nz(r.TimePerCraftSeconds, 0),
      IsMineable: !!r.IsMineable,
      Ingredients: {},
      YieldOutcomes: normalizeOutcomes(r.YieldOutcomes),
      // Deprecated, still accepted for backward compat on Min/Max only
      YieldMinChance: toProb(r.YieldMinChance),
      YieldMaxChance: toProb(r.YieldMaxChance)
    };

    // If only one of min/max present, mirror it
    if (nr.YieldMin !== null && nr.YieldMax === null) nr.YieldMax = nr.YieldMin;
    if (nr.YieldMax !== null && nr.YieldMin === null) nr.YieldMin = nr.YieldMax;

    if (r.Ingredients && typeof r.Ingredients === 'object') {
      for (const [k,v] of Object.entries(r.Ingredients)) {
        const qty = Number(v);
        if (k && Number.isFinite(qty) && qty > 0) nr.Ingredients[k] = qty;
      }
    }
    return nr;
  }

  function load(json) {
    const raw = typeof json === 'string' ? JSON.parse(json) : json;
    const m = {};
    let isArray = true;
    if (Array.isArray(raw)) {
      for (const r of raw) {
        if (!r || !r.Name) continue;
        const nr = normalizeRecipe(r);
        if (nr.Name) m[nr.Name] = nr;
      }
    } else {
      isArray = false;
      for (const [name, r] of Object.entries(raw)) {
        const nr = normalizeRecipe({ ...r, Name: r?.Name || name });
        if (nr.Name) m[nr.Name] = nr;
      }
    }
    map = m; arrayFormat = isArray;
    onChange();
  }

  function exportJson(pretty = true) {
    const obj = arrayFormat ? Object.values(map) : Object.fromEntries(Object.entries(map).map(([k,v]) => [k,v]));
    return JSON.stringify(obj, null, pretty ? 2 : 0);
  }

  function allNames() { return Object.keys(map).sort((a,b) => a.localeCompare(b)); }
  function get(name) { return name ? map[name] : null; }
  function upsert(recipe, oldName = null) {
    const r = normalizeRecipe(recipe);
    if (!r.Name) throw new Error('Recipe must have a Name.');
    if (oldName && oldName !== r.Name) delete map[oldName];
    map[r.Name] = r;
    onChange();
    return r.Name;
  }
  function remove(name){ if (map[name]) { delete map[name]; onChange(); } }
  function clear(){ map = {}; onChange(); }
  function count(){ return Object.keys(map).length; }

  // Init with sample for convenience
  load(SAMPLE);

  return { subscribe, load, exportJson, allNames, get, upsert, remove, clear, count, normalizeRecipe };
})();

// ----------------- Tabs -----------------
(() => {
  const tabs = $$('.tab');
  const panels = $$('.panel');
  tabs.forEach(t => t.addEventListener('click', () => {
    tabs.forEach(x => x.classList.remove('active'));
    panels.forEach(p => p.classList.remove('active'));
    t.classList.add('active');
    $('#' + t.dataset.tab).classList.add('active');
  }));
})();

// ----------------- BUILDER MODULE -----------------
(() => {
  const tblBody = $('#recipesTable tbody');
  const searchBox = $('#searchBox');
  const storeStatus = $('#storeStatus');

  const rName = $('#rName');
  const rMine = $('#rMine');
  const rFocus = $('#rFocus');
  const rTime = $('#rTime');
  const rYield = $('#rYield');
  const rYmin = $('#rYmin');
  const rYmax = $('#rYmax');

  const yieldTypeEl = $('#yieldType');
  const fixedBox = $('#fixedBox');
  const rangeBox = $('#rangeBox');
  const multiBox = $('#multiBox');
  const rangeAvgHint = $('#rangeAvgHint');

  const btnNew = $('#btnNew');
  const btnDuplicate = $('#btnDuplicate');
  const btnDelete = $('#btnDelete');
  const btnSave = $('#btnSave');
  const btnReset = $('#btnReset');
  const btnSortAZ = $('#btnSortAZ');

  const btnAddIng = $('#btnAddIng');
  const btnAutofillIng = $('#btnAutofillIng');
  const ingTableBody = $('#ingTable tbody');

  const btnLoadSample = $('#btnLoadSample');
  const btnExportJson = $('#btnExportJson');
  const btnCopyJson = $('#btnCopyJson');
  const btnLoadText = $('#btnLoadText');
  const btnClearStore = $('#btnClearStore');
  const fileRecipes = $('#fileRecipes');
  const jsonText = $('#jsonText');
  const importStatus = $('#importStatus');

  // Tools panel
  const sampleYields = $('#sampleYields');
  const statCount = $('#statCount');
  const statMean  = $('#statMean');
  const statMin   = $('#statMin');
  const statMax   = $('#statMax');
  const statStd   = $('#statStd');

  const btnApplyAvgFixed = $('#btnApplyAvgFixed');
  const btnApplyRange = $('#btnApplyRange');
  const btnClearSamples = $('#btnClearSamples');
  const btnSamplesToOutcomes = $('#btnSamplesToOutcomes');
  const toolsMsg = $('#toolsMsg');

  // Outcomes grid
  const outTableBody = $('#outTable tbody');
  const btnAddOutcome = $('#btnAddOutcome');
  const btnClearOutcomes = $('#btnClearOutcomes');

  let selected = null; // selected recipe name
  let formDirty = false;

  function renderList() {
    const filter = (searchBox.value || '').toLowerCase().trim();
    const names = Store.allNames().filter(n => !filter || n.toLowerCase().includes(filter));
    tblBody.innerHTML = '';
    for (const name of names) {
      const r = Store.get(name);
      const tr = document.createElement('tr');
      if (name === selected) tr.classList.add('selected');
      const tag = r.IsMineable ? 'mine' : (Object.keys(r.Ingredients).length ? 'craft' : 'solo');
      const yieldFixedCell = (r.Yield == null) ? '—' : fmt2(r.Yield);
      tr.innerHTML = `
        <td>•</td>
        <td>${name}</td>
        <td class="num">${yieldFixedCell}</td>
        <td class="num">${r.YieldMin ?? ''} / ${r.YieldMax ?? ''}</td>
        <td class="num">${fmt2(r.FocusCost)}</td>
        <td class="num">${Math.round(r.TimePerCraftSeconds || 0)}</td>
        <td>${tag}</td>
      `;
      tr.addEventListener('click', () => { select(name); });
      tblBody.appendChild(tr);
    }
    storeStatus.textContent = `${Store.count()} recipes`;
  }

  function select(name) {
    selected = name;
    renderList();
    loadForm(Store.get(name));
  }

  function newRecipe() {
    selected = null;
    loadForm({ Name: '', FocusCost: 0, Yield: 1, YieldMin: null, YieldMax: null, TimePerCraftSeconds: 0, IsMineable: false, Ingredients: {} });
    renderList();
    rName.focus();
  }

  function duplicateSelected() {
    const base = Store.get(selected);
    if (!base) return;
    const copy = JSON.parse(JSON.stringify(base));
    copy.Name = uniqueName(copy.Name + ' Copy');
    loadForm(copy);
  }
  function uniqueName(base) {
    let n = base, i = 2;
    while (Store.get(n)) n = `${base} ${i++}`;
    return n;
  }

  // ----- Outcomes helpers
  function outcomeRows() {
    return Array.from(outTableBody.querySelectorAll('tr')).map(tr => ({
      tr, qty: tr.querySelector('.out-qty'), prob: tr.querySelector('.out-prob')
    })).filter(r => r.qty && r.prob);
  }
  function addOutcomeRow(qty = '', prob = '') {
    const tr = document.createElement('tr');
    tr.innerHTML = `
      <td class="num"><input class="out-qty"  type="number" min="0" step="0.01" value="${qty}"/></td>
      <td class="num"><input class="out-prob" type="number" min="0" step="0.01" value="${prob}"/></td>
      <td><button class="btn small ghost removeOut">✕</button></td>`;
    tr.querySelector('.removeOut').addEventListener('click', () => { tr.remove(); formDirty = true; });
    ['input','change'].forEach(ev => tr.addEventListener(ev, () => formDirty = true));
    outTableBody.appendChild(tr);
  }
  function renderOutcomes(obj) {
    outTableBody.innerHTML = '';
    if (obj && typeof obj === 'object' && Object.keys(obj).length) {
      for (const [q,p] of Object.entries(obj)) {
        const display = (Number(p) <= 1 ? Number(p) * 100 : Number(p)); // show as %
        addOutcomeRow(q, Math.round(display * 100) / 100);
      }
    } else {
      addOutcomeRow('', '');
    }
  }

  // ----- Ingredient helpers
  function renderIngredients(ings) {
    ingTableBody.innerHTML = '';
    for (const [n,q] of Object.entries(ings)) addIngRow(n, q);
    if (Object.keys(ings).length === 0) addIngRow('', 0);
  }
  function addIngRow(name = '', qty = 0) {
    const tr = document.createElement('tr');
    tr.innerHTML = `
      <td>
        <input class="ing-name" list="allNames" type="text" placeholder="Ingredient name" value="${name}"/>
        <datalist id="allNames">${Store.allNames().map(n => `<option value="${n}">`).join('')}</datalist>
      </td>
      <td class="num"><input class="ing-qty" type="number" min="0" step="0.01" value="${qty}"/></td>
      <td><button class="btn small ghost remove">✕</button></td>
    `;
    tr.querySelector('.remove').addEventListener('click', () => { tr.remove(); formDirty = true; });
    ['input','change'].forEach(ev => tr.addEventListener(ev, () => formDirty = true));
    ingTableBody.appendChild(tr);
  }
  // Helper: return the input elements for every ingredient row
  function ingredientRows() {
    return Array.from(ingTableBody.querySelectorAll('tr'))
      .map(tr => ({
        el: tr,
        name: tr.querySelector('.ing-name'),
        qty: tr.querySelector('.ing-qty')
      }))
      .filter(r => r.name && r.qty);
  }

  // ----- Yield type UI
  function applyYieldTypeUI(type) {
    fixedBox.style.display = (type === 'fixed') ? '' : 'none';
    rangeBox.style.display = (type === 'range') ? '' : 'none';
    multiBox.style.display = (type === 'multi') ? '' : 'none';
    recomputeSamplesUI(); // gate Tool buttons accordingly
  }
  function inferYieldTypeFromRecord(r) {
    if (r?.YieldOutcomes && Object.keys(r.YieldOutcomes).length) return 'multi';
    if (r?.Yield != null) return 'fixed';
    if (r?.YieldMin != null || r?.YieldMax != null) return 'range';
    return 'fixed';
  }
  function updateRangeAvgHint() {
    if (!rangeAvgHint) return;
    const min = (rYmin.value === '' ? null : Number(rYmin.value));
    const max = (rYmax.value === '' ? null : Number(rYmax.value));
    if (min == null && max == null) { rangeAvgHint.textContent = 'Average used in “Average” mode: —'; return; }
    const m = (min == null ? max : (max == null ? min : (min + max) / 2));
    rangeAvgHint.textContent = `Average used in “Average” mode: ${Number.isFinite(m) ? Math.round(m*100)/100 : '—'}`;
  }
  ['input','change'].forEach(ev => {
    rYmin?.addEventListener(ev, updateRangeAvgHint);
    rYmax?.addEventListener(ev, updateRangeAvgHint);
  });
  yieldTypeEl?.addEventListener('change', () => applyYieldTypeUI(yieldTypeEl.value));

  // ----- Form load/save
  function loadForm(r) {
    formDirty = false;
    $('#editTitle').textContent = r?.Name ? `Edit: ${r.Name}` : 'New Recipe';
    rName.value = r?.Name || '';
    rMine.checked = !!r?.IsMineable;
    rFocus.value = r?.FocusCost ?? 0;
    rTime.value  = r?.TimePerCraftSeconds ?? 0;

    // Fixed yield can be null -> blank
    rYield.value = (r?.Yield == null) ? '' : r.Yield;

    // Min/Max allow 0 and null -> blank
    rYmin.value  = (r?.YieldMin == null) ? '' : r.YieldMin;
    rYmax.value  = (r?.YieldMax == null) ? '' : r.YieldMax;

    // Outcomes grid
    renderOutcomes(r?.YieldOutcomes);

    // Decide type and show appropriate section
    const t = inferYieldTypeFromRecord(r);
    if (yieldTypeEl) yieldTypeEl.value = t;
    applyYieldTypeUI(t);
    updateRangeAvgHint();

    renderIngredients(r?.Ingredients || {});
    toolsMsg.textContent = '';
  }

  function readForm() {
    const yieldStr = String(rYield.value ?? '').trim();
    const type = yieldTypeEl ? yieldTypeEl.value : 'fixed';

    // Collect Outcomes first
    const outPairs = outcomeRows()
      .map(r => [Number(r.qty.value), Number(r.prob.value)])
      .filter(([q,p]) => Number.isFinite(q) && q >= 0 && Number.isFinite(p) && p >= 0);

    const rec = {
      Name: rName.value.trim(),
      IsMineable: !!rMine.checked,
      FocusCost: nz(rFocus.value, 0),
      TimePerCraftSeconds: nz(rTime.value, 0),

      // Keep null if blank
      Yield: yieldStr === '' ? null : nz(yieldStr, 1),

      // Allow 0 and null
      YieldMin: (rYmin.value === '' ? null : nz(rYmin.value)),
      YieldMax: (rYmax.value === '' ? null : nz(rYmax.value)),

      // Outcomes (unnormalized here; Store will normalize)
      YieldOutcomes: outPairs.length ? Object.fromEntries(outPairs) : null,

      // Deprecated fields are not surfaced in UI anymore
      YieldMinChance: null,
      YieldMaxChance: null,

      Ingredients: {}
    };

    // Prune fields based on selected type
    if (type === 'fixed') {
      rec.YieldMin = rec.YieldMax = null;
      rec.YieldOutcomes = null;
    } else if (type === 'range') {
      rec.Yield = null;
      rec.YieldOutcomes = null;
    } else if (type === 'multi') {
      rec.Yield = null;
      rec.YieldMin = rec.YieldMax = null;
    }

    // gather ingredients
    for (const row of ingredientRows()) {
      const name = row.name.value.trim();
      const qty  = Number(row.qty.value);
      if (name && Number.isFinite(qty) && qty > 0) rec.Ingredients[name] = qty;
    }
    return rec;
  }

  function save() {
    const rec = readForm();
    const old = selected;
    const newName = Store.upsert(rec, old);
    selected = newName;
    renderList();
    loadForm(Store.get(newName));
    formDirty = false;
  }

  function del() {
    if (!selected) return;
    Store.remove(selected);
    selected = null;
    renderList();
    newRecipe();
  }

  // ----- Tools
  function parseSamples(text) {
    // Accept 0; ignore negatives & non-numeric
    const nums = (String(text).match(/[+-]?\d+(\.\d+)?/g) || [])
      .map(Number)
      .filter(n => Number.isFinite(n) && n >= 0);
    return nums;
  }
  function recomputeSamplesUI() {
    const nums = parseSamples(sampleYields.value);
    const n = nums.length;

    if (n === 0) {
      statCount.textContent = '0';
      statMean.textContent  = '—';
      statMin.textContent   = '—';
      statMax.textContent   = '—';
      statStd.textContent   = '—';
      btnApplyAvgFixed.disabled = true;
      btnApplyRange.disabled    = true;
      if (btnSamplesToOutcomes) btnSamplesToOutcomes.disabled = true;
      return;
    }

    const sum = nums.reduce((s,v)=>s+v,0);
    const mean = sum / n;
    const minV = Math.min(...nums);
    const maxV = Math.max(...nums);
    const variance = nums.reduce((s,v)=>s + Math.pow(v - mean, 2), 0) / n; // pop std dev
    const std = Math.sqrt(variance);
    const fmt = x => (Math.round(x * 100) / 100).toString();

    statCount.textContent = String(n);
    statMean.textContent  = fmt(mean);
    statMin.textContent   = fmt(minV);
    statMax.textContent   = fmt(maxV);
    statStd.textContent   = fmt(std);

    // Gate buttons by Yield Type
    const type = yieldTypeEl ? yieldTypeEl.value : 'fixed';
    btnApplyAvgFixed.disabled   = !(mean > 0 && type === 'fixed');
    btnApplyRange.disabled      = !(type === 'range');
    if (btnSamplesToOutcomes) btnSamplesToOutcomes.disabled = !(type === 'multi');
  }
  sampleYields?.addEventListener('input', recomputeSamplesUI);
  btnClearSamples?.addEventListener('click', e => {
    e.preventDefault();
    sampleYields.value = '';
    recomputeSamplesUI();
    toolsMsg.textContent = '';
  });
  btnApplyAvgFixed?.addEventListener('click', e => {
    e.preventDefault();
    if (yieldTypeEl?.value !== 'fixed') return;
    const nums = parseSamples(sampleYields.value);
    if (!nums.length) return;
    const mean = nums.reduce((s,v)=>s+v,0) / nums.length;
    if (mean <= 0) { toolsMsg.textContent = 'Average is 0 — cannot set Fixed Yield to 0. Use Min/Max or Outcomes.'; return; }
    rYield.value = (Math.round(mean * 100) / 100);
    toolsMsg.textContent = 'Applied average to Fixed Yield.';
  });
  btnApplyRange?.addEventListener('click', e => {
    e.preventDefault();
    if (yieldTypeEl?.value !== 'range') return;
    const nums = parseSamples(sampleYields.value);
    if (!nums.length) return;
    const minV = Math.min(...nums);
    const maxV = Math.max(...nums);
    rYmin.value = (Math.round(minV * 100) / 100);
    rYmax.value = (Math.round(maxV * 100) / 100);
    updateRangeAvgHint();
    toolsMsg.textContent = 'Set Yield Min/Max from samples.';
  });
  btnSamplesToOutcomes?.addEventListener('click', e => {
    e.preventDefault();
    if (yieldTypeEl?.value !== 'multi') return;
    const nums = parseSamples(sampleYields.value);
    if (!nums.length) return;
    const freq = {};
    for (const n of nums) freq[n] = (freq[n] || 0) + 1;
    const total = nums.length;

    outTableBody.innerHTML = '';
    for (const [q, count] of Object.entries(freq).sort((a,b) => Number(a[0]) - Number(b[0]))) {
      const pct = (count / total) * 100;
      addOutcomeRow(q, Math.round(pct * 100) / 100);
    }
    toolsMsg.textContent = 'Built outcome % from samples.';
  });

  // ----- Outcomes grid buttons
  btnAddOutcome?.addEventListener('click', e => { e.preventDefault(); addOutcomeRow(); });
  btnClearOutcomes?.addEventListener('click', e => { e.preventDefault(); outTableBody.innerHTML = ''; addOutcomeRow(); });

  // Events
  Store.subscribe(renderList);
  searchBox.addEventListener('input', renderList);
  btnNew.addEventListener('click', newRecipe);
  btnDuplicate.addEventListener('click', duplicateSelected);
  btnDelete.addEventListener('click', del);
  btnSave.addEventListener('click', save);
  btnReset.addEventListener('click', () => { selected ? loadForm(Store.get(selected)) : newRecipe(); formDirty = false; });
  btnSortAZ.addEventListener('click', renderList);

  btnAddIng.addEventListener('click', () => addIngRow());
  btnAutofillIng.addEventListener('click', () => {
    // refresh datalist options
    for (const dl of document.querySelectorAll('datalist#allNames')) {
      dl.innerHTML = Store.allNames().map(n => `<option value="${n}">`).join('');
    }
  });

  btnLoadSample.addEventListener('click', () => { Store.load(SAMPLE); importStatus.textContent = 'Loaded sample'; importStatus.className = 'pill ok'; });
  btnExportJson.addEventListener('click', () => download('recipes.json', Store.exportJson(true)));
  btnCopyJson.addEventListener('click', async () => {
    await navigator.clipboard.writeText(Store.exportJson(true));
    importStatus.textContent = 'Copied to clipboard'; importStatus.className = 'pill ok';
  });
  btnLoadText.addEventListener('click', () => {
    try { Store.load(jsonText.value); importStatus.textContent = 'Loaded from text'; importStatus.className = 'pill ok'; }
    catch (e) { importStatus.textContent = 'Invalid JSON'; importStatus.className = 'pill err'; }
  });
  btnClearStore.addEventListener('click', () => { Store.clear(); importStatus.textContent = 'Cleared'; importStatus.className = 'pill warn'; });
  fileRecipes.addEventListener('change', async () => {
    const f = fileRecipes.files?.[0]; if (!f) return;
    try { Store.load(await f.text()); importStatus.textContent = `Loaded ${f.name}`; importStatus.className = 'pill ok'; }
    catch (e) { importStatus.textContent = 'Invalid JSON'; importStatus.className = 'pill err'; }
  });

  // Initialize the panel once on load/open
  recomputeSamplesUI();
  newRecipe();
  renderList();
})();

// ----------------- CALCULATOR MODULE -----------------
(() => {
  const targetSelect = $('#targetSelect');
  const btnRefreshMaterials = $('#btnRefreshMaterials');
  const yieldModeRadios = $$('input[name="yieldMode"]');
  const calcModeRadios = $$('input[name="calcMode"]');
  const qtyWrap = $('#qtyWrap');
  const allWrap = $('#allWrap');
  const desiredQty = $('#desiredQty');
  const availableFocusQty = $('#availableFocusQty');
  const availableFocusAll = $('#availableFocusAll');
  const maxCraftablePreview = $('#maxCraftablePreview');

  const btnCalc = $('#btnCalc');
  const btnExportCsv = $('#btnExportCsv');

  const errors = $('#errors');
  const summary = $('#summary');
  const summaryBadge = $('#summaryBadge');
  const focusChips = $('#focusChips');
  const timeChips = $('#timeChips');

  const treeOut = $('#treeOut');
  const showTimes = $('#showTimes');
  const leafTableBody = $('#leafTable tbody');

  const state = { lastLines: [], lastTotals: null };

  function updateMaterials() {
    targetSelect.innerHTML = '';
    const names = Store.allNames();
    for (const n of names) {
      const r = Store.get(n);
      const tag = r.IsMineable ? '[mine]' : (Object.keys(r.Ingredients).length ? '[craft]' : '[solo]');
      const opt = document.createElement('option');
      opt.value = n; opt.textContent = `${n} ${tag}`;
      targetSelect.appendChild(opt);
    }
  }
  Store.subscribe(updateMaterials);

  const YieldMode = { Safe:'safe', Avg:'average', Opt:'optimistic' };

  function effectiveYield(rec, mode) {
    // 1) Explicit outcomes override everything
    if (rec.YieldOutcomes && typeof rec.YieldOutcomes === 'object') {
      const pairs = Object.entries(rec.YieldOutcomes)
        .map(([k,v]) => [Number(k), Number(v)])
        .filter(([qty,p]) => Number.isFinite(qty) && qty >= 0 && Number.isFinite(p) && p >= 0);

      if (pairs.length) {
        const min  = Math.min(...pairs.map(([q]) => q));
        const max  = Math.max(...pairs.map(([q]) => q));
        const sumP = pairs.reduce((s,[,p]) => s + p, 0) || 1;
        const avg  = pairs.reduce((s,[q,p]) => s + q * (p / sumP), 0);
        if (mode === YieldMode.Safe) return Math.max(0, min);
        if (mode === YieldMode.Opt)  return Math.max(0, max);
        return Math.max(0, avg);
      }
    }

    // 2) Min/Max window (>= 0) with optional probabilities (deprecated in UI, still supported)
    const hasMin = rec.YieldMin != null && rec.YieldMin >= 0;
    const hasMax = rec.YieldMax != null && rec.YieldMax >= 0;
    if (hasMin || hasMax) {
      const min = hasMin ? rec.YieldMin : (rec.Yield ?? 1);
      const max = hasMax ? rec.YieldMax : (rec.Yield ?? 1);
      if (mode === YieldMode.Safe) return Math.max(0, min);
      if (mode === YieldMode.Opt)  return Math.max(0, max);

      let pMin = rec.YieldMinChance, pMax = rec.YieldMaxChance;
      if (pMin == null && pMax == null) return Math.max(0, (min + max) / 2);
      if (pMin == null) pMin = 1 - pMax;
      if (pMax == null) pMax = 1 - pMin;
      const sum = (pMin || 0) + (pMax || 0);
      if (sum <= 0) return Math.max(0, (min + max) / 2);
      pMin /= sum; pMax /= sum;
      return Math.max(0, min * pMin + max * pMax);
    }

    // 3) Fixed yield (fallback)
    const y = (rec.Yield == null) ? 1 : Number(rec.Yield);
    return Math.max(0, Number.isFinite(y) ? y : 1);
  }

  function calculateFocus(target, unitsRequested, mode) {
    const stack = [];
    const lines = [];
    function recurse(name, reqUnits, level) {
      const rec = Store.get(name);
      if (!rec) throw new Error(`Unknown material: ${name}`);
      if (stack.includes(name)) {
        const cyc = [...stack, name].join(' -> ');
        throw new Error(`Cycle detected: ${cyc}`);
      }
      stack.push(name);

      const y = effectiveYield(rec, mode);
      if (reqUnits > 0 && y <= 0) {
        throw new Error(`Effective yield is 0 for "${rec.Name}" in current yield mode; cannot produce the requested units. Try Average/Optimistic or define probabilities.`);
      }
      const crafts = Math.ceil(reqUnits / Math.max(y, 0.0000001)); // guard tiny floats
      const nodeFocus = crafts * (rec.FocusCost || 0);
      const nodeTime  = crafts * Math.max(0, rec.TimePerCraftSeconds || 0);

      const action = rec.IsMineable
        ? ((rec.FocusCost||0) > 0 ? 'Mine' : 'Gather')
        : 'Craft';

      lines.push({
        Level: level,
        Action: action,
        Material: rec.Name,
        Crafts: crafts,
        Yield: y,
        UnitsRequested: reqUnits,
        FocusUsed: nodeFocus,
        TimeUsedSeconds: nodeTime
      });

      let totalFocus = nodeFocus;
      for (const [ing, perCraft] of Object.entries(rec.Ingredients || {})) {
        const need = perCraft * crafts;
        totalFocus += recurse(ing, need, level + 1);
      }

      stack.pop();
      return totalFocus;
    }
    const total = recurse(target, unitsRequested, 0);
    return { totalFocus: total, lines };
  }

  function sumTimeSeconds(lines) {
    return lines.reduce((s, ln) => s + (ln.TimeUsedSeconds || 0), 0);
  }

  function maxCraftable(target, availableFocus, mode) {
    const oneUnit = calculateFocus(target, 1, mode).totalFocus;
    if (oneUnit <= 0) return 0;
    let lo = 0, hi = 1;
    while (true) {
      const f = calculateFocus(target, hi, mode).totalFocus;
      if (f > availableFocus) break;
      hi *= 2; if (hi > 1_000_000_000) break;
    }
    while (lo < hi) {
      const mid = lo + Math.floor((hi - lo + 1) / 2);
      const f = calculateFocus(target, mid, mode).totalFocus;
      if (f <= availableFocus) lo = mid; else hi = mid - 1;
    }
    return lo;
  }

  function renderTree(lines, showT) {
    const out = [];
    for (const ln of lines) {
      const pad = ' '.repeat(ln.Level * 2);
      const timeTail = showT && ln.TimeUsedSeconds > 0 ? ` + ${formatDuration(ln.TimeUsedSeconds)}` : '';
      if (ln.Action === 'Gather' && ln.FocusUsed === 0) {
        out.push(`${pad}- ${ln.Action} ${ln.UnitsRequested} ${ln.Material} (no Focus cost${timeTail ? ',' + timeTail : ''})`);
      } else {
        out.push(`${pad}- ${ln.Action} ${ln.Crafts}x ${ln.Material} → ${fmt2(ln.FocusUsed)} Focus (Yield ${ln.Yield}, Req ${ln.UnitsRequested})${timeTail}`);
      }
    }
    return out.join('\n');
  }

  function renderLeafChecklist(lines) {
    const leaves = {};
    for (const ln of lines) {
      const rec = Store.get(ln.Material);
      const isLeaf = !rec || !rec.Ingredients || Object.keys(rec.Ingredients).length === 0;
      if (!isLeaf) continue;
      if (!leaves[ln.Material]) leaves[ln.Material] = { units: 0, yieldEff: ln.Yield > 0 ? ln.Yield : (rec?.Yield || 1), rec };
      leaves[ln.Material].units += ln.UnitsRequested;
      if (ln.Yield > 0) leaves[ln.Material].yieldEff = ln.Yield;
    }
    const rows = [];
    for (const [name, v] of Object.entries(leaves)) {
      const yieldEff = Math.max(1, v.yieldEff);
      const crafts = Math.ceil(v.units / yieldEff);
      const focus = crafts * (v.rec?.FocusCost || 0);
      const time = crafts * Math.max(0, v.rec?.TimePerCraftSeconds || 0);
      const action = v.rec?.IsMineable ? ((v.rec?.FocusCost || 0) > 0 ? 'Mine' : 'Gather') : 'Craft';
      rows.push({ action, name, crafts, yieldEff, units: v.units, focus, time });
    }
    rows.sort((a,b) => (b.focus - a.focus) || a.action.localeCompare(b.action) || a.name.localeCompare(b.name));
    leafTableBody.innerHTML = '';
    for (const r of rows) {
      const tr = document.createElement('tr');
      tr.innerHTML = `
        <td>${r.action}</td>
        <td>${r.name}</td>
        <td class="num">${r.crafts}</td>
        <td class="num">${r.yieldEff}</td>
        <td class="num">${r.units}</td>
        <td class="num">${fmt2(r.focus)}</td>
        <td>${r.time > 0 ? formatDuration(r.time) : ''}</td>`;
      leafTableBody.appendChild(tr);
    }
  }

  function chip(text, cls) {
    const el = document.createElement('span');
    el.className = 'chip' + (cls ? ` ${cls}` : '');
    el.textContent = text;
    return el;
  }
  function showError(msg) {
    errors.style.display = '';
    errors.textContent = msg;
    summary.style.display = 'none';
    treeOut.textContent = '{}';
    leafTableBody.innerHTML = '';
    summaryBadge.textContent = 'Error';
  }
  function hideError(){ errors.style.display = 'none'; errors.textContent = ''; }

  function renderSummaryDesired(target, qty, available, safe, opt, timeSafe, timeOpt) {
    summary.style.display = '';
    summaryBadge.textContent = `Target: ${target} × ${qty}`;
    focusChips.innerHTML = ''; timeChips.innerHTML = '';
    if (Math.abs(safe - opt) < 1e-9) focusChips.appendChild(chip(`Total Focus: ${fmt2(safe)}`));
    else focusChips.appendChild(chip(`Total Focus: ${fmt2(safe)} (Safe) → ${fmt2(opt)} (Optimistic)`));
    focusChips.appendChild(chip(`Available: ${fmt2(available)}`));
    if (safe <= available) focusChips.appendChild(chip(`✅ Craftable (Safe). Leftover: ${fmt2(available - safe)}`, 'ok'));
    else focusChips.appendChild(chip(`❌ Short by ${fmt2(safe - available)} (Safe)`, 'err'));
    if (Math.abs(timeSafe - timeOpt) < 1e-6) timeChips.appendChild(chip(`Time: ${formatDuration(timeSafe)}`));
    else timeChips.appendChild(chip(`Time: ${formatDuration(timeSafe)} → ${formatDuration(timeOpt)}`));
  }
  function renderSummaryAll(target, available, bestQtySafe, bestQtyOpt, focusUsedSafe, timeSafe, timeOpt) {
    summary.style.display = '';
    summaryBadge.textContent = `Use all Focus on ${target}`;
    focusChips.innerHTML = ''; timeChips.innerHTML = '';
    if (bestQtySafe === bestQtyOpt) {
      focusChips.appendChild(chip(`Max craftable: ${bestQtySafe}`));
      timeChips.appendChild(chip(`Time: ${formatDuration(timeSafe)}`));
    } else {
      focusChips.appendChild(chip(`Max craftable: ${bestQtySafe} (Safe) → ${bestQtyOpt} (Optimistic)`));
      timeChips.appendChild(chip(`Time: ${formatDuration(timeSafe)} → ${formatDuration(timeOpt)}`));
    }
    focusChips.appendChild(chip(`Focus used (Safe): ${fmt2(focusUsedSafe)} of ${fmt2(available)}`));
  }

  function exportCsv(target, qty, lines, totalFocus) {
    const header = [
      'Level', 'Action', 'Material', 'Crafts', 'Yield', 'UnitsRequested',
      'FocusUsed', 'TimePerCraftSeconds', 'TimeUsedSeconds', 'TimeUsedFormatted'
    ];
    const rows = [header.join(',')];
    for (const ln of lines) {
      const r = Store.get(ln.Material) || {};
      const tpc = nz(r.TimePerCraftSeconds, 0);
      rows.push([
        ln.Level,
        csvEsc(ln.Action),
        csvEsc(ln.Material),
        ln.Crafts,
        ln.Yield,
        ln.UnitsRequested,
        fmt2(ln.FocusUsed),
        Math.round(tpc),
        Math.round(ln.TimeUsedSeconds || 0),
        csvEsc(formatDuration(ln.TimeUsedSeconds || 0))
      ].join(','));
    }
    const totalSec = sumTimeSeconds(lines);
    rows.push(['', '', '', '', '', 'Total Focus', fmt2(totalFocus), '', '', ''].join(','));
    rows.push(['', '', '', '', '', 'Total Time (seconds)', Math.round(totalSec), '', csvEsc(formatDuration(totalSec))].join(','));
    const blob = new Blob([rows.join('\n')], { type: 'text/csv;charset=utf-8' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    const safeName = target.replace(/[^a-z0-9]+/gi, '_').replace(/^_+|_+$/g, '');
    a.download = `focus_breakdown_${safeName}_${qty}_${Date.now()}.csv`;
    a.click();
    URL.revokeObjectURL(a.href);
  }

  // Events
  btnRefreshMaterials.addEventListener('click', updateMaterials);
  calcModeRadios.forEach(r => r.addEventListener('change', () => {
    const mode = getCalcMode();
    qtyWrap.style.display = mode === 'qty' ? '' : 'none';
    allWrap.style.display = mode === 'all' ? '' : 'none';
  }));
  yieldModeRadios.forEach(r => r.addEventListener('change', () => {
    // live preview for "all" mode
    if (getCalcMode() === 'all' && targetSelect.value && isFiniteNum(Number(availableFocusAll.value))) {
      const target = targetSelect.value;
      const available = Number(availableFocusAll.value) || 0;
      const best = maxCraftable(target, available, getYieldMode());
      maxCraftablePreview.textContent = `Max (current yield mode): ${best}`;
    } else {
      maxCraftablePreview.textContent = '—';
    }
  }));
  showTimes.addEventListener('change', () => {
    treeOut.textContent = renderTree(state.lastLines || [], showTimes.checked);
  });

  function getYieldMode() {
    const r = yieldModeRadios.find(x => x.checked);
    return r ? r.value : 'safe';
    }
  function getCalcMode() {
    const r = calcModeRadios.find(x => x.checked);
    return r ? r.value : 'qty';
  }

  btnCalc.addEventListener('click', () => {
    hideError();
    try {
      if (!Store.count()) throw new Error('Load or build recipes first.');
      const target = targetSelect.value;
      if (!target) throw new Error('Pick a target material.');
      const calcMode = getCalcMode();

      if (calcMode === 'all') {
        const available = Number(availableFocusAll.value);
        if (!isFiniteNum(available) || available < 0) throw new Error('Enter available Focus.');
        const bestSafe = maxCraftable(target, available, YieldMode.Safe);
        const bestOpt = maxCraftable(target, available, YieldMode.Opt);

        const safeRun = calculateFocus(target, bestSafe, YieldMode.Safe);
        const focusUsedSafe = safeRun.totalFocus;
        const timeSafe = sumTimeSeconds(safeRun.lines);

        const optRun = calculateFocus(target, bestOpt, YieldMode.Opt);
        const timeOpt = sumTimeSeconds(optRun.lines);

        state.lastLines = safeRun.lines;
        state.lastTotals = { bestQtySafe: bestSafe, bestQtyOpt: bestOpt, focusUsedSafe, timeSafe, timeOpt };

        renderSummaryAll(target, available, bestSafe, bestOpt, focusUsedSafe, timeSafe, timeOpt);
        treeOut.textContent = renderTree(state.lastLines, showTimes.checked);
        renderLeafChecklist(state.lastLines);

        maxCraftablePreview.textContent = `Max (current yield mode): ${maxCraftable(target, available, getYieldMode())}`;
      } else {
        const qty = Number(desiredQty.value || 1);
        const available = Number(availableFocusQty.value || 0);
        if (!isFiniteNum(qty) || qty < 0) throw new Error('Enter desired quantity (>= 0).');

        const safe = calculateFocus(target, qty, YieldMode.Safe);
        const opt = calculateFocus(target, qty, YieldMode.Opt);
        const timeSafe = sumTimeSeconds(safe.lines);
        const timeOpt = sumTimeSeconds(opt.lines);

        state.lastLines = safe.lines;
        state.lastTotals = { focusSafe: safe.totalFocus, focusOpt: opt.totalFocus, timeSafe, timeOpt };

        renderSummaryDesired(target, qty, available, safe.totalFocus, opt.totalFocus, timeSafe, timeOpt);
        treeOut.textContent = renderTree(state.lastLines, showTimes.checked);
        renderLeafChecklist(state.lastLines);
      }
    } catch (e) { showError(String(e.message || e)); }
  });

  btnExportCsv.addEventListener('click', () => {
    if (!state.lastLines?.length) return;
    const qty = getCalcMode() === 'all'
      ? (state.lastTotals?.bestQtySafe ?? 0)
      : (Number(desiredQty.value || 1) || 1);
    const totalFocus = (state.lastTotals?.focusSafe ?? state.lastTotals?.focusUsedSafe ?? 0);
    exportCsv(targetSelect.value, qty, state.lastLines, totalFocus);
  });

  // init
  updateMaterials();
})();
