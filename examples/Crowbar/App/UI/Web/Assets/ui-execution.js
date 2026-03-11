
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
    htmx.ajax('POST', apiUrl('api/execute'), { swap: 'none', values: { bot_id: window._activeBotId || '' } });
    // Force a quick refresh in addition to the 1s poll loop.
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

  window.syncCurrentScript = function () {
    var perfStart = window._uiPerf ? window._uiPerf.begin() : 0;
    var botId = window._activeBotId;
    var url = apiUrl('partial/current-script') + (botId ? '?bot_id=' + encodeURIComponent(botId) : '');
    fetch(url, { cache: 'no-store' })
      .then(function (res) { return res.ok ? res.json() : null; })
      .then(function (state) {
        try {
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
        } finally {
          if (window._uiPerf) window._uiPerf.end('syncCurrentScript', perfStart);
        }
      })
      .catch(function () {
        if (window._uiPerf) window._uiPerf.end('syncCurrentScript', perfStart);
      });
  };
