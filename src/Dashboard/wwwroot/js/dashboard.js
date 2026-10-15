const state = {
    metric: "bytes",
    range: "day",
    customMonth: "",
    session: false,
    adapter: "all",
    country: null,
    countries: [],
    availableMonths: [],
    appsSort: "total",
    appsSortOrder: "desc",
    chartMode: "overlay",
    apps: [],
    timeline: [],
    heatmap: [],
    zoomFrom: null,
    zoomTo: null,
    hoverIndex: null
};

const $ = (id) => document.getElementById(id);
const els = {
    metricSwitch: $("metricSwitch"),
    rangeSwitch: $("rangeSwitch"),
    adapterSwitch: $("adapterSwitch"),
    monthPicker: $("monthPicker"),
    sessionToggle: $("sessionToggle"),
    appsSortSwitch: $("appsSortSwitch"),
    appsSortOrderBtn: $("sort-order-btn"),
    appsSummaryLabel: $("appsSummaryLabel"),
    appsSummaryValue: $("appsSummaryValue"),
    chartModeSwitch: $("chartModeSwitch"),
    chartTooltip: $("chartTooltip"),
    appList: $("appList"),
    timelineCanvas: $("timelineCanvas"),
    heatmapCanvas: $("heatmapCanvas"),
    resetZoom: $("resetZoom"),
    themeToggle: $("themeToggle"),
    exportJson: $("exportJson"),
    exportCsv: $("exportCsv"),
    kpiTotal: $("kpiTotal"),
    kpiSplit: $("kpiSplit"),
    kpiDailyAvg: $("kpiDailyAvg"),
    kpiPeak: $("kpiPeak"),
    kpiPeakSub: $("kpiPeakSub"),
    kpiAppsCount: $("kpiAppsCount"),
    kpiTopCountry: $("kpiTopCountry"),
    countryList: $("countryList"),
    countryMapWrap: $("countryMapWrap"),
    countryViewSwitch: $("countryViewSwitch"),
    activeCountryFilter: $("activeCountryFilter"),
    activeCountryLabel: $("activeCountryLabel"),
    clearCountryFilter: $("clearCountryFilter"),
    compareMonthsBtn: $("compareMonthsBtn"),
    compareSection: $("compareSection"),
    compareClose: $("compareClose"),
    compareMonthA: $("compareMonthA"),
    compareMonthB: $("compareMonthB"),
    compareResults: $("compareResults"),
    modalBackdrop: $("modalBackdrop"),
    modalTitle: $("modalTitle"),
    modalPath: $("modalPath"),
    modalClose: $("modalClose"),
    modalAppTotal: $("modalAppTotal"),
    modalAppShare: $("modalAppShare"),
    modalAppPeak: $("modalAppPeak"),
    modalAppCanvas: $("modalAppCanvas"),
    modalAppCountries: $("modalAppCountries"),
    connectionsBody: $("connectionsBody")
};

Object.assign(els.chartTooltip.style, { position: "absolute", pointerEvents: "none", display: "none", zIndex: "5" });
Object.assign(els.modalAppCanvas.style, { width: "100%", height: "130px" });

const COUNTRY_COORDS = {
    ua: [49, 32], us: [38, -97], ca: [56, -106], mx: [23, -102], br: [-10, -52], ar: [-34, -64], cl: [-30, -71],
    co: [4, -74], pe: [-10, -75], ve: [7, -66], ec: [-1.4, -78.5], bo: [-17, -65], py: [-23, -58], uy: [-33, -56],
    cr: [10, -84], pa: [8.5, -80], do: [19, -70.5], de: [51, 10], nl: [52, 5], gb: [54, -2], ie: [53, -8],
    fr: [46, 2], es: [40, -4], pt: [39.5, -8], it: [42.5, 12.5], ch: [47, 8], at: [47.5, 14], be: [50.5, 4.5],
    lu: [49.8, 6.1], dk: [56, 10], se: [62, 15], no: [62, 10], fi: [64, 26], is: [65, -18], pl: [52, 19],
    cz: [49.8, 15.5], sk: [48.7, 19.7], hu: [47, 19.5], ro: [46, 25], bg: [42.7, 25.5], gr: [39, 22],
    rs: [44, 21], hr: [45.1, 15.2], si: [46.1, 14.8], ba: [44, 17.8], al: [41, 20], mk: [41.6, 21.7],
    me: [42.7, 19.4], xk: [42.6, 20.9], md: [47, 28.5], by: [53.7, 28], lt: [55.2, 23.9], lv: [56.9, 24.6],
    ee: [58.6, 25], ru: [60, 100], tr: [39, 35], cy: [35, 33], mt: [35.9, 14.4], kz: [48, 68], uz: [41.4, 64.6],
    kg: [41.5, 74.6], mn: [47, 103], ge: [42, 43.5], am: [40, 45], az: [40.4, 47.6], il: [31.5, 35],
    jo: [31, 36.5], lb: [33.9, 35.9], ae: [24, 54], sa: [24, 45], qa: [25.3, 51.2], kw: [29.3, 47.5],
    ir: [32, 53], iq: [33, 44], eg: [26, 30], za: [-29, 24], ng: [9, 8], ke: [0.5, 38], et: [9, 40],
    gh: [8, -1], ma: [32, -6], dz: [28, 2.6], tn: [34, 9], in: [21, 78], pk: [30, 70], bd: [24, 90],
    np: [28, 84], lk: [7.9, 80.7], cn: [35, 103], jp: [36, 138], kr: [36.5, 127.8], tw: [23.7, 121],
    hk: [22.3, 114.2], sg: [1.3, 103.8], my: [4, 102], th: [15, 101], vn: [16, 106], id: [-2, 118],
    ph: [12.5, 122], au: [-25, 134], nz: [-41, 174]
};

