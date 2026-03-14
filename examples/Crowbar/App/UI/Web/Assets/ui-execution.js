
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

  function refreshStateUiNow() {
    ['state-tabs', 'state-strip-inline', 'right-panel', 'tick-status'].forEach(function (id) {
      var el = document.getElementById(id);
      if (el) htmx.trigger(el, 'load');
    });
    if (window.pollStatePanes) window.pollStatePanes();
  }

  function scheduleStateUiRefresh(delayMs) {
    window.setTimeout(refreshStateUiNow, delayMs);
  }

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
    htmx.ajax('POST', apiUrl('api/execute'), { swap: 'none', values: { bot_id: window._activeBotId || '' } });
    // Force a quick refresh in addition to the 1s poll loop.
    scheduleStateUiRefresh(120);
    scheduleStateUiRefresh(450);
    scheduleStateUiRefresh(1000);
    setTimeout(window.syncCurrentScript, 120);
    setTimeout(window.syncCurrentScript, 450);
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
    if (window._activeBotId) body.set('bot_id', window._activeBotId);

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
        htmx.ajax('POST', apiUrl('api/execute'), { swap: 'none', values: { bot_id: window._activeBotId || '' } });
        scheduleStateUiRefresh(120);
        scheduleStateUiRefresh(450);
        scheduleStateUiRefresh(1000);
        setTimeout(window.syncCurrentScript, 120);
        setTimeout(window.syncCurrentScript, 450);
      })
      .catch(function () { });
  };

  window.confirmSelfDestruct = function () {
    var layer = document.getElementById('self-destruct-confirm');
    if (layer) layer.classList.add('open');
  };

  window.closeSelfDestructConfirm = function () {
    var layer = document.getElementById('self-destruct-confirm');
    if (layer) layer.classList.remove('open');
  };

  window.executeSelfDestruct = function () {
    window.closeSelfDestructConfirm();
    window.issueControlScriptAndExecute('self_destruct;');
  };

  function isScriptPaneVisible() {
    var pane = document.getElementById('right-pane-script');
    return !!(pane && pane.classList && pane.classList.contains('active') && !pane.hasAttribute('hidden'));
  }

  function scheduleCurrentScriptSync(delayMs) {
    var delay = typeof delayMs === 'number' && delayMs >= 0 ? delayMs : 1000;
    if (window._liveScriptSyncTimer) {
      clearTimeout(window._liveScriptSyncTimer);
    }
    window._liveScriptSyncTimer = window.setTimeout(function () {
      window._liveScriptSyncTimer = null;
      window.syncCurrentScript();
    }, delay);
  }

  window.syncCurrentScript = function () {
    if (!window._liveScriptEditor) return;
    if (document.hidden || !isScriptPaneVisible()) {
      scheduleCurrentScriptSync(1500);
      return;
    }
    if (window._syncCurrentScriptInFlight) {
      window._syncCurrentScriptPending = true;
      return;
    }

    var perfStart = window._uiPerf ? window._uiPerf.begin() : 0;
    var botId = window._activeBotId;
    var url = apiUrl('partial/current-script') + (botId ? '?bot_id=' + encodeURIComponent(botId) : '');
    window._syncCurrentScriptInFlight = true;
    fetch(url, { cache: 'no-store' })
      .then(function (res) { return res.ok ? res.json() : null; })
      .then(function (state) {
        try {
          if (!state || !window._liveScriptEditor) return;
          var text = typeof state.script === 'string' ? state.script : '';
          var current = window._liveScriptEditor.getValue();
          var textChanged = current !== text;
          if (textChanged) window._liveScriptEditor.setValue(text);
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
          var lineChanged = window._liveScriptRunLineNumber !== nextLine;
          if (lineChanged) window.setLiveScriptRunLine(nextLine);
          var isActive = nextLine !== null;
          window._currentScriptIdlePollMs = (textChanged || lineChanged || isActive) ? 250 : 1500;
          if (window._uiPerf) {
            window._uiPerf.setGauge('current_script_poll_ms', window._currentScriptIdlePollMs);
            window._uiPerf.setGauge('current_script_active', isActive ? '1' : '0');
            window._uiPerf.count('current_script_syncs');
          }
        } finally {
          window._syncCurrentScriptInFlight = false;
          if (window._uiPerf) window._uiPerf.end('syncCurrentScript', perfStart);
          if (window._syncCurrentScriptPending) {
            window._syncCurrentScriptPending = false;
            scheduleCurrentScriptSync(0);
          } else {
            scheduleCurrentScriptSync(window._currentScriptIdlePollMs || 1000);
          }
        }
      })
      .catch(function () {
        window._syncCurrentScriptInFlight = false;
        if (window._uiPerf) window._uiPerf.end('syncCurrentScript', perfStart);
        scheduleCurrentScriptSync(1500);
      });
  };
