// Chart.js glue for the LineChart and WaterfallChart components. Colours come from CSS variables so charts follow the page theme.
(function () {
    const palette = ["--series-1", "--series-2", "--series-3", "--series-4"];

    function css(name) {
        return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    }

    function datasets(spec) {
        return spec.series.map((s, i) => {
            const colour = css(palette[i % palette.length]);
            return {
                label: s.name,
                data: s.points,
                showLine: !s.markers,
                spanGaps: false,
                borderColor: colour,
                backgroundColor: colour,
                borderWidth: s.markers ? 0 : 2,
                pointRadius: s.markers ? 3.5 : 0,
                pointHoverRadius: s.markers ? 5 : 3,
            };
        });
    }

    function axis(title) {
        return {
            type: "linear",
            title: { display: title.length > 0, text: title, color: css("--text-muted") },
            ticks: { color: css("--text-muted") },
            grid: { color: css("--grid") },
        };
    }

    window.pricerCharts = {
        render(canvas, spec) {
            const existing = Chart.getChart(canvas);
            if (existing) {
                existing.data.datasets = datasets(spec);
                existing.options.plugins.title.text = spec.title;
                existing.options.plugins.legend.display = spec.series.length > 1;
                existing.update("none");
                return;
            }
            new Chart(canvas, {
                type: "scatter",
                data: { datasets: datasets(spec) },
                options: {
                    animation: false,
                    responsive: true,
                    maintainAspectRatio: false,
                    interaction: { mode: "nearest", intersect: false },
                    plugins: {
                        title: { display: spec.title.length > 0, text: spec.title, color: css("--text") },
                        legend: { display: spec.series.length > 1, labels: { color: css("--text") } },
                    },
                    scales: { x: axis(spec.xLabel), y: axis(spec.yLabel) },
                },
            });
        },
        // Floating bars: each factor spans from the cumulated PnL before it to the one after it; the total starts at zero.
        waterfall(canvas, spec) {
            const rise = css("--series-1"), fall = css("--error"), total = css("--text-muted");
            const data = spec.bars.map(b => [b.from, b.to]);
            const colours = spec.bars.map(b => b.total ? total : (b.to >= b.from ? rise : fall));
            Chart.getChart(canvas)?.destroy();
            new Chart(canvas, {
                type: "bar",
                data: { labels: spec.bars.map(b => b.label), datasets: [{ data, backgroundColor: colours, borderWidth: 0 }] },
                options: {
                    animation: false,
                    responsive: true,
                    maintainAspectRatio: false,
                    plugins: {
                        title: { display: spec.title.length > 0, text: spec.title, color: css("--text") },
                        legend: { display: false },
                        tooltip: {
                            callbacks: {
                                label: item => {
                                    const b = spec.bars[item.dataIndex];
                                    return (b.total ? "Total " : "") + (b.to - b.from).toLocaleString("en-US", { maximumFractionDigits: 2 });
                                },
                            },
                        },
                    },
                    scales: {
                        x: { ticks: { color: css("--text-muted") }, grid: { display: false } },
                        y: {
                            title: { display: spec.yLabel.length > 0, text: spec.yLabel, color: css("--text-muted") },
                            ticks: { color: css("--text-muted") },
                            grid: { color: css("--grid") },
                        },
                    },
                },
            });
        },
        dispose(canvas) {
            Chart.getChart(canvas)?.destroy();
        },
    };
})();