// Спрощені силуети материків: [довгота, широта]. Кожен полігон замкнений програмно (closePath).
const CONTINENTS = [
    // Північна Америка
    [[-168,65.5],[-166,68.5],[-156,71.3],[-141,69.6],[-128,70],[-115,68.5],[-108,68],[-96,68.5],[-94,71.5],[-88,68.5],[-82,69.5],[-81,67],[-87,64],[-93,61],[-94,58.8],[-92.5,57],[-86,55.5],[-82,53],[-79.5,51.5],[-79,55],[-77,58],[-78,62.3],[-73,62],[-69.5,59],[-65,60.3],[-61.5,56.5],[-57,53.5],[-56,51.5],[-60,50.2],[-66,50.2],[-70,47.2],[-65,49],[-64.5,46.5],[-61,45.5],[-65,43.5],[-67,44.8],[-70,43.7],[-70.5,41.8],[-74,40.5],[-75.5,38],[-76,35],[-78,33.8],[-81,31.5],[-80,27],[-80.3,25.2],[-82,26.5],[-83,29.5],[-85.5,30],[-89,30.2],[-90.5,29.1],[-94,29.5],[-97.3,27.5],[-97.5,24],[-97.8,21.5],[-96,19],[-94.5,18.2],[-91,18.8],[-90.5,21],[-87,21.5],[-87.5,18],[-88.3,15.8],[-84,15.8],[-83.3,12.5],[-83.8,10.5],[-82,9],[-79.5,9.5],[-77.4,8.6],[-78,7.5],[-80,7.3],[-81.7,8.2],[-83.7,9],[-85.7,10.8],[-87.5,13],[-91.3,14],[-94.5,16.2],[-97,15.8],[-101,17.3],[-105.5,20],[-105.3,22.5],[-108,25.5],[-112.5,29.5],[-114.7,31.7],[-114.5,30],[-112,26.5],[-110,23],[-109.5,23.2],[-112,25],[-114.5,28],[-117,32.5],[-120.5,34.5],[-122.5,37.5],[-124.2,40.5],[-124,46.2],[-124.7,48.4],[-123,49],[-127,51],[-130,55],[-133,57],[-137,58.5],[-141,60],[-147,60.8],[-152,59.5],[-156,57.5],[-162,55.2],[-164.5,54.6],[-160,58.5],[-162,60],[-165,62],[-161,64.5]],

    // Південна Америка
    [[-77.5,8.5],[-75.5,10.8],[-72,12],[-71.5,10.5],[-68,10.7],[-63,10.7],[-61,10.5],[-60,8.5],[-57,6],[-52,5],[-50,1.5],[-50,0],[-48,-1],[-44.5,-2.5],[-40,-2.8],[-35,-5.2],[-34.8,-8],[-37,-11.5],[-39,-14],[-39,-18],[-41,-22],[-44,-23],[-48,-25.5],[-48.7,-28],[-51,-31],[-53.5,-34],[-56.5,-34.8],[-58.5,-34.5],[-57,-37],[-62,-39],[-65,-41],[-64,-42.5],[-65.5,-45],[-67.5,-46.5],[-66,-48],[-69,-51],[-68.5,-52.5],[-71,-53.8],[-74,-52],[-75.5,-48],[-73.7,-43.5],[-73.5,-38],[-71.5,-32],[-71.5,-28],[-70.3,-23],[-70.2,-18.3],[-75.5,-15.5],[-78,-11],[-81.2,-6],[-80.5,-3.5],[-80,-1],[-80.5,0.5],[-78.5,2],[-77.5,4],[-77.4,6.5]],

    // Євразія
    [[-9.5,37],[-9.5,43],[-8,43.7],[-1.8,43.4],[-1.2,46],[-4.5,48.4],[-1.5,48.7],[1.5,50.2],[3.5,51.4],[5,53.3],[8.5,54],[8.5,57],[10.5,57.7],[10.5,55],[12,54.2],[14,54],[18.5,54.8],[21,55.2],[21.5,57.5],[24,57.2],[23.5,59.2],[28,59.5],[30,60],[23,59.9],[21.5,63],[25,65.5],[22,65.8],[17.5,62.5],[17,60.5],[18.5,59.7],[16.5,57],[14.5,56],[12.8,56],[11.5,59],[8,58],[5.5,59],[5,62],[10,64.5],[14.5,68],[19,70],[25,71],[31,70],[40,67.5],[41,66],[34,66.5],[36,64],[40,64.8],[44,66.2],[44,68.5],[54,68.5],[60,69],[68,68.5],[69,73],[73,72.5],[80,73.5],[87,75],[100,77],[104,77.7],[112,76],[113,73.5],[130,71],[140,72.5],[150,71.5],[160,70],[170,70],[180,69],[180,65.5],[178,64.5],[174,61.5],[170,60],[164,59.8],[163,57.8],[162,54],[160,52.5],[156.5,51],[156,53],[156,57.5],[160,61],[155,59],[142,59.5],[135.5,54.5],[141,53],[140,48],[135,43.5],[132,43],[130,42.5],[129.5,40.5],[127.5,39.5],[129.5,36.5],[129,35],[126.5,34.5],[126.5,37.5],[125,39.5],[122,40.2],[121.5,38.8],[118.5,38.5],[119,37],[122.5,37],[120.5,36],[119,34.5],[121.8,32],[122,30],[120,26],[116.5,23],[113,22],[110.5,21.2],[108.5,21.7],[106.5,20],[105.8,19],[108.5,15.5],[109.3,12],[107,10.5],[105,8.6],[104.8,10.5],[102.5,12],[100.8,13.5],[99.5,10.5],[100.5,7.5],[103.5,4.5],[104,1.5],[101.5,2.5],[100.3,5.5],[98.5,8.5],[98.3,12],[97.7,16.5],[95.5,16],[94.5,19],[92.2,21],[91.5,23],[89,21.8],[86.5,20.3],[84.5,18.5],[82.3,16.5],[80.2,15.5],[80.3,13],[79.8,10.3],[78.5,8.8],[77.5,8],[76.2,10],[74.8,13],[73.2,17.5],[72.8,21],[70.3,21],[69,22.5],[68.5,23.8],[66.5,25.3],[62,25.2],[57.5,25.7],[56.8,27],[54,26.7],[51,28],[50,30],[48.5,29.8],[48,28.5],[50,26.5],[51.5,24.5],[54,24],[56,26],[56.5,24.5],[58.5,23.5],[59.8,22.3],[57.5,18.8],[55,17],[52,16],[48,14],[45,12.8],[43.3,12.7],[42.8,15],[41,19],[39,22],[37,25.5],[35,28],[34.8,29.5],[34.3,31.3],[35.5,33.5],[35.8,36],[32.5,36.2],[30.5,36.5],[27.5,37],[26.5,39.5],[26.2,40.5],[29,41],[31.5,41.2],[35,42],[38,41],[41.5,41.5],[41.7,43.4],[37.5,44.5],[38.2,47],[35,46.5],[33.5,45],[32.5,46.5],[30.8,46.5],[29.7,45.2],[28.6,43.8],[28,42],[26,40.8],[24,40.8],[23.5,40],[22.8,38.3],[23.5,36.5],[21.5,36.8],[21,38.5],[19.5,40.5],[19.5,42],[16.5,43.5],[13.7,45.3],[12.3,45.3],[12.5,44],[14,42],[16,41.8],[18.5,40.2],[17,40.3],[16.5,39.5],[15.7,38],[16,40],[14,40.8],[12,42],[10.5,43.5],[8.8,44.4],[6.5,43.2],[4,43.5],[3.2,42.2],[0.8,41],[-0.3,39.5],[0,38.5],[-0.8,37.6],[-2,36.7],[-5.5,36.1],[-6.3,36.8],[-7.5,37.2],[-9,37]],

    // Африка
    [[-5.8,35.8],[-2,35.1],[2,36.8],[10,37.2],[11,35.5],[10.2,34],[11.5,33.1],[15,32.3],[19.5,30.3],[20,32.3],[23,32.7],[25,31.8],[29,30.9],[32,31.2],[34.2,31.3],[34.9,29.5],[34,27.8],[35.5,24],[37,21],[38.5,18],[40,15.5],[43.2,12.5],[43.3,11.8],[44,10.5],[51,11.8],[51,10.4],[48,5],[44,1.5],[41.5,-2],[39.5,-5],[39,-8],[40.5,-11],[40.5,-15],[36,-18.8],[35,-22],[35.5,-24.3],[32.8,-26.2],[32.8,-29],[30,-31.5],[27.5,-33.5],[24,-34.2],[20,-34.8],[18.4,-34.2],[18.2,-32],[16.5,-28.6],[15,-26.5],[14.5,-22.5],[11.8,-17.5],[13.5,-12.5],[13.2,-9],[12,-5.5],[9.5,-2],[9,0.5],[9.8,3.5],[8.5,4.5],[5.5,4.3],[2,6.3],[-2,4.8],[-4.5,5.2],[-7.5,4.4],[-10.5,6.2],[-13,8.5],[-15,11],[-16.8,12.5],[-17.5,14.7],[-16.5,19],[-16.2,21],[-14.5,26],[-12.5,28],[-9.8,30],[-9.8,32],[-6.8,34]],

    // Австралія
    [[113.5,-22],[114,-26],[115,-30],[115,-34],[118,-35],[123,-34],[126,-32.2],[131,-31.5],[134,-32.5],[135.5,-35],[138,-35.5],[138.5,-34],[137.5,-33],[140,-37.5],[144,-38.5],[146.5,-39],[150,-37.5],[151,-34],[153,-30],[153.2,-27],[150.5,-22.5],[148,-20],[146,-18.5],[145.3,-15],[143.5,-14],[142.5,-10.7],[141.5,-13],[141.5,-17],[139.5,-17.5],[136.5,-15.5],[135.5,-12],[132.5,-11.5],[130,-12.3],[129.5,-15],[127,-14],[125,-15.5],[122.5,-17.5],[121,-19.5],[116.5,-20.7]],

    // Гренландія
    [[-73,78.5],[-62,79],[-40,79],[-20,79],[-19,77],[-22,72],[-25,69],[-32,68],[-40,65],[-43,60],[-48,61],[-52,64.5],[-54,67],[-53,70],[-56,73],[-61,76],[-68,76.5]],

    // Британія
    [[-5.7,50.1],[-3,50.7],[1.4,51.2],[1.7,52.7],[0.2,53.5],[-1.5,55],[-2,57.6],[-3.5,58.6],[-5,58.5],[-6,57],[-5.5,55.5],[-3,54.8],[-3,53.3],[-4.5,53.3],[-5,51.7],[-3.5,51.4]],

    // Японія: Хоншю
    [[130.9,34],[131.5,34.8],[135.5,35.7],[136.8,37],[138.5,37.5],[139.9,40],[140.3,41],[141.5,41.4],[141.9,39.5],[141,37],[140.8,35.7],[139.8,35],[137,34.6],[135.7,33.5],[135,34.3],[133,34.3]],
    // Японія: Хоккайдо
    [[140,41.5],[140,43],[141.5,45.3],[145.5,43.5],[143.5,42],[141,42.5]],
    // Японія: Кюшю
    [[130,33.8],[131.7,33.5],[131.8,31.5],[131,31],[130.2,31.2],[129.8,32.7]],

    // Мадагаскар
    [[49.3,-12],[50.4,-15.5],[49.5,-17.5],[48,-22],[47.2,-25],[45,-25.4],[43.8,-22.5],[43.3,-17.5],[44.5,-16],[47,-15]]
];

