
  function formatAgeSeconds(totalSeconds) {
    if (!(totalSeconds >= 0)) return 'n/a';
    if (totalSeconds < 60) return Math.floor(totalSeconds) + 's ago';
    var mins = Math.floor(totalSeconds / 60);
    var secs = Math.floor(totalSeconds % 60);
    return mins + 'm ' + secs + 's ago';
  }

  window.refreshTickStatusBar = function () {
    var perfStart = window._uiPerf ? window._uiPerf.begin() : 0;
    var shell = document.querySelector('#tick-status .tick-status-shell');
    if (!shell) {
      if (window._uiPerf) window._uiPerf.end('refreshTickStatusBar', perfStart);
      return;
    }

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
    if (!fill || !main || !post) {
      if (window._uiPerf) window._uiPerf.end('refreshTickStatusBar', perfStart);
      return;
    }

    if (state.tick === null || state.observedAtMs <= 0) {
      fill.style.width = '0%';
      main.textContent = 'Next in --';
      post.textContent = 'Last Prayer POST: n/a';
      state.renderPct = null;
      if (window._uiPerf) window._uiPerf.end('refreshTickStatusBar', perfStart);
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
    if (window._uiPerf) window._uiPerf.end('refreshTickStatusBar', perfStart);
  };

  function startTickBarAnimationLoop() {
    if (window._tickBarRaf) return;
    var frame = function () {
      window.refreshTickStatusBar();
      window._tickBarRaf = window.requestAnimationFrame(frame);
    };
    window._tickBarRaf = window.requestAnimationFrame(frame);
  }
