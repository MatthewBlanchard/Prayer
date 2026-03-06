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

  window.issueControlScriptAndExecute = function (scriptText) {
    var script = (scriptText || '').toString().trim();
    if (script.length === 0) return;

    if (window._liveScriptEditor && window._liveScriptEditor.getValue() !== script) {
      window._liveScriptEditor.setValue(script);
    }
    if (window._scriptEditor && window._scriptEditor.getValue() !== script) {
      window._scriptEditor.setValue(script);
    } else {
      var scriptInput = document.getElementById('script-input');
      if (scriptInput) scriptInput.value = script;
    }

    var body = new URLSearchParams();
    body.set('script', script);

    fetch(apiUrl('api/control-input'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8' },
      body: body.toString()
    })
      .then(function (res) {
        return res.text().then(function (text) {
          return { ok: res.ok, text: text || '' };
        });
      })
      .then(function (result) {
        if (!result.ok) {
          if (result.text.length > 0) window.alert(result.text);
          return;
        }

        window._haltHighlightPending = false;
        window._haltHighlightPendingUntil = 0;
        window.setExecuteButtonRunning(true);
        window.setLiveScriptRunLine(1);
        htmx.ajax('POST', apiUrl('api/execute'), { swap: 'none' });
        setTimeout(window.syncCurrentScript, 120);
        setTimeout(window.syncCurrentScript, 450);
      })
      .catch(function () { });
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

    canvas.addEventListener('mousemove', function (e) {
      var state = window._galaxyMapStates.get(canvas);
      if (!state) return;
      var rect = canvas.getBoundingClientRect();
      state.mouseX = e.clientX - rect.left;
      state.mouseY = e.clientY - rect.top;
      window.drawGalaxyMapCanvas(canvas);
    });

    canvas.addEventListener('mouseleave', function () {
      var state = window._galaxyMapStates.get(canvas);
      if (!state) return;
      state.mouseX = null;
      state.mouseY = null;
      window.drawGalaxyMapCanvas(canvas);
    });

    canvas.addEventListener('click', function (e) {
      var state = window._galaxyMapStates.get(canvas);
      if (!state || !state.layout || !Array.isArray(state.layout.pois)) return;
      if (state.layout.pois.length === 0) return;

      var rect = canvas.getBoundingClientRect();
      var clickX = e.clientX - rect.left;
      var clickY = e.clientY - rect.top;

      var nearest = null;
      var nearestDist = Number.POSITIVE_INFINITY;
      state.layout.pois.forEach(function (poi) {
        if (!poi || !poi.point || !poi.id) return;
        var dx = clickX - poi.point.x;
        var dy = clickY - poi.point.y;
        var d = Math.sqrt(dx * dx + dy * dy);
        if (d < nearestDist) {
          nearest = poi;
          nearestDist = d;
        }
      });

      if (!nearest) return;
      var hitRadius = nearest.isStar ? 18 : (nearest.isPlanet ? 14 : 12);
      if (nearestDist > hitRadius) return;

      window.issueControlScriptAndExecute('go ' + nearest.id + ';');
    });
  };

  window.drawGalaxyMapCanvas = function (canvas) {
    var state = window._galaxyMapStates.get(canvas);
    if (!state || !state.layout) return;

    var ctx = state.ctx;
    var cssWidth = state.cssWidth;
    var cssHeight = state.cssHeight;

    ctx.setTransform(state.dpr, 0, 0, state.dpr, 0, 0);
    ctx.clearRect(0, 0, cssWidth, cssHeight);
    var bg = ctx.createLinearGradient(0, 0, 0, cssHeight);
    bg.addColorStop(0, '#090f1b');
    bg.addColorStop(1, '#060b14');
    ctx.fillStyle = bg;
    ctx.fillRect(0, 0, cssWidth, cssHeight);
    var nebula = ctx.createRadialGradient(cssWidth * 0.22, cssHeight * 0.18, 8, cssWidth * 0.22, cssHeight * 0.18, cssWidth * 0.7);
    nebula.addColorStop(0, 'rgba(120,170,255,0.12)');
    nebula.addColorStop(1, 'rgba(120,170,255,0)');
    ctx.fillStyle = nebula;
    ctx.fillRect(0, 0, cssWidth, cssHeight);

    if (state.layout.stars && state.layout.stars.length > 0) {
      state.layout.stars.forEach(function (star) {
        var bloom = ctx.createRadialGradient(star.x, star.y, 0, star.x, star.y, star.glow || 2);
        bloom.addColorStop(0, star.color.replace(',1)', ',0.26)'));
        bloom.addColorStop(1, star.color.replace(',1)', ',0)'));
        ctx.fillStyle = bloom;
        ctx.globalAlpha = 1;
        ctx.beginPath();
        ctx.arc(star.x, star.y, star.glow || 2, 0, Math.PI * 2);
        ctx.fill();

        ctx.fillStyle = star.color;
        ctx.globalAlpha = star.alpha;
        ctx.beginPath();
        ctx.arc(star.x, star.y, star.r, 0, Math.PI * 2);
        ctx.fill();
      });
      ctx.globalAlpha = 1;
    }

    var cp = state.layout.currentPoint || { x: cssWidth * 0.5, y: cssHeight * 0.5 };

    if (state.layout.connectionRays && state.layout.connectionRays.length > 0) {
      var shootRadius = Math.sqrt(cssWidth * cssWidth + cssHeight * cssHeight) * 1.2;
      var labelRadius = Math.max(36, Math.min(cssWidth, cssHeight) * 0.47);
      ctx.strokeStyle = 'rgba(120, 170, 255, 0.44)';
      ctx.fillStyle = '#9cc2ff';
      ctx.font = '11px ui-monospace, SFMono-Regular, Menlo, monospace';
      ctx.lineWidth = 1.4;

      state.layout.connectionRays.forEach(function (ray) {
        var a = ray.angle;
        var sx = cp.x + Math.cos(a) * 12;
        var sy = cp.y - Math.sin(a) * 12;
        var ex = cp.x + Math.cos(a) * shootRadius;
        var ey = cp.y - Math.sin(a) * shootRadius;

        ctx.beginPath();
        ctx.moveTo(sx, sy);
        ctx.lineTo(ex, ey);
        ctx.stroke();

        var tx = cp.x + Math.cos(a) * (labelRadius - 10);
        var ty = cp.y - Math.sin(a) * (labelRadius - 10);
        var label = 'to ' + ray.id;
        ctx.fillStyle = '#b7d7ff';
        ctx.fillText(label, tx + (Math.cos(a) >= 0 ? 4 : -4 - (label.length * 6)), ty);
      });
    }

    if (state.layout.poiOrbits && state.layout.poiOrbits.length > 0) {
      ctx.save();
      ctx.lineWidth = 1;
      state.layout.poiOrbits.forEach(function (orbit, idx) {
        var tint = (idx % 3 === 0) ? '128,188,255' : (idx % 3 === 1) ? '145,220,200' : '255,214,122';
        ctx.strokeStyle = 'rgba(' + tint + ',0.17)';
        ctx.setLineDash([4, 6]);
        ctx.beginPath();
        ctx.arc(cp.x, cp.y, orbit.radius, 0, Math.PI * 2);
        ctx.stroke();
      });
      ctx.setLineDash([]);
      ctx.restore();
    }

    ctx.strokeStyle = 'rgba(90, 201, 119, 0.95)';
    ctx.lineWidth = 1.8;
    ctx.fillStyle = 'rgba(90,201,119,0.20)';
    ctx.beginPath();
    ctx.arc(cp.x, cp.y, 13, 0, Math.PI * 2);
    ctx.fill();
    ctx.beginPath();
    ctx.arc(cp.x, cp.y, 8, 0, Math.PI * 2);
    ctx.stroke();
    ctx.fillStyle = '#5ac977';
    ctx.beginPath();
    ctx.arc(cp.x, cp.y, 3.8, 0, Math.PI * 2);
    ctx.fill();

    if (state.layout.pois && state.layout.pois.length > 0) {
      ctx.strokeStyle = 'rgba(255, 214, 122, 0.40)';
      ctx.fillStyle = '#ffd67a';
      ctx.font = '11px ui-monospace, SFMono-Regular, Menlo, monospace';
      var hoverX = typeof state.mouseX === 'number' ? state.mouseX : null;
      var hoverY = typeof state.mouseY === 'number' ? state.mouseY : null;
      state.layout.pois.forEach(function (poi) {
        var p = poi.point;
        // POI glow bloom
        var baseRadius = poi.isStar ? 8.5 : (poi.isPlanet ? 5.5 : 4.5);
        var glowRadius = poi.isCurrent ? Math.max(20, baseRadius * 2.6) : Math.max(14, baseRadius * 2.2);
        var glow = ctx.createRadialGradient(p.x, p.y, 0, p.x, p.y, glowRadius);
        glow.addColorStop(0, poi.isCurrent ? 'rgba(255,230,170,0.45)' : 'rgba(255,214,122,0.30)');
        glow.addColorStop(1, 'rgba(255,214,122,0)');
        ctx.fillStyle = glow;
        ctx.beginPath();
        ctx.arc(p.x, p.y, glowRadius, 0, Math.PI * 2);
        ctx.fill();

        ctx.fillStyle = '#ffd67a';
        if (poi.isStar || poi.isPlanet) {
          ctx.beginPath();
          ctx.arc(p.x, p.y, baseRadius, 0, Math.PI * 2);
          ctx.fill();
        } else {
          ctx.beginPath();
          ctx.moveTo(p.x, p.y - baseRadius);
          ctx.lineTo(p.x + baseRadius, p.y);
          ctx.lineTo(p.x, p.y + baseRadius);
          ctx.lineTo(p.x - baseRadius, p.y);
          ctx.closePath();
          ctx.fill();
        }

        var showLabel = false;
        if (hoverX !== null && hoverY !== null) {
          var dxh = hoverX - p.x;
          var dyh = hoverY - p.y;
          var hoverDist = Math.sqrt(dxh * dxh + dyh * dyh);
          showLabel = hoverDist <= Math.max(24, baseRadius * 4.2);
        }

        if (poi.isCurrent) {
          ctx.strokeStyle = 'rgba(255, 234, 170, 0.78)';
          ctx.lineWidth = 1.4;
          ctx.beginPath();
          ctx.arc(p.x, p.y, baseRadius + 3, 0, Math.PI * 2);
          ctx.stroke();
          ctx.beginPath();
          ctx.arc(p.x, p.y, baseRadius + 6, 0, Math.PI * 2);
          ctx.stroke();
        }

        if (showLabel && (poi.label || poi.id)) {
          ctx.fillStyle = 'rgba(255, 235, 190, 0.96)';
          ctx.fillText((poi.isCurrent ? 'You are here: ' : '') + (poi.label || poi.id), p.x + 10, p.y - 10);
          ctx.fillStyle = '#ffd67a';
        }
      });
    }
    ctx.fillStyle = '#d7dae0';
    ctx.font = '12px ui-monospace, SFMono-Regular, Menlo, monospace';
    if (state.currentId) ctx.fillText(state.currentId, cp.x + 10, cp.y - 10);
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
      var currentId = ((map && (map.CurrentSystem || map.currentSystem)) || '').trim();
      var currentPoiId = ((map && (map.CurrentPoi || map.currentPoi)) || '').trim();
      var pois = ((map && (map.Pois || map.pois)) || []).filter(function (p) {
        var id = (p && (p.Id || p.id)) || '';
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

      var currentSystem = systems.find(function (s) {
        var id = ((s.Id || s.id) || '').trim();
        return id === currentId;
      }) || systems[0];

      var currentX = getX(currentSystem);
      var currentY = getY(currentSystem);
      if (!isFiniteCoord(currentX, currentY)) {
        currentX = 0;
        currentY = 0;
      }

      var poiPositioned = pois.filter(function (p) {
        return isFiniteCoord(getX(p), getY(p));
      });
      var sunPoi = poiPositioned.find(function (p) {
        var type = ((p && (p.Type || p.type)) || '').toString().trim().toLowerCase();
        var id = ((p && (p.Id || p.id)) || '').toString().trim().toLowerCase();
        var label = ((p && (p.Label || p.label)) || '').toString().trim().toLowerCase();
        return type === 'sun' ||
          type === 'star' ||
          id === 'sun' ||
          id.endsWith('_sun') ||
          label === 'sun' ||
          label.indexOf(' sun') >= 0 ||
          label.indexOf('star') >= 0;
      }) || null;
      var currentPoi = pois.find(function (p) {
        var id = ((p && (p.Id || p.id)) || '').trim();
        return (currentPoiId && id === currentPoiId) || !!(p && (p.isCurrent || p.IsCurrent));
      }) || poiPositioned[0] || null;
      // Anchor the camera/orbit center on the system sun when available; otherwise use system X,Y.
      var anchorX = sunPoi ? getX(sunPoi) : currentX;
      var anchorY = sunPoi ? getY(sunPoi) : currentY;
      if (!isFiniteCoord(anchorX, anchorY)) {
        anchorX = 0;
        anchorY = 0;
      }

      function relPoiPoint(p) {
        var x = getX(p);
        var y = getY(p);
        if (!isFiniteCoord(x, y)) return { x: 0, y: 0 };
        return { x: x - anchorX, y: y - anchorY };
      }

      var allRelX = [];
      var allRelY = [];
      pois.forEach(function (p) {
        var rp = relPoiPoint(p);
        allRelX.push(rp.x);
        allRelY.push(rp.y);
      });
      allRelX.push(0); allRelY.push(0);

      var minRelX = allRelX.reduce(function (m, v) { return Math.min(m, v); }, 0);
      var maxRelX = allRelX.reduce(function (m, v) { return Math.max(m, v); }, 0);
      var minRelY = allRelY.reduce(function (m, v) { return Math.min(m, v); }, 0);
      var maxRelY = allRelY.reduce(function (m, v) { return Math.max(m, v); }, 0);
      // Fit raw bounding box inside canvas while keeping current system centered.
      var halfRangeX = Math.max(8, Math.max(Math.abs(minRelX), Math.abs(maxRelX)));
      var halfRangeY = Math.max(8, Math.max(Math.abs(minRelY), Math.abs(maxRelY)));
      var padding = 34;
      var availableW = Math.max(1, cssWidth - padding * 2);
      var availableH = Math.max(1, cssHeight - padding * 2);
      var scaleX = availableW / (halfRangeX * 2);
      var scaleY = availableH / (halfRangeY * 2);
      var baseScale = Math.max(3.1, Math.min(scaleX, scaleY));
      var centerX = cssWidth * 0.5;
      var centerY = cssHeight * 0.5;

      var currentConnections = ((currentSystem && (currentSystem.Connections || currentSystem.connections)) || [])
        .filter(function (cidRaw) { return typeof cidRaw === 'string' && cidRaw.trim().length > 0; })
        .map(function (cidRaw) { return cidRaw.trim(); });

      function hashAngle(text) {
        var h = 0;
        for (var i = 0; i < text.length; i++) h = ((h << 5) - h) + text.charCodeAt(i);
        return ((Math.abs(h) % 360) / 180) * Math.PI;
      }

      var connectionRays = currentConnections.map(function (cid) {
        var t = systems.find(function (s) { return ((s.Id || s.id) || '').trim() === cid; }) || null;
        var tx = t ? getX(t) : null;
        var ty = t ? getY(t) : null;
        var angle = hashAngle(cid);
        if (isFiniteCoord(tx, ty) && isFiniteCoord(currentX, currentY)) {
          var vx = tx - currentX;
          var vy = ty - currentY;
          if (Math.abs(vx) > 1e-9 || Math.abs(vy) > 1e-9) {
            angle = Math.atan2(vy, vx);
          }
        }
        return { id: cid, angle: angle };
      });

      var layoutPois = pois.map(function (p) {
        var rp = relPoiPoint(p);
        var id = ((p.Id || p.id) || '').trim();
        var label = ((p.Label || p.label) || '').toString().trim();
        var type = ((p.Type || p.type) || '').toString().trim().toLowerCase();
        var isStar = type === 'sun' || type === 'star' || id.toLowerCase() === 'sun' || id.toLowerCase().endsWith('_sun');
        var isPlanet = type.indexOf('planet') >= 0;
        return {
          id: id,
          label: label.length > 0 ? label : id,
          type: type,
          isStar: isStar,
          isPlanet: isPlanet,
          point: { x: centerX + rp.x * baseScale, y: centerY - rp.y * baseScale },
          isCurrent: (currentPoiId && id === currentPoiId) || !!(p && (p.isCurrent || p.IsCurrent))
        };
      });

      var seed = 2166136261 >>> 0;
      for (var si = 0; si < payload.length; si++) {
        seed ^= payload.charCodeAt(si);
        seed = Math.imul(seed, 16777619) >>> 0;
      }
      function nextRand() {
        seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
        return (seed >>> 0) / 4294967295;
      }
      var stars = [];
      var starCount = 190;
      for (var st = 0; st < starCount; st++) {
        var warm = nextRand() > 0.82;
        stars.push({
          x: nextRand() * cssWidth,
          y: nextRand() * cssHeight,
          r: warm ? (0.5 + nextRand() * 1.2) : (0.4 + nextRand() * 1.0),
          alpha: 0.22 + nextRand() * 0.62,
          glow: 1.4 + nextRand() * 3.2,
          color: warm ? 'rgba(255,230,170,1)' : 'rgba(190,220,255,1)'
        });
      }

      var poiOrbits = layoutPois
        .map(function (poi) {
          var dx = poi.point.x - centerX;
          var dy = poi.point.y - centerY;
          return { radius: Math.sqrt(dx * dx + dy * dy), id: poi.id };
        })
        .filter(function (o) { return o.radius > 2.5; })
        .sort(function (a, b) { return a.radius - b.radius; });

      var existing = window._galaxyMapStates.get(canvas);

      var nextState = {
        ctx: ctx,
        dpr: dpr,
        cssWidth: cssWidth,
        cssHeight: cssHeight,
        layout: {
          currentPoint: { x: centerX, y: centerY },
          connectionRays: connectionRays,
          pois: layoutPois,
          poiOrbits: poiOrbits,
          stars: stars
        },
        mouseX: existing ? existing.mouseX : null,
        mouseY: existing ? existing.mouseY : null,
        currentId: currentId,
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
          legend.textContent = 'Known systems: ' + systems.length;
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
    window.renderGalaxyMapCanvases();
    window.ensureScriptEditor();
    refreshEditors();
  });

  window.renderGalaxyMapCanvases();
  window.ensureScriptEditor();
}());