const num = (v) => Number(v) || 0;
const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
const rangeKey = () => (state.customMonth ? "custom_month" : state.range);
const dayKey = (ts) => new Date(ts * 1000).toDateString();
const regionNames = (() => { try { return new Intl.DisplayNames(["en"], { type: "region" }); } catch { return null; } })();

function countryName(code) {
    try { return regionNames.of(String(code).toUpperCase()) || String(code).toUpperCase(); }
    catch { return String(code).toUpperCase(); }
}

function query() {
    let q = state.customMonth ? `range=custom_month&month=${state.customMonth}` : `range=${state.range}`;
    q += `&session=${state.session}&adapter=${encodeURIComponent(state.adapter)}`;
    if (state.country) q += `&country=${encodeURIComponent(state.country)}`;
    return q;
}

function formatBytes(bytes) {
    if (bytes < 1024) return bytes + " B";
    const units = ["KB", "MB", "GB", "TB"];
    let value = bytes, unitIndex = -1;
    do { value /= 1024; unitIndex++; } while (value >= 1024 && unitIndex < units.length - 1);
    return value.toFixed(value >= 10 ? 0 : 1) + " " + units[unitIndex];
}

function formatBytesDetailed(bytes) {
    const value = Number(bytes);
    if (!Number.isFinite(value) || value <= 0) return "0 B";
    if (value < 1024) return Math.round(value) + " B";
    const units = ["KB", "MB", "GB", "TB"];
    let v = value, unitIndex = -1;
    do { v /= 1024; unitIndex++; } while (v >= 1024 && unitIndex < units.length - 1);
    return v.toFixed(v >= 100 ? 1 : 2) + " " + units[unitIndex];
}

function escapeHtml(str) {
    const div = document.createElement("div");
    div.textContent = str;
    return div.innerHTML;
}

function flagCellHtml(countryCode) {
    if (!countryCode) return "";
    const code = countryCode.toLowerCase();
    return `<img class="flag-icon" src="https://flagcdn.com/${code}.svg" alt="${code}" loading="lazy" onerror="this.remove()" />`;
}

function monthLabel(ym) {
    const [y, m] = String(ym).split("-").map(Number);
    if (!y || !m) return String(ym);
    return new Date(y, m - 1, 1).toLocaleDateString("en", { month: "long", year: "numeric" });
}

function bucketStep(points) {
    return points.length > 1 ? points[1].timestamp - points[0].timestamp : 3600;
}

function formatPointTime(ts, stepSec) {
    const d = new Date(ts * 1000);
    const date = d.toLocaleDateString([], { weekday: "short", year: "numeric", month: "short", day: "numeric" });
    return stepSec >= 86400 ? date : `${date}, ${d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`;
}

function segmentedWire(container, onChange) {
    container.addEventListener("click", (e) => {
        const btn = e.target.closest("button");
        if (!btn) return;
        [...container.children].forEach((c) => c.classList.remove("active"));
        btn.classList.add("active");
        onChange(btn.dataset.value);
    });
}

function resetZoom() { state.zoomFrom = null; state.zoomTo = null; }

segmentedWire(els.metricSwitch, (v) => { state.metric = v; renderAll(); });
segmentedWire(els.rangeSwitch, (v) => {
    state.range = v;
    state.customMonth = "";
    els.monthPicker.value = "";
    resetZoom();
    loadAll();
});
segmentedWire(els.adapterSwitch, (v) => { state.adapter = v; resetZoom(); loadAll(); });
segmentedWire(els.appsSortSwitch, (v) => { state.appsSort = v; renderApps(); });
segmentedWire(els.chartModeSwitch, (v) => { state.chartMode = v; renderTimeline(); });
segmentedWire(els.countryViewSwitch, (v) => {
    countryView = v;
    els.countryList.style.display = v === "list" ? "" : "none";
    els.countryMapWrap.style.display = v === "map" ? "block" : "none";
    renderCountryMap();
});

els.monthPicker.addEventListener("change", () => {
    state.customMonth = els.monthPicker.value;
    resetZoom();
    [...els.rangeSwitch.children].forEach((b) =>
        b.classList.toggle("active", !state.customMonth && b.dataset.value === state.range));
    loadAll();
});

if (els.appsSortOrderBtn) {
    els.appsSortOrderBtn.addEventListener("click", () => {
        state.appsSortOrder = state.appsSortOrder === "desc" ? "asc" : "desc";
        els.appsSortOrderBtn.textContent = state.appsSortOrder === "desc" ? "↓" : "↑";
        renderApps();
    });
}

els.sessionToggle.addEventListener("change", () => { state.session = els.sessionToggle.checked; loadAll(); });
els.resetZoom.addEventListener("click", () => { resetZoom(); renderTimeline(); });

els.themeToggle.addEventListener("click", () => {
    const current = document.body.getAttribute("data-theme") === "light" ? "dark" : "light";
    document.body.setAttribute("data-theme", current);
    localStorage.setItem("num-theme", current);
    renderAll();
});

document.body.setAttribute("data-theme", localStorage.getItem("num-theme") || "dark");

els.exportJson.addEventListener("click", () => window.open(`/api/export?${query()}&format=json`, "_blank"));
els.exportCsv.addEventListener("click", () => window.open(`/api/export?${query()}&format=csv`, "_blank"));

els.modalClose.addEventListener("click", closeModal);
els.modalBackdrop.addEventListener("click", (e) => { if (e.target === els.modalBackdrop) closeModal(); });
function closeModal() { els.modalBackdrop.classList.remove("open"); }

async function fetchJson(url) {
    const res = await fetch(url);
    if (!res.ok) throw new Error(`Request failed: ${url}`);
    return res.json();
}

