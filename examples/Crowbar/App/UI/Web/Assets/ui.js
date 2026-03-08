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
  window._mapSubtabSelection = window._mapSubtabSelection || 'system';
  window._galaxyMapViewState = window._galaxyMapViewState || { panX: 0, panY: 0, hasUserPan: false, zoom: 4.5, hasUserZoom: false };
  window._tickBarState = window._tickBarState || { tick: null, observedAtMs: 0, lastPostUtcMs: null, renderPct: null, lastFrameMs: 0 };

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

  function formatAgeSeconds(totalSeconds) {
    if (!(totalSeconds >= 0)) return 'n/a';
    if (totalSeconds < 60) return Math.floor(totalSeconds) + 's ago';
    var mins = Math.floor(totalSeconds / 60);
    var secs = Math.floor(totalSeconds % 60);
    return mins + 'm ' + secs + 's ago';
  }

  window.refreshTickStatusBar = function () {
    var shell = document.querySelector('#tick-status .tick-status-shell');
    if (!shell) return;

    var tickRaw = (shell.getAttribute('data-current-tick') || '').trim();
    var parsedTick = parseInt(tickRaw, 10);
    var hasTick = !isNaN(parsedTick);
    var state = window._tickBarState || (window._tickBarState = { tick: null, observedAtMs: 0, lastPostUtcMs: null, renderPct: null, lastFrameMs: 0 });
    if (hasTick && state.tick !== parsedTick) {
      state.tick = parsedTick;
      state.observedAtMs = Date.now();
    } else if (!hasTick) {
      state.tick = null;
      state.observedAtMs = 0;
    }

    var postRaw = (shell.getAttribute('data-last-post-utc') || '').trim();
    if (postRaw.length > 0) {
      var parsedPost = Date.parse(postRaw);
      state.lastPostUtcMs = isNaN(parsedPost) ? null : parsedPost;
    } else {
      state.lastPostUtcMs = null;
    }

    var fill = shell.querySelector('#tick-status-fill');
    var main = shell.querySelector('.tick-meta-main');
    var post = shell.querySelector('.tick-meta-post');
    if (!fill || !main || !post) return;

    if (state.tick === null || state.observedAtMs <= 0) {
      fill.style.width = '0%';
      main.textContent = 'Next in --';
      post.textContent = 'Last Prayer POST: n/a';
      state.renderPct = null;
      return;
    }

    var tickCycleMs = 10000;
    var nowMs = Date.now();
    var baseMs = state.lastPostUtcMs !== null ? state.lastPostUtcMs : state.observedAtMs;
    var elapsedMs = Math.max(0, nowMs - baseMs);
    var phaseMs = elapsedMs % tickCycleMs;
    var targetPct = phaseMs / tickCycleMs;
    if (!(state.renderPct >= 0 && state.renderPct <= 1)) {
      state.renderPct = targetPct;
    } else {
      var delta = targetPct - state.renderPct;
      if (delta > 0.5) delta -= 1;
      else if (delta < -0.5) delta += 1;
      var dtMs = state.lastFrameMs > 0 ? Math.min(nowMs - state.lastFrameMs, 100) : (1000 / 60);
      var lerpFactor = 1 - Math.pow(1 - 0.18, dtMs / (1000 / 60));
      state.renderPct = (state.renderPct + (delta * lerpFactor) + 1) % 1;
    }
    state.lastFrameMs = nowMs;
    fill.style.width = (state.renderPct * 100).toFixed(1) + '%';

    var toNext = Math.max(0, tickCycleMs - phaseMs);
    main.textContent = 'Next in ' + (toNext / 1000).toFixed(1) + 's';

    if (state.lastPostUtcMs !== null) {
      var ageSec = Math.max(0, (Date.now() - state.lastPostUtcMs) / 1000);
      post.textContent = 'Last Prayer POST: ' + formatAgeSeconds(ageSec);
    } else {
      post.textContent = 'Last Prayer POST: n/a';
    }
  };

  function startTickBarAnimationLoop() {
    if (window._tickBarRaf) return;
    var frame = function () {
      window.refreshTickStatusBar();
      window._tickBarRaf = window.requestAnimationFrame(frame);
    };
    window._tickBarRaf = window.requestAnimationFrame(frame);
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

  window.applyMapSubtabSelection = function () {
    var page = document.querySelector('#state-pane-map .map-page');
    if (!page) return;
    var targetTab = (window._mapSubtabSelection || 'system').toString().trim().toLowerCase();
    if (targetTab !== 'galaxy') targetTab = 'system';

    page.querySelectorAll('.map-subtab-btn').forEach(function (btn) {
      var tab = (btn.getAttribute('data-map-tab') || '').trim().toLowerCase();
      var active = tab === targetTab;
      btn.classList.toggle('active', active);
      btn.setAttribute('aria-selected', active ? 'true' : 'false');
    });
    page.querySelectorAll('.map-subtab-pane').forEach(function (pane) {
      var paneTab = (pane.getAttribute('data-map-pane') || '').trim().toLowerCase();
      pane.classList.toggle('active', paneTab === targetTab);
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

  window._craftingQuery = window._craftingQuery || '';
  window.filterCraftingRecipes = function (query) {
    var next = (typeof query === 'string' ? query : window._craftingQuery || '');
    var q = next.toLowerCase().trim();
    window._craftingQuery = next;

    var pane = document.getElementById('state-pane-crafting');
    if (!pane) return;

    pane.querySelectorAll('.cargo-row').forEach(function (row) {
      var hay = row.getAttribute('data-search') || '';
      row.style.display = q === '' || hay.indexOf(q) >= 0 ? '' : 'none';
    });

    Array.prototype.slice.call(pane.querySelectorAll('details.catalog-group'))
      .reverse()
      .forEach(function (group) {
        var hasVisible = Array.prototype.some.call(
          group.querySelectorAll('.cargo-row'),
          function (row) { return row.style.display !== 'none'; });
        group.style.display = hasVisible ? '' : 'none';
      });

    var input = pane.querySelector("input.catalog-search[oninput*='filterCraftingRecipes']");
    if (input && input.value !== next) input.value = next;
  };

  window.submitCraft = function (_event, form) {
    if (!form) return false;
    var recipeId = (form.getAttribute('data-item-id') || '').trim();
    if (!recipeId) return false;
    var qtyInput = form.querySelector("input[name='qty']");
    var qty = parseInt((qtyInput && qtyInput.value) ? qtyInput.value : '1', 10);
    if (!Number.isFinite(qty) || qty < 1) qty = 1;
    if (qty > 10) qty = 10;
    if (qtyInput) qtyInput.value = String(qty);
    var scriptInput = form.querySelector("input[name='script']");
    if (scriptInput) scriptInput.value = 'craft ' + recipeId + ' ' + qty + ';';
    return true;
  };

  window.initGalaxyMapCanvas = function (canvas) {
    if (!canvas || canvas._mapHandlersAttached) return;
    canvas._mapHandlersAttached = true;

    canvas.addEventListener('mousedown', function (e) {
      if (e.button !== 0) return;
      var state = window._galaxyMapStates.get(canvas);
      if (!state || state.mode !== 'galaxy') return;
      state.dragging = true;
      state.dragMoved = false;
      state.dragStartX = e.clientX;
      state.dragStartY = e.clientY;
      state.dragOriginPanX = (typeof state.panX === 'number') ? state.panX : 0;
      state.dragOriginPanY = (typeof state.panY === 'number') ? state.panY : 0;
      canvas.classList.add('dragging');
    });

    canvas.addEventListener('mousemove', function (e) {
      var state = window._galaxyMapStates.get(canvas);
      if (!state) return;
      var rect = canvas.getBoundingClientRect();
      state.mouseX = e.clientX - rect.left;
      state.mouseY = e.clientY - rect.top;
      if (state.mode === 'galaxy' && state.dragging) {
        if (Math.abs(e.clientX - state.dragStartX) > 3 || Math.abs(e.clientY - state.dragStartY) > 3) {
          state.dragMoved = true;
        }
        state.panX = state.dragOriginPanX + (e.clientX - state.dragStartX);
        state.panY = state.dragOriginPanY + (e.clientY - state.dragStartY);
        window._galaxyMapViewState.panX = state.panX;
        window._galaxyMapViewState.panY = state.panY;
        window._galaxyMapViewState.hasUserPan = true;
      }
      window.drawGalaxyMapCanvas(canvas);
    });

    function stopDrag() {
      var state = window._galaxyMapStates.get(canvas);
      if (!state) return;
      state.dragging = false;
      canvas.classList.remove('dragging');
    }

    canvas.addEventListener('mouseup', stopDrag);
    window.addEventListener('mouseup', stopDrag);

    canvas.addEventListener('mouseleave', function () {
      var state = window._galaxyMapStates.get(canvas);
      if (!state) return;
      state.mouseX = null;
      state.mouseY = null;
      window.drawGalaxyMapCanvas(canvas);
    });

    canvas.addEventListener('wheel', function (e) {
      var state = window._galaxyMapStates.get(canvas);
      if (!state || state.mode !== 'galaxy') return;
      e.preventDefault();

      var rect = canvas.getBoundingClientRect();
      var mx = e.clientX - rect.left;
      var my = e.clientY - rect.top;
      var oldZoom = (typeof state.zoom === 'number' && isFinite(state.zoom)) ? state.zoom : 1;
      var factor = e.deltaY < 0 ? 1.12 : (1 / 1.12);
      var nextZoom = Math.max(0.175, Math.min(4.5, oldZoom * factor));
      if (Math.abs(nextZoom - oldZoom) < 1e-9) return;

      var panX = (typeof state.panX === 'number') ? state.panX : 0;
      var panY = (typeof state.panY === 'number') ? state.panY : 0;
      var cx = state.cssWidth * 0.5;
      var cy = state.cssHeight * 0.5;
      // Zoom around cursor in screen space.
      var ratio = nextZoom / oldZoom;
      state.panX = (mx - cx) * (1 - ratio) + panX * ratio;
      state.panY = (my - cy) * (1 - ratio) + panY * ratio;
      state.zoom = nextZoom;

      window._galaxyMapViewState.panX = state.panX;
      window._galaxyMapViewState.panY = state.panY;
      window._galaxyMapViewState.hasUserPan = true;
      window._galaxyMapViewState.zoom = state.zoom;
      window._galaxyMapViewState.hasUserZoom = true;
      window.drawGalaxyMapCanvas(canvas);
    }, { passive: false });

    canvas.addEventListener('click', function (e) {
      var state = window._galaxyMapStates.get(canvas);
      if (state && state.mode === 'galaxy') {
        if (!state.layout || !Array.isArray(state.layout.systems) || state.layout.systems.length === 0) return;
        if (state.dragMoved) return;
        var rect = canvas.getBoundingClientRect();
        var clickX = e.clientX - rect.left;
        var clickY = e.clientY - rect.top;
        var panX = (typeof state.panX === 'number') ? state.panX : 0;
        var panY = (typeof state.panY === 'number') ? state.panY : 0;
        var zoom = (typeof state.zoom === 'number' && isFinite(state.zoom))
          ? state.zoom
          : 1;
        var cx = state.cssWidth * 0.5;
        var cy = state.cssHeight * 0.5;
        function projectX(x) {
          return cx + ((x - cx) * zoom) + panX;
        }
        function projectY(y) {
          return cy - ((y - cy) * zoom) + panY;
        }

        var nearestSystem = null;
        var nearestSystemDist = Number.POSITIVE_INFINITY;
        state.layout.systems.forEach(function (s) {
          if (!s || !s.id || !s.point) return;
          var sx = projectX(s.point.x);
          var sy = projectY(s.point.y);
          var dx = clickX - sx;
          var dy = clickY - sy;
          var d = Math.sqrt(dx * dx + dy * dy);
          if (d < nearestSystemDist) {
            nearestSystem = s;
            nearestSystemDist = d;
          }
        });
        if (nearestSystem && nearestSystemDist <= 18) {
          window.issueControlScriptAndExecute('go ' + nearestSystem.id + ';');
        }
        return;
      }
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
    function drawMapHud(title, modeLabel) {
      if (!title) return;
      var x = 12;
      var y = 10;
      var padX = 10;
      var padY = 7;

      ctx.save();
      ctx.font = '700 12px ui-monospace, SFMono-Regular, Menlo, monospace';
      var titleW = ctx.measureText(title).width;
      ctx.font = '10px ui-monospace, SFMono-Regular, Menlo, monospace';
      var modeW = ctx.measureText(modeLabel).width;
      var w = Math.max(titleW, modeW) + padX * 2;
      var h = 34;

      var panel = ctx.createLinearGradient(x, y, x, y + h);
      panel.addColorStop(0, 'rgba(10, 18, 30, 0.86)');
      panel.addColorStop(1, 'rgba(8, 13, 22, 0.72)');
      ctx.fillStyle = panel;
      ctx.strokeStyle = 'rgba(120, 170, 255, 0.46)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.roundRect(x, y, w, h, 7);
      ctx.fill();
      ctx.stroke();

      ctx.fillStyle = 'rgba(137, 184, 255, 0.92)';
      ctx.fillRect(x + 1, y + 1, 3, h - 2);
      ctx.fillStyle = '#e4efff';
      ctx.font = '700 12px ui-monospace, SFMono-Regular, Menlo, monospace';
      ctx.fillText(title, x + padX, y + 14);
      ctx.fillStyle = 'rgba(170, 198, 238, 0.92)';
      ctx.font = '10px ui-monospace, SFMono-Regular, Menlo, monospace';
      ctx.fillText(modeLabel, x + padX, y + 27);
      ctx.restore();
    }

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

    if (state.mode === 'galaxy') {
      var panX = (typeof state.panX === 'number') ? state.panX : 0;
      var panY = (typeof state.panY === 'number') ? state.panY : 0;
      var zoom = (typeof state.zoom === 'number' && isFinite(state.zoom)) ? state.zoom : 1;
      var cx = cssWidth * 0.5;
      var cy = cssHeight * 0.5;
      function zx(x) { return cx + ((x - cx) * zoom); }
function zy(y) { return cy - ((y - cy) * zoom); }
      if (state.layout.lines && state.layout.lines.length > 0) {
        ctx.strokeStyle = 'rgba(122, 176, 248, 0.28)';
        ctx.lineWidth = 1;
        state.layout.lines.forEach(function (line) {
          var ax = zx(line.a.x) + panX;
          var ay = zy(line.a.y) + panY;
          var bx = zx(line.b.x) + panX;
          var by = zy(line.b.y) + panY;
          ctx.beginPath();
          ctx.moveTo(ax, ay);
          ctx.lineTo(bx, by);
          ctx.stroke();
        });
      }

      var gx = typeof state.mouseX === 'number' ? state.mouseX : null;
      var gy = typeof state.mouseY === 'number' ? state.mouseY : null;
      var hoverSystem = null;
      if (gx !== null && gy !== null && state.layout.systems && state.layout.systems.length > 0) {
        var nearestSystem = null;
        var nearestSystemDist = Number.POSITIVE_INFINITY;
        state.layout.systems.forEach(function (s) {
          var sx = zx(s.point.x) + panX;
          var sy = zy(s.point.y) + panY;
          var dx = gx - sx;
          var dy = gy - sy;
          var d = Math.sqrt(dx * dx + dy * dy);
          if (d < nearestSystemDist) {
            nearestSystem = { id: s.id, x: sx, y: sy };
            nearestSystemDist = d;
          }
        });
        if (nearestSystem && nearestSystemDist <= 18) hoverSystem = nearestSystem;
      }

      if (state.layout.systems && state.layout.systems.length > 0) {
        function colorForSystem(system) {
          if (system && (system.isStronghold || system.IsStronghold)) {
            return { core: '#ff5252', glow: '255,82,82' };
          }
          var empireRaw = system ? system.empire : '';
          var empire = (empireRaw || '').toString().trim().toLowerCase();
          if (empire === 'voidborn' || empire === 'voidborns') return { core: '#b58dff', glow: '181,141,255' };
          if (empire === 'solarian' || empire === 'solarians') return { core: '#ffd95a', glow: '255,217,90' };
          if (empire === 'crimson' || empire === 'crimsons') return { core: '#ff6767', glow: '255,103,103' };
          if (empire === 'nebula' || empire === 'nebulas') return { core: '#63b8ff', glow: '99,184,255' };
          if (empire === 'outerrim' || empire === 'outerrims') return { core: '#8be47a', glow: '139,228,122' };
          return { core: '#6f7f96', glow: '111,127,150' };
        }
        var landmarkSystems = ['sol', 'krynn', 'haven', 'frontier', 'nexus_prime'];
        state.layout.systems.forEach(function (s) {
          var sx = zx(s.point.x) + panX;
          var sy = zy(s.point.y) + panY;
          var tint = colorForSystem(s);
          var isLandmark = landmarkSystems.indexOf((s.id || '').trim().toLowerCase()) !== -1;
          var r = (s.isCurrent ? 4.6 : isLandmark ? 5.2 : 3.3) * Math.max(0.7, Math.min(1.8, zoom));
          var glowR = (s.isCurrent ? 15 : isLandmark ? 18 : 10) * Math.max(0.7, Math.min(1.8, zoom));
          var glow = ctx.createRadialGradient(sx, sy, 0, sx, sy, glowR);
          glow.addColorStop(0, 'rgba(' + tint.glow + ',' + (s.isCurrent ? '0.40' : '0.26') + ')');
          glow.addColorStop(1, 'rgba(' + tint.glow + ',0)');
          ctx.fillStyle = glow;
          ctx.beginPath();
          ctx.arc(sx, sy, glowR, 0, Math.PI * 2);
          ctx.fill();

          ctx.fillStyle = tint.core;
          ctx.beginPath();
          ctx.arc(sx, sy, r, 0, Math.PI * 2);
          ctx.fill();

          if (s.isCurrent) {
            ctx.strokeStyle = 'rgba(126, 230, 158, 0.95)';
            ctx.lineWidth = 1.4;
            ctx.beginPath();
            ctx.arc(sx, sy, r + 3, 0, Math.PI * 2);
            ctx.stroke();
          }
        });
      }

      if (hoverSystem) {
        ctx.fillStyle = 'rgba(226, 238, 255, 0.95)';
        ctx.font = '11px ui-monospace, SFMono-Regular, Menlo, monospace';
        ctx.fillText(hoverSystem.id, hoverSystem.x + 9, hoverSystem.y - 9);
      }

      // Fixed reference marker at galactic origin.
      var origin = state.layout.originPoint || { x: cssWidth * 0.5, y: cssHeight * 0.5 };
      ctx.strokeStyle = 'rgba(103, 180, 255, 0.6)';
      ctx.lineWidth = 1.2;
      ctx.beginPath();
      ctx.arc(zx(origin.x) + panX, zy(origin.y) + panY, 7 * Math.max(0.7, Math.min(1.6, zoom)), 0, Math.PI * 2);
      ctx.stroke();

      drawMapHud(state.currentId || 'Unknown', 'GALAXY MAP');
      return;
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
      var hoveredPoiId = null;
      if (hoverX !== null && hoverY !== null) {
        var nearestPoi = null;
        var nearestPoiDist = Number.POSITIVE_INFINITY;
        state.layout.pois.forEach(function (poi) {
          var p = poi.point;
          var baseRadius = poi.isStar ? 8.5 : (poi.isPlanet ? 5.5 : 4.5);
          var dxh = hoverX - p.x;
          var dyh = hoverY - p.y;
          var hoverDist = Math.sqrt(dxh * dxh + dyh * dyh);
          var threshold = Math.max(24, baseRadius * 4.2);
          if (hoverDist <= threshold && hoverDist < nearestPoiDist) {
            nearestPoi = poi;
            nearestPoiDist = hoverDist;
          }
        });
        hoveredPoiId = nearestPoi ? nearestPoi.id : null;
      }
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

        var showLabel = hoveredPoiId !== null && poi.id === hoveredPoiId;

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
    drawMapHud(state.currentId || 'Unknown', 'SYSTEM MAP');
  };

  window.renderGalaxyMapCanvases = function () {
    var canvases = document.querySelectorAll('.galaxy-map-canvas');
    canvases.forEach(function (canvas) {
      window.initGalaxyMapCanvas(canvas);
      if (canvas.offsetParent === null) return;

      var payload = canvas.getAttribute('data-map');
      if (!payload) return;

      var map;
      try { map = JSON.parse(payload); } catch (_) { return; }

      function getX(v) {
        if (!v) return null;
        if (v.X != null) return v.X;
        if (v.x != null) return v.x;
        var pos = v.Position || v.position;
        if (pos && pos.X != null) return pos.X;
        if (pos && pos.x != null) return pos.x;
        return null;
      }
      function getY(v) {
        if (!v) return null;
        if (v.Y != null) return v.Y;
        if (v.y != null) return v.y;
        var pos = v.Position || v.position;
        if (pos && pos.Y != null) return pos.Y;
        if (pos && pos.y != null) return pos.y;
        return null;
      }
      function getSystemId(s) {
        if (!s) return '';
        return ((s.Id || s.id || s.system_id || s.SystemId || s.systemId) || '').toString().trim();
      }
      function getPoiId(p) {
        if (!p) return '';
        return ((p.Id || p.id || p.poi_id || p.PoiId || p.poiId) || '').toString().trim();
      }
      function isFiniteCoord(x, y) {
        return typeof x === 'number' && typeof y === 'number' && isFinite(x) && isFinite(y);
      }

      var systems = ((map && (map.Systems || map.systems)) || []).filter(function (s) {
        var id = getSystemId(s);
        return id.length > 0;
      });
      var currentId = ((map && (map.CurrentSystem || map.currentSystem)) || '').trim();
      var currentPoiId = ((map && (map.CurrentPoi || map.currentPoi)) || '').trim();
      var pois = ((map && (map.Pois || map.pois)) || []).filter(function (p) {
        return getPoiId(p).length > 0;
      });

      var ctx = canvas.getContext('2d');
      if (!ctx) return;

      var dpr = window.devicePixelRatio || 1;
      var cssWidth = canvas.clientWidth || 800;
      var cssHeight = canvas.clientHeight || 480;
      canvas.width = Math.max(1, Math.floor(cssWidth * dpr));
      canvas.height = Math.max(1, Math.floor(cssHeight * dpr));
      var mode = (canvas.getAttribute('data-map-mode') || 'system').toLowerCase().trim();
      var centerX = cssWidth * 0.5;
      var centerY = cssHeight * 0.5;
      var existing = window._galaxyMapStates.get(canvas);

      if (mode !== 'galaxy' && systems.length === 0) {
        var fallbackCurrentId = currentId || 'current';
        systems = [{ id: fallbackCurrentId, x: 0, y: 0, connections: [] }];
        currentId = fallbackCurrentId;
      }

      var seed = 2166136261 >>> 0;
      for (var si = 0; si < payload.length; si++) {
        seed ^= payload.charCodeAt(si);
        seed = Math.imul(seed, 16777619) >>> 0;
      }
      seed ^= (mode === 'galaxy' ? 173 : 97);
      function nextRand() {
        seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
        return (seed >>> 0) / 4294967295;
      }
      var stars = [];
      var starCount = mode === 'galaxy' ? 250 : 190;
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

      if (mode === 'galaxy') {
        var galaxySystems = systems;
        if (galaxySystems.length === 0) return;

        var coords = galaxySystems
          .map(function (s) {
            var x = getX(s);
            var y = getY(s);
            return isFiniteCoord(x, y) ? { id: getSystemId(s), x: x, y: y } : null;
          })
          .filter(function (s) { return !!s; });

        var minX = 0;
        var maxX = 0;
        var minY = 0;
        var maxY = 0;
        coords.forEach(function (s) {
          minX = Math.min(minX, s.x);
          maxX = Math.max(maxX, s.x);
          minY = Math.min(minY, s.y);
          maxY = Math.max(maxY, s.y);
        });

        var halfRangeX = Math.max(8, Math.max(Math.abs(minX), Math.abs(maxX)));
        var halfRangeY = Math.max(8, Math.max(Math.abs(minY), Math.abs(maxY)));
        var padding = 34;
        var availableW = Math.max(1, cssWidth - padding * 2);
        var availableH = Math.max(1, cssHeight - padding * 2);

        // Keep a fixed galaxy fit once first computed so the view stays stable.
        var galaxyFit = existing && existing.galaxyFit
          ? existing.galaxyFit
          : { scale: Math.max(0.2, Math.min(availableW / (halfRangeX * 2), availableH / (halfRangeY * 2))) };
        var galaxyScale = galaxyFit.scale;

        var byId = {};
        var layoutSystems = galaxySystems.map(function (s) {
          var id = getSystemId(s);
          var x = getX(s);
          var y = getY(s);
          var px = centerX;
          var py = centerY;
          if (isFiniteCoord(x, y)) {
            px = centerX + x * galaxyScale;
            py = centerY - y * galaxyScale;
          }
          var entry = {
            id: id,
            empire: ((s.Empire || s.empire) || '').toString(),
            isStronghold: !!(s.IsStronghold || s.isStronghold || s.is_stronghold),
            point: { x: px, y: py },
            isCurrent: id === currentId,
            connections: ((s.Connections || s.connections) || [])
              .filter(function (cid) { return typeof cid === 'string' && cid.trim().length > 0; })
              .map(function (cid) { return cid.trim(); })
          };
          byId[id] = entry;
          return entry;
        });

        var lines = [];
        var seen = {};
        layoutSystems.forEach(function (s) {
          s.connections.forEach(function (cid) {
            var t = byId[cid];
            if (!t) return;
            var key = s.id < cid ? (s.id + '|' + cid) : (cid + '|' + s.id);
            if (seen[key]) return;
            seen[key] = true;
            lines.push({ a: s.point, b: t.point });
          });
        });

        var currentLayoutSystem = layoutSystems.find(function (s) { return !!s && !!s.isCurrent; }) || null;
        var defaultPanX = currentLayoutSystem ? (cssWidth * 0.5) - currentLayoutSystem.point.x : 0;
        var defaultPanY = currentLayoutSystem ? (cssHeight * 0.5) - currentLayoutSystem.point.y : 0;

        window._galaxyMapStates.set(canvas, {
          ctx: ctx,
          dpr: dpr,
          cssWidth: cssWidth,
          cssHeight: cssHeight,
          mode: 'galaxy',
          layout: {
            stars: stars,
            systems: layoutSystems,
            lines: lines,
            originPoint: { x: centerX, y: centerY }
          },
          mouseX: existing ? existing.mouseX : null,
          mouseY: existing ? existing.mouseY : null,
          panX: (existing && typeof existing.panX === 'number')
            ? existing.panX
            : (window._galaxyMapViewState && window._galaxyMapViewState.hasUserPan && typeof window._galaxyMapViewState.panX === 'number'
              ? window._galaxyMapViewState.panX
              : defaultPanX),
          panY: (existing && typeof existing.panY === 'number')
            ? existing.panY
            : (window._galaxyMapViewState && window._galaxyMapViewState.hasUserPan && typeof window._galaxyMapViewState.panY === 'number'
              ? window._galaxyMapViewState.panY
              : defaultPanY),
          zoom: (existing && typeof existing.zoom === 'number' && isFinite(existing.zoom))
            ? existing.zoom
            : (window._galaxyMapViewState && window._galaxyMapViewState.hasUserZoom && typeof window._galaxyMapViewState.zoom === 'number'
              ? window._galaxyMapViewState.zoom
              : 4.5),
          dragging: existing ? !!existing.dragging : false,
          dragMoved: existing ? !!existing.dragMoved : false,
          dragStartX: existing ? existing.dragStartX : 0,
          dragStartY: existing ? existing.dragStartY : 0,
          dragOriginPanX: existing ? existing.dragOriginPanX : 0,
          dragOriginPanY: existing ? existing.dragOriginPanY : 0,
          currentId: currentId,
          payload: payload,
          galaxyFit: galaxyFit
        });
        window.drawGalaxyMapCanvas(canvas);
        return;
      }

      var currentSystem = systems.find(function (s) {
        return getSystemId(s) === currentId;
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
        var id = getPoiId(p).toLowerCase();
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
        var id = getPoiId(p);
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

      var currentConnections = ((currentSystem && (currentSystem.Connections || currentSystem.connections)) || [])
        .filter(function (cidRaw) { return typeof cidRaw === 'string' && cidRaw.trim().length > 0; })
        .map(function (cidRaw) { return cidRaw.trim(); });

      function hashAngle(text) {
        var h = 0;
        for (var i = 0; i < text.length; i++) h = ((h << 5) - h) + text.charCodeAt(i);
        return ((Math.abs(h) % 360) / 180) * Math.PI;
      }

      var connectionRays = currentConnections.map(function (cid) {
        var t = systems.find(function (s) { return getSystemId(s) === cid; }) || null;
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
        var id = getPoiId(p);
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

      var poiOrbits = layoutPois
        .map(function (poi) {
          var dx = poi.point.x - centerX;
          var dy = poi.point.y - centerY;
          return { radius: Math.sqrt(dx * dx + dy * dy), id: poi.id };
        })
        .filter(function (o) { return o.radius > 2.5; })
        .sort(function (a, b) { return a.radius - b.radius; });

      var nextState = {
        ctx: ctx,
        dpr: dpr,
        cssWidth: cssWidth,
        cssHeight: cssHeight,
        mode: 'system',
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
        document.querySelectorAll('.galaxy-map-canvas').forEach(function (canvas) {
          var existingPayload = canvas.getAttribute('data-map') || '';
          if (existingPayload !== payload) {
            canvas.setAttribute('data-map', payload);
          }
        });
        window.renderGalaxyMapCanvases();
      })
      .catch(function () { });
  };

  window.centerGalaxyMapOnCurrent = function (canvas) {
    if (!canvas) return;
    var state = window._galaxyMapStates.get(canvas);
    if (!state || state.mode !== 'galaxy' || !state.layout || !Array.isArray(state.layout.systems)) return;
    var current = state.layout.systems.find(function (s) { return !!s && !!s.isCurrent; }) || null;
    if (!current) {
      state.panX = 0;
      state.panY = 0;
      state.zoom = 1;
      window._galaxyMapViewState.panX = state.panX;
      window._galaxyMapViewState.panY = state.panY;
      window._galaxyMapViewState.hasUserPan = true;
      window._galaxyMapViewState.zoom = state.zoom;
      window._galaxyMapViewState.hasUserZoom = true;
      window.drawGalaxyMapCanvas(canvas);
      return;
    }
    state.panX = (state.cssWidth * 0.5) - current.point.x;
    state.panY = (state.cssHeight * 0.5) - current.point.y;
    window._galaxyMapViewState.panX = state.panX;
    window._galaxyMapViewState.panY = state.panY;
    window._galaxyMapViewState.hasUserPan = true;
    window._galaxyMapViewState.zoom = state.zoom;
    window._galaxyMapViewState.hasUserZoom = true;
    window.drawGalaxyMapCanvas(canvas);
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

    if (tab === 'map') {
      window.applyMapSubtabSelection();
      window.renderGalaxyMapCanvases();
    }

    if (focusAfter) tabBtn.focus();
  }

  window.openStateTab = function (tabId) {
    var panel = document.getElementById('state-panel');
    if (!panel) return;
    var normalized = (tabId || '').toString().trim().toLowerCase();
    if (!normalized) return;
    var btn = panel.querySelector("[role='tab'].tab-btn[data-tab='" + normalized + "']");
    if (!btn) return;
    activateStateTab(btn, false);
  };

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
    var mapTabBtn = e.target.closest('.map-subtab-btn');
    if (mapTabBtn) {
      var page = mapTabBtn.closest('.map-page');
      if (!page) return;
      var targetTab = (mapTabBtn.getAttribute('data-map-tab') || '').trim();
      if (!targetTab) return;
      window._mapSubtabSelection = targetTab.toLowerCase();

      page.querySelectorAll('.map-subtab-btn').forEach(function (btn) {
        var active = btn === mapTabBtn;
        btn.classList.toggle('active', active);
        btn.setAttribute('aria-selected', active ? 'true' : 'false');
      });
      page.querySelectorAll('.map-subtab-pane').forEach(function (pane) {
        var paneTab = (pane.getAttribute('data-map-pane') || '').trim();
        pane.classList.toggle('active', paneTab === targetTab);
      });
      window.renderGalaxyMapCanvases();
      return;
    }

    var mapActionBtn = e.target.closest(".map-center-btn[data-map-action='center-current']");
    if (mapActionBtn) {
      var page = mapActionBtn.closest('.map-page');
      if (!page) return;
      var canvas = page.querySelector(".map-subtab-pane.active .galaxy-map-canvas[data-map-mode='galaxy']");
      if (canvas) window.centerGalaxyMapOnCurrent(canvas);
      return;
    }

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
      if (elt.id === 'state-pane-map' && elt.classList.contains('active')) {
        var mapPane = elt;
        var mapCanvases = mapPane.querySelectorAll('.galaxy-map-canvas');
        var isDraggingMap = Array.prototype.some.call(mapCanvases, function (canvas) {
          var state = window._galaxyMapStates.get(canvas);
          return !!(state && state.dragging);
        });
        if (isDraggingMap) {
          // Avoid replacing the map DOM while user is actively dragging the map.
          e.preventDefault();
          return;
        }
      }
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

  document.body.addEventListener('htmx:beforeSwap', function (e) {
    var detail = (e || {}).detail || {};
    var elt = detail.elt || null;
    if (elt &&
      elt.id === 'state-pane-map' &&
      elt.classList &&
      elt.classList.contains('active')) {
      var mapCanvases = elt.querySelectorAll('.galaxy-map-canvas');
      var isDraggingMap = Array.prototype.some.call(mapCanvases, function (canvas) {
        var state = window._galaxyMapStates.get(canvas);
        return !!(state && state.dragging);
      });
      if (isDraggingMap) {
        // Request was already in-flight when drag started; suppress the swap.
        detail.shouldSwap = false;
        return;
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
    window.filterCraftingRecipes(window._craftingQuery || '');
    window.applyMapSubtabSelection();
    window.renderGalaxyMapCanvases();
    window.refreshTickStatusBar();
    window.ensureScriptEditor();
    refreshEditors();
  });

  window.applyMapSubtabSelection();
  window.renderGalaxyMapCanvases();
  window.refreshTickStatusBar();
  startTickBarAnimationLoop();
  window.ensureScriptEditor();
}());
