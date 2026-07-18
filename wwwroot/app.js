const state = {
  minutes: 60,
  timer: null,
  data: null,
  token: new URLSearchParams(location.hash.slice(1)).get("token") || new URLSearchParams(location.search).get("token") || ""
};
const $ = (id) => document.getElementById(id);
const fmt = (n, digits = 1) => Number(n || 0).toLocaleString(undefined, { maximumFractionDigits: digits, minimumFractionDigits: digits });
const memory = (mb) => mb >= 1024 ? `${fmt(mb / 1024, 2)} GB` : `${fmt(mb, 0)} MB`;
const clock = (date) => new Date(date).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
const esc = (value) => String(value ?? "").replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));

function path(values, width = 100, height = 28, fixedMax = null) {
  if (!values?.length) return "";
  const min = fixedMax ? 0 : Math.min(...values);
  const max = fixedMax || Math.max(...values);
  const span = Math.max(.001, max - min);
  return values.map((v, i) => `${i ? "L" : "M"}${(i / Math.max(1, values.length - 1) * width).toFixed(2)},${(height - ((v - min) / span * height)).toFixed(2)}`).join(" ");
}

function spark(el, values, color = "var(--cyan)") {
  el.innerHTML = values?.length > 1 ? `<svg viewBox="0 0 100 28" preserveAspectRatio="none" aria-hidden="true"><path d="${path(values)}" style="stroke:${color}"/></svg>` : "";
}

function systemChart(points) {
  const el = $("systemChart");
  if (!points?.length) { el.innerHTML = '<div class="empty">Waiting for history</div>'; return; }
  const commits = points.map(p => p.commit);
  const available = points.map(p => p.available);
  const cpu = points.map(p => p.cpu);
  const max = Math.max(...commits, ...available, 1) * 1.08;
  const commitPath = path(commits, 1000, 280, max);
  const availablePath = path(available, 1000, 280, max);
  const cpuPath = path(cpu, 1000, 280, 100);
  const fill = `${commitPath} L1000,280 L0,280 Z`;
  el.innerHTML = `<svg viewBox="0 0 1000 280" preserveAspectRatio="none" role="img" aria-label="Commit, available memory, and CPU history">
    <defs><linearGradient id="commitFill" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#d8ff66"/><stop offset="1" stop-color="#d8ff66" stop-opacity="0"/></linearGradient></defs>
    <path class="fill" d="${fill}" fill="url(#commitFill)"/><path class="commit-line" d="${commitPath}"/><path class="available-line" d="${availablePath}"/><path class="cpu-line" d="${cpuPath}"/>
  </svg>`;
  const mid = points[Math.floor(points.length / 2)];
  $("chartStart").textContent = clock(points[0].t);
  $("chartMid").textContent = clock(mid.t);
}

function renderStatus(d) {
  const s = d.summary;
  const labels = { stable: "SYSTEM STABLE", pressure: "MEMORY PRESSURE", critical: "CRITICAL PRESSURE", unknown: "WAITING FOR DATA" };
  $("machineState").textContent = labels[d.status] || "ONLINE";
  $("statusLabel").textContent = labels[d.status] || "ONLINE";
  const narrative = d.status === "critical" ? "Commit or available RAM has crossed the critical threshold. Review the pressure signals below."
    : d.status === "pressure" ? "The system is usable, but memory headroom is becoming constrained."
    : "Memory headroom and commit pressure are currently within the monitoring envelope.";
  $("statusNarrative").textContent = narrative;
  document.documentElement.dataset.status = d.status;
  $("processCount").textContent = s.processCount;
  $("trackedCount").textContent = s.trackedCount;
  $("unresolvedCount").textContent = s.unresolvedCount;
  $("commitPercent").textContent = fmt(s.commitPercent, 0);
  $("commitUsed").textContent = `${fmt(s.commitGb)} GB`;
  $("commitLimit").textContent = `${fmt(s.commitLimitGb)} GB`;
  $("commitBar").style.width = `${Math.min(100, s.commitPercent)}%`;
  $("availableRam").textContent = fmt(s.availableGb);
  $("physicalTotal").textContent = fmt(s.physicalTotalGb);
  $("cpuNow").textContent = fmt(s.cpuPercent, 0);
  $("trackedMemory").textContent = fmt(s.trackedPrivateGb, 2);
  $("launches").textContent = s.starts;
  $("stops").textContent = s.stops;
  spark($("ramSpark"), d.systemSeries.map(p => p.available));
  spark($("cpuSpark"), d.systemSeries.map(p => p.cpu), "var(--amber)");
  const total = Math.max(12, Math.min(38, s.starts + s.stops));
  $("eventTicks").innerHTML = Array.from({ length: total }, (_, i) => `<i class="${i >= total - Math.min(total, s.starts) ? "hot" : ""}"></i>`).join("");
}