const monthValue = (m) => (typeof m === "string" ? m : (m && (m.month || m.value || m.key)) || "");

let loadSeq = 0;
async function loadAll() {
    const seq = ++loadSeq;
    const q = query();
    const needMonths = !state.availableMonths.length;
    try {
        const [apps, timeline, heatmap, adapters, countries, months] = await Promise.all([
            fetchJson(`/api/apps?${q}`),
            fetchJson(`/api/timeline?${q}`),
            fetchJson(`/api/heatmap?${q}`),
            fetchJson(`/api/adapters`),
            fetchJson(`/api/countries?${q}`).catch(() => []),
            needMonths ? fetchJson(`/api/months`).catch(() => []) : null
        ]);
        if (seq !== loadSeq) return;
        state.apps = apps;
        state.timeline = timeline;
        state.heatmap = heatmap;
        state.countries = Array.isArray(countries) ? countries : [];
        if (Array.isArray(months) && months.length) {
            state.availableMonths = months.map(monthValue).filter(Boolean).sort().reverse();
            renderMonths();
        }
        renderAdapterSwitch(adapters);
        renderAll();
    } catch (err) {
        console.error("loadAll failed:", err);
    }
}

function renderMonths() {
    const opts = state.availableMonths.map((m) => `<option value="${escapeHtml(m)}">${escapeHtml(monthLabel(m))}</option>`).join("");
    els.monthPicker.innerHTML = `<option value="">Select month…</option>${opts}`;
    els.monthPicker.value = state.customMonth;
    els.compareMonthA.innerHTML = opts;
    els.compareMonthB.innerHTML = opts;
    const [latest, prev] = state.availableMonths;
    els.compareMonthA.value = prev || latest || "";
    els.compareMonthB.value = latest || "";
}

function renderAdapterSwitch(adapters) {
    const known = new Set(adapters);
    if (state.adapter !== "all" && !known.has(state.adapter)) state.adapter = "all";
    els.adapterSwitch.innerHTML = "";
    for (const name of ["all", ...adapters]) {
        const btn = document.createElement("button");
        btn.dataset.value = name;
        btn.textContent = name === "all" ? "All Networks" : name;
        if (state.adapter === name) btn.classList.add("active");
        els.adapterSwitch.appendChild(btn);
    }
}

function renderAll() {
    renderKpis();
    renderApps();
    renderTimeline();
    renderHeatmap();
    renderCountries();
}

function renderKpis() {
    const timeline = state.timeline || [];
    let sent = 0, received = 0, active = 0;
    for (const a of state.apps || []) {
        const s = num(a.bytesSent), r = num(a.bytesReceived);
        sent += s;
        received += r;
        if (s + r > 0) active++;
    }
    const days = new Set();
    let tlTotal = 0, peak = null;
    for (const p of timeline) {
        const t = num(p.bytesSent) + num(p.bytesReceived);
        if (t <= 0) continue;
        tlTotal += t;
        days.add(dayKey(p.timestamp));
        if (!peak || t > peak.t) peak = { t, ts: p.timestamp };
    }
    els.kpiTotal.textContent = formatBytesDetailed(sent + received);
    els.kpiSplit.textContent = `↓ ${formatBytesDetailed(received)} / ↑ ${formatBytesDetailed(sent)}`;
    els.kpiDailyAvg.textContent = formatBytesDetailed(days.size ? tlTotal / days.size : 0);
    els.kpiPeak.textContent = peak ? formatBytesDetailed(peak.t) : "0 B";
    els.kpiPeakSub.textContent = peak ? formatPointTime(peak.ts, bucketStep(timeline)) : "—";
    els.kpiAppsCount.textContent = active;
    const top = sortedCountries()[0];
    els.kpiTopCountry.innerHTML = top ? `${flagCellHtml(top.countryCode)} ${escapeHtml(countryName(top.countryCode))}` : "—";
}

function appMetricValue(app) {
    const sent = num(app && app.bytesSent), received = num(app && app.bytesReceived);
    if (state.appsSort === "received") return received;
    if (state.appsSort === "sent") return sent;
    return sent + received;
}

function summaryLabel() {
    if (state.appsSort === "sent") return "Total Upload";
    if (state.appsSort === "received") return "Total Download";
    return "Total Traffic";
}

function renderAppsSummary() {
    if (!els.appsSummaryLabel || !els.appsSummaryValue) return;
    const apps = Array.isArray(state.apps) ? state.apps : [];
    els.appsSummaryLabel.textContent = summaryLabel();
    els.appsSummaryValue.textContent = formatBytesDetailed(apps.reduce((s, a) => s + appMetricValue(a), 0));
}

function renderApps() {
    renderAppsSummary();
    const apps = Array.isArray(state.apps) ? state.apps : [];
    const totalAll = apps.reduce((s, a) => s + num(a.bytesSent) + num(a.bytesReceived), 0) || 1;
    const dir = state.appsSortOrder === "desc" ? -1 : 1;
    const sorted = [...apps].sort((a, b) => state.appsSort === "name"
        ? dir * a.processName.localeCompare(b.processName)
        : dir * (appMetricValue(a) - appMetricValue(b)));

    els.appList.innerHTML = "";
    for (const app of sorted) {
        const row = document.createElement("div");
        row.className = "app-row";
        const value = appMetricValue(app);
        const display = state.metric === "percent" ? ((value / totalAll) * 100).toFixed(2) + "%" : formatBytes(value);
        const initials = app.processName.slice(0, 2).toUpperCase();
        const iconHtml = app.iconDataUrl
            ? `<img class="app-icon-img" src="${app.iconDataUrl}" alt=""
                    onerror="this.replaceWith(Object.assign(document.createElement('div'),
                        { className: 'app-icon', textContent: '${initials}' }))" />`
            : `<div class="app-icon">${initials}</div>`;
        row.innerHTML = `
            ${iconHtml}
            <div class="app-info">
                <div class="app-name">${escapeHtml(app.processName)}</div>
                <div class="app-path">${escapeHtml(app.executablePath || "Path unavailable")}</div>
            </div>
            <div class="app-usage">
                <div class="app-total">${display}</div>
                <div class="app-split">↑ ${formatBytes(app.bytesSent)} ↓ ${formatBytes(app.bytesReceived)}</div>
            </div>`;
        row.addEventListener("click", () => openAppModal(app));
        els.appList.appendChild(row);
    }
}

const countryTotal = (c) => num(c.totalBytes) || num(c.bytesReceived) + num(c.bytesSent);
const sortedCountries = () => state.countries.filter((c) => c && c.countryCode).sort((a, b) => countryTotal(b) - countryTotal(a));

let countryView = "list", mapCanvas = null, mapBubbles = [];

function toggleCountry(code) {
    const same = state.country && state.country.toLowerCase() === String(code).toLowerCase();
    state.country = same ? null : code;
    resetZoom();
    loadAll();
}

els.clearCountryFilter.addEventListener("click", () => { state.country = null; resetZoom(); loadAll(); });
els.countryList.addEventListener("click", (e) => {
    const row = e.target.closest(".country-row");
    if (row) toggleCountry(row.dataset.code);
});

