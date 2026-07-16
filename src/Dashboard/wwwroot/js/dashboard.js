const state = {
    metric: "bytes",        // bytes | percent
    range: "day",
    session: false,
    adapter: "all",          // "all" or a specific adapter name from /api/adapters
    appsSort: "total",       // total | received | sent | name
    appsSortOrder: "desc",
    chartMode: "overlay",    // overlay | download | upload | combined
    apps: [],
    timeline: [],
    heatmap: [],
    zoomFrom: null,
    zoomTo: null
};

const els = {
    metricSwitch: document.getElementById("metricSwitch"),
    rangeSwitch: document.getElementById("rangeSwitch"),
    adapterSwitch: document.getElementById("adapterSwitch"),
    sessionToggle: document.getElementById("sessionToggle"),
    appsSortSwitch: document.getElementById("appsSortSwitch"),
    appsSortOrderBtn: document.getElementById("sort-order-btn"),
    chartModeSwitch: document.getElementById("chartModeSwitch"),
    appList: document.getElementById("appList"),
    timelineCanvas: document.getElementById("timelineCanvas"),
    heatmapCanvas: document.getElementById("heatmapCanvas"),
    resetZoom: document.getElementById("resetZoom"),
    themeToggle: document.getElementById("themeToggle"),
    exportJson: document.getElementById("exportJson"),
    exportCsv: document.getElementById("exportCsv"),
    modalBackdrop: document.getElementById("modalBackdrop"),
    modalTitle: document.getElementById("modalTitle"),
    modalPath: document.getElementById("modalPath"),
    modalClose: document.getElementById("modalClose"),
    connectionsBody: document.getElementById("connectionsBody")
};

