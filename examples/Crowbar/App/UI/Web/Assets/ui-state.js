
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

  document.body.addEventListener('htmx:afterRequest', function (e) {
    var detail = (e || {}).detail || {};
    var path = (detail.pathInfo || {}).requestPath || '';
    var perf = window._uiPerf;
    if (perf) {
      var reqCfg = detail.requestConfig || {};
      var startedAt = reqCfg._perfStartedAt;
      if (typeof startedAt === 'number' && isFinite(startedAt)) {
        perf.end('htmxRequest', startedAt);
      }
    }
    if (path.endsWith('/api/add-bot') || path.endsWith('/api/llm-select') || path === 'api/add-bot' || path === 'api/llm-select') {
      window.closeAllSidebarLayers();
    }
  });

  document.body.addEventListener('htmx:beforeRequest', function (e) {
    var detail = (e || {}).detail || {};
    var path = (detail.pathInfo || {}).requestPath || '';
    var elt = detail.elt || null;
    if (window._uiPerf) {
      detail.requestConfig = detail.requestConfig || {};
      detail.requestConfig._perfStartedAt = window._uiPerf.begin();
    }

    if ((path.indexOf('partial/state') >= 0) && elt && elt.classList && elt.classList.contains('tab-pane')) {
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
      if (active && active.tagName === 'INPUT' && active.classList && active.classList.contains('catalog-search') && elt.contains(active)) {
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

    if (path.endsWith('/api/execute') || path.endsWith('/api/control-input') ||
        path === 'api/execute' || path === 'api/control-input') {
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
    if (elt && elt.id === 'state-pane-map' && elt.classList && elt.classList.contains('active')) {
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
    if (elt && elt.id === 'state-tabs') {
      var panel = document.getElementById('state-panel');
      if (panel) {
        var activePane = panel.querySelector('.tab-pane.active');
        var activeTab = activePane ? (activePane.id || '').replace(/^state-pane-/, '') : '';
        var activeBtn = activeTab
          ? elt.querySelector("[role='tab'].tab-btn[data-tab='" + activeTab + "']")
          : null;
        var targetBtn = activeBtn ||
          elt.querySelector("[role='tab'].tab-btn[data-tab='map']") ||
          elt.querySelector("[role='tab'].tab-btn");
        if (targetBtn) window._activateStateTab(targetBtn, false);
      }
    }
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

  // Client-side bot selection. window._activeBotId is seeded from the server on initial load.
  window.selectBot = function (botId) {
    window._activeBotId = botId || null;
    // Re-poll all bot-scoped partials immediately; the server renders active state via bot_id query param.
    document.querySelectorAll('.tab-pane.active[hx-get]').forEach(function (pane) {
      htmx.trigger(pane, 'load');
    });
    ['bots-panel', 'state-tabs', 'state-strip-inline', 'right-panel', 'tick-status'].forEach(function (id) {
      var el = document.getElementById(id);
      if (el) htmx.trigger(el, 'load');
    });
  };

  // Inject bot_id into every HTMX request so the server always knows which bot to target.
  document.body.addEventListener('htmx:configRequest', function (e) {
    var botId = window._activeBotId;
    if (!botId) return;
    var params = ((e || {}).detail || {}).parameters;
    if (params && typeof params === 'object') {
      params['bot_id'] = botId;
    }
  });