function renderCountries() {
    const on = !!state.country;
    els.activeCountryFilter.style.display = on ? "inline-flex" : "none";
    els.activeCountryLabel.innerHTML = on ? `${flagCellHtml(state.country)} ${escapeHtml(countryName(state.country))}` : "";

    const top = sortedCountries().slice(0, 10);
    const max = top.length ? countryTotal(top[0]) || 1 : 1;
    els.countryList.innerHTML = top.map((c) => {
        const code = c.countryCode, total = countryTotal(c);
        const active = on && state.country.toLowerCase() === code.toLowerCase() ? " active" : "";
        return `<div class="country-row${active}" data-code="${escapeHtml(code)}">
            ${flagCellHtml(code)}
            <div class="country-name">${escapeHtml(countryName(code))}</div>
            <div class="country-bar"><div class="country-bar-fill" style="width:${((total / max) * 100).toFixed(1)}%"></div></div>
            <div class="country-usage">
                <div class="country-bytes">↓ ${formatBytes(num(c.bytesReceived))} / ↑ ${formatBytes(num(c.bytesSent))} · <strong>Total ${formatBytes(total)}</strong></div>
                <div class="country-conns">${num(c.connectionCount)} connections</div>
            </div>
        </div>`;
    }).join("") || `<div class="countries-hint">No country data</div>`;

    renderCountryMap();
}

function bubbleAt(e) {
    for (let i = mapBubbles.length - 1; i >= 0; i--) {
        const b = mapBubbles[i];
        if (Math.hypot(e.offsetX - b.x, e.offsetY - b.y) <= b.r + 2) return b;
    }
    return null;
}

function renderCountryMap() {
    if (countryView !== "map") return;
    if (!mapCanvas) {
        mapCanvas = document.createElement("canvas");
        mapCanvas.style.cssText = "width:100%;aspect-ratio:360/138;display:block";
        els.countryMapWrap.appendChild(mapCanvas);
        mapCanvas.addEventListener("click", (e) => { const b = bubbleAt(e); if (b) toggleCountry(b.code); });
        mapCanvas.addEventListener("mousemove", (e) => {
            const b = bubbleAt(e);
            mapCanvas.style.cursor = b ? "pointer" : "default";
            mapCanvas.title = b ? `${b.name}: ${formatBytesDetailed(b.total)}` : "";
        });
    }
    const { ctx, width, height } = fitCanvasToContainer(mapCanvas);
    const muted = getCss("--text-muted"), accent = getCss("--accent");
    const px = (lon) => ((lon + 180) / 360) * width;
    const py = (lat) => ((80 - clamp(lat, -58, 80)) / 138) * height;

    // Фонова сітка крапок: один beginPath() і один fill() на всю сітку.
    ctx.fillStyle = hexToRgba(muted.startsWith("#") ? muted : "#788296", 0.15);
    ctx.beginPath();
    for (let lon = -180; lon <= 180; lon += 6) {
        for (let lat = -56; lat <= 80; lat += 6) {
            const x = px(lon), y = py(lat);
            ctx.moveTo(x + 1, y);
            ctx.arc(x, y, 1, 0, Math.PI * 2);
        }
    }
    ctx.fill();

    // Силуети материків: всі полігони в одному контурі, одна заливка й одна обводка.
    ctx.beginPath();
    for (const poly of CONTINENTS) {
        ctx.moveTo(px(poly[0][0]), py(poly[0][1]));
        for (let i = 1; i < poly.length; i++) {
            ctx.lineTo(px(poly[i][0]), py(poly[i][1]));
        }
        ctx.closePath();
    }
    ctx.fillStyle = hexToRgba(accent, 0.08);
    ctx.fill();
    ctx.lineWidth = 1;
    ctx.lineJoin = "round";
    ctx.strokeStyle = hexToRgba(accent, 0.28);
    ctx.stroke();

    const list = sortedCountries();
    const max = list.length ? countryTotal(list[0]) || 1 : 1;
    const radiusMax = Math.min(26, width / 22);
    let missing = 0;
    mapBubbles = [];
    for (const c of list) {
        const pos = COUNTRY_COORDS[c.countryCode.toLowerCase()];
        if (!pos) { missing++; continue; }
        const total = countryTotal(c), k = Math.sqrt(total / max);
        mapBubbles.push({ code: c.countryCode, name: countryName(c.countryCode), total, k, x: px(pos[1]), y: py(pos[0]), r: 4 + k * radiusMax });
    }

    for (const b of mapBubbles) {
        const selected = state.country && state.country.toLowerCase() === b.code.toLowerCase();
        ctx.beginPath();
        ctx.arc(b.x, b.y, b.r, 0, Math.PI * 2);
        ctx.fillStyle = hexToRgba(accent, 0.2 + 0.6 * b.k);
        ctx.fill();
        ctx.lineWidth = selected ? 2.5 : 1;
        ctx.strokeStyle = selected ? (getCss("--text") || "#fff") : hexToRgba(accent, 0.9);
        ctx.stroke();
    }

    ctx.font = "10px sans-serif";
    ctx.textAlign = "center";
    ctx.fillStyle = getCss("--text") || muted;
    mapBubbles.slice(0, 6).forEach((b) => ctx.fillText(b.code.toUpperCase(), b.x, b.y - b.r - 3));
    ctx.textAlign = "left";
    if (missing) {
        ctx.fillStyle = muted;
        ctx.fillText(`+${missing} without coordinates`, 6, height - 6);
    }
}

let modalSeq = 0;
async function openAppModal(app) {
    const seq = ++modalSeq;
    const appTotal = num(app.bytesSent) + num(app.bytesReceived);
    const grandTotal = state.apps.reduce((s, a) => s + num(a.bytesSent) + num(a.bytesReceived), 0);

    els.modalTitle.textContent = app.processName;
    if (app.executablePath) {
        els.modalPath.textContent = app.executablePath;
        els.modalPath.classList.remove("disabled");
        els.modalPath.onclick = () => revealInExplorer(app.executablePath);
    } else {
        els.modalPath.textContent = "Executable path unavailable";
        els.modalPath.classList.add("disabled");
        els.modalPath.onclick = null;
    }

    els.modalAppTotal.textContent = formatBytesDetailed(appTotal);
    els.modalAppShare.textContent = `${(grandTotal ? (appTotal / grandTotal) * 100 : 0).toFixed(1)}%`;
    els.modalAppPeak.textContent = "…";
    els.modalAppCountries.innerHTML = "";
    els.connectionsBody.innerHTML = `<tr><td colspan="5">Loading…</td></tr>`;
    els.modalBackdrop.classList.add("open");
    fitCanvasToContainer(els.modalAppCanvas);

    const groupKey = (app.executablePath || app.processName).toLowerCase();
    const [connRes, detRes] = await Promise.allSettled([
        fetchJson(`/api/connections?group=${encodeURIComponent(groupKey)}`),
        fetchJson(`/api/app-details?name=${encodeURIComponent(app.processName)}&${query()}`)
    ]);
    if (seq !== modalSeq) return;

    if (connRes.status === "rejected") {
        console.error("Error loading connections:", connRes.reason);
        els.connectionsBody.innerHTML = `<tr><td colspan="5">Could not load live connections</td></tr>`;
    } else if (!connRes.value.length) {
        els.connectionsBody.innerHTML = `<tr><td colspan="5">No active connections right now</td></tr>`;
    } else {
        els.connectionsBody.innerHTML = connRes.value.map((c) => `
            <tr>
                <td>${escapeHtml(c.protocol)}</td>
                <td>${flagCellHtml(c.countryCode)}</td>
                <td>${escapeHtml(c.remoteAddress)}</td>
                <td>${c.remotePort || "-"}</td>
                <td>${c.localPort}</td>
            </tr>`).join("");
    }

    if (detRes.status === "rejected") console.error("Error loading app details:", detRes.reason);
    renderAppDetails(appTotal, grandTotal, detRes.status === "fulfilled" ? detRes.value || {} : {});
}

