(function () {
  function apiUrl(path) {
    var p = typeof path === 'string' ? path : '';
    return p.replace(/^\/+/, '');
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
  window._scriptCommandRegex = null;
  window._scriptKeywordRegex = buildNameRegex(['repeat', 'until', 'if', 'halt'], true);
  window._scriptSystemRegex = null;
  window._scriptPoiRegex = null;
  window._scriptSymbolRegex = null;

  function refreshEditors() {
    if (window._scriptEditor) window._scriptEditor.refresh();
    if (window._liveScriptEditor) window._liveScriptEditor.refresh();
  }

  function setCommandRegex(commandNames) {
    window._scriptCommandRegex = buildNameRegex(commandNames || [], true);
    refreshEditors();
  }

  window.setScriptLocationSymbols = function (systemSymbols, poiSymbols) {
    window._scriptSystemRegex = buildNameRegex(systemSymbols || [], false);
    window._scriptPoiRegex = buildNameRegex(poiSymbols || [], false);
    refreshEditors();
  };

  window.setScriptSymbols = function (nextSymbols) {
    window._scriptSymbolRegex = buildNameRegex(nextSymbols || [], false);
    refreshEditors();
  };

  window.loadEditorBootstrap = function () {
    if (window._editorBootstrapLoaded) return;
    window._editorBootstrapLoaded = true;
    fetch(apiUrl('bootstrap/editor-data'), { cache: 'no-store' })
      .then(function (res) { return res.ok ? res.json() : null; })
      .then(function (data) {
        if (!data) return;
        if (Array.isArray(data.commandNames)) setCommandRegex(data.commandNames);
        if (Array.isArray(data.scriptHighlightNames)) window.setScriptSymbols(data.scriptHighlightNames);
        window.setScriptLocationSymbols(data.systemHighlightNames || [], data.poiHighlightNames || []);
        if (data.galaxyMap) window._galaxyMap = data.galaxyMap;
      })
      .catch(function () { });
  };

  window.setLiveScriptRunLine = function (lineNumber) {
    var editor = window._liveScriptEditor;
    if (!editor) return;
    var prev = window._liveScriptRunLineHandle;
    if (typeof prev === 'number' && prev >= 0 && prev < editor.lineCount()) {
      editor.removeLineClass(prev, 'background', 'run-line-active');
    }
    var next = (typeof lineNumber === 'number' && lineNumber > 0)
      ? Math.min(editor.lineCount() - 1, lineNumber - 1)
      : -1;
    if (next >= 0) {
      editor.addLineClass(next, 'background', 'run-line-active');
      window._liveScriptRunLineHandle = next;
    } else {
      window._liveScriptRunLineHandle = null;
    }
    window._liveScriptRunLineNumber = (typeof lineNumber === 'number') ? lineNumber : null;
  };

  window.syncCurrentScript = function () {
    fetch(apiUrl('partial/current-script'), { cache: 'no-store' })
      .then(function (res) { return res.ok ? res.json() : null; })
      .then(function (state) {
        if (!state || !window._liveScriptEditor) return;
        var text = typeof state.script === 'string' ? state.script : '';
        var current = window._liveScriptEditor.getValue();
        if (current !== text) window._liveScriptEditor.setValue(text);
        var nextLine = (typeof state.currentScriptLine === 'number') ? state.currentScriptLine : null;
        if (window._liveScriptRunLineNumber !== nextLine) window.setLiveScriptRunLine(nextLine);
      })
      .catch(function () { });
  };

  window.closeAllSidebarLayers = function () {
    document.querySelectorAll('.panel-layer.open').forEach(function (layer) {
      layer.classList.remove('open');
    });
  };

  window.openSidebarLayer = function (id) {
    window.closeAllSidebarLayers();
    var layer = document.getElementById(id);
    if (layer) layer.classList.add('open');
  };

  window.filterCatalogEntries = function (query) {
    var q = (query || '').toLowerCase().trim();
    document.querySelectorAll('#state-pane-catalog .catalog-entry').forEach(function (row) {
      var hay = row.getAttribute('data-search') || '';
      row.style.display = q === '' || hay.indexOf(q) >= 0 ? '' : 'none';
    });
  };

  window.initGalaxyMapCanvas = function (canvas) {
    if (!canvas || canvas._mapHandlersAttached) return;
    canvas._mapHandlersAttached = true;

    canvas.addEventListener('mousedown', function (e) {
      if (e.button !== 0) return;
      var state = window._galaxyMapStates.get(canvas);
      if (!state) return;
      state.dragging = true;
      state.dragStartX = e.clientX;
      state.dragStartY = e.clientY;
      state.dragOriginX = state.panX;
      state.dragOriginY = state.panY;
      canvas.classList.add('dragging');
    });

    canvas.addEventListener('mousemove', function (e) {
      var state = window._galaxyMapStates.get(canvas);
      if (!state || !state.dragging) return;
      state.panX = state.dragOriginX + (e.clientX - state.dragStartX);
      state.panY = state.dragOriginY + (e.clientY - state.dragStartY);
      window.drawGalaxyMapCanvas(canvas);
    });

    function stopDrag() {
      var state = window._galaxyMapStates.get(canvas);
      if (!state) return;
      state.dragging = false;
      canvas.classList.remove('dragging');
    }

    canvas.addEventListener('mouseup', stopDrag);
    canvas.addEventListener('mouseleave', stopDrag);

    canvas.addEventListener('wheel', function (e) {
      var state = window._galaxyMapStates.get(canvas);
      if (!state) return;
      e.preventDefault();
      var rect = canvas.getBoundingClientRect();
      var mx = e.clientX - rect.left;
      var my = e.clientY - rect.top;
      var oldZoom = state.zoom;
      var factor = e.deltaY < 0 ? 1.25 : 1 / 1.25;
      var nextZoom = Math.max(0.35, Math.min(40, oldZoom * factor));
      if (Math.abs(nextZoom - oldZoom) < 1e-9) return;
      state.panX = mx - ((mx - state.panX) * (nextZoom / oldZoom));
      state.panY = my - ((my - state.panY) * (nextZoom / oldZoom));
      state.zoom = nextZoom;
      window.drawGalaxyMapCanvas(canvas);
    }, { passive: false });
  };

  window.drawGalaxyMapCanvas = function (canvas) {
    var state = window._galaxyMapStates.get(canvas);
    if (!state || !state.layout) return;

    var ctx = state.ctx;
    var cssWidth = state.cssWidth;
    var cssHeight = state.cssHeight;

    ctx.setTransform(state.dpr, 0, 0, state.dpr, 0, 0);
    ctx.clearRect(0, 0, cssWidth, cssHeight);
    ctx.fillStyle = '#0b0f16';
    ctx.fillRect(0, 0, cssWidth, cssHeight);

    function tx(p) {
      return { x: p.x * state.zoom + state.panX, y: p.y * state.zoom + state.panY };
    }

    var drawn = {};
    ctx.strokeStyle = 'rgba(120, 170, 255, 0.32)';
    ctx.lineWidth = 1;
    state.layout.systems.forEach(function (s) {
      var a = tx(s.point);
      s.connections.forEach(function (cid) {
        var t = state.layout.byId[cid];
        if (!t) return;
        var key = s.id < cid ? (s.id + '|' + cid) : (cid + '|' + s.id);
        if (drawn[key]) return;
        drawn[key] = true;
        var b = tx(t.point);
        ctx.beginPath();
        ctx.moveTo(a.x, a.y);
        ctx.lineTo(b.x, b.y);
        ctx.stroke();
      });
    });

    ctx.fillStyle = '#8eb8ff';
    state.layout.systems.forEach(function (s) {
      var p = tx(s.point);
      ctx.beginPath();
      ctx.arc(p.x, p.y, 2.5, 0, Math.PI * 2);
      ctx.fill();
    });

    ctx.fillStyle = '#d7dae0';
    ctx.font = '11px ui-monospace, SFMono-Regular, Menlo, monospace';
    state.layout.systems.forEach(function (s) {
      var p = tx(s.point);
      ctx.fillText(s.id, p.x + 5, p.y - 5);
    });
  };

  window.renderGalaxyMapCanvases = function () {
    var canvases = document.querySelectorAll('.galaxy-map-canvas');
    canvases.forEach(function (canvas) {
      window.initGalaxyMapCanvas(canvas);

      var payload = canvas.getAttribute('data-map');
      if (!payload) return;

      var map;
      try { map = JSON.parse(payload); } catch (_) { return; }

      function getX(v) { return v && (v.X != null ? v.X : v.x); }
      function getY(v) { return v && (v.Y != null ? v.Y : v.y); }
      function isFiniteCoord(x, y) {
        return typeof x === 'number' && typeof y === 'number' && isFinite(x) && isFinite(y);
      }

      var systems = ((map && (map.Systems || map.systems)) || []).filter(function (s) {
        var id = (s && (s.Id || s.id)) || '';
        return typeof id === 'string' && id.trim().length > 0;
      });
      if (systems.length === 0) return;

      var ctx = canvas.getContext('2d');
      if (!ctx) return;

      var dpr = window.devicePixelRatio || 1;
      var cssWidth = canvas.clientWidth || 800;
      var cssHeight = canvas.clientHeight || 480;
      canvas.width = Math.max(1, Math.floor(cssWidth * dpr));
      canvas.height = Math.max(1, Math.floor(cssHeight * dpr));

      var positioned = systems.filter(function (s) {
        var x = getX(s);
        var y = getY(s);
        return isFiniteCoord(x, y);
      });

      var minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
      positioned.forEach(function (s) {
        var x = getX(s);
        var y = getY(s);
        if (x < minX) minX = x;
        if (x > maxX) maxX = x;
        if (y < minY) minY = y;
        if (y > maxY) maxY = y;
      });

      if (!isFinite(minX) || !isFinite(minY)) {
        minX = 0; maxX = 1; minY = 0; maxY = 1;
      }

      var padding = 28;
      var spanX = Math.max(1e-6, maxX - minX);
      var spanY = Math.max(1e-6, maxY - minY);
      var scaleX = Math.max(1, cssWidth - padding * 2) / spanX;
      var scaleY = Math.max(1, cssHeight - padding * 2) / spanY;
      var baseScale = Math.min(scaleX, scaleY);

      var byId = {};
      var layoutSystems = systems.map(function (s) {
        var id = ((s.Id || s.id) || '').trim();
        var x = getX(s);
        var y = getY(s);
        var px = isFiniteCoord(x, y) ? (padding + (x - minX) * baseScale) : (cssWidth * 0.5);
        var py = isFiniteCoord(x, y) ? (cssHeight - (padding + (y - minY) * baseScale)) : (cssHeight * 0.5);
        var mapped = {
          id: id,
          point: { x: px, y: py },
          connections: (s.Connections || s.connections || [])
            .filter(function (cidRaw) { return typeof cidRaw === 'string' && cidRaw.trim().length > 0; })
            .map(function (cidRaw) { return cidRaw.trim(); })
        };
        byId[id] = mapped;
        return mapped;
      });
      var existing = window._galaxyMapStates.get(canvas);
      var nextState = {
        ctx: ctx,
        dpr: dpr,
        cssWidth: cssWidth,
        cssHeight: cssHeight,
        layout: { systems: layoutSystems, byId: byId },
        zoom: existing ? existing.zoom : 1,
        panX: existing ? existing.panX : 0,
        panY: existing ? existing.panY : 0,
        dragging: false,
        payload: payload
      };

      window._galaxyMapStates.set(canvas, nextState);
      window.drawGalaxyMapCanvas(canvas);
    });
  };

  window.pollGalaxyMapData = function () {
    fetch(apiUrl('partial/map-data'), { cache: 'no-store' })
      .then(function (res) { return res.ok ? res.json() : null; })
      .then(function (map) {
        if (!map) return;
        var payload = JSON.stringify(map);
        var canvas = document.getElementById('state-map-canvas');
        if (canvas) {
          var existingPayload = canvas.getAttribute('data-map') || '';
          if (existingPayload !== payload) {
            canvas.setAttribute('data-map', payload);
          }
          window.renderGalaxyMapCanvases();
        }
        var systems = ((map && (map.Systems || map.systems)) || []);
        var legend = document.getElementById('map-legend');
        if (legend) {
          legend.textContent = 'Known systems: ' + systems.length + ' | Drag: pan | Wheel: zoom';
        }
      })
      .catch(function () { });
  };

  window.ensureScriptEditor = function () {
    if (!window.CodeMirror) return;

    var input = document.getElementById('script-input');
    var liveInput = document.getElementById('current-script-input');
    if ((!input && !liveInput) || (window._scriptEditor && window._liveScriptEditor)) return;

    if (!CodeMirror.modes.spacemolt) {
      CodeMirror.defineMode('spacemolt', function () {
        var functionNameRegex = /[A-Za-z_][A-Za-z0-9_]*(?=\s*\()/;
        var numberRegex = /(?:\d+\.\d+|\d+)\b/;
        var boolWordRegex = /\b(?:true|false|and|or|not)\b/i;
        var multiOpRegex = /(?:==|!=|<=|>=|&&|\|\|)/;
        var singleOpRegex = /[+\-*/%<>=!]/;
        var bracketRegex = /[(){}]/;

        return {
          startState: function () { return { lineStart: true }; },
          token: function (stream, state) {
            if (stream.sol()) state.lineStart = true;
            if (stream.eatSpace()) return null;
            if (state.lineStart && window._scriptCommandRegex && stream.match(window._scriptCommandRegex, true, true)) {
              state.lineStart = false;
              return 'keyword';
            }
            if (state.lineStart && window._scriptKeywordRegex && stream.match(window._scriptKeywordRegex, true, true)) {
              state.lineStart = false;
              return 'keyword';
            }
            if (stream.peek() === ';') {
              stream.next();
              state.lineStart = false;
              return 'operator';
            }
            if (stream.match(functionNameRegex, true, true)) {
              state.lineStart = false;
              return 'def';
            }
            if (stream.match(numberRegex, true, true)) {
              state.lineStart = false;
              return 'number';
            }
            if (stream.match(boolWordRegex, true, true)) {
              state.lineStart = false;
              return 'builtin';
            }
            if (stream.match(multiOpRegex, true, true) || stream.match(singleOpRegex, true, true)) {
              state.lineStart = false;
              return 'operator';
            }
            if (stream.match(bracketRegex, true, true)) {
              state.lineStart = false;
              return 'bracket';
            }
            if (window._scriptSystemRegex && stream.match(window._scriptSystemRegex, true, true)) {
              state.lineStart = false;
              return 'variable-2';
            }
            if (window._scriptPoiRegex && stream.match(window._scriptPoiRegex, true, true)) {
              state.lineStart = false;
              return 'string-2';
            }
            if (window._scriptSymbolRegex && stream.match(window._scriptSymbolRegex, true, true)) {
              state.lineStart = false;
              return 'atom';
            }
            stream.next();
            state.lineStart = false;
            return null;
          }
        };
      });
    }

    if (input && !window._scriptEditor) {
      var editor = CodeMirror.fromTextArea(input, {
        mode: 'spacemolt',
        theme: 'material-darker',
        lineNumbers: true,
        indentUnit: 2,
        tabSize: 2,
        indentWithTabs: false
      });
      window._scriptEditor = editor;
      var form = document.getElementById('script-form');
      if (form) form.addEventListener('submit', function () { editor.save(); });
    }

    if (liveInput && !window._liveScriptEditor) {
      window._liveScriptEditor = CodeMirror.fromTextArea(liveInput, {
        mode: 'spacemolt',
        theme: 'material-darker',
        lineNumbers: true,
        readOnly: true
      });
      window._liveScriptRunLineHandle = null;
      window._liveScriptRunLineNumber = null;
    }

    window.loadEditorBootstrap();
    window.syncCurrentScript();
    if (!window._liveScriptPoller) {
      window._liveScriptPoller = setInterval(window.syncCurrentScript, 1000);
    }
  };

  document.addEventListener('click', function (e) {
    var btn = e.target.closest('.tab-btn');
    if (!btn) return;
    var panel = document.getElementById('state-panel');
    if (!panel) return;
    var tab = btn.getAttribute('data-tab');
    panel.querySelectorAll('.tab-btn').forEach(function (b) { b.classList.toggle('active', b === btn); });
    panel.querySelectorAll('.tab-pane').forEach(function (p) { p.classList.remove('active'); });
    var target = document.getElementById('state-pane-' + tab);
    if (target) target.classList.add('active');
  });

  document.addEventListener('click', function (e) {
    var addBtn = e.target.closest('#open-add-bot');
    if (addBtn) {
      window.openSidebarLayer('add-bot-panel-layer');
      return;
    }

    var llmBtn = e.target.closest('#open-llm-settings');
    if (llmBtn) {
      window.openSidebarLayer('llm-panel-layer');
      return;
    }

    var closeBtn = e.target.closest('[data-close-layer]');
    if (closeBtn) {
      window.closeAllSidebarLayers();
      return;
    }

    var openLayer = e.target.closest('.panel-layer.open');
    if (openLayer && e.target === openLayer) {
      window.closeAllSidebarLayers();
    }
  });

  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') window.closeAllSidebarLayers();
  });

  document.body.addEventListener('htmx:afterRequest', function (e) {
    var path = (((e || {}).detail || {}).pathInfo || {}).requestPath || '';
    if (path.endsWith('/api/add-bot') || path.endsWith('/api/llm-select') || path === 'api/add-bot' || path === 'api/llm-select') {
      window.closeAllSidebarLayers();
    }
  });

  document.addEventListener('htmx:afterSwap', function () {
    window.ensureScriptEditor();
    refreshEditors();
  });

  window.ensureScriptEditor();
}());
