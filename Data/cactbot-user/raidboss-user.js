'use strict';

// ============================================================
// CactBridge relay - broadcasts every Cactbot alarm/alert/info
// text AND timeline entries back via OverlayPlugin
// BroadcastMessage so the CactBridge Dalamud plugin can display
// them in-game.
//
// SETUP:
//   1. Copy this file to your Cactbot user config directory.
//      For IINACT this is usually:
//        %APPDATA%\XIVLauncher\pluginConfigs\IINACT\cactbot-user\
//   2. In IINACT open the Cactbot raidboss overlay settings and
//      set "User Config Path" to that directory, then reload.
//   3. The plugin overlay should show "Relay loaded" briefly
//      on startup - that confirms the file is working.
// ============================================================

(function cactbridgeRelay() {

  // ----- helpers ------------------------------------------------

  function sendBroadcast(text, type, extra) {
    const trimmed = String(text ?? '').trim();
    if (!trimmed) return;
    var msg = { type: type || 'info', text: trimmed };
    if (extra) Object.assign(msg, extra);
    // Prefer native CactBridge bridge (bypasses OverlayPlugin WebSocket)
    if (typeof window.__cactbridgeBroadcast === 'function') {
      window.__cactbridgeBroadcast(JSON.stringify(msg));
      return;
    }
    // Fallback to OverlayPlugin broadcast API
    if (typeof callOverlayHandler !== 'function') return;
    void callOverlayHandler({
      call: 'broadcast',
      source: 'cactbot-ui-relay',
      msg: msg,
    });
  }

  // ----- Patch callOverlayHandler with WS timeout fallback ---------
  // If the headless browser's WebSocket to OverlayPlugin fails to open,
  // queued Cactbot messages (e.g. cactbotLoadUser / cactbotLoadData) are
  // never sent and their promises never settle — the getUserConfigLocation
  // callback never fires, so addOverlayListener('LogLine',…) is never
  // registered, and the timeline controller is never created.
  //
  // This MUST use Object.defineProperty to intercept the exact moment
  // overlay_plugin_api.ts's init() assigns window.callOverlayHandler,
  // because the FIRST call (to cactbotLoadData) happens synchronously
  // after the assignment — a polling setTimeout would miss it.
  //
  // When the wrapped promise times out (8 s) it RESOLVES with fallback
  // data instead of rejecting.  Crucially, Cactbot's user_config.ts
  // chains .then() on these promises; a rejection would skip the chain
  // and the getUserConfigLocation callback would never fire.
  (function patchCallOverlayHandler() {
    var realFn = null;
    var wsTimedOut = false;

    function wrapFn(orig) {
      return function(msg) {
        var result;
        try { result = orig.call(this, msg); }
        catch (e) { return Promise.reject(e); }
        if (!result || typeof result.then !== 'function') return result;

        function fallback() {
          var call = msg && msg.call;
          if (call === 'cactbotLoadData') {
            // loadUserFiles awaits readOptions?.data ?? {}
            return { data: {} };
          }
          if (call === 'cactbotLoadUser') {
            // loadUser is called via .then((e) => loadUser(e))
            return {
              detail: {
                userLocation: '',
                localUserFiles: null,
                cactbotVersion: '0.0.0.0',
                overlayPluginVersion: '0.0.0.0',
                ffxivPluginVersion: '0.0.0.0',
                actVersion: '0.0.0.0',
                gameRegion: 'International',
                parserLanguage: 'en',
                systemLocale: 'en',
                displayLanguage: 'en',
                language: 'en',
              },
            };
          }
          if (call === 'cactbotRequestState') return {};
          return {};
        }

        return new Promise(function(resolve) {
          var timer = setTimeout(function() {
            wsTimedOut = true;
            resolve(fallback());
          }, 8000);
          result.then(function(v) {
            clearTimeout(timer);
            if (!wsTimedOut) resolve(v);
          }, function() {
            clearTimeout(timer);
            resolve(fallback());
          });
        });
      };
    }

    Object.defineProperty(window, 'callOverlayHandler', {
      configurable: true,
      enumerable: true,
      get: function() { return realFn; },
      set: function(fn) {
        realFn = wrapFn(fn);
      },
    });
  })();

  // Dedup within 3 s per source so different hooks don't suppress each other.
  // The key includes the base type ("info"/"alarm"/"alert"/"timeline") so an
  // alert broadcast and a timeline broadcast with the same text are NOT
  // mutually suppressed — each overlay gets its own data.
  const seen = new Map();
  function sendDeduped(text, type, extra) {
    const raw = String(text ?? '').trim();
    if (!raw) return;
    var key = raw;
    // Use type prefix only for non-info types to avoid double-prefixing
    if (type && type !== 'info') key = type + ':' + raw;
    const now = Date.now();
    const last = seen.get(key);
    if (last !== undefined && now - last < 3000) return;
    seen.set(key, now);
    // Prune stale entries occasionally.
    if (seen.size > 200) {
      const cutoff = now - 10000;
      for (const [k, t] of seen) if (t < cutoff) seen.delete(k);
    }
    sendBroadcast(text, type, extra);
  }

  // ----- Method 1: Options.TransformText hook -------------------
  // Called by popup-text.ts _addTextFor() for EVERY alarm/alert/info
  // text before it is written into the DOM.  Severity is unknown here
  // so we broadcast as 'info'; the MutationObserver below will
  // re-broadcast with the correct severity if the dedup window allows.
  if (typeof Options !== 'undefined') {
    const origTransformText = Options.TransformText ?? null;
    Options.TransformText = function (text) {
      const out = origTransformText ? origTransformText.call(this, text) : text;
      const result = (out !== null && out !== undefined) ? out : text;
      sendDeduped(result, 'info');
      return out;
    };
  }

  // ----- Method 2: MutationObserver on Cactbot alert elements ---
  // Watches for div.alarm-text / div.alert-text / div.info-text nodes
  // added to the DOM (class names confirmed from popup-text.ts
  // _makeTextElement: `const textElementClass = \`\${textType}-text\``).
  // This fires AFTER TransformText so the dedup Map prevents duplicates,
  // but it carries the correct severity type.  It also forwards the
  // duration (in seconds) Cactbot shows each severity for, read from its
  // per-severity options (DisplayAlarmTextForSeconds /
  // DisplayAlertTextForSeconds / DisplayInfoTextForSeconds).  That is the
  // resolved display duration Cactbot uses for any trigger that does not
  // set its own durationSeconds / per-trigger Duration.
  function cactbotSeverityDuration(textType) {
    try {
      if (typeof Options === 'undefined' || !Options) return null;
      var key = 'Display' + textType.charAt(0).toUpperCase() +
        textType.slice(1) + 'TextForSeconds';
      var v = Options[key];
      if (typeof v === 'number' && isFinite(v) && v > 0) return v;
    } catch (e) { /* never break the observer */ }
    return null;
  }

  function checkNode(node) {
    if (node.nodeType !== 1) return;
    const cl = node.classList;
    if (!cl) return;
    const text = (node.innerText || node.textContent || '').trim();
    if (!text) return;
    var type = null;
    if      (cl.contains('alarm-text')) type = 'alarm';
    else if (cl.contains('alert-text')) type = 'alert';
    else if (cl.contains('info-text'))  type = 'info';
    if (!type) return;
    var dur = cactbotSeverityDuration(type);
    sendDeduped(text, type, dur ? { duration: dur } : undefined);
  }

  function startAlertObserver() {
    const observer = new MutationObserver(function (mutations) {
      for (const m of mutations) {
        for (const node of m.addedNodes) checkNode(node);
      }
    });
    observer.observe(document.documentElement, { childList: true, subtree: true });
  }

  // ----- Method 3: Timeline entry capture via prototype hook ---
  // Monkey-patch TimelineUI.prototype so we intercept every
  // OnShowInfoText / OnShowAlertText / OnShowAlarmText call.
  // This catches timeline entries that ALSO produce popup text
  // (via alerttext/alarmtext directives).  For plain timer-bar
  // entries we fall back to DOM observation (Method 4 below).
  // We retry every 500 ms for up to 10 s because TimelineUI may
  // not be defined yet when this script runs.

  function hookTimelineUI() {
    if (typeof TimelineUI === 'undefined' || !TimelineUI.prototype) return false;
    var hooked = false;

    var origInfo = TimelineUI.prototype.OnShowInfoText;
    if (origInfo) {
      TimelineUI.prototype.OnShowInfoText = function (text, currentTime) {
        if (text) sendDeduped(text, 'timeline', { time: currentTime || 0 });
        return origInfo.call(this, text, currentTime);
      };
      hooked = true;
    }
    var origAlert = TimelineUI.prototype.OnShowAlertText;
    if (origAlert) {
      TimelineUI.prototype.OnShowAlertText = function (text, currentTime) {
        if (text) sendDeduped(text, 'timeline', { time: currentTime || 0 });
        return origAlert.call(this, text, currentTime);
      };
      hooked = true;
    }
    var origAlarm = TimelineUI.prototype.OnShowAlarmText;
    if (origAlarm) {
      TimelineUI.prototype.OnShowAlarmText = function (text, currentTime) {
        if (text) sendDeduped(text, 'timeline', { time: currentTime || 0 });
        return origAlarm.call(this, text, currentTime);
      };
      hooked = true;
    }
    return hooked;
  }

  // Try immediately, then retry up to 20 times (10 s).
  if (!hookTimelineUI()) {
    var retries = 0;
    var retryTimer = setInterval(function () {
      if (hookTimelineUI() || ++retries >= 20) clearInterval(retryTimer);
    }, 500);
  }

  // ----- Method 4: Timer-bar DOM observation (fallback) --------
  // Watches for <timer-bar> elements added inside #timeline and
  // broadcasts them as timeline entries.  Catches entries that
  // don't produce popup text (plain timeline entries).
  // Cactbot's <timer-bar> stores the ability name in the `lefttext`
  // attribute and the remaining time is exposed via the `.value`
  // numeric property (seconds remaining).  As a fallback we compute
  // time remaining from the `duration` and `value` attributes.

  const broadcastedTimers = new WeakSet();

  function checkTimelineBar(bar) {
    if (broadcastedTimers.has(bar)) return;
    broadcastedTimers.add(bar);

    // Extract ability name from lefttext attribute (Cactbot <timer-bar>)
    var text = (bar.getAttribute('lefttext') ||
                bar.getAttribute('aria-label') ||
                bar.getAttribute('text') ||
                bar.innerText ||
                bar.textContent || '').trim();
    // Strip trailing parenthesised time if present
    text = text.replace(/\s*\([^)]*\)\s*$/, '').trim();
    if (!text) return;

    // Approximate time remaining from the bar's value property
    var seconds = 0;
    if (typeof bar.value === 'number') {
      // bar.value property returns remaining time in seconds
      seconds = Math.max(0, bar.value);
    } else {
      var dur = parseFloat(bar.getAttribute('duration'));
      var val = parseFloat(bar.getAttribute('value'));
      if (!isNaN(dur) && !isNaN(val)) {
        seconds = Math.max(0, dur - val);
      }
    }

    sendDeduped(text, 'timeline', { time: seconds || 0 });
  }

  // Observe the entire document (like the alert observer) so we catch
  // timer-bar elements even if #timeline hasn't been created yet by
  // Cactbot's async module loading.  We also scan children of added
  // nodes in case multiple entries arrive as a fragment.
  function startTimelineObserver() {
    var tlObserver = new MutationObserver(function (mutations) {
      for (var m = 0; m < mutations.length; m++) {
        var added = mutations[m].addedNodes;
        if (!added) continue;
        for (var n = 0; n < added.length; n++) {
          var node = added[n];
          if (node.nodeType !== 1) continue;
          // Direct timer-bar addition
          if (node.tagName && node.tagName.toLowerCase() === 'timer-bar') {
            checkTimelineBar(node);
          }
          // Also scan descendants for timer-bars added as part of a container
          if (node.querySelectorAll) {
            var bars = node.querySelectorAll('timer-bar');
            for (var b = 0; b < bars.length; b++) checkTimelineBar(bars[b]);
          }
        }
      }
    });
    tlObserver.observe(document.documentElement, { childList: true, subtree: true });
  }

  // ----- Startup -------------------------------------------------
  function startAll() {
    startAlertObserver();
    startTimelineObserver();
    // Startup confirmation - if the plugin shows "Relay loaded" the file is working.
    sendBroadcast('Relay loaded', 'info');
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', startAll);
  } else {
    startAll();
  }

})();
