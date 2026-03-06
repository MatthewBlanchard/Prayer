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
  window._haltHighlightPending = false;
  window._haltHighlightPendingUntil = 0;
  window._statePaneUiState = window._statePaneUiState || {};

  function captureStatePaneUiState(pane) {
    if (!pane || !pane.id) return;
    var openKeys = [];
    pane.querySelectorAll('details[open]').forEach(function (d) {
      var key = (d.getAttribute('data-persist-key') || '').trim();
      if (!key) {
        var summary = d.querySelector('summary');
        key = summary ? ('summary:' + (summary.textContent || '').trim()) : '';
      }
      if (key) openKeys.push(key);
    });
    window._statePaneUiState[pane.id] = {
      scrollTop: pane.scrollTop || 0,
      openKeys: openKeys
    };
  }

  function restoreStatePaneUiState(pane) {
    if (!pane || !pane.id) return;
    var state = window._statePaneUiState[pane.id];
    if (!state) return;

    var openSet = new Set(state.openKeys || []);
    pane.querySelectorAll('details').forEach(function (d) {
      var key = (d.getAttribute('data-persist-key') || '').trim();
      if (!key) {
        var summary = d.querySelector('summary');
        key = summary ? ('summary:' + (summary.textContent || '').trim()) : '';
      }
      if (!key) return;
      d.open = openSet.has(key);
    });

    var top = typeof state.scrollTop === 'number' ? state.scrollTop : 0;
    pane.scrollTop = top;
    requestAnimationFrame(function () { pane.scrollTop = top; });
  }

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

  window.handlePromptAfterRequest = function (e) {
    var detail = (e || {}).detail || {};
    var xhr = detail.xhr || null;
    if (!xhr) return;

    var responseText = xhr.responseText || '';
    if (xhr.status < 200 || xhr.status >= 300) {
      if (responseText.length > 0) window.alert(responseText);
      return;
    }

    if (responseText.length === 0) return;
    if (window._scriptEditor) {
      window._scriptEditor.setValue(responseText);
      window._scriptEditor.focus();
      return;
    }

    var scriptInput = document.getElementById('script-input');
    if (scriptInput) scriptInput.value = responseText;
  };

  window.setExecuteButtonRunning = function (isRunning) {
    var executeBtn =
      document.getElementById('execute-btn') ||
      document.querySelector("form[hx-post='api/execute'] button");
    if (!executeBtn) return;
    executeBtn.classList.toggle('run-active', !!isRunning);
  };

  window.executeIfOk = function (e) {
    var detail = (e || {}).detail || {};
    var xhr = detail.xhr || null;
    if (!xhr) return;
    if (xhr.status < 200 || xhr.status >= 300) return;

    var sourceForm = detail.elt || null;
    if (sourceForm) {
      var hiddenScript = sourceForm.querySelector("input[name='script']");
      var nextScript = hiddenScript ? (hiddenScript.value || '') : '';
      if (nextScript.length > 0) {
        if (window._liveScriptEditor && window._liveScriptEditor.getValue() !== nextScript) {
          window._liveScriptEditor.setValue(nextScript);
        }
        if (window._scriptEditor && window._scriptEditor.getValue() !== nextScript) {
          window._scriptEditor.setValue(nextScript);
        } else {
          var scriptInput = document.getElementById('script-input');
          if (scriptInput) scriptInput.value = nextScript;
        }
      }
    }

    // Mirror execute-form behavior so run-line highlighting never stays stale.
    window._haltHighlightPending = false;
    window._haltHighlightPendingUntil = 0;
    window.setExecuteButtonRunning(true);
    window.setLiveScriptRunLine(1);
    htmx.ajax('POST', apiUrl('api/execute'), { swap: 'none' });
    // Force a quick refresh in addition to the 1s poll loop.
    setTimeout(window.syncCurrentScript, 120);
    setTimeout(window.syncCurrentScript, 450);
  };

  window.useMissionPrompt = function (promptText, missionId, returnPoi) {
    var lines = [];
    var text = (promptText || '').toString().trim();
    if (text.length > 0) lines.push(text);
    var mid = (missionId || '').toString().trim();
    if (mid.length > 0) lines.push('mission_id=' + mid);
    var poi = (returnPoi || '').toString().trim();
    if (poi.length > 0) lines.push('return_poi=' + poi);
    if (lines.length === 0) return;
    var finalPrompt = lines.join('\n');
    var promptInput = document.querySelector("#prompt-form textarea[name='prompt']");
    if (!promptInput) return;
    promptInput.value = finalPrompt;
    promptInput.focus();
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
        var now = Date.now();
        if (window._haltHighlightPending) {
          if (now < window._haltHighlightPendingUntil) {
            nextLine = null;
          } else {
            window._haltHighlightPending = false;
            window._haltHighlightPendingUntil = 0;
          }
        }
        window.setExecuteButtonRunning(nextLine !== null);
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

  window._tradeCatalogQuery = window._tradeCatalogQuery || '';
  if (typeof window._tradeOnlyWithOrders !== 'boolean') window._tradeOnlyWithOrders = true;
  window.toggleTradeOnlyWithOrders = function (enabled) {
    window._tradeOnlyWithOrders = !!enabled;
    window.filterTradeCatalogItems(window._tradeCatalogQuery || '');
  };
  window.filterTradeCatalogItems = function (query) {
    var next = (typeof query === 'string' ? query : window._tradeCatalogQuery || '');
    var q = next.toLowerCase().trim();
    window._tradeCatalogQuery = next;

    var pane = document.getElementById('state-pane-trade');
    if (!pane) return;

    pane.querySelectorAll('.trade-catalog-entry').forEach(function (row) {
      var hay = row.getAttribute('data-search') || '';
      var hasOrders = (row.getAttribute('data-has-orders') || '') === 'true';
      var matchesSearch = q === '' || hay.indexOf(q) >= 0;
      var matchesOrders = !window._tradeOnlyWithOrders || hasOrders;
      row.style.display = matchesSearch && matchesOrders ? '' : 'none';
    });

    Array.prototype.slice.call(pane.querySelectorAll('details.trade-item-category'))
      .reverse()
      .forEach(function (group) {
        var hasVisible = Array.prototype.some.call(
          group.querySelectorAll('.trade-catalog-entry'),
          function (row) { return row.style.display !== 'none'; });
        group.style.display = hasVisible ? '' : 'none';
      });

    var input = pane.querySelector("input.catalog-search[oninput*='filterTradeCatalogItems']");
    if (input && input.value !== next) input.value = next;
    var toggle = pane.querySelector('.trade-only-orders-toggle input[type="checkbox"]');
    if (toggle && toggle.checked !== !!window._tradeOnlyWithOrders) {
      toggle.checked = !!window._tradeOnlyWithOrders;
    }
  };

  window.submitTradeBuy = function (_event, form) {
    if (!form) return false;
    var itemId = (form.getAttribute('data-item-id') || '').trim();
    if (!itemId) return false;
    var qtyInput = form.querySelector("input[name='qty']");
    var qty = parseInt((qtyInput && qtyInput.value) ? qtyInput.value : '1', 10);
    if (!Number.isFinite(qty) || qty < 1) qty = 1;
    if (qtyInput) qtyInput.value = String(qty);
    var scriptInput = form.querySelector("input[name='script']");
    if (scriptInput) scriptInput.value = 'buy ' + itemId + ' ' + qty + ';';
    return true;
  };

  window._shipCatalogQuery = window._shipCatalogQuery || '';
  window.filterShipCatalogEntries = function (query) {
    var next = (typeof query === 'string' ? query : window._shipCatalogQuery || '');
    var q = next.toLowerCase().trim();
    window._shipCatalogQuery = next;

    var pane = document.getElementById('state-pane-shipyard');
    if (!pane) return;

    pane.querySelectorAll('.shipyard-ship-detail').forEach(function (row) {
      var hay = row.getAttribute('data-search') || '';
      row.style.display = q === '' || hay.indexOf(q) >= 0 ? '' : 'none';
    });

    Array.prototype.slice.call(pane.querySelectorAll('details.shipyard-faction-group'))
      .reverse()
      .forEach(function (group) {
        var hasVisible = Array.prototype.some.call(
          group.querySelectorAll('.shipyard-ship-detail'),
          function (row) { return row.style.display !== 'none'; });
        group.style.display = hasVisible ? '' : 'none';
      });

    var input = pane.querySelector("input.catalog-search[oninput*='filterShipCatalogEntries']");
    if (input && input.value !== next) input.value = next;
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

  function activateStateTab(tabBtn, focusAfter) {
    if (!tabBtn) return;
    var panel = document.getElementById('state-panel');
    if (!panel) return;

    var tab = tabBtn.getAttribute('data-tab');
    if (!tab) return;

    panel.querySelectorAll("[role='tab']").forEach(function (b) {
      var selected = (b === tabBtn);
      b.setAttribute('aria-selected', selected ? 'true' : 'false');
      b.setAttribute('tabindex', selected ? '0' : '-1');
      b.classList.toggle('active', selected);
    });

    panel.querySelectorAll('.tab-pane').forEach(function (p) {
      p.classList.remove('active');
      p.setAttribute('hidden', '');
    });

    var target = document.getElementById('state-pane-' + tab);
    if (target) {
      target.classList.add('active');
      target.removeAttribute('hidden');
    }

    if (focusAfter) tabBtn.focus();
  }

  document.addEventListener('click', function (e) {
    var btn = e.target.closest("[role='tab'].tab-btn");
    if (!btn) return;
    activateStateTab(btn, false);
  });

  document.addEventListener('keydown', function (e) {
    var currentTab = e.target.closest("[role='tab'].tab-btn");
    if (!currentTab) return;
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) return;

    var tabList = currentTab.closest("[role='tablist']");
    if (!tabList) return;

    var tabs = Array.prototype.slice.call(tabList.querySelectorAll("[role='tab'].tab-btn"));
    if (tabs.length === 0) return;

    var index = tabs.indexOf(currentTab);
    if (index < 0) return;

    var nextIndex = index;
    if (e.key === 'ArrowRight') nextIndex = (index + 1) % tabs.length;
    else if (e.key === 'ArrowLeft') nextIndex = (index - 1 + tabs.length) % tabs.length;
    else if (e.key === 'Home') nextIndex = 0;
    else if (e.key === 'End') nextIndex = tabs.length - 1;

    e.preventDefault();
    activateStateTab(tabs[nextIndex], true);
  });

  document.addEventListener('click', function (e) {
    var promptBtn = e.target.closest('.mission-use-prompt');
    if (promptBtn) {
      window.useMissionPrompt(
        promptBtn.getAttribute('data-mission-prompt') || '',
        promptBtn.getAttribute('data-mission-id') || '',
        promptBtn.getAttribute('data-return-poi') || '');
      return;
    }

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
    var detail = (e || {}).detail || {};
    var path = (detail.pathInfo || {}).requestPath || '';
    if (path.endsWith('/api/add-bot') || path.endsWith('/api/llm-select') || path === 'api/add-bot' || path === 'api/llm-select') {
      window.closeAllSidebarLayers();
    }
  });

  document.body.addEventListener('htmx:beforeRequest', function (e) {
    var detail = (e || {}).detail || {};
    var path = (detail.pathInfo || {}).requestPath || '';
    var elt = detail.elt || null;

    if ((path.indexOf('partial/state') >= 0) &&
      elt &&
      elt.classList &&
      elt.classList.contains('tab-pane')) {
      var active = document.activeElement;
      if (active &&
        active.tagName === 'INPUT' &&
        active.classList &&
        active.classList.contains('catalog-search') &&
        elt.contains(active)) {
        e.preventDefault();
        return;
      }
      captureStatePaneUiState(elt);
    }

    if (path.endsWith('/api/save-example') || path === 'api/save-example') {
      var scriptValue = '';
      if (window._scriptEditor) {
        scriptValue = window._scriptEditor.getValue() || '';
      } else {
        var scriptInput = document.getElementById('script-input');
        scriptValue = scriptInput ? (scriptInput.value || '') : '';
      }
      detail.parameters = detail.parameters || {};
      detail.parameters.script = scriptValue;
    }

    if (path.endsWith('/api/halt') || path === 'api/halt') {
      window._haltHighlightPending = true;
      window._haltHighlightPendingUntil = Date.now() + 10000;
      window.setLiveScriptRunLine(null);
      window.setExecuteButtonRunning(false);
      return;
    }

    if (path.endsWith('/api/execute') ||
      path.endsWith('/api/control-input') ||
      path === 'api/execute' ||
      path === 'api/control-input') {
      window._haltHighlightPending = false;
      window._haltHighlightPendingUntil = 0;
      if (path.endsWith('/api/execute') || path === 'api/execute') {
        window.setExecuteButtonRunning(true);
      }
    }
  });

  document.addEventListener('htmx:afterSwap', function (e) {
    var detail = (e || {}).detail || {};
    var elt = detail.elt || null;
    if (elt && elt.classList && elt.classList.contains('tab-pane')) {
      restoreStatePaneUiState(elt);
    }
    window.filterTradeCatalogItems(window._tradeCatalogQuery || '');
    window.filterShipCatalogEntries(window._shipCatalogQuery || '');
    window.ensureScriptEditor();
    refreshEditors();
  });

  window.ensureScriptEditor();
}());
