
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
    if (!window._liveScriptSyncStarted) {
      window._liveScriptSyncStarted = true;
      window._currentScriptIdlePollMs = 1000;
      window.syncCurrentScript();
    }
  };
