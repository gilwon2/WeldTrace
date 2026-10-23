// WeldTrace 차트. 데이터는 페이지의 <script type="application/json">에서 읽는다 (인라인 JS에 값을 섞지 않기 위해).
(function () {
    const C = {
        ink: "#1c2330", muted: "#7a8494", grid: "#eceff3",
        accent: "#2f6fdb", ok: "#1f8a5b", warn: "#d08a1c", ng: "#c8322f", base: "rgba(120,130,150,0.10)",
    };

    function read(id) {
        const el = document.getElementById(id);
        return el ? JSON.parse(el.textContent) : null;
    }

    function ready() {
        if (typeof Chart === "undefined") return false;
        Chart.defaults.font.family = getComputedStyle(document.body).fontFamily;
        Chart.defaults.color = C.muted;
        Chart.defaults.animation = false;
        return true;
    }

    const axis = (extra) => Object.assign({ grid: { color: C.grid }, ticks: { maxRotation: 0, autoSkipPadding: 18 } }, extra || {});

    // 기준 구간을 회색 배경으로 칠하는 플러그인
    const baselineShade = {
        id: "baselineShade",
        beforeDatasetsDraw(chart, _args, opts) {
            if (!opts || !opts.count) return;
            const x = chart.scales.x, area = chart.chartArea;
            const end = x.getPixelForValue(Math.min(opts.count, chart.data.labels.length) - 0.5);
            chart.ctx.save();
            chart.ctx.fillStyle = C.base;
            chart.ctx.fillRect(area.left, area.top, Math.max(0, end - area.left), area.bottom - area.top);
            chart.ctx.restore();
        },
    };

    window.WeldCharts = {
        daily(canvasId, dataId) {
            const d = read(dataId);
            const el = document.getElementById(canvasId);
            if (!d || !el || !ready()) return;
            new Chart(el, {
                data: {
                    labels: d.labels,
                    datasets: [
                        { type: "bar", label: "불량률(%)", data: d.ngRate, backgroundColor: "rgba(200,50,47,0.55)", yAxisID: "y", order: 2 },
                        { type: "line", label: "평균 출력 비율", data: d.ratio, borderColor: C.accent, backgroundColor: C.accent,
                          pointRadius: 0, borderWidth: 2, tension: 0.2, yAxisID: "y2", order: 1 },
                    ],
                },
                options: {
                    maintainAspectRatio: false,
                    interaction: { mode: "index", intersect: false },
                    plugins: { legend: { position: "bottom" } },
                    scales: {
                        x: axis(),
                        y: axis({ beginAtZero: true, title: { display: true, text: "불량률(%)" } }),
                        y2: axis({ position: "right", grid: { drawOnChartArea: false }, title: { display: true, text: "출력 비율" } }),
                    },
                },
            });
        },

        drift(ewmaId, cusumId, dataId) {
            const d = read(dataId);
            if (!d || !ready()) return;
            const ewmaEl = document.getElementById(ewmaId);
            if (ewmaEl) {
                new Chart(ewmaEl, {
                    type: "line",
                    data: {
                        labels: d.labels,
                        datasets: [
                            { label: "일평균 출력 비율", data: d.value, showLine: false, pointRadius: 2.5,
                              pointBackgroundColor: d.alarm.map(a => a ? C.ng : C.muted), borderColor: C.muted },
                            { label: "EWMA", data: d.ewma, borderColor: C.ink, borderWidth: 2.2, pointRadius: 0, tension: 0.15 },
                            { label: "관리 하한(LCL)", data: d.lcl, borderColor: C.ng, borderDash: [5, 4], borderWidth: 1.4, pointRadius: 0 },
                            { label: "관리 상한(UCL)", data: d.ucl, borderColor: C.ok, borderDash: [5, 4], borderWidth: 1.2, pointRadius: 0 },
                            { label: "직선 추세", data: d.trend, borderColor: C.accent, borderDash: [2, 3], borderWidth: 1.2, pointRadius: 0 },
                        ],
                    },
                    options: {
                        maintainAspectRatio: false,
                        interaction: { mode: "index", intersect: false },
                        plugins: { legend: { position: "bottom" }, baselineShade: { count: d.baselineDays } },
                        scales: { x: axis(), y: axis({ title: { display: true, text: "실측 / 공칭" } }) },
                    },
                    plugins: [baselineShade],
                });
            }
            const cusumEl = document.getElementById(cusumId);
            if (cusumEl) {
                new Chart(cusumEl, {
                    type: "line",
                    data: {
                        labels: d.labels,
                        datasets: [
                            { label: "하향 CUSUM", data: d.cusum, borderColor: C.ink, borderWidth: 2, pointRadius: 0, fill: false },
                            { label: "결정 구간 H", data: d.labels.map(() => d.h), borderColor: C.ng, borderDash: [5, 4], borderWidth: 1.4, pointRadius: 0 },
                        ],
                    },
                    options: {
                        maintainAspectRatio: false,
                        interaction: { mode: "index", intersect: false },
                        plugins: { legend: { position: "bottom" }, baselineShade: { count: d.baselineDays } },
                        scales: { x: axis(), y: axis({ beginAtZero: true }) },
                    },
                    plugins: [baselineShade],
                });
            }
        },

        steps(canvasId, dataId) {
            const d = read(dataId);
            const el = document.getElementById(canvasId);
            if (!d || !el || !ready()) return;
            new Chart(el, {
                data: {
                    labels: d.labels,
                    datasets: [
                        { type: "bar", label: "출력 비율", data: d.ratio,
                          backgroundColor: d.faulty.map(f => f ? C.ng : "rgba(47,111,219,0.55)"),
                          borderColor: d.deviation.map(v => v ? C.warn : "transparent"), borderWidth: 2 },
                        { type: "line", label: "판정선 0.95", data: d.labels.map(() => d.threshold),
                          borderColor: C.ng, borderDash: [5, 4], borderWidth: 1.4, pointRadius: 0 },
                    ],
                },
                options: {
                    maintainAspectRatio: false,
                    plugins: { legend: { position: "bottom" } },
                    scales: {
                        x: axis({ title: { display: true, text: "스텝" } }),
                        y: axis({ min: 0, suggestedMax: 1.05, title: { display: true, text: "실측 / 공칭" } }),
                    },
                },
            });
        },
    };
})();