function renderAppDetails(appTotal, grandTotal, d) {
    const timeline = Array.isArray(d.timeline) ? d.timeline : [];
    const total = num(d.totalBytes) || appTotal;
    els.modalAppTotal.textContent = formatBytesDetailed(total);
    els.modalAppShare.textContent = `${(grandTotal ? (total / grandTotal) * 100 : 0).toFixed(1)}%`;

    const days = new Map();
    for (const p of timeline) {
        const k = dayKey(p.timestamp), e = days.get(k) || { ts: p.timestamp, v: 0 };
        e.v += num(p.bytesSent) + num(p.bytesReceived);
        days.set(k, e);
    }
    let peak = null;
    for (const e of days.values()) if (!peak || e.v > peak.v) peak = e;
    const pd = d.peakDay;
    if (peak) {
        const label = new Date(peak.ts * 1000).toLocaleDateString([], { month: "short", day: "numeric", year: "numeric" });
        els.modalAppPeak.textContent = `${label} · ${formatBytesDetailed(peak.v)}`;
    } else if (pd) {
        const label = typeof pd === "string" ? pd : pd.date || pd.day || "—";
        const bytes = typeof pd === "object" ? num(pd.bytes || pd.totalBytes) : 0;
        els.modalAppPeak.textContent = bytes ? `${label} · ${formatBytesDetailed(bytes)}` : label;
    } else {
        els.modalAppPeak.textContent = "—";
    }

    drawMiniChart(timeline);

    const rawCountries = Array.isArray(d.topCountries) ? d.topCountries : (Array.isArray(d.countries) ? d.countries : []);
    const countries = rawCountries
        .filter((c) => c && c.countryCode)
        .sort((a, b) => countryTotal(b) - countryTotal(a))
        .slice(0, 6);
    const maxCountry = countries.length ? countryTotal(countries[0]) || 1 : 1;
    els.modalAppCountries.innerHTML = countries.map((c) => {
        const total = countryTotal(c);
        const pct = ((total / maxCountry) * 100).toFixed(1);
        return `<div class="modal-country-row">
            ${flagCellHtml(c.countryCode)}
            <div class="country-name">${escapeHtml(countryName(c.countryCode))}</div>
            <div class="country-bar"><div class="country-bar-fill" style="width:${pct}%"></div></div>
            <div class="country-bytes">${formatBytes(total)}</div>
        </div>`;
    }).join("") || `<span class="countries-hint">No country data</span>`;
}

function drawMiniChart(points) {
    const { ctx, width, height } = fitCanvasToContainer(els.modalAppCanvas);
    if (points.length < 2) {
        ctx.fillStyle = getCss("--text-muted");
        ctx.font = "13px sans-serif";
        ctx.fillText("No timeline data", 12, 20);
        return;
    }
    const values = points.map((p) => num(p.bytesSent) + num(p.bytesReceived));
    let max = 1;
    for (const v of values) if (v > max) max = v;
    const pad = { left: 44, right: 8, top: 10, bottom: 22 };
    const plotW = width - pad.left - pad.right, plotH = height - pad.top - pad.bottom;
    const stepX = plotW / (points.length - 1);
    drawGridAndYLabels(ctx, width, height, pad, plotH, max);
    drawXAxisLabels(ctx, points, pad, plotW, height);
    drawLine(ctx, values, (i) => pad.left + stepX * i, (v) => pad.top + plotH - (v / max) * plotH, getCss("--accent"), pad, plotH);
}

async function revealInExplorer(executablePath) {
    try {
        await fetch("/api/reveal", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ executablePath })
        });
    } catch (err) {
        console.error("revealInExplorer failed:", err);
    }
}

let compareSeq = 0;
els.compareMonthsBtn.addEventListener("click", () => {
    const open = els.compareSection.style.display === "none";
    els.compareSection.style.display = open ? "" : "none";
    if (open) runCompare();
});
els.compareClose.addEventListener("click", () => { els.compareSection.style.display = "none"; });
els.compareMonthA.addEventListener("change", runCompare);
els.compareMonthB.addEventListener("change", runCompare);

async function runCompare() {
    const a = els.compareMonthA.value, b = els.compareMonthB.value;
    if (!a || !b) { els.compareResults.innerHTML = ""; return; }
    const seq = ++compareSeq;
    els.compareResults.innerHTML = `<p class="countries-hint">Loading…</p>`;
    try {
        const data = await fetchJson(`/api/compare?monthA=${encodeURIComponent(a)}&monthB=${encodeURIComponent(b)}`);
        if (seq === compareSeq) renderCompare(a, b, data || {});
    } catch (err) {
        console.error("compare failed:", err);
        if (seq === compareSeq) els.compareResults.innerHTML = `<p class="countries-hint">Could not load comparison</p>`;
    }
}

function normMonth(m) {
    m = m || {};
    const received = num(m.downloadBytes ?? m.bytesReceived ?? m.received);
    const sent = num(m.uploadBytes ?? m.bytesSent ?? m.sent);
    return { received, sent, total: num(m.totalBytes ?? m.total) || received + sent, apps: Array.isArray(m.apps) ? m.apps : [] };
}

function deltaInfo(a, b) {
    if (a === 0) return b === 0 ? { cls: "flat", text: "0%" } : { cls: "up", text: "new" };
    const d = ((b - a) / a) * 100;
    return { cls: d > 0.05 ? "up" : d < -0.05 ? "down" : "flat", text: `${d > 0 ? "+" : ""}${d.toFixed(1)}%` };
}

function renderCompare(keyA, keyB, data) {
    const A = normMonth(data.monthA || data.a), B = normMonth(data.monthB || data.b);
    const la = escapeHtml(monthLabel(keyA)), lb = escapeHtml(monthLabel(keyB));
    const stat = (label, x, y) => {
        const d = deltaInfo(x, y);
        return `<div class="compare-metric">
            <div class="compare-metric-label">${label}</div>
            <div class="compare-metric-values"><span>${la}: <strong>${formatBytesDetailed(x)}</strong></span> <span>${lb}: <strong>${formatBytesDetailed(y)}</strong></span></div>
            <span class="delta-badge ${d.cls}">${d.text}</span>
        </div>`;
    };

    let rows = "";
    if (Array.isArray(data.topApps)) {
        rows = data.topApps.map((item) => {
            const name = item.processName || item.name || "";
            const a = num(item.bytesA);
            const b = num(item.bytesB);
            const diff = item.deltaBytes !== undefined ? num(item.deltaBytes) : b - a;
            const d = typeof item.deltaPercent === "number" && Number.isFinite(item.deltaPercent)
                ? {
                    cls: item.deltaPercent > 0.05 ? "up" : item.deltaPercent < -0.05 ? "down" : "flat",
                    text: `${item.deltaPercent > 0 ? "+" : ""}${item.deltaPercent.toFixed(1)}%`
                }
                : deltaInfo(a, b);
            return `<tr>
                <td>${escapeHtml(name)}</td>
                <td>${formatBytes(a)}</td>
                <td>${formatBytes(b)}</td>
                <td>${diff < 0 ? "−" : "+"}${formatBytesDetailed(Math.abs(diff))}</td>
                <td><span class="delta-badge ${d.cls}">${d.text}</span></td>
            </tr>`;
        }).join("");
    } else {
        const apps = new Map();
        const collect = (list, key) => {
            for (const p of list) {
                const name = p.processName || p.name;
                if (!name) continue;
                const e = apps.get(name) || { a: 0, b: 0 };
                e[key] += num(p.totalBytes) || num(p.bytesSent) + num(p.bytesReceived);
                apps.set(name, e);
            }
        };
        collect(A.apps, "a");
        collect(B.apps, "b");

        rows = [...apps].sort((x, y) => y[1].a + y[1].b - (x[1].a + x[1].b)).slice(0, 10).map(([name, v]) => {
            const d = deltaInfo(v.a, v.b), diff = v.b - v.a;
            return `<tr>
                <td>${escapeHtml(name)}</td>
                <td>${formatBytes(v.a)}</td>
                <td>${formatBytes(v.b)}</td>
                <td>${diff < 0 ? "−" : "+"}${formatBytesDetailed(Math.abs(diff))}</td>
                <td><span class="delta-badge ${d.cls}">${d.text}</span></td>
            </tr>`;
        }).join("");
    }

    els.compareResults.innerHTML = `
        <div class="compare-summary">${stat("Total", A.total, B.total)}${stat("Download", A.received, B.received)}${stat("Upload", A.sent, B.sent)}</div>
        <table class="compare-table">
            <thead><tr><th>Application</th><th>${la}</th><th>${lb}</th><th>Difference</th><th>Change</th></tr></thead>
            <tbody>${rows || `<tr><td colspan="5">No application data</td></tr>`}</tbody>
        </table>`;
}

