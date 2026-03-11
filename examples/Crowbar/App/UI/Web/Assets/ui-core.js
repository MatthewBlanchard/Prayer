(function () {
  function apiUrl(path) {
    var p = typeof path === 'string' ? path : '';
    return p.replace(/^\/+/, '');
  }

  function nowMs() {
    return (window.performance && typeof window.performance.now === 'function')
      ? window.performance.now()
      : Date.now();
  }

  window._uiPerf = window._uiPerf || (function () {
    var metrics = {};
    var gauges = {};
    var longTaskCount = 0;
    var longTaskTotalMs = 0;
    var memory = { used: null, total: null, limit: null, at: 0 };
    var history = [];
    var maxHistory = 180;
    var panel = null;
    var panelBody = null;
    var panelVisible = (window.location && /(?:\?|&)perf=1(?:&|$)/.test(window.location.search || ''));
    var refreshTimer = null;
    var memoryTimer = null;
    var hasLongTaskObserver = false;

    function ensurePanel() {
      if (panel || !document.body) return;
      panel = document.createElement('div');
      panel.id = 'perf-overlay';
      panel.style.position = 'fixed';
      panel.style.right = '10px';
      panel.style.bottom = '10px';
      panel.style.zIndex = '99999';
      panel.style.background = 'rgba(8,12,19,0.86)';
      panel.style.color = '#d7e8ff';
      panel.style.border = '1px solid rgba(120,170,255,0.42)';
      panel.style.borderRadius = '8px';
      panel.style.padding = '8px 10px';
      panel.style.minWidth = '230px';
      panel.style.maxWidth = '320px';
      panel.style.font = '12px/1.35 ui-monospace, SFMono-Regular, Menlo, monospace';
      panel.style.backdropFilter = 'blur(2px)';
      panel.style.whiteSpace = 'pre-wrap';
      panel.style.display = panelVisible ? 'block' : 'none';
      panelBody = document.createElement('div');
      panel.appendChild(panelBody);
      document.body.appendChild(panel);
    }

    function formatMetric(name) {
      var m = metrics[name];
      if (!m || !m.count) return null;
      var avg = m.total / m.count;
      return name + ': avg ' + avg.toFixed(2) + 'ms / max ' + m.max.toFixed(2) + 'ms / n=' + m.count;
    }

    function renderPanel() {
      if (!panelVisible) return;
      ensurePanel();
      if (!panelBody) return;
      var lines = [];
      lines.push('Perf Monitor');
      if (memory.used !== null) {
        lines.push('heap: ' + memory.used + ' MB / ' + memory.total + ' MB');
      } else {
        lines.push('heap: unavailable');
      }
      lines.push('long tasks: ' + longTaskCount + ' (' + longTaskTotalMs.toFixed(0) + 'ms)');
      var keyMetrics = ['renderGalaxyMapCanvases', 'drawGalaxyMapCanvas', 'refreshTickStatusBar', 'syncCurrentScript', 'htmxRequest'];
      keyMetrics.forEach(function (key) {
        var line = formatMetric(key);
        if (line) lines.push(line);
      });
      Object.keys(gauges).forEach(function (key) {
        lines.push(key + ': ' + gauges[key]);
      });
      panelBody.textContent = lines.join('\n');
    }

    function setPanelVisible(visible) {
      panelVisible = !!visible;
      ensurePanel();
      if (panel) panel.style.display = panelVisible ? 'block' : 'none';
      if (panelVisible) renderPanel();
    }

    function startObservers() {
      if (!hasLongTaskObserver && window.PerformanceObserver) {
        try {
          var observer = new PerformanceObserver(function (list) {
            list.getEntries().forEach(function (entry) {
              var dur = typeof entry.duration === 'number' ? entry.duration : 0;
              longTaskCount += 1;
              longTaskTotalMs += dur;
              history.push({ ts: Date.now(), type: 'longtask', duration: dur });
              if (history.length > maxHistory) history.shift();
            });
          });
          observer.observe({ entryTypes: ['longtask'] });
          hasLongTaskObserver = true;
        } catch (_) { }
      }

      if (!memoryTimer) {
        memoryTimer = setInterval(function () {
          var perfMem = window.performance && window.performance.memory;
          if (!perfMem) return;
          memory.used = Math.round((perfMem.usedJSHeapSize || 0) / (1024 * 1024));
          memory.total = Math.round((perfMem.totalJSHeapSize || 0) / (1024 * 1024));
          memory.limit = Math.round((perfMem.jsHeapSizeLimit || 0) / (1024 * 1024));
          memory.at = Date.now();
        }, 5000);
      }

      if (!refreshTimer) {
        refreshTimer = setInterval(renderPanel, 1000);
      }
    }

    return {
      mark: function (name, durationMs) {
        if (typeof durationMs !== 'number' || !isFinite(durationMs) || durationMs < 0) return;
        var metric = metrics[name] || (metrics[name] = { count: 0, total: 0, max: 0, last: 0 });
        metric.count += 1;
        metric.total += durationMs;
        metric.last = durationMs;
        if (durationMs > metric.max) metric.max = durationMs;
      },
      begin: function () { return nowMs(); },
      end: function (name, startedAt) {
        if (typeof startedAt !== 'number' || !isFinite(startedAt)) return;
        this.mark(name, Math.max(0, nowMs() - startedAt));
      },
      setGauge: function (name, value) {
        gauges[name] = value;
      },
      snapshot: function () {
        return {
          metrics: metrics,
          gauges: gauges,
          memory: memory,
          longTaskCount: longTaskCount,
          longTaskTotalMs: longTaskTotalMs,
          history: history.slice(-50)
        };
      },
      start: function () { startObservers(); },
      show: function () { setPanelVisible(true); },
      hide: function () { setPanelVisible(false); }
    };
  }());

  window.showPerfOverlay = function () { window._uiPerf.show(); };
  window.hidePerfOverlay = function () { window._uiPerf.hide(); };
  window.getUiPerfSnapshot = function () { return window._uiPerf.snapshot(); };
  window._uiPerfStart = window._uiPerfStart || false;
  if (!window._uiPerfStart) {
    window._uiPerfStart = true;
    window._uiPerf.start();
    window._uiPerf.setGauge('perf_overlay', 'toggle via showPerfOverlay()');
    window._uiPerf.mark('startup', 0);
  }

  function buildNameRegex(names, lineStartOnly) {
    if (!Array.isArray(names) || names.length === 0) return null;
    var escaped = names
      .filter(function (n) { return typeof n === 'string' && n.trim().length > 0; })
      .map(function (n) { return n.trim().replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); })
      .sort(function (a, b) { return b.length - a.length; });
    if (escaped.length === 0) return null;
    var core = '(?:' + escaped.join('|') + ')';
    var pattern = lineStartOnly ? core + '\\b' : '\\b' + core + '\\b';
    return new RegExp(pattern, 'i');
  }

  window.buildNameRegex = buildNameRegex;
  window._galaxyMapStates = window._galaxyMapStates || new WeakMap();
  // driftT lives outside the WeakMap so it survives HTMX DOM replacement of canvas elements.
  window._starDriftT = window._starDriftT || { system: 0, galaxy: 0 };
  // Star parallax drift loop — runs continuously, advances driftT and redraws visible canvases.
  if (!window._starDriftRafRunning) {
    window._starDriftRafRunning = true;
    var _starDriftPrev = null;
    var _starDriftFrame = function (ts) {
      window.requestAnimationFrame(_starDriftFrame);
      var dt = _starDriftPrev === null ? 0 : Math.min((ts - _starDriftPrev) / 1000, 0.1);
      _starDriftPrev = ts;
      if (dt === 0) return;
      window._starDriftT.system += dt;
      window._starDriftT.galaxy += dt;
      document.querySelectorAll('.galaxy-map-canvas').forEach(function (canvas) {
        if (canvas.offsetParent === null) return; // hidden
        var state = window._galaxyMapStates.get(canvas);
        if (!state || !state.layout || !state.layout.stars || state.layout.stars.length === 0) return;
        state.driftT = window._starDriftT[state.mode] || 0;
        window.drawGalaxyMapCanvas(canvas);
      });
    };
    window.requestAnimationFrame(_starDriftFrame);
  }
  window._scriptCommandRegex = null;
  window._scriptKeywordRegex = buildNameRegex(['repeat', 'until', 'if', 'halt'], true);
  window._scriptSystemRegex = null;
  window._scriptPoiRegex = null;
  window._scriptSymbolRegex = null;
  window._haltHighlightPending = false;
  window._haltHighlightPendingUntil = 0;
  window._statePaneUiState = window._statePaneUiState || {};
  window._mapSubtabSelection = window._mapSubtabSelection || 'system';
  window._galaxyMapViewState = window._galaxyMapViewState || { panX: 0, panY: 0, hasUserPan: false, zoom: 4.5, hasUserZoom: false };
  window._tickBarState = window._tickBarState || { tick: null, observedAtMs: 0, lastPostUtcMs: null, renderPct: null, lastFrameMs: 0 };