function renderSignals(d) {
  const s = d.summary, signals = [];
  if (s.commitPercent >= 78) signals.push({ level: s.commitPercent >= 90 ? "critical" : "", title: "Commit pressure elevated", text: `${fmt(s.commitGb)} of ${fmt(s.commitLimitGb)} GB committed`, value: `${fmt(s.commitPercent,0)}%` });
  if (s.availableGb < 2.5) signals.push({ level: s.availableGb < 1 ? "critical" : "", title: "Physical headroom low", text: "Windows may increasingly rely on the page file", value: `${fmt(s.availableGb)} GB` });
  if (d.unresolved.length) signals.push({ level: "", title: `${d.unresolved.length} unresolved owners`, text: "ProcLens could not trace these processes to a recognized session root", value: memory(d.unresolved.reduce((a,x) => a + x.privateMb, 0)) });
  const peak = [...d.categories].sort((a,b) => b.peakMb - a.peakMb)[0];
  if (peak?.peakMb >= 900) signals.push({ level: "", title: `${peak.name} crossed 900 MB`, text: "Highest workload peak in the selected window", value: memory(peak.peakMb) });
  if (!signals.length) signals.push({ level: "good", title: "No pressure thresholds crossed", text: "ProcLens will flag memory, commit and ownership anomalies here", value: "CLEAR" });
  $("signalCount").textContent = signals.filter(x => x.level !== "good").length;
  $("signals").innerHTML = signals.slice(0,5).map(x => `<div class="signal ${x.level}"><i></i><div><strong>${esc(x.title)}</strong><p>${esc(x.text)}</p></div><b>${esc(x.value)}</b></div>`).join("");
}

function renderCategories(categories) {
  const max = Math.max(...categories.map(x => x.currentMb), 1);
  $("categories").innerHTML = categories.slice(0,12).map((x, i) => {
    const values = x.series.map(p => p.v);
    return `<div class="category"><div class="category-name"><strong>${esc(x.name)}</strong><span class="${x.unresolved ? "unresolved" : ""}">${x.unresolved ? "● OWNER UNRESOLVED" : `${fmt(x.currentCpu)}% CPU`}</span></div>
      <div class="category-spark"><svg viewBox="0 0 100 24" preserveAspectRatio="none"><path d="${path(values,100,24)}" style="stroke:${i === 0 ? 'var(--acid)' : 'var(--cyan)'}"/></svg></div>
      <div class="category-values">${memory(x.currentMb)} <span>/ ${memory(x.peakMb)}</span></div></div>`;
  }).join("") || '<div class="empty">No process samples in range</div>';
}

function renderOwners(owners) {
  $("owners").innerHTML = owners.map(x => `<tr><td>${x.unresolved ? '<i class="unresolved-tag"></i>' : ''}${esc(x.name)}</td><td>${x.processCount}</td><td>${memory(x.currentMb)}</td><td>${memory(x.peakMb)}</td><td>${fmt(x.currentCpu)}%</td></tr>`).join("") || '<tr><td colspan="5">No owner data yet</td></tr>';
}

function renderUnresolved(items) {
  $("unresolved").innerHTML = items.slice(0,8).map(x => `<div class="unresolved-item"><div class="unresolved-top"><strong>${esc(x.category)}</strong><b>${memory(x.privateMb)}</b></div><p>${esc(x.name)} · PID ${x.pid} · ${fmt(x.cpu)}% CPU</p><small>${esc(x.owner)}</small></div>`).join("") || '<div class="empty">No unresolved process owners in latest sample</div>';
}

function renderActivity(events) {
  $("activityFeed").innerHTML = events.slice(0,28).map(x => `<div class="activity ${x.type === 'process_stop' ? 'stop' : ''}"><time>${clock(x.timeUtc)}</time><i></i><div><strong>${x.type === 'process_stop' ? 'Stopped' : 'Started'} · ${esc(x.category)}</strong><p>PID ${x.pid} · ${esc(x.owner)}</p></div></div>`).join("") || '<div class="empty">No starts or stops in range</div>';
}

function renderRun(runs) {
  if (!runs.length) return;
  const run = runs[0];
  const boot = new Date(run.bootUtc).toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
  $("runInfo").textContent = `BOOT ${boot} · RUN ${run.runId.slice(0,8).toUpperCase()}`;
}

async function load(manual = false) {
  const button = $("refresh"); button.classList.add("loading");
  try {
    const response = await fetch(`/api/dashboard?minutes=${state.minutes}&token=${encodeURIComponent(state.token)}`, { cache: "no-store" });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const d = await response.json(); state.data = d;
    renderStatus(d); systemChart(d.systemSeries); renderSignals(d); renderCategories(d.categories); renderOwners(d.owners); renderUnresolved(d.unresolved); renderActivity(d.lifecycle); renderRun(d.runs);
    const age = d.latestSampleUtc ? Math.max(0, Math.round((Date.now() - new Date(d.latestSampleUtc)) / 1000)) : null;
    $("updated").textContent = age === null ? "No samples yet" : `SAMPLE ${age}s AGO · AUTO REFRESH 15s`;
    if (manual) toast("Dashboard refreshed");
  } catch (error) {
    $("machineState").textContent = "COLLECTOR OFFLINE";
    $("statusLabel").textContent = "DASHBOARD DISCONNECTED";
    $("statusNarrative").textContent = state.token ? "The local ProcLens API did not answer. Check the tray icon." : "Open this dashboard from the ProcLens tray icon to authorize local access.";
    toast(`Refresh failed: ${error.message}`);
  } finally { button.classList.remove("loading"); }
}

function toast(message) { const el = $("toast"); el.textContent = message; el.classList.add("show"); setTimeout(() => el.classList.remove("show"), 2200); }
document.querySelectorAll(".range button").forEach(button => button.addEventListener("click", () => {
  document.querySelectorAll(".range button").forEach(x => x.classList.remove("active")); button.classList.add("active"); state.minutes = Number(button.dataset.minutes); load(true);
}));
$("refresh").addEventListener("click", () => load(true));
load(); state.timer = setInterval(load, 15000);