function visibleTimeline() {
    if (state.zoomFrom === null) return state.timeline;
    return state.timeline.filter((p) => p.timestamp >= state.zoomFrom && p.timestamp <= state.zoomTo);
}

function fitCanvasToContainer(canvas) {
    const rect = canvas.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;
    const cssWidth = Math.max(1, Math.round(rect.width));
    const cssHeight = Math.max(1, Math.round(rect.height));
    const targetW = Math.round(cssWidth * dpr);
    const targetH = Math.round(cssHeight * dpr);
    if (canvas.width !== targetW || canvas.height !== targetH) {
        canvas.width = targetW;
        canvas.height = targetH;
    }
    const ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, cssWidth, cssHeight);
    return { ctx, width: cssWidth, height: cssHeight };
}

const timelineLayout = {
    padding: { left: 44, right: 12, top: 16, bottom: 28 },
    plotW: 0,
    plotH: 0,
    pointCount: 0,
    dragStartX: null,
    dragCurrentX: null,
    dragging: false,
    mouse: { x: 0, y: 0 }
};

function seriesFor(points) {
    return {
        download: points.map((p) => p.bytesReceived),
        upload: points.map((p) => p.bytesSent),
        combined: points.map((p) => p.bytesSent + p.bytesReceived)
    };
}

function activeSeriesConfig() {
    switch (state.chartMode) {
        case "download": return [{ key: "download", color: getCss("--download") }];
        case "upload": return [{ key: "upload", color: getCss("--upload") }];
        case "combined": return [{ key: "combined", color: getCss("--accent") }];
        default: return [{ key: "download", color: getCss("--download") }, { key: "upload", color: getCss("--upload") }];
    }
}

function formatXLabel(ts, range) {
    const d = new Date(ts * 1000);
    if (range === "day" || range === "today") return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
    if (range === "year" || range === "this_year" || range === "all") return d.toLocaleDateString([], { month: "short", year: "2-digit" });
    return d.toLocaleDateString([], { month: "short", day: "numeric" });
}

function renderTimeline() {
    const canvas = els.timelineCanvas;
    const { ctx, width, height } = fitCanvasToContainer(canvas);
    wireTimelineEvents(canvas);

    const points = visibleTimeline();
    timelineLayout.pointCount = points.length;

    if (points.length === 0) {
        ctx.fillStyle = getCss("--text-muted");
        ctx.font = "13px sans-serif";
        ctx.fillText("No data for this range yet", 12, 20);
        timelineLayout.plotW = 0;
        hideTooltip();
        return;
    }

    const series = seriesFor(points);
    const configs = activeSeriesConfig();
    let maxValue = 1;
    for (const cfg of configs) for (const v of series[cfg.key]) if (v > maxValue) maxValue = v;

    const padding = timelineLayout.padding;
    const plotW = width - padding.left - padding.right;
    const plotH = height - padding.top - padding.bottom;
    timelineLayout.plotW = plotW;
    timelineLayout.plotH = plotH;

    drawGridAndYLabels(ctx, width, height, padding, plotH, maxValue);
    drawXAxisLabels(ctx, points, padding, plotW, height);

    const stepX = points.length > 1 ? plotW / (points.length - 1) : 0;
    const xFor = (i) => padding.left + stepX * i;
    const yFor = (v) => padding.top + plotH - (v / maxValue) * plotH;

    if (points.length === 1) {
        for (const cfg of configs) {
            ctx.fillStyle = cfg.color;
            ctx.beginPath();
            ctx.arc(xFor(0), yFor(series[cfg.key][0]), 3, 0, Math.PI * 2);
            ctx.fill();
        }
        hideTooltip();
        return;
    }

    for (const cfg of configs) drawLine(ctx, series[cfg.key], xFor, yFor, cfg.color, padding, plotH);
    drawZoomSelectionIfDragging(ctx, padding, plotH);

    const i = state.hoverIndex;
    if (i !== null && i < points.length && !timelineLayout.dragging) {
        const x = xFor(i);
        ctx.save();
        ctx.setLineDash([4, 4]);
        ctx.strokeStyle = getCss("--text-muted");
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(x, padding.top);
        ctx.lineTo(x, padding.top + plotH);
        ctx.stroke();
        ctx.restore();
        for (const cfg of configs) {
            ctx.beginPath();
            ctx.arc(x, yFor(series[cfg.key][i]), 4, 0, Math.PI * 2);
            ctx.fillStyle = cfg.color;
            ctx.fill();
            ctx.lineWidth = 2;
            ctx.strokeStyle = "rgba(255,255,255,0.9)";
            ctx.stroke();
        }
        showTooltip(points, i);
    } else {
        hideTooltip();
    }
}

function showTooltip(points, i) {
    const p = points[i], tip = els.chartTooltip;
    const dl = num(p.bytesReceived), ul = num(p.bytesSent);
    tip.innerHTML = `
        <div>${formatPointTime(p.timestamp, bucketStep(points))}</div>
        <div style="color:${getCss("--download") || "#22c55e"}">Download: ${formatBytesDetailed(dl)}</div>
        <div style="color:${getCss("--upload") || "#f59e0b"}">Upload: ${formatBytesDetailed(ul)}</div>
        <div><strong>Total: ${formatBytesDetailed(dl + ul)}</strong></div>`;
    tip.style.display = "block";
    tip.style.opacity = "1";
    tip.classList.add("visible");
    const wrap = tip.parentElement, { x, y } = timelineLayout.mouse;
    let left = x + 14, top = y + 14;
    if (left + tip.offsetWidth > wrap.clientWidth) {
        left = x - tip.offsetWidth - 14;
        if (left < 0) left = x + 14;
    }
    if (top + tip.offsetHeight > wrap.clientHeight) top = y - tip.offsetHeight - 14;
    tip.style.left = Math.max(0, left) + "px";
    tip.style.top = Math.max(0, top) + "px";
}

function hideTooltip() {
    els.chartTooltip.style.display = "none";
    els.chartTooltip.style.opacity = "0";
    els.chartTooltip.classList.remove("visible");
}

function drawLine(ctx, values, xFor, yFor, color, padding, plotH) {
    ctx.strokeStyle = color;
    ctx.lineWidth = 2;
    ctx.beginPath();
    values.forEach((v, i) => {
        const x = xFor(i), y = yFor(v);
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    });
    ctx.stroke();

    const gradient = ctx.createLinearGradient(0, padding.top, 0, padding.top + plotH);
    gradient.addColorStop(0, color + "33");
    gradient.addColorStop(1, color + "00");
    ctx.lineTo(xFor(values.length - 1), padding.top + plotH);
    ctx.lineTo(padding.left, padding.top + plotH);
    ctx.closePath();
    ctx.fillStyle = gradient;
    ctx.fill();
}