function formatBytes(bytes) {
    if (bytes < 1024) return bytes + " B";
    const units = ["KB", "MB", "GB", "TB"];
    let value = bytes;
    let unitIndex = -1;
    do {
        value /= 1024;
        unitIndex++;
    } while (value >= 1024 && unitIndex < units.length - 1);
    return value.toFixed(value >= 10 ? 0 : 1) + " " + units[unitIndex];
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

segmentedWire(els.metricSwitch, (v) => { state.metric = v; renderAll(); });
segmentedWire(els.rangeSwitch, (v) => { state.range = v; state.zoomFrom = null; state.zoomTo = null; loadAll(); });
segmentedWire(els.adapterSwitch, (v) => { state.adapter = v; state.zoomFrom = null; state.zoomTo = null; loadAll(); });
segmentedWire(els.appsSortSwitch, (v) => { state.appsSort = v; renderApps(); });
segmentedWire(els.chartModeSwitch, (v) => { state.chartMode = v; renderTimeline(); });

if (els.appsSortOrderBtn) {
    els.appsSortOrderBtn.addEventListener("click", () => {
        state.appsSortOrder = state.appsSortOrder === "desc" ? "asc" : "desc";
        els.appsSortOrderBtn.textContent = state.appsSortOrder === "desc" ? "↓" : "↑";
        renderApps();
    });
}

els.sessionToggle.addEventListener("change", () => { state.session = els.sessionToggle.checked; loadAll(); });
els.resetZoom.addEventListener("click", () => { state.zoomFrom = null; state.zoomTo = null; renderTimeline(); });

els.themeToggle.addEventListener("click", () => {
    const current = document.body.getAttribute("data-theme") === "light" ? "dark" : "light";
    document.body.setAttribute("data-theme", current);
    localStorage.setItem("num-theme", current);
});

(function initTheme() {
    const saved = localStorage.getItem("num-theme") || "dark";
    document.body.setAttribute("data-theme", saved);
})();

els.exportJson.addEventListener("click", () => window.open(`/api/export?range=${state.range}&format=json`, "_blank"));
els.exportCsv.addEventListener("click", () => window.open(`/api/export?range=${state.range}&format=csv`, "_blank"));

els.modalClose.addEventListener("click", closeModal);
els.modalBackdrop.addEventListener("click", (e) => { if (e.target === els.modalBackdrop) closeModal(); });

function flagCellHtml(countryCode) {
    if (!countryCode) return "";
    const code = countryCode.toLowerCase();
    return `<img class="flag-icon" src="https://flagcdn.com/${code}.svg" alt="${code}" loading="lazy" onerror="this.remove()" />`;
}

function closeModal() { els.modalBackdrop.classList.remove("open"); }

async function fetchJson(url) {
    const res = await fetch(url);
    if (!res.ok) throw new Error(`Request failed: ${url}`);
    return res.json();
}

async function loadAll() {
    const q = `range=${state.range}&session=${state.session}&adapter=${encodeURIComponent(state.adapter)}`;
    try {
        const [apps, timeline, heatmap, adapters] = await Promise.all([
            fetchJson(`/api/apps?${q}`),
            fetchJson(`/api/timeline?${q}`),
            fetchJson(`/api/heatmap?${q}`),
            fetchJson(`/api/adapters`)
        ]);
        state.apps = apps;
        state.timeline = timeline;
        state.heatmap = heatmap;
        renderAdapterSwitch(adapters);
        renderAll();
    } catch (err) {
        console.error("loadAll failed:", err);
    }
}

function renderAdapterSwitch(adapters) {
    const known = new Set(adapters);
    if (state.adapter !== "all" && !known.has(state.adapter)) {
        state.adapter = "all";
    }

    els.adapterSwitch.innerHTML = "";

    const allBtn = document.createElement("button");
    allBtn.dataset.value = "all";
    allBtn.textContent = "All Networks";
    if (state.adapter === "all") allBtn.classList.add("active");
    els.adapterSwitch.appendChild(allBtn);

    for (const name of adapters) {
        const btn = document.createElement("button");
        btn.dataset.value = name;
        btn.textContent = name;
        if (state.adapter === name) btn.classList.add("active");
        els.adapterSwitch.appendChild(btn);
    }
}

function renderAll() {
    renderApps();
    renderTimeline();
    renderHeatmap();
}

function appMetricValue(app) {
    if (state.appsSort === "received") return app.bytesReceived;
    if (state.appsSort === "sent") return app.bytesSent;
    return app.bytesSent + app.bytesReceived;
}

function renderApps() {
    const totalAll = state.apps.reduce((sum, a) => sum + a.bytesSent + a.bytesReceived, 0) || 1;
    let sorted = [...state.apps];

    // --- ПОВНІСТЮ ЗАМІНИТИ СТАРИЙ БЛОК СОРТУВАННЯ НА ЦЕЙ: ---
    const isDesc = state.appsSortOrder === "desc";
    sorted.sort((a, b) => {
        if (state.appsSort === "name") {
            return isDesc 
                ? b.processName.localeCompare(a.processName) 
                : a.processName.localeCompare(b.processName);
        }
        
        const valA = state.appsSort === "sent" ? a.bytesSent 
                   : state.appsSort === "received" ? a.bytesReceived 
                   : (a.bytesSent + a.bytesReceived);
                   
        const valB = state.appsSort === "sent" ? b.bytesSent 
                   : state.appsSort === "received" ? b.bytesReceived 
                   : (b.bytesSent + b.bytesReceived);
                   
        return isDesc ? valB - valA : valA - valB;
    });

    els.appList.innerHTML = "";
    for (const app of sorted) {
        const row = document.createElement("div");
        row.className = "app-row";

        const value = appMetricValue(app);
        const display = state.metric === "percent"
            ? ((value / totalAll) * 100).toFixed(2) + "%"
            : formatBytes(value);

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
                <div class="app-split">&#8593; ${formatBytes(app.bytesSent)} &#8595; ${formatBytes(app.bytesReceived)}</div>
            </div>
        `;
        row.addEventListener("click", () => openAppModal(app));
        els.appList.appendChild(row);
    }
}

function escapeHtml(str) {
    const div = document.createElement("div");
    div.textContent = str;
    return div.innerHTML;
}

async function openAppModal(app) {
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

    // 1. Спочатку показуємо "Loading..." і відкриваємо модалку.
    // Змінюємо colspan на 5, оскільки додалася колонка "Country"
    els.connectionsBody.innerHTML = `<tr><td colspan="5">Loading…</td></tr>`;
    els.modalBackdrop.classList.add("open");

    try {
        const groupKey = (app.executablePath || app.processName).toLowerCase();
        
        // 2. Робимо запит до нашого швидкого C# сервера
        const connections = await fetchJson(`/api/connections?group=${encodeURIComponent(groupKey)}`);
        
        if (connections.length === 0) {
            els.connectionsBody.innerHTML = `<tr><td colspan="5">No active connections right now</td></tr>`;
            return;
        }

        // 3. Рендеримо таблицю ОДИН РАЗ і з усіма необхідними даними
        els.connectionsBody.innerHTML = connections.map(c => `
            <tr>
                <td>${c.protocol}</td>
                <td>${flagCellHtml(c.countryCode)}</td>
                <td>${escapeHtml(c.remoteAddress)}</td>
                <td>${c.remotePort || "-"}</td>
                <td>${c.localPort}</td>
            </tr>
        `).join("");

    } catch (error) {
        console.error("Error loading connections:", error);
        els.connectionsBody.innerHTML = `<tr><td colspan="5">Could not load live connections</td></tr>`;
    }
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

function visibleTimeline() {
    if (state.zoomFrom === null) return state.timeline;
    return state.timeline.filter(p => p.timestamp >= state.zoomFrom && p.timestamp <= state.zoomTo);
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

// Drag-to-zoom state lives in one object alongside layout metrics, and
// `dragging` is tracked explicitly (rather than inferred from a nullable
// start position) so a live mousemove redraw can show the selection box
// without fighting a "reset on every render" rule.
const timelineLayout = {
    padding: { left: 44, right: 12, top: 16, bottom: 28 },
    plotW: 0,
    plotH: 0,
    pointCount: 0,
    dragStartX: null,
    dragCurrentX: null,
    dragging: false
};

function seriesFor(points) {
    return {
        download: points.map(p => p.bytesReceived),
        upload: points.map(p => p.bytesSent),
        combined: points.map(p => p.bytesSent + p.bytesReceived)
    };
}

function activeSeriesConfig() {
    switch (state.chartMode) {
        case "download":
            return [{ key: "download", color: getCss("--download") }];
        case "upload":
            return [{ key: "upload", color: getCss("--upload") }];
        case "combined":
            return [{ key: "combined", color: getCss("--accent") }];
        case "overlay":
        default:
            return [
                { key: "download", color: getCss("--download") },
                { key: "upload", color: getCss("--upload") }
            ];
    }
}

function formatXLabel(unixSeconds, range) {
    const date = new Date(unixSeconds * 1000);
    if (range === "day") {
        return date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
    }
    if (range === "week" || range === "month") {
        return date.toLocaleDateString([], { month: "short", day: "numeric" });
    }
    return date.toLocaleDateString([], { month: "short", year: "2-digit" });
}

function renderTimeline() {
    const canvas = els.timelineCanvas;
    const { ctx, width, height } = fitCanvasToContainer(canvas);

    const points = visibleTimeline();

    if (points.length === 0) {
        ctx.fillStyle = getCss("--text-muted");
        ctx.font = "13px sans-serif";
        ctx.fillText("No data for this range yet", 12, 20);
        timelineLayout.plotW = 0;
        timelineLayout.pointCount = 0;
        wireZoomDrag(canvas);
        return;
    }

    const series = seriesFor(points);
    const activeConfigs = activeSeriesConfig();
    const maxValue = Math.max(1, ...activeConfigs.flatMap(cfg => series[cfg.key]));

    const padding = timelineLayout.padding;
    const plotW = width - padding.left - padding.right;
    const plotH = height - padding.top - padding.bottom;

    timelineLayout.plotW = plotW;
    timelineLayout.plotH = plotH;
    timelineLayout.pointCount = points.length;

    drawGridAndYLabels(ctx, width, height, padding, plotH, maxValue);
    drawXAxisLabels(ctx, points, padding, plotW, height);

    const stepX = points.length > 1 ? plotW / (points.length - 1) : 0;
    const xFor = (i) => padding.left + stepX * i;
    const yFor = (value) => padding.top + plotH - (value / maxValue) * plotH;

    if (points.length === 1) {
        for (const cfg of activeConfigs) {
            const y = yFor(series[cfg.key][0]);
            ctx.fillStyle = cfg.color;
            ctx.beginPath();
            ctx.arc(xFor(0), y, 3, 0, Math.PI * 2);
            ctx.fill();
        }
        wireZoomDrag(canvas);
        return;
    }

    for (const cfg of activeConfigs) {
        drawLine(ctx, series[cfg.key], xFor, yFor, cfg.color, padding, plotH);
    }

    drawZoomSelectionIfDragging(ctx, padding, plotH);

    wireZoomDrag(canvas);
}

function drawLine(ctx, values, xFor, yFor, color, padding, plotH) {
    ctx.strokeStyle = color;
    ctx.lineWidth = 2;
    ctx.beginPath();
    values.forEach((v, i) => {
        const x = xFor(i);
        const y = yFor(v);
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

        const label = formatBytes(maxValue - (maxValue / 4) * i);
        ctx.fillStyle = getCss("--text-muted");
        ctx.font = "11px sans-serif";
        ctx.fillText(label, 2, y + 3);
    }
}

function drawXAxisLabels(ctx, points, padding, plotW, height) {
    const maxLabels = 6;
    const step = Math.max(1, Math.ceil(points.length / maxLabels));
    const stepX = points.length > 1 ? plotW / (points.length - 1) : 0;

    ctx.fillStyle = getCss("--text-muted");
    ctx.font = "10.5px sans-serif";
    ctx.textAlign = "center";

    for (let i = 0; i < points.length; i += step) {
        const x = padding.left + stepX * i;
        const label = formatXLabel(points[i].timestamp, state.range);
        ctx.fillText(label, x, height - 8);
    }

    ctx.textAlign = "left";
}

let zoomDragHandlerAttached = false;

function wireZoomDrag(canvas) {
    // Listeners are attached once (the canvas element is never recreated);
    // the live drag state and layout are always read fresh from
    // `timelineLayout` on each event, so there's nothing stale to track here.
    if (zoomDragHandlerAttached) return;
    zoomDragHandlerAttached = true;

    canvas.addEventListener("mousedown", (e) => {
        timelineLayout.dragStartX = e.offsetX;
        timelineLayout.dragCurrentX = e.offsetX;
        timelineLayout.dragging = true;
    });

    canvas.addEventListener("mousemove", (e) => {
        if (!timelineLayout.dragging) return;
        timelineLayout.dragCurrentX = e.offsetX;
        renderTimeline();
    });

    canvas.addEventListener("mouseup", (e) => {
        const dragStartX = timelineLayout.dragStartX;
        timelineLayout.dragging = false;
        timelineLayout.dragCurrentX = null;
        if (dragStartX === null) return;
        timelineLayout.dragStartX = null;

        const endX = e.offsetX;
        const x0 = Math.min(dragStartX, endX);
        const x1 = Math.max(dragStartX, endX);
        if (x1 - x0 < 8) { renderTimeline(); return; }

        const currentPoints = visibleTimeline();
        if (currentPoints.length < 2) return;

        const { plotW, pointCount, padding } = timelineLayout;
        if (plotW <= 0 || pointCount < 2) return;
        const stepX = plotW / (pointCount - 1);

        const i0 = Math.max(0, Math.round((x0 - padding.left) / stepX));
        const i1 = Math.min(currentPoints.length - 1, Math.round((x1 - padding.left) / stepX));
        if (i1 <= i0) { renderTimeline(); return; }

        state.zoomFrom = currentPoints[i0].timestamp;
        state.zoomTo = currentPoints[i1].timestamp;
        renderTimeline();
    });

    // If the user starts a drag and releases the mouse button outside the
    // canvas, a "stuck" start point could otherwise block the next click.
    window.addEventListener("mouseup", () => {
        if (timelineLayout.dragging) {
            timelineLayout.dragging = false;
            timelineLayout.dragCurrentX = null;
            renderTimeline();
        }
        timelineLayout.dragStartX = null;
    });
}

function drawZoomSelectionIfDragging(ctx, padding, plotH) {
    if (!timelineLayout.dragging || timelineLayout.dragStartX === null || timelineLayout.dragCurrentX === null) {
        return;
    }

    const x0 = Math.min(timelineLayout.dragStartX, timelineLayout.dragCurrentX);
    const x1 = Math.max(timelineLayout.dragStartX, timelineLayout.dragCurrentX);
    if (x1 - x0 < 2) return;

    ctx.fillStyle = hexToRgba(getCss("--accent"), 0.18);
    ctx.fillRect(x0, padding.top, x1 - x0, plotH);
    ctx.strokeStyle = hexToRgba(getCss("--accent"), 0.6);
    ctx.lineWidth = 1;
    ctx.strokeRect(x0, padding.top, x1 - x0, plotH);
}

function renderHeatmap() {
    const canvas = els.heatmapCanvas;
    const { ctx, width, height } = fitCanvasToContainer(canvas);

    const cells = state.heatmap || [];
    const maxValue = Math.max(...cells.map(c => c.totalBytes), 1);
    // Index 0 is Monday: the backend already remaps SQLite's Sunday-first
    // day-of-week so the week always starts on Monday here.
    const days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    const leftPad = 34;
    const topPad = 16;
    const cols = 24;
    const rows = 7;
    const cellW = (width - leftPad - 6) / cols;
    const cellH = (height - topPad - 6) / rows;

    ctx.font = "10px sans-serif";
    ctx.fillStyle = getCss("--text-muted");
    days.forEach((d, r) => ctx.fillText(d, 2, topPad + r * cellH + cellH / 2 + 3));

    ctx.textAlign = "center";
    for (let h = 0; h < cols; h += 3) {
        const x = leftPad + h * cellW + cellW / 2;
        ctx.fillText(`${h}:00`, x, topPad - 4);
    }
    ctx.textAlign = "left";

    const map = new Map(cells.map(c => [`${c.dayOfWeek}-${c.hour}`, c.totalBytes]));

    for (let r = 0; r < rows; r++) {
        for (let c = 0; c < cols; c++) {
            const value = map.get(`${r}-${c}`) || 0;
            const intensity = value / maxValue;
            ctx.fillStyle = heatColor(intensity);
            ctx.fillRect(leftPad + c * cellW, topPad + r * cellH, cellW - 2, cellH - 2);
        }
    }
}

function heatColor(intensity) {
    if (intensity <= 0) return "rgba(120,130,150,0.12)";
    const accent = getCss("--accent");
    const alpha = 0.15 + intensity * 0.75;
    return hexToRgba(accent, alpha);
}

function hexToRgba(hex, alpha) {
    const clean = hex.replace("#", "").trim();
    const bigint = parseInt(clean.length === 3
        ? clean.split("").map(ch => ch + ch).join("")
        : clean, 16);
    const r = (bigint >> 16) & 255;
    const g = (bigint >> 8) & 255;
    const b = bigint & 255;
    return `rgba(${r},${g},${b},${alpha})`;
}

function getCss(varName) {
    return getComputedStyle(document.body).getPropertyValue(varName).trim();
}

window.addEventListener("resize", () => { renderTimeline(); renderHeatmap(); });

loadAll();
setInterval(loadAll, 15000);
