  window._galaxyMapResourceFilterState = window._galaxyMapResourceFilterState || { selected: {} };

  function getSelectedResourceLookup() {
    var state = window._galaxyMapResourceFilterState || { selected: {} };
    var selected = state.selected || {};
    var lookup = {};
    Object.keys(selected).forEach(function (resourceId) {
      if (!selected[resourceId]) return;
      lookup[resourceId.toLowerCase()] = true;
    });
    return lookup;
  }

  window.bindGalaxyMapResourceFilters = function () {
    var selectedLookup = getSelectedResourceLookup();
    document.querySelectorAll('[data-map-resource-checkbox]').forEach(function (checkbox) {
      var rid = (checkbox.getAttribute('data-map-resource-checkbox') || checkbox.value || '').toString().trim();
      if (!rid) return;
      checkbox.checked = !!selectedLookup[rid.toLowerCase()];
    });

    if (window._mapResourceFilterHandlerAttached) return;
    window._mapResourceFilterHandlerAttached = true;
    document.addEventListener('change', function (e) {
      var target = e.target;
      if (!target || !target.matches || !target.matches('[data-map-resource-checkbox]')) return;
      var rid = (target.getAttribute('data-map-resource-checkbox') || target.value || '').toString().trim();
      if (!rid) return;
      var ridKey = rid.toLowerCase();

      window._galaxyMapResourceFilterState = window._galaxyMapResourceFilterState || { selected: {} };
      var selected = window._galaxyMapResourceFilterState.selected || {};
      selected[ridKey] = !!target.checked;
      window._galaxyMapResourceFilterState.selected = selected;

      // Keep duplicate resource checkboxes in sync if multiple map fragments are on-screen.
      document.querySelectorAll('[data-map-resource-checkbox]').forEach(function (checkbox) {
        var otherRid = (checkbox.getAttribute('data-map-resource-checkbox') || checkbox.value || '').toString().trim();
        if (otherRid.toLowerCase() === ridKey) checkbox.checked = !!target.checked;
      });
      window.renderGalaxyMapCanvases();
    });
  };

  window.initGalaxyMapCanvas = function (canvas) {
    if (!canvas || canvas._mapHandlersAttached) return;
    canvas._mapHandlersAttached = true;

    if (!window._mapDragReleaseHandlerAttached) {
      window._mapDragReleaseHandlerAttached = true;
      window.addEventListener('mouseup', function () {
        document.querySelectorAll('.galaxy-map-canvas').forEach(function (activeCanvas) {
          var activeState = window._galaxyMapStates.get(activeCanvas);
          if (!activeState || !activeState.dragging) return;
          activeState.dragging = false;
          activeCanvas.classList.remove('dragging');
        });
      });
    }

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
        var zoom = (typeof state.zoom === 'number' && isFinite(state.zoom)) ? state.zoom : 1;
        var cx = state.cssWidth * 0.5;
        var cy = state.cssHeight * 0.5;
        function projectX(x) { return cx + ((x - cx) * zoom) + panX; }
        function projectY(y) { return cy - ((y - cy) * zoom) + panY; }

        var nearestSystem = null;
        var nearestSystemDist = Number.POSITIVE_INFINITY;
        state.layout.systems.forEach(function (s) {
          if (!s || !s.id || !s.point) return;
          var sx = projectX(s.point.x);
          var sy = projectY(s.point.y);
          var dx = clickX - sx;
          var dy = clickY - sy;
          var d = Math.sqrt(dx * dx + dy * dy);
          if (d < nearestSystemDist) { nearestSystem = s; nearestSystemDist = d; }
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
        if (d < nearestDist) { nearest = poi; nearestDist = d; }
      });

      if (!nearest) return;
      var hitRadius = nearest.isStar ? 18 : (nearest.isPlanet ? 14 : 12);
      if (nearestDist > hitRadius) return;

      window.issueControlScriptAndExecute('go ' + nearest.id + ';');
    });
  };

  window.drawGalaxyMapCanvas = function (canvas) {
    var perfStart = window._uiPerf ? window._uiPerf.begin() : 0;
    var state = window._galaxyMapStates.get(canvas);
    if (!state || !state.layout) {
      if (window._uiPerf) window._uiPerf.end('drawGalaxyMapCanvas', perfStart);
      return;
    }

    try {
      var ctx = state.ctx;
      var cssWidth = state.cssWidth;
      var cssHeight = state.cssHeight;
      var driftT = (state.driftT || 0);

    function drawMapHud(title, modeLabel) {
      if (!title) return;
      var x = 12;
      var y = 10;
      var padX = 10;

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
        // wrap drifted position so stars re-enter from the opposite edge
        var sx = ((star.x + star.driftX * driftT) % cssWidth + cssWidth) % cssWidth;
        var sy = ((star.y + star.driftY * driftT) % cssHeight + cssHeight) % cssHeight;
        // subtle twinkle: ±8% alpha oscillation
        var twinkle = 1 + 0.08 * Math.sin(driftT * 0.7 + star.phase);
        var a = Math.min(1, star.alpha * twinkle);

        var bloom = ctx.createRadialGradient(sx, sy, 0, sx, sy, star.glow || 2);
        bloom.addColorStop(0, star.color.replace(',1)', ',0.26)'));
        bloom.addColorStop(1, star.color.replace(',1)', ',0)'));
        ctx.fillStyle = bloom;
        ctx.globalAlpha = 1;
        ctx.beginPath();
        ctx.arc(sx, sy, star.glow || 2, 0, Math.PI * 2);
        ctx.fill();

        ctx.fillStyle = star.color;
        ctx.globalAlpha = a;
        ctx.beginPath();
        ctx.arc(sx, sy, star.r, 0, Math.PI * 2);
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
        ctx.lineWidth = 1;
        state.layout.lines.forEach(function (line) {
          var ax = zx(line.a.x) + panX;
          var ay = zy(line.a.y) + panY;
          var bx = zx(line.b.x) + panX;
          var by = zy(line.b.y) + panY;
          ctx.strokeStyle = line.unexplored ? 'rgba(122, 176, 248, 0.10)' : 'rgba(122, 176, 248, 0.28)';
          ctx.beginPath();
          ctx.moveTo(ax, ay);
          ctx.lineTo(bx, by);
          ctx.stroke();
        });
      }

      var botRoutes = state.layout.botRoutes || [];
      if (Array.isArray(botRoutes) && botRoutes.length > 0) {
        botRoutes.forEach(function (route) {
          if (!route || !Array.isArray(route.segments) || route.segments.length === 0) return;
          var lineColor = route.color || '#7ee69e';
          ctx.save();
          ctx.strokeStyle = lineColor;
          ctx.globalAlpha = route.isActive ? 0.9 : 0.62;
          ctx.lineWidth = Math.max(1.3, (route.isActive ? 2.5 : 1.8) * Math.max(0.7, Math.min(1.4, zoom)));
          route.segments.forEach(function (segment) {
            var ax = zx(segment.a.x) + panX;
            var ay = zy(segment.a.y) + panY;
            var bx = zx(segment.b.x) + panX;
            var by = zy(segment.b.y) + panY;
            ctx.beginPath();
            ctx.moveTo(ax, ay);
            ctx.lineTo(bx, by);
            ctx.stroke();

            // Directional chevron at midpoint of each leg
            var dx = bx - ax;
            var dy = by - ay;
            var len = Math.sqrt(dx * dx + dy * dy);
            if (len < 20) return; // leg too short to bother
            var ux = dx / len;
            var uy = dy / len;
            var mx = ax + dx * 0.5;
            var my = ay + dy * 0.5;
            var hs = 3.5 * zoom; // world-space constant size
            ctx.save();
            ctx.fillStyle = lineColor;
            ctx.beginPath();
            // chevron: tip at (mx + ux*hs), wings at ±perpendicular
            ctx.moveTo(mx + ux * hs, my + uy * hs);
            ctx.lineTo(mx - ux * hs - uy * hs, my - uy * hs + ux * hs);
            ctx.lineTo(mx - ux * (hs * 0.4), my - uy * (hs * 0.4));
            ctx.lineTo(mx - ux * hs + uy * hs, my - uy * hs - ux * hs);
            ctx.closePath();
            ctx.fill();
            ctx.restore();
          });

          if (Array.isArray(route.markers)) {
            route.markers.forEach(function (marker) {
              var px = zx(marker.point.x) + panX;
              var py = zy(marker.point.y) + panY;
              var radius = marker.isEnd ? 5.6 : 4.3;
              ctx.fillStyle = lineColor;
              ctx.beginPath();
              ctx.arc(px, py, radius, 0, Math.PI * 2);
              ctx.fill();
            });
          }
          ctx.restore();
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
        var selectedResourceLookup = getSelectedResourceLookup();
        var hasSelectedResources = Object.keys(selectedResourceLookup).length > 0;
        function matchesSelectedResources(system) {
          if (!hasSelectedResources) return true;
          if (!system || !Array.isArray(system.resourceIds) || system.resourceIds.length === 0) return false;
          for (var i = 0; i < system.resourceIds.length; i++) {
            var rid = (system.resourceIds[i] || '').toString().trim().toLowerCase();
            if (rid && selectedResourceLookup[rid]) return true;
          }
          return false;
        }
        function colorForSystem(system) {
          if (system && (system.isStronghold || system.IsStronghold)) {
            return { core: '#ff9f3f', glow: '255,159,63' };
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
          var isLandmark = landmarkSystems.indexOf((s.id || '').trim().toLowerCase()) !== -1;
          var isExplored = !!(s.isExplored || s.IsExplored);
          var isVisited = !!s.hasKnownPois;
          var isCompletelyUnexplored = !s.isCurrent && !isVisited && !isExplored;
          var isVisitedNotExplored = !s.isCurrent && isVisited && !isExplored;
          var brightnessAlpha = isCompletelyUnexplored ? 0.30 : (isVisitedNotExplored ? 0.62 : 1.0);
          var matchesSelection = false;
          if (hasSelectedResources && isExplored && !s.isCurrent) {
            matchesSelection = matchesSelectedResources(s);
            brightnessAlpha = matchesSelection
              ? Math.max(brightnessAlpha, 0.94)
              : Math.min(brightnessAlpha, 0.18);
          }
          var isStronghold = !!(s.isStronghold || s.IsStronghold);
          var tint = (hasSelectedResources && matchesSelection && !isStronghold)
            ? { core: '#ffd95a', glow: '255,217,90' }
            : colorForSystem(s);
          var r = (s.isCurrent ? 4.6 : isLandmark ? 5.2 : 3.3) * Math.max(0.7, Math.min(1.8, zoom));
          var glowR = (s.isCurrent ? 15 : isLandmark ? 18 : 10) * Math.max(0.7, Math.min(1.8, zoom));
          ctx.globalAlpha = brightnessAlpha;
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
          ctx.globalAlpha = 1.0;

          if (s.hasStation) {
            var orbitRadius = Math.max(6.2, r + 3.7);
            var basePhase = ((s.point.x * 0.0413) + (s.point.y * 0.0637)) % (Math.PI * 2);
            if (basePhase < 0) basePhase += Math.PI * 2;
            ctx.save();
            ctx.strokeStyle = 'rgba(255, 223, 140, 0.72)';
            ctx.lineWidth = Math.max(1.0, 0.74 * zoom);
            ctx.setLineDash([1.8, 2.6]);
            ctx.lineDashOffset = 0;
            ctx.beginPath();
            ctx.arc(sx, sy, orbitRadius, 0, Math.PI * 2);
            ctx.stroke();
            ctx.setLineDash([]);

            var orbitSpeed = 0.95;
            var satelliteSize = Math.max(1.8, r * 0.45);
            var phase = basePhase + (driftT * orbitSpeed);
            var ox = Math.cos(phase) * orbitRadius;
            var oy = Math.sin(phase) * orbitRadius;
            var mx = sx + ox;
            var my = sy - oy;
            ctx.fillStyle = 'rgba(255, 229, 161, 0.96)';
            ctx.beginPath();
            ctx.moveTo(mx, my - satelliteSize);
            ctx.lineTo(mx + satelliteSize, my);
            ctx.lineTo(mx, my + satelliteSize);
            ctx.lineTo(mx - satelliteSize, my);
            ctx.closePath();
            ctx.fill();
            ctx.restore();
          }

          var markers = Array.isArray(s.botMarkers) ? s.botMarkers : [];
          if (markers.length > 0) {
            var ringBase = r + 3.2;
            markers.slice(0, 5).forEach(function (marker, idx) {
              ctx.save();
              ctx.globalAlpha = marker.isActive ? 0.95 : 0.82;
              ctx.strokeStyle = marker.color || '#7ee69e';
              ctx.lineWidth = marker.isActive ? 1.8 : 1.4;
              ctx.beginPath();
              ctx.arc(sx, sy, ringBase + (idx * 3), 0, Math.PI * 2);
              ctx.stroke();
              ctx.restore();
            });

            // Render bot nametags at the bot's current system, stacked when multiple bots share it.
            var visibleMarkers = markers
              .filter(function (marker) { return !!marker && !!marker.label; })
              .slice(0, 8);
            if (visibleMarkers.length > 0) {
              var baseY = sy - (r + 11);
              var tagGap = 4;
              var tagHeight = 15;
              var totalH = (visibleMarkers.length * tagHeight) + ((visibleMarkers.length - 1) * tagGap);
              var startY = baseY - totalH;
              visibleMarkers.forEach(function (marker, idx) {
                var labelText = marker.label;
                if (marker.routeEta) labelText += ' ' + marker.routeEta;

                ctx.save();
                ctx.font = '700 10px ui-monospace, SFMono-Regular, Menlo, monospace';
                var textW = ctx.measureText(labelText).width;
                var padX = 6;
                var tagW = Math.max(42, textW + (padX * 2));
                var tagX = sx - (tagW * 0.5);
                var tagY = startY + (idx * (tagHeight + tagGap));

                ctx.fillStyle = 'rgba(8, 17, 24, 0.84)';
                ctx.strokeStyle = marker.color || '#7ee69e';
                ctx.lineWidth = marker.isActive ? 1.4 : 1.1;
                ctx.beginPath();
                ctx.roundRect(tagX, tagY, tagW, tagHeight, 4);
                ctx.fill();
                ctx.stroke();

                ctx.fillStyle = 'rgba(230, 244, 255, 0.96)';
                ctx.fillText(labelText, tagX + padX, tagY + 11);
                ctx.restore();
              });
            }
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

      if (Array.isArray(botRoutes) && botRoutes.length > 0) {
        var routeTitle = 'Bot Routes: ' + botRoutes.length;
        var activeCount = botRoutes.filter(function (r) { return !!(r && r.isActive); }).length;
        var routeMeta = activeCount > 0 ? ('active=' + activeCount) : 'active=0';
        var rx = 12;
        var ry = cssHeight - 42;
        var rpad = 8;
        ctx.save();
        ctx.font = '700 11px ui-monospace, SFMono-Regular, Menlo, monospace';
        var rw = Math.max(ctx.measureText(routeTitle).width, ctx.measureText(routeMeta).width) + (rpad * 2);
        ctx.fillStyle = 'rgba(8, 17, 24, 0.78)';
        ctx.strokeStyle = 'rgba(122, 190, 241, 0.55)';
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.roundRect(rx, ry, rw, 30, 6);
        ctx.fill();
        ctx.stroke();
        ctx.fillStyle = '#d8ffef';
        ctx.fillText(routeTitle, rx + rpad, ry + 12);
        ctx.fillStyle = 'rgba(174, 255, 220, 0.92)';
        ctx.font = '10px ui-monospace, SFMono-Regular, Menlo, monospace';
        ctx.fillText(routeMeta, rx + rpad, ry + 24);
        ctx.restore();
      }
      return;
    }

    // System map mode
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
    } finally {
      if (window._uiPerf) window._uiPerf.end('drawGalaxyMapCanvas', perfStart);
    }
  };

  function createMapStars(mode, cssWidth, cssHeight) {
    var seed = (42 ^ (mode === 'galaxy' ? 173 : 97)) >>> 0;
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
        color: warm ? 'rgba(255,230,170,1)' : 'rgba(190,220,255,1)',
        driftX: (nextRand() - 0.5) * 3.5,
        driftY: (nextRand() - 0.5) * 3.5,
        phase: nextRand() * Math.PI * 2
      });
    }
    return stars;
  }

  function getCanvasMapPayload(canvas, existing) {
    var payload = canvas.getAttribute('data-map') || '';
    if (!payload) return null;
    if (existing && existing.sourcePayload === payload && existing.sourceMap) {
      return { payload: payload, map: existing.sourceMap };
    }
    try {
      return { payload: payload, map: JSON.parse(payload) };
    } catch (_) {
      return null;
    }
  }

  window.renderGalaxyMapCanvases = function () {
    var perfStart = window._uiPerf ? window._uiPerf.begin() : 0;
    var _hasArrivalRoutes = false;
    window.bindGalaxyMapResourceFilters();
    var canvases = document.querySelectorAll('.galaxy-map-canvas');
    if (window._uiPerf) window._uiPerf.setGauge('map_canvases', canvases.length);
    canvases.forEach(function (canvas) {
      window.initGalaxyMapCanvas(canvas);
      if (canvas.offsetParent === null) return;

      var existing = window._galaxyMapStates.get(canvas);
      var mapPayload = getCanvasMapPayload(canvas, existing);
      if (!mapPayload) return;
      var payload = mapPayload.payload;
      var map = mapPayload.map;

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
      var galaxySystems = ((map && (map.GalaxySystems || map.galaxySystems)) || []).filter(function (s) {
        var id = getSystemId(s);
        return id.length > 0;
      });
      var currentId = ((map && (map.CurrentSystem || map.currentSystem)) || '').trim();
      var currentPoiId = ((map && (map.CurrentPoi || map.currentPoi)) || '').trim();
      var pois = ((map && (map.Pois || map.pois)) || []).filter(function (p) {
        return getPoiId(p).length > 0;
      });
      var botMarkers = ((map && (map.BotMarkers || map.botMarkers)) || []).filter(function (m) {
        if (!m) return false;
        var systemId = ((m.SystemId || m.systemId) || '').toString().trim();
        return systemId.length > 0;
      });
      var botRoutes = ((map && (map.BotRoutes || map.botRoutes)) || []).filter(function (r) {
        if (!r) return false;
        var currentSystemId = ((r.CurrentSystemId || r.currentSystemId) || '').toString().trim();
        var hops = r.Hops || r.hops || [];
        return currentSystemId.length > 0 && Array.isArray(hops) && hops.length > 0;
      });
      var resourceFilters = ((map && (map.ResourceFilters || map.resourceFilters)) || []).filter(function (r) {
        if (!r) return false;
        var resourceId = ((r.ResourceId || r.resourceId) || '').toString().trim();
        var systemIds = (r.SystemIds || r.systemIds || []);
        return resourceId.length > 0 && Array.isArray(systemIds) && systemIds.length > 0;
      });

      var ctx = canvas.getContext('2d');
      if (!ctx) return;

      var dpr = window.devicePixelRatio || 1;
      var cssWidth = canvas.clientWidth || 800;
      var cssHeight = canvas.clientHeight || 480;
      var mode = (canvas.getAttribute('data-map-mode') || 'system').toLowerCase().trim();
      var centerX = cssWidth * 0.5;
      var centerY = cssHeight * 0.5;
      var pixelWidth = Math.max(1, Math.floor(cssWidth * dpr));
      var pixelHeight = Math.max(1, Math.floor(cssHeight * dpr));
      var sizeChanged = !existing ||
        existing.dpr !== dpr ||
        existing.cssWidth !== cssWidth ||
        existing.cssHeight !== cssHeight ||
        canvas.width !== pixelWidth ||
        canvas.height !== pixelHeight;
      if (sizeChanged) {
        canvas.width = pixelWidth;
        canvas.height = pixelHeight;
        if (window._uiPerf) window._uiPerf.count('map_canvas_resizes');
      }

      if (mode !== 'galaxy' && systems.length === 0) {
        var fallbackCurrentId = currentId || 'current';
        systems = [{ id: fallbackCurrentId, x: 0, y: 0, connections: [] }];
        currentId = fallbackCurrentId;
      }

      var stars = (!sizeChanged && existing && existing.layout && existing.layout.stars)
        ? existing.layout.stars
        : createMapStars(mode, cssWidth, cssHeight);
      if ((sizeChanged || !existing || !existing.layout || !existing.layout.stars) && window._uiPerf) {
        window._uiPerf.count('map_starfield_rebuilds');
      }

      if (mode === 'galaxy') {
        var galaxySystemsForRender = galaxySystems.length > 0 ? galaxySystems : systems;
        // allSystemsExtra contains every bot's known systems for route coordinate resolution.
        var allSystemsExtra = ((map && (map.AllSystems || map.allSystems)) || []);

        // If the selected bot has no local systems, fall back to allSystems for scale computation
        // so that routes from other bots can still be drawn.
        var coordsSource = galaxySystemsForRender.length > 0 ? galaxySystemsForRender : allSystemsExtra;
        if (coordsSource.length === 0) return;

        var coords = coordsSource
          .map(function (s) {
            var x = getX(s);
            var y = getY(s);
            return isFiniteCoord(x, y) ? { id: getSystemId(s), x: x, y: y } : null;
          })
          .filter(function (s) { return !!s; });

        var minX = 0, maxX = 0, minY = 0, maxY = 0;
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
        var byIdLower = {};
        var resourceIdsBySystemIdLower = {};
        resourceFilters.forEach(function (resourceFilter) {
          var resourceId = ((resourceFilter.ResourceId || resourceFilter.resourceId) || '').toString().trim();
          if (!resourceId) return;
          var systemIds = resourceFilter.SystemIds || resourceFilter.systemIds || [];
          systemIds.forEach(function (systemIdRaw) {
            var systemId = (systemIdRaw || '').toString().trim();
            if (!systemId) return;
            var key = systemId.toLowerCase();
            resourceIdsBySystemIdLower[key] = resourceIdsBySystemIdLower[key] || [];
            if (resourceIdsBySystemIdLower[key].indexOf(resourceId) === -1) {
              resourceIdsBySystemIdLower[key].push(resourceId);
            }
          });
        });
        var layoutSystems = galaxySystemsForRender.map(function (s) {
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
            hasStation: !!(s.HasStation || s.hasStation || s.has_station),
            hasKnownPois: !!(s.HasKnownPois || s.hasKnownPois),
            isExplored: !!(s.IsExplored || s.isExplored),
            resourceIds: resourceIdsBySystemIdLower[id.toLowerCase()] || [],
            point: { x: px, y: py },
            isCurrent: id === currentId,
            connections: ((s.Connections || s.connections) || [])
              .filter(function (cid) { return typeof cid === 'string' && cid.trim().length > 0; })
              .map(function (cid) { return cid.trim(); })
          };
          byId[id] = entry;
          byIdLower[id.toLowerCase()] = entry;
          return entry;
        });

        // Extend byIdLower with systems from all bots so routes for non-selected bots
        // can be resolved even when they travel through systems the selected bot hasn't visited.
        allSystemsExtra.forEach(function (s) {
          var eid = getSystemId(s);
          if (!eid || byIdLower[eid.toLowerCase()]) return;
          var ex = getX(s), ey = getY(s);
          if (typeof ex !== 'number' || typeof ey !== 'number') return;
          var epx = centerX + ex * galaxyScale;
          var epy = centerY - ey * galaxyScale;
          byIdLower[eid.toLowerCase()] = { id: eid, point: { x: epx, y: epy }, connections: [] };
        });

        var routeOverlays = [];
        var routeMetaByBotId = {};
        botRoutes.forEach(function (route) {
          var routeBotId = ((route.BotId || route.botId) || '').toString().trim();
          var routeLabel = ((route.Label || route.label) || routeBotId || 'bot').toString().trim();
          var routeColor = ((route.Color || route.color) || '#7ee69e').toString().trim();
          var routeCurrentSystem = ((route.CurrentSystemId || route.currentSystemId) || '').toString().trim();
          var routeTarget = ((route.TargetSystemId || route.targetSystemId) || '').toString().trim();
          var routeHopsRaw = route.Hops || route.hops || [];
          var routeHops = Array.isArray(routeHopsRaw)
            ? routeHopsRaw.map(function (h) { return (h || '').toString().trim(); }).filter(function (h) { return h.length > 0; })
            : [];
          if (!routeCurrentSystem || routeHops.length === 0) return;

          var routeSystems = [routeCurrentSystem];
          routeHops.forEach(function (h) { routeSystems.push(h); });

          var compactRoute = [];
          routeSystems.forEach(function (sid) {
            if (compactRoute.length === 0 || compactRoute[compactRoute.length - 1] !== sid) {
              compactRoute.push(sid);
            }
          });

          var routeSegments = [];
          var routeMarkers = [];
          compactRoute.forEach(function (sid, idx) {
            var node = byIdLower[sid.toLowerCase()];
            if (!node) return;
            routeMarkers.push({ id: sid, point: node.point, isStart: idx === 0, isEnd: idx === compactRoute.length - 1 });
          });
          for (var ridx = 0; ridx + 1 < compactRoute.length; ridx++) {
            var from = byIdLower[compactRoute[ridx].toLowerCase()];
            var to = byIdLower[compactRoute[ridx + 1].toLowerCase()];
            if (!from || !to) continue;
            routeSegments.push({ a: from.point, b: to.point });
          }
          if (routeSegments.length === 0) return;

          var routeEta = null;
          var routeArrivalIso = ((route.ArrivalTime || route.arrivalTime) || '').toString().trim();
          if (routeArrivalIso) {
            var arrivalMs = new Date(routeArrivalIso).getTime();
            var secsLeft = Math.max(0, Math.ceil((arrivalMs - Date.now()) / 1000));
            var etaMins = Math.floor(secsLeft / 60);
            var etaSecs = secsLeft % 60;
            routeEta = secsLeft <= 0 ? 'arriving...' : (etaMins > 0 ? etaMins + 'm ' : '') + etaSecs + 's';
            _hasArrivalRoutes = true;
          }

          routeOverlays.push({
            botId: routeBotId,
            label: routeLabel,
            eta: routeEta,
            color: routeColor,
            isActive: !!(route.IsActive || route.isActive),
            targetId: routeTarget,
            segments: routeSegments,
            markers: routeMarkers
          });
          if (routeBotId) {
            routeMetaByBotId[routeBotId.toLowerCase()] = { eta: routeEta };
          }
        });

        botMarkers.forEach(function (m) {
          var markerSystemId = ((m.SystemId || m.systemId) || '').toString().trim();
          if (!markerSystemId) return;
          var target = byIdLower[markerSystemId.toLowerCase()];
          if (!target) return;
          target.botMarkers = target.botMarkers || [];
          target.botMarkers.push({
            botId: ((m.BotId || m.botId) || '').toString().trim(),
            label: ((m.Label || m.label) || '').toString().trim(),
            color: ((m.Color || m.color) || '#7ee69e').toString().trim(),
            isActive: !!(m.IsActive || m.isActive),
            routeEta: (function () {
              var markerBotId = ((m.BotId || m.botId) || '').toString().trim().toLowerCase();
              if (!markerBotId) return null;
              var meta = routeMetaByBotId[markerBotId];
              return meta && meta.eta ? ('(' + meta.eta + ')') : null;
            })()
          });
        });
        layoutSystems.forEach(function (s) {
          if (!Array.isArray(s.botMarkers) || s.botMarkers.length === 0) return;
          s.botMarkers.sort(function (a, b) {
            if (!!a.isActive === !!b.isActive) return 0;
            return a.isActive ? -1 : 1;
          });
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
            lines.push({ a: s.point, b: t.point, unexplored: !s.hasKnownPois || !t.hasKnownPois });
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
            originPoint: { x: centerX, y: centerY },
            botRoutes: routeOverlays
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
          sourcePayload: payload,
          sourceMap: map,
          galaxyFit: galaxyFit
        });
        window.drawGalaxyMapCanvas(canvas);
        return;
      }

      // System map layout
      var currentSystem = systems.find(function (s) { return getSystemId(s) === currentId; }) || systems[0];
      var currentX = getX(currentSystem);
      var currentY = getY(currentSystem);
      if (!isFiniteCoord(currentX, currentY)) { currentX = 0; currentY = 0; }

      var poiPositioned = pois.filter(function (p) { return isFiniteCoord(getX(p), getY(p)); });
      var sunPoi = poiPositioned.find(function (p) {
        var type = ((p && (p.Type || p.type)) || '').toString().trim().toLowerCase();
        var id = getPoiId(p).toLowerCase();
        var label = ((p && (p.Label || p.label)) || '').toString().trim().toLowerCase();
        return type === 'sun' || type === 'star' || id === 'sun' || id.endsWith('_sun') ||
          label === 'sun' || label.indexOf(' sun') >= 0 || label.indexOf('star') >= 0;
      }) || null;
      var currentPoi = pois.find(function (p) {
        var id = getPoiId(p);
        return (currentPoiId && id === currentPoiId) || !!(p && (p.isCurrent || p.IsCurrent));
      }) || poiPositioned[0] || null;
      var anchorX = sunPoi ? getX(sunPoi) : currentX;
      var anchorY = sunPoi ? getY(sunPoi) : currentY;
      if (!isFiniteCoord(anchorX, anchorY)) { anchorX = 0; anchorY = 0; }

      function relPoiPoint(p) {
        var x = getX(p);
        var y = getY(p);
        if (!isFiniteCoord(x, y)) return { x: 0, y: 0 };
        return { x: x - anchorX, y: y - anchorY };
      }

      var allRelX = [], allRelY = [];
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
          if (Math.abs(vx) > 1e-9 || Math.abs(vy) > 1e-9) angle = Math.atan2(vy, vx);
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

      window._galaxyMapStates.set(canvas, {
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
        payload: payload,
        sourcePayload: payload,
        sourceMap: map
      });

      window.drawGalaxyMapCanvas(canvas);
    });

    if (_hasArrivalRoutes) {
      if (!window._routeCountdownTimer) {
        window._routeCountdownTimer = setInterval(window.renderGalaxyMapCanvases, 1000);
      }
    } else {
      if (window._routeCountdownTimer) {
        clearInterval(window._routeCountdownTimer);
        window._routeCountdownTimer = null;
      }
    }
    if (window._uiPerf) window._uiPerf.setGauge('route_timer', window._routeCountdownTimer ? 'on' : 'off');
    if (window._uiPerf) window._uiPerf.setGauge('map_drag_release_handler', window._mapDragReleaseHandlerAttached ? '1' : '0');
    if (window._uiPerf) window._uiPerf.end('renderGalaxyMapCanvases', perfStart);
  };

  window.pollGalaxyMapData = function () {
    var botId = window._activeBotId;
    var url = apiUrl('partial/map-data') + (botId ? '?bot_id=' + encodeURIComponent(botId) : '');
    fetch(url, { cache: 'no-store' })
      .then(function (res) { return res.ok ? res.json() : null; })
      .then(function (map) {
        if (!map) return;
        var payload = JSON.stringify(map);
        if (window._uiPerf) {
          window._uiPerf.count('map_json_polls');
          window._uiPerf.setGauge('map_payload_bytes', payload.length);
        }
        var changedCanvasCount = 0;
        document.querySelectorAll('.galaxy-map-canvas').forEach(function (canvas) {
          var existingState = window._galaxyMapStates.get(canvas);
          var existingPayload = existingState && existingState.sourcePayload
            ? existingState.sourcePayload
            : (canvas.getAttribute('data-map') || '');
          if (existingState) {
            existingState.sourcePayload = payload;
            existingState.sourceMap = map;
          }
          if (existingPayload !== payload) {
            changedCanvasCount += 1;
            canvas.setAttribute('data-map', payload);
          }
        });
        if (window._uiPerf) {
          window._uiPerf.setGauge('map_changed_canvases', changedCanvasCount);
          window._uiPerf.event('map_poll', { bytes: payload.length, changedCanvases: changedCanvasCount });
        }
        window.renderGalaxyMapCanvases();
      })
      .catch(function () { });
  };

  window.ensureGalaxyMapDataPolling = function () {
    if (window._galaxyMapDataPoller) return;
    window._galaxyMapDataPoller = window.setInterval(function () {
      if (document.hidden) return;
      var activeMapPane = document.querySelector('#state-pane-map.tab-pane.active');
      if (!activeMapPane) return;
      window.pollGalaxyMapData();
    }, 1000);
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