function drawGridAndYLabels(ctx, width, height, padding, plotH, maxValue) {
    ctx.strokeStyle = getCss("--border");
    ctx.lineWidth = 1;
    for (let i = 0; i <= 4; i++) {
        const y = padding.top + (plotH / 4) * i;
        ctx.beginPath();
        ctx.moveTo(padding.left, y);
        ctx.lineTo(width - padding.right, y);
        ctx.stroke();
        ctx.fillStyle = getCss("--text-muted");
        ctx.font = "11px sans-serif";
        ctx.fillText(formatBytes(maxValue - (maxValue / 4) * i), 2, y + 3);
    }
}

function drawXAxisLabels(ctx, points, padding, plotW, height) {
    const step = Math.max(1, Math.ceil(points.length / 6));
    const stepX = points.length > 1 ? plotW / (points.length - 1) : 0;
    ctx.fillStyle = getCss("--text-muted");
    ctx.font = "10.5px sans-serif";
    ctx.textAlign = "center";
    for (let i = 0; i < points.length; i += step) {
        ctx.fillText(formatXLabel(points[i].timestamp, rangeKey()), padding.left + stepX * i, height - 8);
    }
    ctx.textAlign = "left";
}

function indexAt(x) {
    const { plotW, pointCount, padding } = timelineLayout;
    if (plotW <= 0 || pointCount < 2 || x < padding.left - 6 || x > padding.left + plotW + 6) return null;
    return clamp(Math.round((x - padding.left) / (plotW / (pointCount - 1))), 0, pointCount - 1);
}

function zoomWithWheel(e) {
    e.preventDefault();
    const L = timelineLayout, all = state.timeline, cur = visibleTimeline();
    if (L.plotW <= 0 || all.length < 3 || cur.length < 2) return;

    const fullFrom = all[0].timestamp, fullTo = all[all.length - 1].timestamp, fullSpan = fullTo - fullFrom;
    const ratio = clamp((e.offsetX - L.padding.left) / L.plotW, 0, 1);
    const fi = ratio * (cur.length - 1), lo = Math.floor(fi), hi = Math.min(cur.length - 1, lo + 1);
    const focus = cur[lo].timestamp + (cur[hi].timestamp - cur[lo].timestamp) * (fi - lo);
    const span = cur[cur.length - 1].timestamp - cur[0].timestamp;
    const newSpan = span * (e.deltaY < 0 ? 0.75 : 1.25);

    if (newSpan >= fullSpan) {
        resetZoom();
    } else {
        if (newSpan < (fullSpan / (all.length - 1)) * 2) return;
        const from = clamp(focus - newSpan * ratio, fullFrom, fullTo - newSpan);
        state.zoomFrom = from;
        state.zoomTo = from + newSpan;
    }
    L.mouse = { x: e.offsetX, y: e.offsetY };
    state.hoverIndex = indexAt(e.offsetX);
    renderTimeline();
}

let timelineEventsWired = false;
let timelineRafPending = false;
function wireTimelineEvents(canvas) {
    if (timelineEventsWired) return;
    timelineEventsWired = true;
    const L = timelineLayout;

    canvas.addEventListener("mousedown", (e) => {
        L.dragStartX = e.offsetX;
        L.dragCurrentX = e.offsetX;
        L.dragging = true;
        state.hoverIndex = null;
    });

    canvas.addEventListener("mousemove", (e) => {
        if (L.dragging) {
            L.dragCurrentX = e.offsetX;
        } else {
            L.mouse = { x: e.offsetX, y: e.offsetY };
            state.hoverIndex = indexAt(e.offsetX);
        }
        if (!timelineRafPending) {
            timelineRafPending = true;
            requestAnimationFrame(() => {
                timelineRafPending = false;
                renderTimeline();
            });
        }
    });

    canvas.addEventListener("mouseleave", () => {
        state.hoverIndex = null;
        renderTimeline();
    });

    canvas.addEventListener("mouseup", (e) => {
        const startX = L.dragStartX;
        L.dragging = false;
        L.dragCurrentX = null;
        if (startX === null) return;
        L.dragStartX = null;

        const x0 = Math.min(startX, e.offsetX), x1 = Math.max(startX, e.offsetX);
        if (x1 - x0 < 8) { renderTimeline(); return; }

        const cur = visibleTimeline();
        if (cur.length < 2 || L.plotW <= 0 || L.pointCount < 2) return;
        const stepX = L.plotW / (L.pointCount - 1);
        const i0 = Math.max(0, Math.round((x0 - L.padding.left) / stepX));
        const i1 = Math.min(cur.length - 1, Math.round((x1 - L.padding.left) / stepX));
        if (i1 <= i0) { renderTimeline(); return; }

        state.zoomFrom = cur[i0].timestamp;
        state.zoomTo = cur[i1].timestamp;
        renderTimeline();
    });

    canvas.addEventListener("wheel", zoomWithWheel, { passive: false });
    canvas.addEventListener("dblclick", () => { resetZoom(); renderTimeline(); });

    window.addEventListener("mouseup", () => {
        if (L.dragging) {
            L.dragging = false;
            L.dragCurrentX = null;
            renderTimeline();
        }
        L.dragStartX = null;
    });
}

function drawZoomSelectionIfDragging(ctx, padding, plotH) {
    const L = timelineLayout;
    if (!L.dragging || L.dragStartX === null || L.dragCurrentX === null) return;
    const x0 = Math.min(L.dragStartX, L.dragCurrentX), x1 = Math.max(L.dragStartX, L.dragCurrentX);
    if (x1 - x0 < 2) return;
    ctx.fillStyle = hexToRgba(getCss("--accent"), 0.18);
    ctx.fillRect(x0, padding.top, x1 - x0, plotH);
    ctx.strokeStyle = hexToRgba(getCss("--accent"), 0.6);
    ctx.lineWidth = 1;
    ctx.strokeRect(x0, padding.top, x1 - x0, plotH);
}

function renderHeatmap() {
    const { ctx, width, height } = fitCanvasToContainer(els.heatmapCanvas);
    const cells = state.heatmap || [];
    const maxValue = Math.max(...cells.map((c) => c.totalBytes), 1);
    const days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    const leftPad = 34, topPad = 16, cols = 24, rows = 7;
    const cellW = (width - leftPad - 6) / cols;
    const cellH = (height - topPad - 6) / rows;

    ctx.font = "10px sans-serif";
    ctx.fillStyle = getCss("--text-muted");
    days.forEach((d, r) => ctx.fillText(d, 2, topPad + r * cellH + cellH / 2 + 3));

    ctx.textAlign = "center";
    for (let h = 0; h < cols; h += 3) ctx.fillText(`${h}:00`, leftPad + h * cellW + cellW / 2, topPad - 4);
    ctx.textAlign = "left";

    const map = new Map(cells.map((c) => [`${c.dayOfWeek}-${c.hour}`, c.totalBytes]));
    for (let r = 0; r < rows; r++) {
        for (let c = 0; c < cols; c++) {
            ctx.fillStyle = heatColor((map.get(`${r}-${c}`) || 0) / maxValue);
            ctx.fillRect(leftPad + c * cellW, topPad + r * cellH, cellW - 2, cellH - 2);
        }
    }
}

function heatColor(intensity) {
    if (intensity <= 0) return "rgba(120,130,150,0.12)";
    return hexToRgba(getCss("--accent"), 0.15 + intensity * 0.75);
}

function hexToRgba(hex, alpha) {
    const clean = hex.replace("#", "").trim();
    const n = parseInt(clean.length === 3 ? clean.split("").map((ch) => ch + ch).join("") : clean, 16);
    return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${alpha})`;
}

function getCss(varName) {
    return getComputedStyle(document.body).getPropertyValue(varName).trim();
}

window.addEventListener("resize", () => { renderTimeline(); renderHeatmap(); renderCountryMap(); });

loadAll();
setInterval(loadAll, 15000);