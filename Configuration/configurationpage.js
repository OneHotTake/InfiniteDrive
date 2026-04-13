define(['loading', 'emby-input', 'emby-select', 'emby-checkbox', 'emby-button', 'emby-textarea'],
function (loading) {
    'use strict';

    var pluginId = '3c45a87e-2b4f-4d1a-9e73-8f12c3456789';

    // ── Module-level state ────────────────────────────────────────────────────
    var _dashInterval       = null;
    var _catalogPollTimer   = null;
    var _searchTimer        = null;
    var _catalogLimits      = {};
    var _loadedConfig       = null;
    var _wizardStep         = 1;
    var _wizardCatalogs     = [];
    var _unsavedChanges     = false;
    var _authExpired        = false;

    // ── Auth expired handling ─────────────────────────────────────────────────
    function stopPolling() {
        if (_dashInterval) { clearInterval(_dashInterval); _dashInterval = null; }
        if (_catalogPollTimer) { clearInterval(_catalogPollTimer); _catalogPollTimer = null; }
        if (_searchTimer) { clearTimeout(_searchTimer); _searchTimer = null; }
    }

    function showAuthExpiredBanner(view) {
        if (_authExpired) return;
        _authExpired = true;
        stopPolling();

        var banner = document.createElement('div');
        banner.id = 'es-auth-expired-banner';
        banner.className = 'es-alert es-alert-warning';
        banner.style.cssText = 'position:fixed;top:0;left:0;right:0;margin:0;padding:12px 16px;z-index:9999;display:flex;align-items:center;gap:12px;';
        banner.innerHTML = '<span>⚠️ Session expired. Reload the page to reconnect.</span><button type="button" is="emby-button" class="raised button">Reload</button>';

        var btn = banner.querySelector('button');
        btn.onclick = function() { location.reload(); };

        document.body.insertBefore(banner, document.body.firstChild);
    }

    // ── Authenticated fetch ───────────────────────────────────────────────────
    // Emby requires X-Emby-Token on all custom plugin API routes.
    // ApiClient is a global injected by Emby's SPA — no require() needed.
    function esFetch(url, opts) {
        opts = opts || {};
        var token = (typeof ApiClient !== 'undefined' && ApiClient.accessToken) ? ApiClient.accessToken() : '';
        opts.headers = Object.assign({ 'X-Emby-Token': token }, opts.headers || {});
        return fetch(url, opts);
    }

    // ── Notification helper ───────────────────────────────────────────────────
    function esAlert(msg) {
        if (typeof Dashboard !== 'undefined' && Dashboard.alert) {
            Dashboard.alert({ message: msg });
        } else {
            alert(msg);
        }
    }

    // ── DOM helpers ───────────────────────────────────────────────────────────
    function q(view, id)        { return view.querySelector('#' + id); }
    function esVal(view, id)    { var el = q(view, id); return el ? el.value : ''; }
    function esChk(view, id)    { var el = q(view, id); return el ? el.checked : false; }
    function esErrorText(msg)   { return '✗ ' + msg; }
    function esInt(view, id, d) { var v = parseInt(esVal(view, id), 10); return isNaN(v) ? d : v; }
    function setText(view, id, val) { var el = q(view, id); if (el) el.textContent = val; }
    function setBadge(view, id, type, text) {
        var el = q(view, id);
        if (el) { el.textContent = text; el.className = 'es-badge es-badge-' + type; }
    }
    function esc(s) {
        if (!s) return '';
        return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
    }
    function pad(n) { return n != null ? (n < 10 ? '0'+n : ''+n) : '00'; }
    function shortenUrl(u) { return u && u.length > 35 ? u.slice(0,35)+'…' : u; }
    function fmtDate(iso) {
        try { var d = new Date(iso); return d.toLocaleDateString()+' '+d.toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'}); }
        catch(e) { return iso; }
    }
    function fmtRelative(d) {
        var diff = Math.floor((Date.now() - d.getTime()) / 1000);
        if (diff < 60)    return diff + 's ago';
        if (diff < 3600)  return Math.floor(diff/60)  + 'm ago';
        if (diff < 86400) return Math.floor(diff/3600) + 'h ago';
        return Math.floor(diff/86400) + 'd ago';
    }

    // ── Wizard navigation ─────────────────────────────────────────────────────
    function updateWizardProgress(view, step) {
        var indicators = view.querySelectorAll('.es-wizard-step-indicator');
        for (var i = 0; i < indicators.length; i++) {
            var stepNum = parseInt(indicators[i].getAttribute('data-es-wizard-step'), 10);
            indicators[i].classList.remove('active', 'completed');
            if (stepNum === step) {
                indicators[i].classList.add('active');
            } else if (stepNum < step) {
                indicators[i].classList.add('completed');
            }
        }
    }

    function showWizardStep(view, step) {
        // Remove active class from all wizard step contents
        var contents = view.querySelectorAll('.es-wizard-step-content');
        for (var i = 0; i < contents.length; i++) {
            contents[i].classList.remove('active');
        }

        // Add active class to target step
        var target = view.querySelector('[data-es-wizard-content="' + step + '"]');
        if (!target) {
            return;
        }
        target.classList.add('active');

        // Update navigation buttons
        var backBtn = view.querySelector('[data-es-wiz-back]');
        var nextBtn = view.querySelector('[data-es-wiz-next]');
        if (backBtn) {
            backBtn.style.display = step === 1 ? 'none' : 'block';
            if (step > 1) backBtn.setAttribute('data-es-wiz-back', step - 1);
        }
        if (nextBtn) {
            if (step === 5) {
                nextBtn.style.display = 'none';
            } else {
                nextBtn.style.display = 'block';
                nextBtn.removeAttribute('disabled');
                nextBtn.setAttribute('data-es-wiz-next', step + 1);
                // Apple-style nav: step 3 shows "Finish & Sync" instead of "Next →"
                if (step === 3) {
                    nextBtn.textContent = 'Finish & Sync';
                    nextBtn.setAttribute('data-es-wiz-finish', 'true');
                    nextBtn.removeAttribute('data-es-wiz-next');
                } else {
                    nextBtn.textContent = 'Next \u2192';
                    nextBtn.removeAttribute('data-es-wiz-finish');
                    nextBtn.setAttribute('data-es-wiz-next', step + 1);
                }
            }
        }

        _wizardStep = step;
        updateWizardProgress(view, step);
    }

    function initWizardTab(view, cfg) {
        if (!cfg) return;
        function set(id, v) { var el = q(view, id); if (el && v != null) el.value = v; }
        function chk(id, v) { var el = q(view, id); if (el) el.checked = !!v; }

        // Populate wizard fields from config
        set('wiz-aio-url',              cfg.PrimaryManifestUrl || '');
        set('wiz-aio-backup-url',       cfg.SecondaryManifestUrl || '');
        set('wiz-aio-metadata-url',     cfg.AioMetadataBaseUrl || '');
        set('wiz-rss-feeds',            cfg.SystemRssFeedUrls || '');
        set('wiz-base-path',            cfg.SyncPathBase || '/media/infinitedrive');
        set('wiz-library-name-movies',   cfg.LibraryNameMovies || 'Streamed Movies');
        set('wiz-library-name-series',  cfg.LibraryNameSeries || 'Streamed Series');
        set('wiz-library-name-anime',   cfg.LibraryNameAnime || 'Streamed Anime');
        set('wiz-meta-lang',            cfg.MetadataLanguage || 'en');
        set('wiz-meta-country',         cfg.MetadataCountry || 'US');
        set('wiz-meta-img-lang',        cfg.ImageLanguage || 'en');
        set('wiz-emby-base-url',        cfg.EmbyBaseUrl || window.location.origin);
        chk('wiz-enable-anime',         cfg.EnableAnimeLibrary || false);
        chk('wiz-use-cinemeta',         cfg.EnableCinemetaCatalog != null ? cfg.EnableCinemetaCatalog : true);
        chk('wiz-enable-backup-aio',    cfg.EnableBackupAioStreams || false);

        // Show inferred Emby URL hint
        var inferredEl = q(view, 'wiz-emby-url-inferred');
        if (inferredEl) inferredEl.textContent = window.location.origin;

        // Backup AIOStreams toggle visibility
        var backupChk = q(view, 'wiz-enable-backup-aio');
        var backupFields = q(view, 'wiz-backup-aio-fields');
        function syncBackupVisibility() {
            if (backupFields) backupFields.style.display = backupChk && backupChk.checked ? 'block' : 'none';
        }
        if (backupChk) {
            // Use both 'change' and 'click' events for emby-checkbox compatibility
            backupChk.addEventListener('change', syncBackupVisibility);
            backupChk.addEventListener('click', syncBackupVisibility);
            // Initial sync
            syncBackupVisibility();
            // Also sync after a short delay to handle web component initialization
            setTimeout(syncBackupVisibility, 50);
        }

        // Anime library checkbox - enable/disable library name input
        var animeChk = q(view, 'wiz-enable-anime');
        var animeInput = q(view, 'wiz-library-name-anime');
        function syncAnimeState() {
            if (animeChk && animeInput) {
                animeInput.disabled = !animeChk.checked;
                animeInput.style.opacity = animeChk.checked ? '1' : '0.5';
            }
        }
        if (animeChk) {
            animeChk.addEventListener('change', syncAnimeState);
            animeChk.addEventListener('click', syncAnimeState);
            // Initial sync
            syncAnimeState();
            // Also sync after a short delay to handle web component initialization
            setTimeout(syncAnimeState, 50);
        }

        // Reset test success when URL changes
        var urlField = q(view, 'wiz-aio-url');
        if (urlField) {
            urlField.addEventListener('input', function() {
                _wizardTestSuccess = false;
            });
        }

        // Show completion screen if first run is complete
        if (cfg.IsFirstRunComplete) {
            var completeDiv = q(view, 'es-wizard-complete');
            var wizardNav = q(view, 'es-wizard-nav');
            var wizardProgress = q(view, '.es-wizard-progress');
            if (completeDiv) completeDiv.style.display = 'block';
            if (wizardNav) wizardNav.style.display = 'none';
            if (wizardProgress) wizardProgress.style.display = 'none';
            loadCompletionStats(view);
        } else {
            showWizardStep(view, 1);
        }
    }

    function wizBack(view, step) {
        // Stop catalog poll timer when navigating back
        if (_catalogPollTimer) { clearInterval(_catalogPollTimer); _catalogPollTimer = null; }
        showWizardStep(view, step);
    }

    function updateWizardSummary(view) {
        var basePath = esVal(view, 'wiz-base-path') || '/media/infinitedrive';
        if (!basePath) basePath = '/media/infinitedrive';

        setText(view, 'wiz-summary-movies', basePath + '/catalog/movies');
        setText(view, 'wiz-summary-series', basePath + '/catalog/shows');

        var enableAnime = esChk(view, 'wiz-enable-anime');
        var animeRow = q(view, 'wiz-summary-anime-row');
        if (enableAnime) {
            if (animeRow) animeRow.style.display = 'block';
            setText(view, 'wiz-summary-anime', basePath + '/anime');
        } else {
            if (animeRow) animeRow.style.display = 'none';
        }
    }

    function loadCompletionStats(view) {
        esFetch('/InfiniteDrive/Status')
            .then(function(r) { return r.json(); })
            .then(function(data) {
                if (data.CatalogItemCount) setText(view, 'es-complete-catalog', data.CatalogItemCount);
                if (data.StrmFileCount) setText(view, 'es-complete-strm', data.StrmFileCount);
                // Use SyncStates instead of CatalogSources (API change compatibility)
                if (data.SyncStates && data.SyncStates.length) {
                    setText(view, 'es-complete-sources', data.SyncStates.length);
                }
            })
            .catch(function() {});
    }

    // ── Sources tab ───────────────────────────────────────────────────────────
    function refreshSourcesTab(view) {
        var tbody = q(view, 'es-sources-body');
        if (!tbody) return;

        tbody.innerHTML = '<tr><td colspan="5" style="opacity:.4;text-align:center;padding:2em">Loading sources…</td></tr>';

        esFetch('/InfiniteDrive/Status')
            .then(function(r) { return r.json(); })
            .then(function(data) {
                // Use SyncStates instead of CatalogSources (API change compatibility)
                var sources = data.SyncStates || [];
                if (!sources.length) {
                    tbody.innerHTML = '<tr><td colspan="5" style="opacity:.4;text-align:center;padding:2em">No sources configured yet. Complete the Setup Wizard first.</td></tr>';
                    return;
                }

                var html = '';
                sources.forEach(function(src) {
                    // Use LastReachedAt instead of LastReachableAt (API change compatibility)
                    var statusBadge = src.LastReachedAt ? '<span class="es-badge es-badge-ok">Active</span>' : '<span class="es-badge es-badge-warn">Not reachable</span>';
                    var lastSync = src.LastSyncAt ? fmtRelative(new Date(src.LastSyncAt)) : 'Never';
                    // Type is inferred from SourceKey since it's not in the API
                    var type = 'source';
                    if (src.SourceKey.indexOf(':movie:') !== -1) type = 'movie';
                    else if (src.SourceKey.indexOf(':series:') !== -1) type = 'series';
                    else if (src.SourceKey.indexOf(':anime:') !== -1) type = 'anime';

                    html += '<tr>' +
                        '<td>' + esc(src.SourceKey || 'Unknown') + '</td>' +
                        '<td>' + esc(type) + '</td>' +
                        '<td>' + statusBadge + '</td>' +
                        '<td>' + lastSync + '</td>' +
                        '<td>' + (src.ItemCount || 0) + '</td>' +
                        '</tr>';
                });
                tbody.innerHTML = html;
            })
            .catch(function(err) {
                tbody.innerHTML = '<tr><td colspan="5" style="opacity:.4;text-align:center;padding:2em">Failed to load sources: ' + esc(err.message) + '</td></tr>';
            });
    }

    // ── Tab switching ─────────────────────────────────────────────────────────
    function showTab(view, name) {
        var tabMap = {
            'setup': 'setup',
            'overview': 'overview',
            'health': 'overview',
            'settings': 'settings',
            'content': 'content',
            'marvin': 'marvin',
            'improbability': 'marvin',
            'blocked': 'content',
            'content-mgmt': 'content'
        };
        var mappedName = tabMap[name] || name;

        ['setup','overview','settings','content','marvin'].forEach(function(t) {
            var c = q(view, 'es-tab-content-' + t);
            if (c) c.classList.toggle('active', mappedName === t);
        });
        var btns = view.querySelectorAll('[data-es-tab]');
        for (var i = 0; i < btns.length; i++) {
            btns[i].classList.toggle('active', btns[i].getAttribute('data-es-tab') === mappedName);
        }
        if (name === 'improbability' || name === 'marvin') {
            loadImprobabilityStatus(view);
        } else if (name === 'settings') {
            // v0.65.5: Fix System Status Offline Bug — save config before switching to settings
            if (_loadedConfig && _unsavedChanges) {
                ApiClient.updatePluginConfiguration(pluginId, _loadedConfig)
                    .then(function() {
                        _unsavedChanges = false;
                        refreshDashboard(view);
                    })
                    .catch(function() {});
            } else {
                refreshDashboard(view);
            }
            setDashboardView(view, 'quick');
            if (!_dashInterval) _dashInterval = setInterval(function() { refreshDashboard(view); }, 10000);
        } else {
            if (_dashInterval) { clearInterval(_dashInterval); _dashInterval = null; }
        }
        if (name === 'settings' && _loadedConfig) { populateSettings(view, _loadedConfig); }
        if (name === 'setup' && _loadedConfig) { initWizardTab(view, _loadedConfig); }
        if (name === 'overview') { refreshSourcesTab(view); }
        if (name === 'blocked') { loadBlockedItems(view); }
        if (name === 'content-mgmt') { loadContentMgmtSources(view); }
    }

    // ── Sources tab ───────────────────────────────────────────────────────────
    function initSourcesTab(view, cfg) {
        if (!cfg) return;
        function set(id, v) { var el = q(view, id); if (el && v != null) el.value = v; }
        function chk(id, v) { var el = q(view, id); if (el) el.checked = !!v; }
        set('src-aio-url',      cfg.PrimaryManifestUrl   || '');
        set('src-duck-url',     cfg.SecondaryManifestUrl || '');
        chk('src-use-cinemeta', cfg.EnableCinemetaCatalog != null ? cfg.EnableCinemetaCatalog : true);
        set('src-cinemeta-url', cfg.CinemetaUrl || 'https://v3-cinemeta.strem.io/manifest.json');
        updateSourcesStatusStrip(view, cfg);
    }

    function updateSourcesStatusStrip(view, cfg) {
        function setStrip(dotId, textId, value) {
            var dot  = q(view, dotId);
            var text = q(view, textId);
            if (!dot || !text) return;
            if (value) {
                dot.className  = 'es-dot es-dot-ok';
                text.textContent = 'Configured';
            } else {
                dot.className  = 'es-dot es-dot-none';
                text.textContent = 'Not configured';
            }
        }
        setStrip('es-src-dot-aio',      'es-src-status-aio',      cfg && cfg.PrimaryManifestUrl);
        setStrip('es-src-dot-duck',     'es-src-status-duck',     cfg && cfg.SecondaryManifestUrl);
        setStrip('es-src-dot-cinemeta', 'es-src-status-cinemeta', cfg && cfg.EnableCinemetaCatalog && cfg.CinemetaUrl);
    }

    function saveSourcesTab(view) {
        var aioUrl      = esVal(view, 'src-aio-url').trim();
        var duckUrl     = esVal(view, 'src-duck-url').trim();
        var useCinemeta = esChk(view, 'src-use-cinemeta');
        var cinemetaUrl = esVal(view, 'src-cinemeta-url').trim() || 'https://v3-cinemeta.strem.io/manifest.json';

        // Merge into existing config
        var base = _loadedConfig ? JSON.parse(JSON.stringify(_loadedConfig)) : {};
        base.PrimaryManifestUrl   = aioUrl;
        base.SecondaryManifestUrl = duckUrl;
        base.EnableCinemetaCatalog = useCinemeta;
        base.CinemetaUrl           = cinemetaUrl;
        // Also sync to cfg-manifest-url for settings tab compatibility
        var cfgEl = q(view, 'cfg-manifest-url');
        if (cfgEl) cfgEl.value = aioUrl;

        ApiClient.updatePluginConfiguration(pluginId, base)
            .then(function() {
                _loadedConfig = base;
                updateSourcesStatusStrip(view, base);

                // Auto-create directories and libraries
                var cfg = {
                    SyncPathMovies: base.SyncPathMovies || (base.BaseSyncPath || '/media/infinitedrive') + '/movies',
                    SyncPathShows:  base.SyncPathShows  || (base.BaseSyncPath || '/media/infinitedrive') + '/shows',
                    BaseSyncPath:   base.BaseSyncPath || '/media/infinitedrive'
                };
                createEmbyLibraries(cfg);

                // Show progress bar
                var prog = q(view, 'es-sync-progress');
                if (prog) prog.style.display = 'block';
                animateSyncProgress(view);

                // Trigger catalog sync
                esFetch('/InfiniteDrive/Trigger?task=catalog_sync', {method:'POST'}).catch(function(){});

                // Start catalog progress polling
                startCatalogPoll(view, 'cfg');

                // Show catalog picker when catalogs load
                setTimeout(function() {
                    var picker = q(view, 'es-catalog-picker-setup');
                    if (picker) picker.style.display = 'block';
                    loadCatalogs(view, 'cfg');
                }, 5000);

                // Re-fetch full config
                ApiClient.getPluginConfiguration(pluginId)
                    .then(function(fullCfg) { _loadedConfig = fullCfg; })
                    .catch(function() {});
            })
            .catch(function(err) { Dashboard.alert('Save failed: ' + (err && err.message || err)); });
    }

    function testSource(view, type) {
        var urlMap    = { aio: 'src-aio-url', duck: 'src-duck-url', cinemeta: 'src-cinemeta-url' };
        var statusMap = { aio: 'src-aio-status', duck: 'src-duck-status', cinemeta: 'src-cinemeta-status' };
        var dotMap    = { aio: 'es-src-dot-aio', duck: 'es-src-dot-duck', cinemeta: 'es-src-dot-cinemeta' };
        var stripMap  = { aio: 'es-src-status-aio', duck: 'es-src-status-duck', cinemeta: 'es-src-status-cinemeta' };

        var url       = esVal(view, urlMap[type]).trim();
        var statusEl  = q(view, statusMap[type]);
        var dotEl     = q(view, dotMap[type]);
        var stripEl   = q(view, stripMap[type]);

        if (!statusEl) return;
        if (!url) {
            statusEl.textContent = 'Enter a URL first.';
            statusEl.style.color = '#c87800';
            return;
        }

        // Basic URL format validation
        var looksValid = /^https?:\/\/.+/i.test(url);
        var hasManifest = url.indexOf('manifest.json') !== -1;
        if (!looksValid) {
            statusEl.textContent = '✗ Must be a valid https:// URL.';
            statusEl.style.color = '#dc3545';
            if (dotEl) dotEl.className = 'es-dot es-dot-error';
            if (stripEl) stripEl.textContent = 'Invalid URL';
            return;
        }

        statusEl.textContent = 'Checking…';
        statusEl.style.color = '';

        // Try to test via server-side TestUrl endpoint
        esFetch('/InfiniteDrive/TestUrl', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ManifestUrl: url })
        })
            .then(function(r) { return r.json(); })
            .then(function(data) {
                if (data.Ok) {
                    statusEl.textContent = '✓ Connected' + (data.LatencyMs ? ' (' + data.LatencyMs + ' ms)' : '');
                    statusEl.style.color = '#28a745';
                    if (dotEl) dotEl.className = 'es-dot es-dot-ok';
                    if (stripEl) stripEl.textContent = 'Connected';
                } else {
                    statusEl.textContent = '✗ ' + (data.Message || 'Could not connect');
                    statusEl.style.color = '#dc3545';
                    if (dotEl) dotEl.className = 'es-dot es-dot-error';
                    if (stripEl) stripEl.textContent = 'Error';
                }
            })
            .catch(function() {
                // Fallback: if server endpoint not available, just validate URL format
                var msg = hasManifest ? 'URL looks valid — save and run Test All to verify' : 'URL may be missing /manifest.json';
                statusEl.textContent = msg;
                statusEl.style.color = hasManifest ? '#c87800' : '#dc3545';
                if (dotEl) dotEl.className = hasManifest ? 'es-dot es-dot-warn' : 'es-dot es-dot-error';
            });
    }

    function testAllSources(view) {
        testSource(view, 'aio');
        testSource(view, 'duck');
        testSource(view, 'cinemeta');
    }

    // ── Accordion toggle ──────────────────────────────────────────────────────
    function toggleAccordion(el) {
        // el is the .es-accordion-hdr element; parent is .es-accordion
        var accordion = el.parentElement;
        if (!accordion) return;
        accordion.classList.toggle('es-accordion-open');
    }

    // ── Dashboard view toggle ────────────────────────────────────────────────────
    function setDashboardView(view, viewType) {
        var debugSections = view.querySelectorAll('[data-es-dashboard-debug="true"]');
        var isDebug = viewType === 'debug';
        for (var i = 0; i < debugSections.length; i++) {
            debugSections[i].classList.toggle('es-debug-visible', isDebug);
        }
        q(view, 'es-view-quick').classList.toggle('active', viewType === 'quick');
        q(view, 'es-view-debug').classList.toggle('active', viewType === 'debug');
    }

    // ── Wizard ────────────────────────────────────────────────────────────────
    var _wizardTestSuccess = false;

    function wizNext(view, step) {
        // Validate and save data from current step before moving to next
        if (step === 2) {
            // Validate step 1 data (provider URL) before proceeding to step 2
            var url = esVal(view, 'wiz-aio-url');
            if (!url || url === 'https://' || url === 'http://') {
                alert('Please enter a valid AIOStreams manifest URL and click Test Connection first.');
                return;
            }
            // Check if test connection succeeded
            if (!_wizardTestSuccess) {
                alert('Please click Test Connection and wait for it to succeed before continuing.');
                return;
            }
        }

        if (step === 3) {
            // Auto-load catalogs when entering step 3 — no button required
            loadCatalogs(view, 'wiz');
        }

        showWizardStep(view, step);
    }

    // ── Manifest URL auto-parse ───────────────────────────────────────────────
    function parseManifestUrl(url, view, prefix) {
        var resultEl = q(view, prefix + '-manifest-parse-result');
        function showParseResult(ok, msg) {
            if (!resultEl) return;
            resultEl.textContent = msg;
            resultEl.style.display = 'block';
            resultEl.className = ok ? 'es-parse-ok' : 'es-parse-warn';
        }
        if (!url || url === 'https://' || url === 'http://') {
            if (resultEl) resultEl.style.display = 'none';
            return;
        }
        // Token may contain '/' (standard base64) — use .+ not [^/]+
        var m = url.trim().replace(/\/+$/, '').match(
            /^(https?:\/\/[^\/]+)(\/stremio\/([^\/]+)\/(.+))?\/manifest\.json$/i
        );
        if (!m) {
            if (url.length > 10) showParseResult(false, '⚠ URL not recognised — expected …/stremio/uuid/token/manifest.json');
            return;
        }
        var set = function(id, v) { var el = q(view, id); if (el) el.value = v || ''; };
        set(prefix + '-aio-url', m[1]);
        set(prefix + '-uuid',    m[3]);
        set(prefix + '-token',   m[4]);
        showParseResult(true, m[3] ? '✓ Parsed — base URL, UUID and token extracted' : '✓ Parsed — unauthenticated URL');
    }
    function bindManifestField(view, fieldId, prefix) {
        var el = q(view, fieldId);
        if (!el) return;
        var debounceTimer = null;
        var parse = function() { parseManifestUrl(el.value, view, prefix); };
        var debouncedParse = function() {
            clearTimeout(debounceTimer);
            debounceTimer = setTimeout(parse, 300);
        };
        el.addEventListener('blur',   parse);
        el.addEventListener('change', debouncedParse);
        el.addEventListener('paste',  debouncedParse);
        el.addEventListener('keyup',  function(e) { if (e.key === 'Enter') parse(); else debouncedParse(); });
    }

    // ── Error guidance helper (Sprint 14: UX-ERROR-GUIDANCE) ───────────────────
    function makeErrorGuidance(errorMsg) {
        if (!errorMsg) return '<span>✗ Connection failed</span>';
        var lower = errorMsg.toLowerCase();
        var html = '<div style="margin-bottom:.6em"><strong style="color:#dc3545">✗ ' + esc(errorMsg) + '</strong></div>';
        if (lower.indexOf('certificate') !== -1 || lower.indexOf('ssl') !== -1) {
            html += '<div style="font-size:.9em;line-height:1.5"><strong>SSL Certificate Error</strong><br/>' +
                '🔧 How to fix:<br/>' +
                '<ol style="margin:.4em 0 0 1.2em;padding:0"><li>Check your manifest URL is correct (no typos, https://)</li>' +
                '<li>If self-hosted: ensure your domain has a valid SSL certificate</li>' +
                '<li>Try disabling SSL verification in "Connection details" (Advanced)</li></ol>' +
                '<a href="https://github.com/anthonybudd/stremio-addon-sdk/wiki" target="_blank" style="color:var(--theme-button-background,#00a4dc);font-size:.85em">📖 See docs</a></div>';
        } else if (lower.indexOf('refused') !== -1 || lower.indexOf('unreachable') !== -1) {
            html += '<div style="font-size:.9em;line-height:1.5"><strong>Connection Refused</strong><br/>' +
                '🔧 How to fix:<br/>' +
                '<ol style="margin:.4em 0 0 1.2em;padding:0"><li>Check the server is running at that URL</li>' +
                '<li>Verify the port number is correct (usually 7860 for local, or custom domain for hosted)</li>' +
                '<li>Check firewall/network settings allow access</li></ol></div>';
        } else if (lower.indexOf('timeout') !== -1) {
            html += '<div style="font-size:.9em;line-height:1.5"><strong>Connection Timeout</strong><br/>' +
                '🔧 How to fix:<br/>' +
                '<ol style="margin:.4em 0 0 1.2em;padding:0"><li>Check your internet connection</li>' +
                '<li>Try again in a few seconds (server may be starting up)</li>' +
                '<li>If self-hosted, check the server is not overloaded</li></ol></div>';
        } else {
            html += '<div style="font-size:.9em;opacity:.7">📖 Still stuck? Check your manifest URL format and network connection, then try again.</div>';
        }
        return html;
    }

    // ── Friendly error message mapper (Sprint 67: v0.67.1) ─────────────────────
    // Maps fetch/JSON parse errors to user-friendly messages
    function mapFetchError(err) {
        if (!err) return 'Connection failed';
        var msg = err.message || String(err);
        var lower = msg.toLowerCase();
        // JSON parse errors
        if (lower.indexOf('unexpected token') !== -1 || lower.indexOf('json') !== -1) {
            return 'Unexpected response from provider. Check the manifest URL and try again.';
        }
        // Network errors
        if (lower.indexOf('failed to fetch') !== -1 || lower.indexOf('networkerror') !== -1) {
            return 'Could not reach the server. Check your network connection.';
        }
        if (lower.indexOf('timeout') !== -1) {
            return 'Connection timed out. Is your provider reachable?';
        }
        // HTTP status codes in response
        if (msg.indexOf('401') !== -1 || msg.indexOf('403') !== -1) {
            return 'Authentication failed. Check your manifest token.';
        }
        if (msg.indexOf('404') !== -1) {
            return 'Manifest URL not found. Verify the URL is correct.';
        }
        if (msg.match(/\b5\d\d\b/)) {
            return 'Provider returned a server error. Try again shortly.';
        }
        // Default: show sanitized message
        return msg.length > 100 ? 'Connection failed. Check the URL and try again.' : msg;
    }

    // ── Test connection ───────────────────────────────────────────────────────
    function testConnection(view) {
        var resultEl = q(view, 'es-conn-result');
        if (!resultEl) return;
        resultEl.style.display = 'block';
        resultEl.className = 'es-alert es-alert-info';
        resultEl.textContent = 'Testing connection…';

        var manifestUrl = esVal(view, 'es-manifest-url');
        var baseUrl     = esVal(view, 'es-aio-url');
        var uuid        = esVal(view, 'es-uuid');
        var token       = esVal(view, 'es-token');

        // If only manifest URL is given, try to parse it first
        if (manifestUrl && !baseUrl) {
            parseManifestUrl(manifestUrl, view, 'es');
            baseUrl = esVal(view, 'es-aio-url');
            uuid    = esVal(view, 'es-uuid');
            token   = esVal(view, 'es-token');
        }

        if (!manifestUrl && !baseUrl) {
            resultEl.className = 'es-alert es-alert-warn';
            resultEl.textContent = '⚠ Enter a Manifest URL or Base URL first.';
            return;
        }

        // POST body avoids URL-encoding issues with base64 tokens containing '/', '+', '='
        // and keeps credentials out of server logs.
        esFetch('/InfiniteDrive/TestUrl', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ Url: baseUrl, Uuid: uuid, Token: token, ManifestUrl: manifestUrl })
        })
            .then(function(r) { return r.json(); })
            .then(function(data) {
                if (data.Ok) {
                    resultEl.className = 'es-alert es-alert-success';
                    resultEl.textContent = '✓ Connected in ' + (data.LatencyMs || '?') + ' ms'
                        + (data.AddonName ? ' — ' + data.AddonName : '');
                    var card = q(view, 'es-wiz-catalog-addon-card');
                    if (card) card.style.display = data.IsStreamOnly ? 'block' : 'none';
                } else {
                    resultEl.className = 'es-alert es-alert-error';
                    resultEl.innerHTML = makeErrorGuidance(data.Message || 'Connection failed');
                }
            })
            .catch(function(err) {
                resultEl.className = 'es-alert es-alert-error';
                var friendlyMsg = mapFetchError(err);
                resultEl.innerHTML = makeErrorGuidance(friendlyMsg);
            });
    }

    // ── Version badge: fetch from /InfiniteDrive/Status and populate ────────────
    function loadVersionBadge(view) {
        var els = view.querySelectorAll('#es-plugin-version');
        if (!els.length) return;
        esFetch('/InfiniteDrive/Status')
            .then(function(r) { return r.ok ? r.json() : null; })
            .then(function(data) {
                if (data && data.Version) {
                    // Trim to semver (major.minor.patch) for cleaner display
                    var semver = data.Version.match(/^\d+\.\d+\.\d+/);
                    var versionText = semver ? semver[0] : data.Version;
                    // Don't show 0.0.0 - use fallback instead
                    if (versionText === '0.0.0' || versionText === '0.0') {
                        versionText = null;
                    }
                    for (var i = 0; i < els.length; i++) {
                        els[i].textContent = versionText ? 'v' + versionText : 'v?.?.?';
                        els[i].title = versionText ? '' : 'Version unavailable — check plugin manifest';
                    }
                } else {
                    // Fallback: show v?.?.? with tooltip
                    for (var i = 0; i < els.length; i++) {
                        els[i].textContent = 'v?.?.?';
                        els[i].title = 'Version unavailable — check plugin manifest';
                    }
                }
            })
            .catch(function() {
                // On fetch error, show fallback
                for (var i = 0; i < els.length; i++) {
                    els[i].textContent = 'v?.?.?';
                    els[i].title = 'Version unavailable — check plugin manifest';
                }
            });
    }

    // ── Load / populate config ────────────────────────────────────────────────
    function loadConfig(view) {
        loading.show();
        loadVersionBadge(view);
        ApiClient.getPluginConfiguration(pluginId)
            .then(function(cfg) {
                _loadedConfig = cfg;

                // v0.65.1: Base URL auto-detect and warning
                var warningEl = q(view, 'es-base-url-warning');
                var suggestedEl = q(view, 'es-base-url-suggested');

                if (!cfg.EmbyBaseUrl || cfg.EmbyBaseUrl === 'http://127.0.0.1:8096' || cfg.EmbyBaseUrl === 'http://localhost:8096') {
                    cfg.EmbyBaseUrl = window.location.origin;
                    // Auto-save the corrected URL
                    ApiClient.updatePluginConfiguration(pluginId, cfg)
                        .then(function() {
                            if (warningEl && suggestedEl) {
                                suggestedEl.textContent = cfg.EmbyBaseUrl;
                                warningEl.style.display = 'block';
                            }
                        })
                        .catch(function() {});
                } else if (cfg.EmbyBaseUrl && (cfg.EmbyBaseUrl.indexOf('localhost') !== -1 || cfg.EmbyBaseUrl.indexOf('127.0.0.1') !== -1)) {
                    // Show warning if still using localhost
                    if (warningEl && suggestedEl) {
                        suggestedEl.textContent = window.location.origin;
                        warningEl.style.display = 'block';
                    }
                } else {
                    // Hide warning if URL is valid (not localhost)
                    if (warningEl) warningEl.style.display = 'none';
                }

                populateWizard(view, cfg);
                populateSettings(view, cfg);
                initSourcesTab(view, cfg);
                initWizardTab(view, cfg);
                // Default to setup; if already configured show setup
                if (cfg.IsFirstRunComplete) {
                    showTab(view, 'setup');
                }
                // Show admin-only tabs for admin users
                ApiClient.getCurrentUser().then(function(user) {
                    if (user && user.Policy && user.Policy.IsAdministrator) {
                        view.querySelectorAll('.es-admin-tab').forEach(function(t) {
                            t.style.display = '';
                        });
                    }
                }).catch(function() {});
                loading.hide();
            })
            .catch(function() { loading.hide(); });
    }

    function populateWizard(view, cfg) {
        function set(id, v) { var el = q(view, id); if (el && v != null) el.value = v; }
        function chk(id, v) { var el = q(view, id); if (el) el.checked = !!v; }
        set('es-manifest-url', cfg.PrimaryManifestUrl);
        // Sprint 14: Discover channel toggle
        chk('es-enable-discover',      cfg.EnableDiscoverChannel != null ? cfg.EnableDiscoverChannel : true);
        // Sprint 11: Single BaseSyncPath — derive subpaths
        var base = cfg.BaseSyncPath || '/media/infinitedrive';
        set('es-base-path', base);
        var movies = base + '/movies';
        var shows  = base + '/shows';
        var elM = q(view, 'es-derived-movies');
        var elS = q(view, 'es-derived-shows');
        if (elM) elM.textContent = movies;
        if (elS) elS.textContent = shows;
        set('es-emby-base',    cfg.EmbyBaseUrl || window.location.origin);
        set('es-emby-apikey',  cfg.EmbyApiKey || '');
        set('es-meta-lang-dropdown',       cfg.MetadataLanguage            || 'en');
        set('es-meta-country-dropdown',    cfg.MetadataCertificationCountry || 'US');
        set('es-meta-img-lang-dropdown',   cfg.MetadataImageLanguage        || 'en');
        chk('es-enable-aio',           cfg.EnableAioStreamsCatalog);
        loadCatalogLimits(cfg);
        if (cfg.PrimaryManifestUrl || cfg.SecondaryManifestUrl) loadCatalogs(view, 'wiz');
    }

    function populateSettings(view, cfg) {
        function set(id, v) {
            var el = q(view, id); if (!el) return;
            if (el.type === 'checkbox') el.checked = !!v;
            else el.value = v != null ? v : '';
        }
        set('cfg-manifest-url',           cfg.PrimaryManifestUrl);
        set('cfg-aio-backup-url',        cfg.SecondaryManifestUrl || '');
        set('cfg-aio-metadata-url',     cfg.AioMetadataBaseUrl);
        set('cfg-rss-feeds',             cfg.SystemRssFeedUrls || '');
        chk('cfg-enable-backup-aio',     cfg.EnableBackupAioStreams || false);

        // Sync backup AIOStreams visibility
        var backupChk = q(view, 'cfg-enable-backup-aio');
        var backupFields = q(view, 'cfg-backup-aio-fields');
        function syncCfgBackup() {
            if (backupFields) backupFields.style.display = backupChk && backupChk.checked ? 'block' : 'none';
        }
        if (backupChk) {
            // Use both 'change' and 'click' events for emby-checkbox compatibility
            backupChk.addEventListener('change', syncCfgBackup);
            backupChk.addEventListener('click', syncCfgBackup);
            // Initial sync
            syncCfgBackup();
            // Also sync after a short delay to handle web component initialization
            setTimeout(syncCfgBackup, 50);
        }
        // Sprint 11: BaseSyncPath with derived subpaths
        var base = cfg.BaseSyncPath || '/media/infinitedrive';
        set('cfg-base-path', base);
        var elM = q(view, 'cfg-derived-movies');
        var elS = q(view, 'cfg-derived-shows');
        if (elM) elM.textContent = base + '/movies';
        if (elS) elS.textContent = base + '/shows';

        // v0.65.12: Populate readonly path displays
        var rom = q(view, 'cfg-readonly-movies');
        var ros = q(view, 'cfg-readonly-series');
        var roa = q(view, 'cfg-readonly-anime');
        var roaRow = q(view, 'cfg-readonly-anime-row');
        if (rom) rom.textContent = base + '/movies';
        if (ros) ros.textContent = base + '/shows';
        if (roa && roaRow && cfg.EnableAnimeLibrary && cfg.SyncPathAnime) {
            roa.textContent = cfg.SyncPathAnime;
            roaRow.style.display = 'block';
        }

        set('cfg-emby-base',              cfg.EmbyBaseUrl || window.location.origin);
        set('cfg-sig-validity',            cfg.SignatureValidityDays || 365);
        set('cfg-meta-lang-dropdown',       cfg.MetadataLanguage             || 'en');
        set('cfg-meta-country-dropdown',    cfg.MetadataCertificationCountry  || 'US');
        set('cfg-meta-img-lang-dropdown',   cfg.MetadataImageLanguage         || 'en');
        set('cfg-enable-aio',             cfg.EnableAioStreamsCatalog);
        set('cfg-catalog-ids',            cfg.AioStreamsCatalogIds);
        set('cfg-stream-types',           cfg.AioStreamsAcceptedStreamTypes);
        // Stream type checkboxes
        var stypes = (cfg.AioStreamsAcceptedStreamTypes || '').split(',').map(function(s){return s.trim();}).filter(Boolean);
        ['debrid','usenet','torrent','http','live'].forEach(function(t) {
            var cb = q(view, 'cfg-stype-' + t);
            if (cb) cb.checked = stypes.length > 0 && stypes.indexOf(t) !== -1;
        });
        set('cfg-library-name-movies',    cfg.LibraryNameMovies || 'Streamed Movies');
        set('cfg-library-name-series',    cfg.LibraryNameSeries || 'Streamed Series');
        set('cfg-cache-lifetime',         cfg.CacheLifetimeMinutes);
        set('cfg-api-budget',             cfg.ApiDailyBudget);
        set('cfg-max-concurrent',         cfg.MaxConcurrentResolutions);
        set('cfg-item-cap',               cfg.CatalogItemCap);
        set('cfg-sync-interval',          cfg.CatalogSyncIntervalHours);
        set('cfg-sync-hour',              cfg.SyncScheduleHour != null ? cfg.SyncScheduleHour : 3);
        set('cfg-candidates-per-provider', cfg.CandidatesPerProvider != null ? cfg.CandidatesPerProvider : 3);
        set('cfg-sync-resolve-timeout',   cfg.SyncResolveTimeoutSeconds);
        set('cfg-provider-priority',      cfg.ProviderPriorityOrder);
        set('cfg-next-up-lookahead',      cfg.NextUpLookaheadEpisodes);
        set('cfg-proxy-mode',             cfg.ProxyMode);
        set('cfg-max-proxy',              cfg.MaxConcurrentProxyStreams);
        set('cfg-delete-strm-on-readoption', cfg.DeleteStrmOnReadoption);
        set('cfg-dont-panic',             cfg.DontPanic);
        set('cfg-plugin-secret',          cfg.PluginSecret);

        // Sprint 209: Parental Controls
        set('cfg-tmdb-api-key',           cfg.TmdbApiKey || '');
        chk('cfg-block-unrated',         cfg.BlockUnratedForRestricted !== false);
        updateFilterStatus(view, cfg.TmdbApiKey);

        // Sprint 64: Anime library config
        checkAnimePluginStatus(view);
        set('cfg-enable-anime',           cfg.EnableAnimeLibrary);
        set('cfg-anime-path',             cfg.SyncPathAnime || '/media/infinitedrive/anime');
        toggleAnimePathVisibility(view, cfg.EnableAnimeLibrary);

        // v0.60.3: PluginSecret warning — show banner if empty or too short
        var secretWarn = view.querySelector('#es-secret-empty-warning');
        if (secretWarn) {
            secretWarn.style.display = (!cfg.PluginSecret || cfg.PluginSecret.length < 10) ? '' : 'none';
        }

        // PluginSecret first-run warning (v0.60.3)
        var secretWarn = q(view, 'es-plugin-secret-warning');
        if (secretWarn) {
            var isAuto = cfg.PluginSecret && cfg._pluginSecretAutoGenerated;
            var isEmpty = !cfg.PluginSecret;
            secretWarn.style.display = (isEmpty || isAuto) ? '' : 'none';
        }        var baseUrl = (cfg.EmbyBaseUrl || window.location.origin).replace(/\/$/, '');
        var wuEl = q(view, 'cfg-webhook-url-display');
        if (wuEl) wuEl.value = baseUrl + '/InfiniteDrive/Webhook/Sync';
        loadCatalogLimits(cfg);
        if (cfg.PrimaryManifestUrl || cfg.SecondaryManifestUrl) loadCatalogs(view, 'cfg');
    }

    function loadCatalogLimits(cfg) {
        if (!cfg.CatalogItemLimitsJson) return;
        try {
            var lims = JSON.parse(cfg.CatalogItemLimitsJson);
            if (lims && typeof lims === 'object') {
                Object.keys(lims).forEach(function(k) { if (lims[k] > 0) _catalogLimits[k] = lims[k]; });
            }
        } catch(e) {}
    }

    // ── Sprint 209: Parental Controls helpers ────────────────────────────────
    function updateFilterStatus(view, tmdbKey) {
        var statusEl = q(view, 'es-filter-status');
        if (!statusEl) return;

        var indicator = statusEl.querySelector('.es-status-indicator');
        var text = statusEl.querySelector('.es-status-text');

        if (tmdbKey && tmdbKey.trim().length > 0) {
            if (indicator) {
                indicator.className = 'es-status-indicator es-status-active';
                indicator.textContent = '✅';
            }
            if (text) text.textContent = 'TMDB API key configured — parental filtering is active';
        } else {
            if (indicator) {
                indicator.className = 'es-status-indicator es-status-inactive';
                indicator.textContent = '⚠️';
            }
            if (text) text.textContent = 'No TMDB key configured — parental filtering is inactive';
        }
    }

    // ── Sprint 64: Anime library helpers ────────────────────────────────────
    function checkAnimePluginStatus(view) {
        esFetch('/InfiniteDrive/AnimePluginStatus')
            .then(function(r) { return r.json(); })
            .then(function(d) {
                var toggle = q(view, 'cfg-enable-anime');
                var warning = q(view, 'es-anime-plugin-warning');
                if (d.Installed) {
                    if (warning) warning.style.display = 'none';
                } else {
                    if (warning) warning.style.display = '';
                }
            })
            .catch(function() { /* endpoint unavailable — no warning */ });
    }

    function toggleAnimePathVisibility(view, enabled) {
        var wrapper = q(view, 'es-anime-path-wrapper');
        if (wrapper) wrapper.style.display = enabled ? '' : 'none';
    }

    // ── Save config ───────────────────────────────────────────────────────────
    function saveAndSync(view) {
        var btn = q(view, 'es-save-btn');
        if (btn) btn.disabled = true;

        var manifestUrl = esVal(view, 'es-manifest-url');
        var baseUrl     = esVal(view, 'es-aio-url');
        var uuid        = esVal(view, 'es-uuid');
        var token       = esVal(view, 'es-token');
        var enableAio   = esChk(view, 'es-enable-aio');

        var cfg = {
            PrimaryManifestUrl:      manifestUrl,
            SecondaryManifestUrl:    '',
            SyncPathMovies:          esVal(view, 'es-base-path') + '/movies',
            SyncPathShows:           esVal(view, 'es-base-path') + '/shows',
            BaseSyncPath:            esVal(view, 'es-base-path'),
            EmbyBaseUrl:             esVal(view, 'es-emby-base'),
            EmbyApiKey:              esVal(view, 'es-emby-apikey'),
            MetadataLanguage:              esVal(view, 'es-meta-lang-dropdown')     || 'en',
            MetadataCertificationCountry:  esVal(view, 'es-meta-country-dropdown')  || 'US',
            MetadataImageLanguage:         esVal(view, 'es-meta-img-lang-dropdown') || 'en',
            LibraryNameMovies:       esVal(view, 'es-library-name-movies') || 'Streamed Movies',
            LibraryNameSeries:       esVal(view, 'es-library-name-series') || 'Streamed Series',
            EnableDiscoverChannel:   esChk(view, 'es-enable-discover'),
            EnableAioStreamsCatalog: enableAio,
            AioStreamsCatalogIds:    getSelectedCatalogIds(view, 'wiz'),
            CatalogItemLimitsJson:   getCatalogLimitsJson(),
            IsFirstRunComplete:      true
        };
        ApiClient.updatePluginConfiguration(pluginId, cfg)
            .then(function() {
                _loadedConfig = cfg;
                var prog = q(view, 'es-sync-progress');
                if (prog) prog.style.display = 'block';
                animateSyncProgress(view);
                // Create Emby libraries automatically
                createEmbyLibraries(cfg);
                esFetch('/InfiniteDrive/Trigger?task=catalog_sync', {method:'POST'}).catch(function(){});
            })
            .then(function() {
                // After wizard save, re-fetch full config so Settings tab shows all fields
                ApiClient.getPluginConfiguration(pluginId)
                    .then(function(fullCfg) {
                        _loadedConfig = fullCfg;
                    })
                    .catch(function() {
                        // If re-fetch fails, keep wizard config but warn
                        console.warn('[InfiniteDrive] Failed to re-fetch full config after wizard save');
                    });
            })
            .catch(function() { if (btn) btn.disabled = false; });
    }

    function animateSyncProgress(view) {
        var bar = q(view, 'es-sync-bar'), msg = q(view, 'es-sync-msg');
        // Sprint 14: UX-PROGRESS-DELIGHT — enthusiastic progress messages
        var steps = [
            [10,'💾 Saving your configuration…'],
            [30,'🔍 Fetching AIOStreams manifest…'],
            [50,'✍️ Writing .strm files to your library…'],
            [75,'📚 Organizing your sources…'],
            [90,'🔎 Triggering Emby library scan…'],
            [100,'🎉 All set! Your library is being built…']
        ];
        var i = 0;
        var iv = setInterval(function() {
            if (i >= steps.length) { clearInterval(iv); return; }
            if (bar) bar.style.width = steps[i][0] + '%';
            if (msg) { msg.textContent = steps[i][1]; if (steps[i][0] === 100) msg.className = 'es-alert es-alert-success'; }
            if (steps[i][0] === 100) {
                var banner = q(view, 'es-wizard-complete-banner');
                if (banner) banner.style.display = 'block';
                var welcome = q(view, 'es-welcome-banner');
                if (welcome) welcome.style.display = 'block';
            }
            i++;
        }, 1800);
    }

    // ── Auto-create Emby libraries on first run ────────────────────────────────────
    function createEmbyLibraries(cfg) {
        if (!cfg || !cfg.SyncPathMovies || !cfg.SyncPathShows) return;

        // STEP 1: Create directories on disk first (Emby won't create libraries if paths don't exist)
        esFetch('/InfiniteDrive/Setup/CreateDirectories', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                MoviesPath: cfg.SyncPathMovies,
                ShowsPath: cfg.SyncPathShows
            })
        })
        .then(function(res) { return res.json(); })
        .then(function(dirRes) {
            console.log('[InfiniteDrive] Directories created:', dirRes);

            // STEP 2: Only create libraries after directories exist
            // Create Movies library
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('Libraries/VirtualFolders'),
                data: JSON.stringify({
                    Name: cfg.LibraryNameMovies || 'Streamed Movies',
                    CollectionType: 'movies',
                    Paths: [cfg.SyncPathMovies]
                }),
                contentType: 'application/json'
            }).then(function() {
                console.log('[InfiniteDrive] Movies library created successfully');
            }).catch(function(err) {
                console.log('[InfiniteDrive] Movies library creation failed or already exists:', err);
            });

            // Create Shows library
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('Libraries/VirtualFolders'),
                data: JSON.stringify({
                    Name: cfg.LibraryNameSeries || 'Streamed Series',
                    CollectionType: 'tvshows',
                    Paths: [cfg.SyncPathShows]
                }),
                contentType: 'application/json'
            }).then(function() {
                console.log('[InfiniteDrive] Shows library created successfully');
            }).catch(function(err) {
                console.log('[InfiniteDrive] Shows library creation failed or already exists:', err);
            });
        })
        .catch(function(err) {
            console.error('[InfiniteDrive] Failed to create directories:', err);
        });
    }

    function saveSettings(view) {
        var cfg = {
            PrimaryManifestUrl:         esVal(view, 'cfg-manifest-url'),
            AioMetadataBaseUrl:         esVal(view, 'cfg-aio-metadata-url'),
            EnableBackupAioStreams:     esChk(view, 'cfg-enable-backup-aio'),
            SecondaryManifestUrl:      esChk(view, 'cfg-enable-backup-aio')
                                           ? esVal(view, 'cfg-aio-backup-url') || ''
                                           : '',
            SystemRssFeedUrls:          esVal(view, 'cfg-rss-feeds') || '',
            SyncPathMovies:             esVal(view, 'cfg-base-path') + '/movies',
            SyncPathShows:              esVal(view, 'cfg-base-path') + '/shows',
            BaseSyncPath:               esVal(view, 'cfg-base-path'),
            EmbyBaseUrl:                esVal(view, 'cfg-emby-base'),
            MetadataLanguage:              esVal(view, 'cfg-meta-lang-dropdown')     || 'en',
            MetadataCertificationCountry:  esVal(view, 'cfg-meta-country-dropdown')  || 'US',
            MetadataImageLanguage:         esVal(view, 'cfg-meta-img-lang-dropdown') || 'en',
            EnableDiscoverChannel:      true,
            EnableAioStreamsCatalog:    esChk(view, 'cfg-enable-aio'),
            AioStreamsCatalogIds:       getSelectedCatalogIds(view, 'cfg'),
            AioStreamsAcceptedStreamTypes: (function() {
                var checked = [];
                ['debrid','usenet','torrent','http','live'].forEach(function(t) {
                    var cb = q(view, 'cfg-stype-' + t);
                    if (cb && cb.checked) checked.push(t);
                });
                return checked.length ? checked.join(',') : '';
            })(),
            LibraryNameMovies:          esVal(view, 'cfg-library-name-movies') || 'Streamed Movies',
            LibraryNameSeries:          esVal(view, 'cfg-library-name-series') || 'Streamed Series',
            SignatureValidityDays:    esInt(view, 'cfg-sig-validity', 365),
            CacheLifetimeMinutes:       esInt(view, 'cfg-cache-lifetime', 360),
            ApiDailyBudget:             esInt(view, 'cfg-api-budget', 2000),
            MaxConcurrentResolutions:   esInt(view, 'cfg-max-concurrent', 3),
            CatalogItemCap:             esInt(view, 'cfg-item-cap', 500),
            CatalogSyncIntervalHours:   esInt(view, 'cfg-sync-interval', 24),
            SyncScheduleHour:           esInt(view, 'cfg-sync-hour', 3),
            CandidatesPerProvider:      esInt(view, 'cfg-candidates-per-provider', 3),
            SyncResolveTimeoutSeconds:  esInt(view, 'cfg-sync-resolve-timeout', 30),
            ProviderPriorityOrder:      esVal(view, 'cfg-provider-priority'),
            NextUpLookaheadEpisodes:    esInt(view, 'cfg-next-up-lookahead', 2),
            ProxyMode:                  esVal(view, 'cfg-proxy-mode'),
            MaxConcurrentProxyStreams:  esInt(view, 'cfg-max-proxy', 5),
            DeleteStrmOnReadoption:     esChk(view, 'cfg-delete-strm-on-readoption'),
            DontPanic:                  esChk(view, 'cfg-dont-panic'),
            PluginSecret:               esVal(view, 'cfg-plugin-secret'),
            CatalogItemLimitsJson:      getCatalogLimitsJson(),
            EnableAnimeLibrary:         esChk(view, 'cfg-enable-anime'),
            SyncPathAnime:              esVal(view, 'cfg-anime-path') || '/media/infinitedrive/anime',
            // Sprint 209: Parental Controls
            TmdbApiKey:                 esVal(view, 'cfg-tmdb-api-key') || '',
            BlockUnratedForRestricted:  esChk(view, 'cfg-block-unrated'),
            IsFirstRunComplete:         _loadedConfig ? !!_loadedConfig.IsFirstRunComplete : false
        };
        // CONF-JSON: validate CatalogItemLimitsJson before saving
        if (cfg.CatalogItemLimitsJson) {
            try { JSON.parse(cfg.CatalogItemLimitsJson); } catch(e) {
                Dashboard.alert('Catalog Item Limits JSON is invalid: ' + e.message + '\nReset the limits table or fix the JSON before saving.');
                return;
            }
        }
        ApiClient.updatePluginConfiguration(pluginId, cfg)
            .then(function() {
                _loadedConfig = cfg;
                Dashboard.processPluginConfigurationUpdateResult();
                // Re-provision libraries on settings save to keep them in sync
                return esFetch('/InfiniteDrive/Setup/ProvisionLibraries', {method:'POST'});
            })
            .then(function() {
                // Libraries provisioned successfully
            })
            .catch(function(err) { Dashboard.alert('Save failed: ' + (err && err.message || err)); });
    }

    // ── Health dashboard ──────────────────────────────────────────────────────
    function refreshDashboard(view) {
        if (_authExpired) return;

        esFetch('/InfiniteDrive/Status')
            .then(function(r) {
                if (r.status === 401) { showAuthExpiredBanner(view); return; }
                return r.json();
            })
            .then(function(d) { if (d) renderDashboard(view, d); })
            .catch(function(err) { console.warn('[InfiniteDrive] Status fetch failed:', err); });

        // U1: Fetch unhealthy items separately (admin-only, may return 403 for non-admins)
        esFetch('/InfiniteDrive/UnhealthyItems')
            .then(function(r) {
                if (r.status === 401) { showAuthExpiredBanner(view); return; }
                return r.json();
            })
            .then(function(d) { if (d) renderUnhealthyItems(view, d); })
            .catch(function() { /* non-admin or offline — silently skip */ });

        // A1-UI: Fetch recent playback errors
        esFetch('/InfiniteDrive/RecentErrors')
            .then(function(r) {
                if (r.status === 401) { showAuthExpiredBanner(view); return; }
                return r.json();
            })
            .then(function(d) { if (d) renderRecentErrors(view, d); })
            .catch(function() { /* non-admin or offline — silently skip */ });
    }

    function renderUnhealthyItems(view, d) {
        var countEl = q(view, 'es-unhealthy-count');
        var tbody   = q(view, 'es-unhealthy-body');
        if (!tbody) return;

        var items = (d && d.items) ? d.items : [];
        if (countEl) countEl.textContent = items.length ? '(' + items.length + ')' : '(none)';

        if (!items.length) {
            tbody.innerHTML = '<tr><td colspan="4" style="opacity:.4">No failed items ✓</td></tr>';
            return;
        }
        tbody.innerHTML = items.map(function(i) {
            var ep = (i.season != null ? ' S' + pad(i.season) + 'E' + pad(i.episode || 0) : '');
            var retry = i.retry_after ? new Date(i.retry_after).toLocaleString() : '—';
            return '<tr>'
                + '<td><code>' + esc(i.imdb_id) + '</code></td>'
                + '<td>' + esc(i.title) + ep + '</td>'
                + '<td>' + (i.season != null ? 'S' + pad(i.season) + 'E' + pad(i.episode || 0) : '—') + '</td>'
                + '<td style="font-size:.82em;opacity:.7">' + esc(retry) + '</td>'
                + '</tr>';
        }).join('');
    }


    function renderRecentErrors(view, d) {
        var tbody = q(view, 'es-errors-body');
        if (!tbody) return;
        var errors = (d && d.errors) ? d.errors : [];
        var countEl = q(view, 'es-errors-count');
        if (countEl) countEl.textContent = errors.length ? '(' + errors.length + ')' : '(none)';
        if (!errors.length) {
            tbody.innerHTML = '<tr><td colspan="5" style="opacity:.4">No playback errors ✓</td></tr>';
            return;
        }
        tbody.innerHTML = errors.map(function(e) {
            var ep  = (e.season != null ? ' S' + pad(e.season) + 'E' + pad(e.episode || 0) : '');
            var ts  = e.played_at ? new Date(e.played_at).toLocaleString() : '—';
            return '<tr>'
                + '<td><code>' + esc(e.imdb_id) + '</code></td>'
                + '<td>' + esc(e.title || '') + ep + '</td>'
                + '<td style="font-size:.82em;opacity:.7">' + esc(e.client || '') + '</td>'
                + '<td style="font-size:.8em;color:#e06c75;max-width:220px;word-break:break-word">' + esc(e.error || '') + '</td>'
                + '<td style="font-size:.78em;opacity:.55">' + esc(ts) + '</td>'
                + '</tr>';
        }).join('');
    }

    function renderDashboard(view, d) {
        var ts = q(view, 'es-refresh-ts');
        if (ts) ts.textContent = 'Updated ' + new Date().toLocaleTimeString();

        var aio = d.AioStreams || {};
        setBadge(view, 'es-health-aio', aio.Ok ? 'ok' : 'error', aio.Ok ? 'Connected' : 'Offline');
        setText(view, 'es-health-aio-url', aio.Url ? '(' + shortenUrl(aio.Url) + ')' : '');
        setText(view, 'es-health-aio-ms',  aio.LatencyMs >= 0 ? aio.LatencyMs + ' ms' : '');

        var addonRow = q(view, 'es-health-addon-row');
        if (addonRow) addonRow.style.display = d.AioStreamsAddonName ? '' : 'none';
        setText(view, 'es-health-addon-name',    d.AioStreamsAddonName    || '');
        setText(view, 'es-health-addon-version', d.AioStreamsAddonVersion ? 'v' + d.AioStreamsAddonVersion : '');

        var soRow = q(view, 'es-health-streamonly-row');
        if (soRow) soRow.style.display = d.AioStreamsIsStreamOnly ? '' : 'none';

        var pfxRow = q(view, 'es-health-prefixes-row');
        if (pfxRow) pfxRow.style.display = d.AioStreamsStreamPrefixes ? '' : 'none';
        setText(view, 'es-health-prefixes', d.AioStreamsStreamPrefixes || '');

        setBadge(view, 'es-health-configured', d.IsConfigured ? 'ok' : 'warn',
            d.IsConfigured ? 'Yes' : 'No — complete the wizard');

        var c = d.Cache || {};
        setText(view, 'es-stat-valid',        c.ValidUnexpired != null ? c.ValidUnexpired : '—');
        setText(view, 'es-stat-stale',        c.Stale          != null ? c.Stale          : '—');
        setText(view, 'es-stat-failed',       c.Failed         != null ? c.Failed         : '—');
        setText(view, 'es-stat-catalog',      d.CatalogItemCount  != null ? d.CatalogItemCount  : '—');
        setText(view, 'es-stat-library',      d.LibraryItemCount  != null ? d.LibraryItemCount  : '—');
        setText(view, 'es-stat-strm',         d.StrmItemCount     != null ? d.StrmItemCount     : '—');
        setText(view, 'es-stat-resurrections',d.ResurrectionCount    != null ? d.ResurrectionCount    : '—');
        setText(view, 'es-stat-readopted',    d.ReadoptedCount       != null ? d.ReadoptedCount       : '—');
        setText(view, 'es-stat-pending-expand',d.PendingExpansionCount != null ? d.PendingExpansionCount : '—');

        var b = d.ApiBudget || {}, used = b.PercentUsed || 0;
        setText(view, 'es-budget-text', (b.CallsMade||0)+' / '+(b.CallsBudget||0)+' calls today ('+used+'%)');
        var bbar = q(view, 'es-budget-bar');
        if (bbar) { bbar.style.width = Math.min(used,100)+'%'; bbar.style.background = used>90?'#dc3545':used>70?'#ffc107':''; }

        var sb = q(view, 'es-sync-body');
        if (sb) {
            var ss = d.SyncStates || [], srcStats = d.SourceStats || {};
            if (!ss.length) { sb.innerHTML = '<tr><td colspan="8" style="opacity:.4">No syncs yet</td></tr>'; }
            else sb.innerHTML = ss.map(function(s) {
                var bc = s.Status==='ok'?'es-badge-ok':s.Status==='warn'?'es-badge-warn':'es-badge-error';
                var errCell = s.LastError
                    ? '<span title="'+esc(s.LastError)+'" style="cursor:help;color:#dc3545">'+esc(s.LastError.substring(0,40))+(s.LastError.length>40?'…':'')+'</span>'
                    : '—';
                return '<tr>'+
                    '<td style="font-family:monospace;font-size:.82em">'+esc(s.SourceKey)+'</td>'+
                    '<td>'+(s.LastSyncAt?fmtDate(s.LastSyncAt):'—')+'</td>'+
                    '<td>'+(s.LastReachedAt?fmtDate(s.LastReachedAt):'—')+'</td>'+
                    '<td>'+(s.ItemCount||0)+'</td>'+
                    '<td>'+(srcStats[s.SourceKey]!=null?'<b>'+srcStats[s.SourceKey]+'</b>':'—')+'</td>'+
                    '<td><span class="es-badge '+bc+'">'+esc(s.Status||'?')+'</span></td>'+
                    '<td>'+(s.ConsecutiveFailures||0)+'</td>'+
                    '<td style="max-width:220px;overflow:hidden">'+errCell+'</td>'+
                    '</tr>';
            }).join('');
        }

        var cov = d.Coverage || {}, covPct = cov.CoveragePercent || 0;
        setText(view,'es-cov-pct',      covPct+'%');
        setText(view,'es-cov-valid',    cov.ValidCached  != null ? cov.ValidCached  : '—');
        setText(view,'es-cov-stale',    cov.StaleCached  != null ? cov.StaleCached  : '—');
        setText(view,'es-cov-uncached', cov.Uncached     != null ? cov.Uncached     : '—');
        setText(view,'es-cov-total',    cov.TotalStrm    != null ? cov.TotalStrm    : '—');
        var covBar = q(view,'es-cov-bar');
        if (covBar) { covBar.style.width=Math.min(covPct,100)+'%'; covBar.style.background=covPct>=80?'#28a745':covPct>=40?'#ffc107':'#dc3545'; }

        // v0.65.7: Show cache guidance when valid cache is 0 (cold start)
        var cacheGuidance = q(view, 'es-cache-guidance');
        if (cacheGuidance) {
            cacheGuidance.style.display = (cov.ValidCached === 0 && cov.TotalStrm > 0) ? 'block' : 'none';
        }

        // Sprint 66: Item state counts for Doctor panel
        setText(view,'es-state-catalogued', d.CataloguedCount  != null ? d.CataloguedCount  : '—');
        setText(view,'es-state-present',    d.PresentCount     != null ? d.PresentCount     : '—');
        setText(view,'es-state-resolved',   d.ResolvedCount    != null ? d.ResolvedCount    : '—');
        setText(view,'es-state-retired',    d.RetiredCount     != null ? d.RetiredCount     : '—');
        setText(view,'es-state-pinned',     d.PinnedCount      != null ? d.PinnedCount      : '—');
        setText(view,'es-state-orphaned',   d.OrphanedCount    != null ? d.OrphanedCount    : '—');

        var cb = q(view,'es-clients-body');
        if (cb) {
            var clients = d.ClientProfiles || [];
            if (!clients.length) { cb.innerHTML='<tr><td colspan="6" style="opacity:.4">No clients yet.</td></tr>'; }
            else cb.innerHTML = clients.map(function(cl) {
                var modeLabel = cl.SupportsRedirect===0?'<span style="color:#ffc107">proxy</span>':'<span style="color:#28a745">redirect</span>';
                var brate = cl.MaxSafeBitrate!=null?(cl.MaxSafeBitrate>=1000?(cl.MaxSafeBitrate/1000).toFixed(1)+' Mbps':cl.MaxSafeBitrate+' kbps'):'<span style="opacity:.4">learning…</span>';
                return '<tr><td style="font-weight:600">'+esc(cl.ClientType)+'</td><td>'+modeLabel+'</td><td>'+brate+'</td><td>'+esc(cl.PreferredQuality||'—')+'</td><td>'+cl.TestCount+'</td><td>'+(cl.LastTestedAt?fmtDate(cl.LastTestedAt):'—')+'</td></tr>';
            }).join('');
        }

        var pb = q(view,'es-plays-body');
        if (pb) {
            var plays = d.RecentPlays || [];
            if (!plays.length) { pb.innerHTML='<tr><td colspan="8" style="opacity:.4">No plays yet</td></tr>'; }
            else pb.innerHTML = plays.map(function(p) {
                var mc = p.ResolutionMode==='cached'?'mode-cached':p.ResolutionMode==='failed'?'mode-failed':p.ResolutionMode&&p.ResolutionMode.indexOf('sync')===0?'mode-sync':'';
                return '<tr>'+
                    '<td><a href="https://www.imdb.com/title/'+esc(p.ImdbId)+'" target="_blank" style="color:var(--theme-button-background,#00a4dc)">'+esc(p.ImdbId)+'</a></td>'+
                    '<td>'+esc(p.Title||'')+'</td>'+
                    '<td>'+(p.Season?'S'+pad(p.Season)+'E'+pad(p.Episode):'—')+'</td>'+
                    '<td class="'+mc+'">'+esc(p.ResolutionMode)+'</td>'+
                    '<td>'+esc(p.QualityServed||'—')+'</td>'+
                    '<td>'+esc(p.ClientType||'—')+'</td>'+
                    '<td>'+(p.LatencyMs!=null?p.LatencyMs:'—')+'</td>'+
                    '<td>'+(p.PlayedAt?fmtDate(p.PlayedAt):'—')+'</td>'+
                    '</tr>';
            }).join('');
        }

        // Render provider health
        if (d.Providers && d.Providers.length > 0) {
            renderProviderHealth(view, d.Providers);
        }

        // Render task schedule information
        renderTaskSchedules(view, _loadedConfig);

        // Sprint 79: Update Edit Manifest button
        updateManifestEditButton(view, d);
    }

    // ── Sprint 79: Update Edit Manifest button ─────────────────────────────────
    function updateManifestEditButton(view, status) {
        var btn = view.querySelector('#btnEditManifest');
        var hostInfo = view.querySelector('#manifestHostInfo');
        var hostName = view.querySelector('#manifestHostName');

        if (!btn) return;

        if (status && status.ManifestConfigureUrl) {
            btn.style.display = 'inline-flex';
            btn.onclick = function() {
                window.open(status.ManifestConfigureUrl, '_blank');
            };

            if (hostInfo && hostName && status.ManifestHost) {
                hostInfo.style.display = 'block';
                hostName.textContent = status.ManifestHost;
            }
        } else {
            btn.style.display = 'none';
            if (hostInfo) hostInfo.style.display = 'none';
        }
    }

    function renderProviderHealth(view, providers) {
        var card = q(view, 'es-health-providers-card');
        var container = q(view, 'es-health-providers');
        if (!container) return;

        if (!providers || providers.length === 0) {
            if (card) card.style.display = 'none';
            return;
        }

        if (card) card.style.display = 'block';

        var html = '<div style="font-size:.88em;opacity:.75;margin-bottom:.4em;font-weight:600">Provider Status</div>';
        providers.forEach(function(p) {
            var statusBadge = p.Ok ? '<span style="color:#28a745">●</span> Connected' : '<span style="color:#dc3545">●</span> Offline';
            var latency = p.LatencyMs >= 0 ? ' (' + p.LatencyMs + ' ms)' : '';
            var displayName = p.DisplayName || 'Provider';
            html += '<div style="display:flex;align-items:center;gap:.5em;margin:.25em 0;font-size:.85em"><span style="min-width:80px">' + esc(statusBadge) + '</span><span style="flex:1">' + esc(displayName) + latency + '</span></div>';
        });
        container.innerHTML = html;
    }

    function renderTaskSchedules(view, cfg) {
        if (!cfg) return;

        // Catalog Sync — Daily at the configured hour
        var catalogHour = cfg.CatalogSyncHour != null ? cfg.CatalogSyncHour : 3;
        var catalogSchedule = catalogHour < 0 ? 'Manual only' : 'Daily at ' + pad(catalogHour) + ':00 UTC';
        setText(view, 'es-task-schedule-catalog', catalogSchedule);

        // Link Resolver — Hourly
        setText(view, 'es-task-schedule-resolver', 'Hourly');

        // File Resurrection — On demand
        setText(view, 'es-task-schedule-resurrection', 'On demand');

        // Library Re-adoption — Daily
        setText(view, 'es-task-schedule-readoption', 'Daily');

        // Episode Expander — On demand
        setText(view, 'es-task-schedule-expander', 'On demand');

        // Dead Link Scan — Weekly
        setText(view, 'es-task-schedule-deadlinks', 'Weekly');
    }

    // ── Task runner ───────────────────────────────────────────────────────────
    function runTask(view, taskKey, btnEl) {
        var origText = btnEl ? btnEl.innerHTML : null;
        if (btnEl) { btnEl.disabled = true; btnEl.innerHTML = '⏳ Running…'; }
        esFetch('/InfiniteDrive/Trigger?task=' + encodeURIComponent(taskKey), {method:'POST'})
            .then(function(r) { return r.json(); })
            .then(function(data) {
                if (btnEl) { btnEl.disabled = false; btnEl.innerHTML = origText; }
                if (data.Status === 'ok') {
                    if (typeof Dashboard !== 'undefined') Dashboard.alert({ message: 'Task started — see Health tab for progress.' });
                    setTimeout(function() { refreshDashboard(view); }, 3000);
                    setTimeout(function() { refreshDashboard(view); }, 10000);
                    if (taskKey === 'catalog_sync') { startCatalogPoll(view, 'cfg'); startCatalogPoll(view, 'wiz'); }
                } else { Dashboard.alert('Error: ' + (data.Message || 'unknown')); }
            })
            .catch(function(err) {
                if (btnEl) { btnEl.disabled = false; btnEl.innerHTML = origText; }
                Dashboard.alert('Request failed: ' + err.message);
            });
    }

    // ── Per-provider loading spinners (Sprint 14: UX-CATALOG-AUTO-LOAD) ────────
    function showProviderLoadingSpinners(panel, prefix) {
        var html = '<div style="padding:.5em;font-size:.85em;opacity:.7">' +
            '<div style="display:grid;gap:.4em">' +
            '<div id="es-load-aio-'+prefix+'" style="display:flex;align-items:center;gap:.4em"><span style="animation:spin 1s linear infinite">🔄</span> AIOStreams catalogs</div>' +
            '<div id="es-load-trakt-'+prefix+'" style="display:flex;align-items:center;gap:.4em;opacity:.4"><span style="animation:spin 1s linear infinite;opacity:0">🔄</span> Trakt watchlist (if enabled)</div>' +
            '<div id="es-load-mdblist-'+prefix+'" style="display:flex;align-items:center;gap:.4em;opacity:.4"><span style="animation:spin 1s linear infinite;opacity:0">🔄</span> MDBList (if enabled)</div>' +
            '</div></div>';
        panel.innerHTML = html;
    }

    // ── Catalog discovery ─────────────────────────────────────────────────────
    function loadCatalogs(view, prefix) {
        var panel = q(view, 'es-catalog-panel-' + prefix);
        if (!panel) return;
        showProviderLoadingSpinners(panel, prefix);

        // Capture currently checked IDs before reload
        var prevSet = null;
        var cbs = view.querySelectorAll('.es-catcb-' + prefix);
        if (cbs.length) {
            var allIds = {}, checkedIds = {};
            for (var i = 0; i < cbs.length; i++) { allIds[cbs[i].value] = true; }
            var checked = view.querySelectorAll('.es-catcb-' + prefix + ':checked');
            for (var i = 0; i < checked.length; i++) { checkedIds[checked[i].value] = true; }
            if (Object.keys(checkedIds).length < Object.keys(allIds).length) prevSet = checkedIds;
        } else {
            var hiddenEl = q(view, prefix === 'wiz' ? 'es-catalog-ids-wiz' : 'cfg-catalog-ids');
            if (hiddenEl && hiddenEl.value.trim()) {
                prevSet = {};
                hiddenEl.value.split(',').forEach(function(id) { id = id.trim(); if (id) prevSet[id] = true; });
                if (!Object.keys(prevSet).length) prevSet = null;
            }
        }

        // Pass the live manifest URL so the panel works before the first save.
        var manifestFieldId = prefix === 'wiz' ? 'wiz-aio-url' : 'cfg-manifest-url';
        var liveManifestUrl = esVal(view, manifestFieldId);
        var catalogsUrl = '/InfiniteDrive/Catalogs' +
            (liveManifestUrl ? '?manifestUrl=' + encodeURIComponent(liveManifestUrl) : '');

        esFetch(catalogsUrl)
            .then(function(r) { return r.json(); })
            .then(function(data) {
                if (data.Error) { panel.innerHTML = '<div class="es-alert es-alert-error" style="font-size:.85em;margin:0">' + makeErrorGuidance(data.Error) + '</div>'; return; }
                var catalogs = data.Catalogs || [];
                if (!catalogs.length) {
                    panel.innerHTML = '<div class="es-alert es-alert-warn" style="font-size:.85em;margin:0"><strong>Stream-only addon detected.</strong> No source. Enable Trakt or MDBList to populate the library.</div>';
                    return;
                }
                renderCatalogPanel(view, panel, catalogs, prefix, prevSet);
            })
            .catch(function(err) { panel.innerHTML = '<div class="es-alert es-alert-error" style="font-size:.85em;margin:0">' + makeErrorGuidance('Failed: ' + err.message) + '</div>'; });
    }

    function renderCatalogPanel(view, panel, catalogs, prefix, prevSet) {
        var html = '<table class="es-cpt"><thead><tr>'+
            '<th style="width:26px"></th><th>Source</th><th style="width:60px">Type</th>'+
            '<th style="min-width:110px">Progress</th><th style="width:60px">Limit</th><th style="width:70px">Order</th>'+
            '</tr></thead><tbody>';
        catalogs.forEach(function(c, idx) {
            var checked = (prevSet === null || prevSet[c.Id]) ? ' checked' : '';
            var t = (c.Type || '').toLowerCase();
            var tStyle = t==='movie'?'background:rgba(0,164,220,0.2);color:#7ecbdf':
                         t==='series'?'background:rgba(40,167,69,0.2);color:#7de98d':
                         t==='anime'?'background:rgba(255,193,7,0.2);color:#ffd454':
                         'background:rgba(255,255,255,0.1)';
            var srcKey = 'aio:' + (c.Type||'movie') + ':' + c.Id;
            var progKey = cprogKey(srcKey);
            var limitVal = _catalogLimits[srcKey] ? String(_catalogLimits[srcKey]) : '';
            var isFirst = idx === 0, isLast = idx === catalogs.length - 1;
            var upDisabled = isFirst ? ' disabled style="opacity:.3"' : '';
            var downDisabled = isLast ? ' disabled style="opacity:.3"' : '';
            html += '<tr>'+
                '<td><input type="checkbox" class="es-catcb-'+prefix+'" value="'+esc(c.Id)+'"'+checked+' /></td>'+
                '<td style="max-width:180px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" title="'+esc(c.Name||c.Id)+'">'+esc(c.Name||c.Id)+'</td>'+
                '<td><span class="es-type-badge" style="'+tStyle+'">'+esc(c.Type||'')+'</span></td>'+
                '<td id="es-cprog-'+prefix+'-'+progKey+'"><span style="opacity:.3;font-size:.8em">—</span></td>'+
                '<td><input type="number" class="es-catalog-limit-input" style="width:52px;background:rgba(255,255,255,0.06);border:1px solid rgba(255,255,255,0.15);border-radius:3px;padding:.15em .3em;font-size:.82em;color:inherit" placeholder="100" min="1" max="5000" value="'+esc(limitVal)+'" data-srckey="'+esc(srcKey)+'" /></td>'+
                '<td style="text-align:center;white-space:nowrap"><button type="button" is="emby-button" class="raised button" data-es-catalog-move="up" data-es-catalog-prefix="'+prefix+'" data-es-catalog-idx="'+idx+'" style="width:1.5em;height:1.5em;padding:0;border-radius:50%;font-size:.7em;margin:0 .1em"'+upDisabled+'>▲</button><button type="button" is="emby-button" class="raised button" data-es-catalog-move="down" data-es-catalog-prefix="'+prefix+'" data-es-catalog-idx="'+idx+'" style="width:1.5em;height:1.5em;padding:0;border-radius:50%;font-size:.7em;margin:0 .1em"'+downDisabled+'>▼</button></td>'+
                '</tr>';
        });
        html += '</tbody></table>';
        panel.innerHTML = html;
        // Bind limit inputs directly since they're freshly created
        var inputs = panel.querySelectorAll('.es-catalog-limit-input');
        for (var i = 0; i < inputs.length; i++) {
            (function(inp) { inp.addEventListener('change', function() { setCatalogLimit(inp); }); })(inputs[i]);
        }
        startCatalogPoll(view, prefix);
    }

    function moveCatalogRow(view, prefix, idx, direction) {
        var panel = q(view, 'es-catalog-panel-' + prefix);
        if (!panel) return;
        var tbody = panel.querySelector('tbody');
        if (!tbody) return;
        var rows = tbody.querySelectorAll('tr');
        if (idx < 0 || idx >= rows.length) return;

        var newIdx = direction === 'up' ? idx - 1 : idx + 1;
        if (newIdx < 0 || newIdx >= rows.length) return;

        var currentRow = rows[idx];
        var targetRow = rows[newIdx];
        if (direction === 'down') {
            targetRow.parentNode.insertBefore(currentRow, targetRow.nextSibling);
        } else {
            targetRow.parentNode.insertBefore(currentRow, targetRow);
        }

        // Re-render to update button states and data attributes
        var catalogs = [];
        var checkboxes = panel.querySelectorAll('.es-catcb-' + prefix);
        checkboxes.forEach(function(cb) {
            catalogs.push({ Id: cb.value, Name: cb.closest('tr').children[1].textContent.trim(), Type: cb.closest('tr').children[2].textContent.trim() });
        });
        var prevSet = {};
        checkboxes.forEach(function(cb) { if (cb.checked) prevSet[cb.value] = true; });
        // Note: we lose catalog limit values here, but that's acceptable for reordering
        renderCatalogPanel(view, panel, catalogs, prefix, prevSet);

        // Update the catalog IDs field with the new order and save
        var orderedIds = catalogs.map(function(c) { return c.Id; }).join(',');
        var inputId = prefix === 'wiz' ? 'es-catalog-ids-wiz' : 'cfg-catalog-ids';
        var inp = q(view, inputId);
        if (inp) {
            inp.value = orderedIds;
            saveSettings(view);
        }
    }

    function selectAllCatalogs(view, prefix, select) {
        var cbs = view.querySelectorAll('.es-catcb-' + prefix);
        for (var i = 0; i < cbs.length; i++) cbs[i].checked = select;
    }

    function getSelectedCatalogIds(view, prefix) {
        var all = view.querySelectorAll('.es-catcb-' + prefix);
        if (!all.length) {
            var inp = q(view, prefix === 'wiz' ? 'es-catalog-ids-wiz' : 'cfg-catalog-ids');
            return inp ? inp.value : '';
        }
        var allIds = [], checkedIds = [];
        for (var i = 0; i < all.length; i++) allIds.push(all[i].value);
        var checked = view.querySelectorAll('.es-catcb-' + prefix + ':checked');
        for (var i = 0; i < checked.length; i++) checkedIds.push(checked[i].value);
        return checkedIds.length === allIds.length ? '' : checkedIds.join(',');
    }

    // ── Catalog progress polling ──────────────────────────────────────────────
    function cprogKey(k) { return encodeURIComponent(k).replace(/%/g, '_'); }

    function fillProgressCell(cell, it) {
        var target = it.ItemsTarget || 0, running = it.ItemsRunning || 0;
        var pct = target > 0 ? Math.min(100, Math.round(running * 100 / target)) : 0;
        var st = it.Status || 'never';
        var stClass = st==='ok'?'es-badge-ok':st==='warn'?'es-badge-warn':st==='error'?'es-badge-error':st==='running'?'es-badge-running':'es-badge-never';
        var stText  = st==='running'?'↻ running':st==='never'?'never':st;
        var syncLbl = it.LastSyncAt ? ' <span style="font-size:.72em;opacity:.4">'+esc(fmtRelative(new Date(it.LastSyncAt)))+'</span>' : '';
        cell.innerHTML = '<div style="display:flex;align-items:center;gap:.3em">'+
            '<div class="es-cpbar-wrap"><div class="es-cpbar-fill" style="width:'+pct+'%"></div></div>'+
            '<span style="font-size:.75em;opacity:.6;white-space:nowrap">'+(target>0?running+'/'+target:'—')+'</span>'+
            ' <span class="es-badge '+stClass+'" style="font-size:.72em;padding:.1em .35em">'+esc(stText)+'</span>'+
            syncLbl+'</div>';
    }

    function pollCatalogProgress(view) {
        esFetch('/InfiniteDrive/CatalogProgress')
            .then(function(r) { return r.json(); })
            .then(function(data) {
                var items = data.Items || [];
                items.forEach(function(it) {
                    var key = cprogKey(it.SourceKey);
                    ['cfg','wiz'].forEach(function(prefix) {
                        var cell = q(view, 'es-cprog-' + prefix + '-' + key);
                        if (cell) fillProgressCell(cell, it);
                    });
                });
                if (!data.IsAnyRunning && _catalogPollTimer) {
                    clearInterval(_catalogPollTimer); _catalogPollTimer = null;
                }
            }).catch(function(){});
    }

    function startCatalogPoll(view, prefix) {
        pollCatalogProgress(view);
        if (!_catalogPollTimer) _catalogPollTimer = setInterval(function() { pollCatalogProgress(view); }, 3000);
    }

    function setCatalogLimit(inp) {
        var srcKey = inp.getAttribute('data-srckey');
        var val = parseInt(inp.value, 10);
        if (srcKey) {
            if (!isNaN(val) && val > 0) _catalogLimits[srcKey] = val;
            else delete _catalogLimits[srcKey];
        }
    }

    function getCatalogLimitsJson() {
        var keys = Object.keys(_catalogLimits);
        return keys.length ? JSON.stringify(_catalogLimits) : '';
    }

    // ── Item inspector ────────────────────────────────────────────────────────
    function inspect(view) {
        var imdb    = (esVal(view, 'es-inspect-imdb') || '').trim();
        var season  = esVal(view, 'es-inspect-season');
        var episode = esVal(view, 'es-inspect-ep');
        if (!imdb) { Dashboard.alert('Enter an IMDB ID first'); return; }
        var url = '/InfiniteDrive/Inspect?imdb=' + encodeURIComponent(imdb);
        if (season)  url += '&season='  + encodeURIComponent(season);
        if (episode) url += '&episode=' + encodeURIComponent(episode);
        esFetch(url).then(function(r) { return r.json(); }).then(function(d) {
            var rows = [];
            function row(label, val) {
                return '<tr><td style="opacity:.55;font-size:.85em;padding-right:1em;white-space:nowrap">'+esc(label)+'</td><td>'+(val!=null?val:'<span style="opacity:.35">—</span>')+'</td></tr>';
            }
            if (d.Error && !d.Found) {
                rows.push(row('Status', '<span style="color:#dc3545">'+esc(d.Error)+'</span>'));
            } else {
                rows.push(row('IMDB', '<a href="https://www.imdb.com/title/'+esc(d.ImdbId||'')+'" target="_blank" style="color:var(--theme-button-background,#00a4dc)">'+esc(d.ImdbId||'')+'</a>'));
                rows.push(row('Title',  esc(d.Title||'—')));
                rows.push(row('Year',   d.Year != null ? d.Year : null));
                rows.push(row('Type',   esc(d.MediaType||'—')));
                rows.push(row('Source', esc(d.Source||'—')));
                if (d.StrmPath) rows.push(row('.strm', esc(d.StrmPath)+' '+(d.StrmExists?'<span style="color:#28a745">✓</span>':'<span style="color:#dc3545">✗</span>')));
                var cc = d.CacheStatus==='valid'?'#28a745':d.CacheStatus==='stale'?'#ffc107':'#dc3545';
                rows.push(row('Cache', d.CacheHit?'<span style="color:'+cc+'">'+esc(d.CacheStatus)+'</span>':'<span style="opacity:.4">no cache entry</span>'));
                if (d.CacheHit) {
                    rows.push(row('Quality', esc(d.QualityTier||'—')));
                    var br = d.BitrateKbps!=null?(d.BitrateKbps>=1000?(d.BitrateKbps/1000).toFixed(1)+' Mbps':d.BitrateKbps+' kbps'):null;
                    rows.push(row('Bitrate', br));
                    rows.push(row('Fallbacks', d.HasFallbacks?'<span style="color:#28a745">yes</span>':'<span style="opacity:.4">none</span>'));
                    rows.push(row('Resolved', d.ResolvedAt?fmtDate(d.ResolvedAt):null));
                    rows.push(row('Expires',  d.ExpiresAt?fmtDate(d.ExpiresAt):null));
                    rows.push(row('Plays',    d.PlayCount!=null?d.PlayCount:null));
                }
                if (d.StrmPlayUrl) rows.push(row('Play URL', '<code style="font-size:.8em;word-break:break-all">'+esc(d.StrmPlayUrl)+'</code>'));
            }
            var body = q(view,'es-inspect-body'), result = q(view,'es-inspect-result');
            if (body) body.innerHTML = rows.join('');
            if (result) result.style.display = '';
            var candSection = q(view,'es-candidates-section'), candBody = q(view,'es-candidates-body');
            if (candSection && candBody) {
                if (d.Candidates && d.Candidates.length) {
                    candBody.innerHTML = d.Candidates.map(function(can) {
                        var br = can.BitrateKbps!=null?(can.BitrateKbps>=1000?(can.BitrateKbps/1000).toFixed(1)+' Mbps':can.BitrateKbps+' kbps'):'—';
                        var sc = can.Status==='valid'?'#28a745':can.Status==='suspect'?'#ffc107':'#dc3545';
                        return '<tr><td style="opacity:.5">'+can.Rank+'</td><td>'+esc(can.ProviderKey||'—')+'</td><td>'+esc(can.StreamType||'—')+'</td><td>'+esc(can.QualityTier||'—')+'</td><td>'+br+'</td><td>'+(can.IsCached?'<span style="color:#28a745">✓</span>':'<span style="opacity:.35">—</span>')+'</td><td style="color:'+sc+'">'+esc(can.Status||'—')+'</td><td style="font-size:.8em;opacity:.55">'+(can.ExpiresAt?fmtDate(can.ExpiresAt):'—')+'</td></tr>';
                    }).join('');
                    candSection.style.display = '';
                } else { candSection.style.display = 'none'; }
            }
        }).catch(function(err) { Dashboard.alert('Inspect failed: ' + err.message); });
    }

    // ── Search ────────────────────────────────────────────────────────────────
    function searchDebounce(view) {
        if (_searchTimer) clearTimeout(_searchTimer);
        _searchTimer = setTimeout(function() { doSearch(view); }, 350);
    }

    function doSearch(view) {
        var searchQ = (esVal(view, 'es-search-q') || '').trim();
        var resultsDiv  = q(view, 'es-search-results');
        var resultsBody = q(view, 'es-search-body');
        if (!resultsDiv || !resultsBody) return;
        if (searchQ.length < 2) { resultsDiv.style.display = 'none'; return; }
        esFetch('/InfiniteDrive/Search?q=' + encodeURIComponent(searchQ) + '&limit=15')
            .then(function(r) { return r.json(); })
            .then(function(d) {
                if (!d.Results || !d.Results.length) {
                    resultsBody.innerHTML = '<tr><td colspan="6" style="opacity:.4">No results for "'+esc(searchQ)+'"</td></tr>';
                } else {
                    resultsBody.innerHTML = d.Results.map(function(r) {
                        return '<tr style="cursor:pointer" data-es-imdbid="'+esc(r.ImdbId)+'">'+
                            '<td>'+esc(r.Title)+'</td>'+
                            '<td>'+(r.Year!=null?r.Year:'—')+'</td>'+
                            '<td style="opacity:.55">'+esc(r.MediaType)+'</td>'+
                            '<td style="color:var(--theme-button-background,#00a4dc)">'+esc(r.ImdbId)+'</td>'+
                            '<td style="opacity:.5;font-size:.85em">'+esc(r.Source)+'</td>'+
                            '<td>'+(r.HasValidCache?'<span style="color:#28a745">✓</span>':'<span style="opacity:.35">—</span>')+'</td>'+
                            '</tr>';
                    }).join('');
                }
                resultsDiv.style.display = '';
            })
            .catch(function() { resultsDiv.style.display = 'none'; });
    }

    function pickSearchResult(view, imdbId) {
        var el = q(view, 'es-inspect-imdb');
        if (el) {
            el.value = imdbId;
            var s = q(view,'es-inspect-season'); if (s) s.value = '';
            var e = q(view,'es-inspect-ep');     if (e) e.value = '';
            var res = q(view,'es-search-results'); if (res) res.style.display = 'none';
            inspect(view);
        }
    }


    // ── Validate / cache ops / webhook ────────────────────────────────────────
    function validateSetup(view, resultDivId) {
        var resultDiv = q(view, resultDivId);
        if (resultDiv) { resultDiv.style.display=''; resultDiv.innerHTML='<span style="opacity:.55">Checking…</span>'; }
        esFetch('/InfiniteDrive/Validate', {method:'POST'})
            .then(function(r) { return r.json(); })
            .then(function(d) {
                if (!resultDiv) return;
                var overall = d.Status==='ok'?'<span style="color:#28a745;font-weight:700">✓ All checks passed</span>':
                              d.Status==='warn'?'<span style="color:#ffc107;font-weight:700">⚠ Some warnings</span>':
                              '<span style="color:#dc3545;font-weight:700">✗ Errors found</span>';
                resultDiv.innerHTML = '<div style="margin-bottom:.5em">'+overall+'</div>' +
                    (d.Checks||[]).map(function(c) {
                        var col=c.Status==='ok'?'#28a745':c.Status==='warn'?'#ffc107':'#dc3545';
                        var icon=c.Status==='ok'?'✓':c.Status==='warn'?'⚠':'✗';
                        return '<div style="margin:.25em 0;font-size:.85em"><span style="color:'+col+'">'+icon+' <strong>'+esc(c.Check)+'</strong></span> — '+esc(c.Message)+'</div>';
                    }).join('');
            })
            .catch(function(err) { if (resultDiv) resultDiv.innerHTML='<span style="color:#dc3545">Validation failed: '+esc(err.message)+'</span>'; });
    }

    function invalidateItem(view) {
        var imdb = (esVal(view,'es-inspect-imdb')||'').trim();
        if (!imdb) { Dashboard.alert('Run Inspect first'); return; }
        var url = '/InfiniteDrive/Invalidate?imdb='+encodeURIComponent(imdb);
        var s=esVal(view,'es-inspect-season'), e=esVal(view,'es-inspect-ep');
        if (s) url += '&season='+encodeURIComponent(s);
        if (e) url += '&episode='+encodeURIComponent(e);
        esFetch(url,{method:'POST'}).then(function(r){return r.json();})
            .then(function(d){Dashboard.alert(d.Status==='ok'?'Cache invalidated ✓':'Error: '+d.Message);})
            .catch(function(err){Dashboard.alert('Error: '+err.message);});
    }

    function queueItem(view) {
        var imdb = (esVal(view,'es-inspect-imdb')||'').trim();
        if (!imdb) { Dashboard.alert('Run Inspect first'); return; }
        var url = '/InfiniteDrive/Queue?imdb='+encodeURIComponent(imdb);
        var s=esVal(view,'es-inspect-season'), e=esVal(view,'es-inspect-ep');
        if (s) url += '&season='+encodeURIComponent(s);
        if (e) url += '&episode='+encodeURIComponent(e);
        esFetch(url,{method:'POST'}).then(function(r){return r.json();})
            .then(function(d){Dashboard.alert(d.Status==='ok'?'Queued ✓':'Error: '+d.Message);})
            .catch(function(err){Dashboard.alert('Error: '+err.message);});
    }

    // A11 — Raw AIOStreams response inspector
    function fetchRawStreams(view) {
        var imdb    = (esVal(view, 'es-raw-imdb')    || '').trim();
        var season  = esVal(view, 'es-raw-season');
        var episode = esVal(view, 'es-raw-episode');
        var result  = q(view, 'es-raw-result');
        var meta    = q(view, 'es-raw-meta');
        var pre     = q(view, 'es-raw-json');

        if (!imdb) { Dashboard.alert('Enter an IMDB ID'); return; }
        if (result) result.style.display = 'none';
        if (meta) meta.textContent = 'Fetching…';
        if (pre) pre.textContent = '';
        if (result) result.style.display = 'block';

        var url = '/InfiniteDrive/RawStreams?imdb=' + encodeURIComponent(imdb);
        if (season) url += '&season=' + encodeURIComponent(season);
        if (episode) url += '&episode=' + encodeURIComponent(episode);

        esFetch(url)
            .then(function(r) { return r.json(); })
            .then(function(d) {
                if (meta) {
                    var info = imdb + (season ? ' S' + season + 'E' + (episode||'?') : '');
                    meta.textContent = info + ' — ' + (d.stream_count != null ? d.stream_count + ' streams' : 'error') + ' in ' + (d.elapsed_ms || 0) + 'ms';
                }
                if (pre) pre.textContent = JSON.stringify(d, null, 2);
            })
            .catch(function(err) {
                if (meta) meta.textContent = 'Error: ' + err.message;
                if (pre) pre.textContent = '';
            });
    }

    function clearClientProfiles(view) {
        if (!confirm('Reset all learned client profiles?')) return;
        esFetch('/InfiniteDrive/Trigger',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({Task:'clear_client_profiles'})})
            .then(function(r){return r.json();})
            .then(function(d){Dashboard.alert(d.Status==='ok'?'Profiles cleared.':d.Message); if(d.Status==='ok') refreshDashboard(view);})
            .catch(function(err){Dashboard.alert('Error: '+err.message);});
    }

    // ── Init — bind all listeners ──────────────────────────────────────────────
    function initView(view) {

        // Manifest URL auto-parse (input events — not affected by custom element upgrade)
        bindManifestField(view, 'es-manifest-url', 'es');
        bindManifestField(view, 'cfg-manifest-url', 'cfg');

        // Sprint 14: Show quickstart guide by default on first view (UX-ONBOARD-ZERO)
        var quickstartContent = q(view, 'es-quickstart-content');
        var quickstartChevron = q(view, 'es-quickstart-chevron');
        if (quickstartContent && !localStorage.getItem('es-quickstart-expanded')) {
            quickstartContent.style.display = 'block';
            if (quickstartChevron) quickstartChevron.style.transform = 'rotate(90deg)';
            localStorage.setItem('es-quickstart-expanded', 'true');
        } else if (quickstartContent && localStorage.getItem('es-quickstart-expanded') === 'true') {
            quickstartContent.style.display = 'block';
            if (quickstartChevron) quickstartChevron.style.transform = 'rotate(90deg)';
        }

        // Sprint 14: Provider cards handled via event delegation in click handler below

        // Sprint 14: URL validation — show example .strm URL in real-time (UX-LIBRARY-URL-VALIDATION)
        var urlInput = q(view, 'es-emby-base');
        if (urlInput) {
            var updateUrlExample = function() {
                var baseUrl = (urlInput.value || '').trim();
                var exampleDiv = q(view, 'es-url-example');
                var exampleText = q(view, 'es-url-example-text');
                if (exampleDiv && exampleText) {
                    if (baseUrl && baseUrl !== 'http://' && baseUrl !== 'https://') {
                        var example = baseUrl.replace(/\/$/, '') + '/play.html?file=/media/infinitedrive/catalog/movies/example.mkv';
                        exampleText.textContent = example;
                        exampleDiv.style.display = 'block';
                    } else {
                        exampleDiv.style.display = 'none';
                    }
                }
            };
            urlInput.addEventListener('input', updateUrlExample);
            urlInput.addEventListener('change', updateUrlExample);
            updateUrlExample();
        }

        // Sprint 64: Anime toggle → show/hide path field
        var animeToggle = q(view, 'cfg-enable-anime');
        if (animeToggle) {
            animeToggle.addEventListener('change', function() {
                toggleAnimePathVisibility(view, animeToggle.checked);
            });
        }

        // Sprint 209: TMDB API key → update filter status
        var tmdbKeyInput = q(view, 'cfg-tmdb-api-key');
        if (tmdbKeyInput) {
            tmdbKeyInput.addEventListener('input', function() {
                updateFilterStatus(view, tmdbKeyInput.value);
            });
            tmdbKeyInput.addEventListener('change', function() {
                updateFilterStatus(view, tmdbKeyInput.value);
            });
        }

        // Sprint 11: Base path → update derived paths display in real-time
        var basePathInput = q(view, 'es-base-path');
        if (basePathInput) {
            basePathInput.addEventListener('input', function() {
                var base = basePathInput.value.replace(/\/+$/, '');
                var elM = q(view, 'es-derived-movies');
                var elS = q(view, 'es-derived-shows');
                if (elM) elM.textContent = base + '/catalog/movies';
                if (elS) elS.textContent = base + '/catalog/shows';
            });
        }

        var cfgBasePathInput = q(view, 'cfg-base-path');
        if (cfgBasePathInput) {
            cfgBasePathInput.addEventListener('input', function() {
                var base = cfgBasePathInput.value.replace(/\/+$/, '');
                var elM = q(view, 'cfg-derived-movies');
                var elS = q(view, 'cfg-derived-shows');
                if (elM) elM.textContent = base + '/catalog/movies';
                if (elS) elS.textContent = base + '/catalog/shows';
            });
        }

        // BUG-CONN-CLEAR: hide stale connection result whenever any credential field changes
        ['es-manifest-url', 'es-aio-url', 'es-uuid', 'es-token'].forEach(function(id) {
            var el = q(view, id);
            if (el) el.addEventListener('input', function() {
                var res = q(view, 'es-conn-result');
                if (res) res.style.display = 'none';
            });
        });

        // Regenerate PluginSecret button
        var regenSecretBtn = view.querySelector('#es-regen-secret-btn');
        if (regenSecretBtn) {
            regenSecretBtn.addEventListener('click', function() {
                // Generate a new 32-byte base64 secret client-side
                var bytes = new Uint8Array(32);
                window.crypto.getRandomValues(bytes);
                var b64 = btoa(String.fromCharCode.apply(null, bytes));
                var inp = view.querySelector('#cfg-plugin-secret');
                if (inp) inp.value = b64;
                var warn = view.querySelector('#es-regen-secret-warning');
                if (warn) warn.style.display = '';
            });

        // Summon Marvin button handler
        var marvinBtn = q(view, 'es-summon-marvin-btn');
        if (marvinBtn) {
            marvinBtn.addEventListener('click', function() {
                summonMarvin(view);
            });
        }

        // Refresh Now button handler
        var refreshNowBtn = q(view, 'es-refresh-now-btn');
        if (refreshNowBtn) {
            refreshNowBtn.addEventListener('click', function() {
                triggerRefreshNow(view);
            });
        }
        }

        // Search input (input event)
        var searchEl = view.querySelector('#es-search-q');
        if (searchEl) searchEl.addEventListener('input', function() { searchDebounce(view); });


        // My Picks and Blocked tabs
        initBlockedTab(view);

        // ── Single capture-phase delegated click listener ─────────────────────
        // Capture phase (3rd arg = true) fires BEFORE emby-button custom element
        // handlers and before any stopPropagation, so it survives DOM node
        // replacement that occurs when Emby upgrades is="emby-button" elements.
        view.addEventListener('click', function(e) {
            var el = e.target;
            for (var d = 0; d < 8 && el && el !== view; d++) {
                if (el.getAttribute) {
                    var tab  = el.getAttribute('data-es-tab');
                    var wn   = el.getAttribute('data-es-wiz-next');
                    var wb   = el.getAttribute('data-es-wiz-back');
                    var wizTest = el.getAttribute('data-es-wiz-test');
                    var wizFinish = el.getAttribute('data-es-wiz-finish');
                    var lc   = el.getAttribute('data-es-load-catalogs');
                    var sel  = el.getAttribute('data-es-select');
                    var task = el.getAttribute('data-es-task');
                    var act  = el.getAttribute('data-es-action');
                    var imdb = el.getAttribute('data-es-imdbid');
                    var view_type = el.getAttribute('data-es-dashboard-view');
                    var catMove = el.getAttribute('data-es-catalog-move');
                    var catPrefix = el.getAttribute('data-es-catalog-prefix');
                    var catIdx = el.getAttribute('data-es-catalog-idx');
                    var srcTest         = el.getAttribute('data-es-src-test');
                    var srcTestAll      = el.getAttribute('data-es-src-test-all');
                    var srcSave         = el.getAttribute('data-es-src-save');
                    var accordionHdr    = el.classList && el.classList.contains('es-accordion-hdr');
                    var navigateItem   = el.getAttribute('data-es-navigate-item');
                    if (tab)  { e.stopPropagation(); showTab(view, tab); return; }
                    if (navigateItem) { e.stopPropagation(); Dashboard.navigate('item?id=' + navigateItem); return; }
                    if (srcTest !== null) { e.stopPropagation(); testSource(view, srcTest); return; }
                    if (srcTestAll !== null) { e.stopPropagation(); testAllSources(view); return; }
                    if (srcSave !== null) { e.stopPropagation(); saveSourcesTab(view); return; }
                    if (accordionHdr) { e.stopPropagation(); toggleAccordion(el); return; }
                    if (wn)   { e.stopPropagation(); wizNext(view, parseInt(wn, 10)); return; }
                    if (wb)   { e.stopPropagation(); wizBack(view, parseInt(wb, 10)); return; }
                    if (lc)   { e.stopPropagation(); loadCatalogs(view, lc); return; }
                    if (sel)  { e.stopPropagation(); selectAllCatalogs(view, sel, el.getAttribute('data-es-select-val') === 'true'); return; }
                    if (task) { e.stopPropagation(); runTask(view, task, el); return; }
                    if (imdb) { e.stopPropagation(); pickSearchResult(view, imdb); return; }
                    if (view_type) { e.stopPropagation(); setDashboardView(view, view_type); return; }
                    if (catMove && catPrefix && catIdx) { e.stopPropagation(); moveCatalogRow(view, catPrefix, parseInt(catIdx, 10), catMove); return; }
                    if (act)  { e.stopPropagation(); dispatchAction(view, act, el); return; }
                    if (wizTest !== null) { e.stopPropagation(); testWizardConnection(view, wizTest); return; }
                    if (wizFinish !== null) { e.stopPropagation(); finishWizard(view); return; }
                }
                el = el.parentElement;
            }
        }, true); // capture = true

        // Stop polling when page is hidden, restart when visible
        document.addEventListener('visibilitychange', function() {
            if (document.hidden) {
                stopPolling();
            } else if (!_authExpired) {
                // Only restart if auth hasn't expired
                var activeTab = view.querySelector('[data-es-tab].active');
                if (activeTab && activeTab.getAttribute('data-es-tab') === 'settings') {
                    _dashInterval = setInterval(function() { refreshDashboard(view); }, 10000);
                }
            }
        });
    }

    // Dispatch table for data-es-action values
    function dispatchAction(view, act, el) {
        switch (act) {
            case 'test-connection':    testConnection(view);    break;
            case 'save-and-sync':      saveAndSync(view);       break;
            case 'refresh-dashboard':  refreshDashboard(view);  break;
            case 'refresh-sources':    refreshSourcesTab(view); break;
            case 'save-settings':      saveSettings(view);      break;
            case 'validate-settings':  validateSetup(view, 'es-validate-result-settings'); break;
            case 'clear-profiles':     clearClientProfiles(view); break;
            case 'inspect':            inspect(view);           break;
            case 'invalidate-item':    invalidateItem(view);    break;
            case 'queue-item':         queueItem(view);         break;
            case 'raw-streams':          fetchRawStreams(view);   break;
            case 'toggle-advanced':          toggleAdvanced(view);                    break;
            case 'toggle-debug':             toggleDebug(view);                       break;
            case 'toggle-wiz-conn-details':  toggleConnDetails(view, 'es-wiz-conn');  break;
            case 'toggle-cfg-conn-details':  toggleConnDetails(view, 'cfg-conn');     break;
            case 'reset-wizard-confirm': resetWizardConfirm(view); break;
            case 'purge-catalog-confirm': purgeCatalogConfirm(view); break;
            case 'purge-cancel':        purgeCatalogCancel(view); break;
            case 'purge-catalog-execute': purgeCatalogExecute(view); break;
            case 'nuclear-step1':       nuclearStep1(view);      break;
            case 'nuclear-step2':       nuclearStep2(view);      break;
            case 'nuclear-cancel':      nuclearCancel(view);     break;
            case 'nuclear-execute':     nuclearExecute(view);    break;
            case 'edit-provider':       editProvider(view, el);  break;
            case 'remove-provider':     removeProvider(view, el); break;
            case 'add-new-provider':    addNewProvider(view);    break;
            case 'reset-settings-confirm': resetSettingsConfirm(view); break;
            case 'toggle-quickstart': toggleQuickstartGuide(view); break;
            case 'rerun-wizard': rerunWizard(view); break;
            case 'rotate-api-key': rotateApiKey(view); break;
            case 'go-dashboard':       showTab(view, 'settings'); break;
        }
    }

    function rerunWizard(view) {
        // Switch to wizard tab (v0.51+: no longer supporting multiple providers)
        showTab(view, 'setup');
        var field = q(view, 'wiz-aio-url');
        if (field) { field.focus(); if (field.scrollIntoView) field.scrollIntoView({behavior: 'smooth'}); }
    }

    // ── Wizard connection test ───────────────────────────────────────────────────
    var _wizardTestRetryCount = 0;

    function testWizardConnection(view, type) {
        var isBackup = type === 'aio-backup';
        var url = isBackup ? esVal(view, 'wiz-aio-backup-url') : esVal(view, 'wiz-aio-url');
        var statusEl = q(view, isBackup ? 'wiz-aio-backup-status' : 'wiz-aio-status');
        var resultEl = q(view, 'wiz-connection-result');

        if (!url || url === 'https://' || url === 'http://') {
            if (statusEl) { statusEl.textContent = 'Please enter a URL'; statusEl.style.color = '#dc3545'; }
            return;
        }

        if (statusEl) { statusEl.textContent = 'Testing…'; statusEl.style.color = ''; }
        if (resultEl) resultEl.style.display = 'none';

        // POST /InfiniteDrive/TestUrl with JSON body (not GET /TestConnection)
        esFetch('/InfiniteDrive/TestUrl', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ManifestUrl: url })
        })
        .then(function(r) { return r.json(); })
        .then(function(data) {
                if (data.Ok) {
                    if (statusEl) { statusEl.textContent = '✓ Connected'; statusEl.style.color = '#28a745'; }
                    if (resultEl) {
                        resultEl.textContent = '✓ Connected to your AIOStreams provider!';
                        resultEl.className = 'es-alert es-alert-success';
                        resultEl.style.display = 'block';
                        resultEl.innerHTML = '✓ Connected to your AIOStreams provider!';
                    }
                    // Mark test as successful so Next button can proceed
                    _wizardTestSuccess = true;
                    _wizardTestRetryCount = 0;
                    // Load catalogs after successful connection
                    loadWizardCatalogs(view);
                } else {
                    var isTimeout = data.Message && (
                        data.Message.toLowerCase().indexOf('timed out') !== -1 ||
                        data.Message.toLowerCase().indexOf('timeout') !== -1
                    );
                    if (isTimeout && _wizardTestRetryCount === 0) {
                        // Auto-retry once after 3 seconds
                        _wizardTestRetryCount = 1;
                        if (statusEl) { statusEl.textContent = '⏳ Retrying…'; statusEl.style.color = '#ffc107'; }
                        if (resultEl) {
                            resultEl.className = 'es-alert es-alert-warn';
                            resultEl.style.display = 'block';
                            resultEl.innerHTML = 'Connection timed out — provider may be slow to start.<br/>Retrying in <span id="wiz-retry-count">3</span>…';
                        }
                        var countEl = document.getElementById('wiz-retry-count');
                        var countdown = 3;
                        var countdownInterval = setInterval(function() {
                            countdown--;
                            if (countEl) countEl.textContent = countdown;
                            if (countdown <= 0) {
                                clearInterval(countdownInterval);
                                testWizardConnection(view, type);
                            }
                        }, 1000);
                        return;
                    }
                    // Show final error (after retry or if not a timeout)
                    _wizardTestRetryCount = 0;
                    if (statusEl) { statusEl.textContent = '✗ Failed'; statusEl.style.color = '#dc3545'; }
                    if (resultEl) {
                        var errorMsg = '✗ Connection failed: ' + (data.Message || 'Unknown error');
                        if (isTimeout) {
                            errorMsg = '✗ Connection timed out — provider may be slow to start.<br/>Please try again in a few seconds.';
                        }
                        resultEl.innerHTML = errorMsg;
                        resultEl.className = 'es-alert es-alert-error';
                        resultEl.style.display = 'block';
                    }
                }
            })
            .catch(function(err) {
                _wizardTestRetryCount = 0;
                if (statusEl) { statusEl.textContent = '✗ Error'; statusEl.style.color = '#dc3545'; }
                if (resultEl) {
                    resultEl.textContent = '✗ Connection error: ' + mapFetchError(err);
                    resultEl.className = 'es-alert es-alert-error';
                    resultEl.style.display = 'block';
                }
            });
    }

    function loadWizardCatalogs(view) {
        var panel = q(view, 'es-catalog-panel-wiz');
        if (!panel) return;

        panel.innerHTML = '<span style="display:block;padding:.5em;opacity:.6;font-size:.85em">Loading catalogs…</span>';

        // Get the manifest URL from the input field
        var manifestUrl = esVal(view, 'wiz-aio-url');
        var catalogsUrl = '/InfiniteDrive/Catalogs' +
            (manifestUrl ? '?manifestUrl=' + encodeURIComponent(manifestUrl) : '');

        esFetch(catalogsUrl)
            .then(function(r) { return r.json(); })
            .then(function(data) {
                if (data.Error) {
                    panel.innerHTML = '<span style="display:block;padding:.5em;opacity:.6;font-size:.85em;color:#dc3545">' + esc(data.Error) + '</span>';
                    return;
                }

                var catalogs = data.Catalogs || [];
                if (!catalogs.length) {
                    panel.innerHTML = '<span style="display:block;padding:.5em;opacity:.6;font-size:.85em">No catalogs available from this provider.</span>';
                    return;
                }

                var html = '';
                catalogs.forEach(function(cat) {
                    var checked = 'checked';
                    html += '<label class="checkboxContainer" style="display:block;padding:.3em 0;font-size:.9em">' +
                        '<input type="checkbox" is="emby-checkbox" class="es-catalog-checkbox" ' +
                        'data-es-catalog-id="' + esc(cat.Id) + '" ' + checked + '> ' +
                        esc(cat.Name || cat.Id || 'Unknown') +
                        '</label>';
                });
                panel.innerHTML = html;
                _wizardCatalogs = catalogs;
            })
            .catch(function(err) {
                panel.innerHTML = '<span style="display:block;padding:.5em;opacity:.6;font-size:.85em;color:#dc3545">Failed to load catalogs.</span>';
            });
    }

    // ── Wizard finish ───────────────────────────────────────────────────────────
    function finishWizard(view) {
        var url = esVal(view, 'wiz-aio-url');
        if (!url) {
            alert('Please complete all wizard steps before finishing.');
            return;
        }

        // Collect wizard data
        var basePath = esVal(view, 'wiz-base-path') || '/media/infinitedrive';
        var config = {
            PrimaryManifestUrl: url,
            AioMetadataBaseUrl: esVal(view, 'wiz-aio-metadata-url') || '',
            EnableBackupAioStreams: esChk(view, 'wiz-enable-backup-aio'),
            SecondaryManifestUrl: esChk(view, 'wiz-enable-backup-aio')
                                       ? esVal(view, 'wiz-aio-backup-url') || ''
                                       : '',
            SystemRssFeedUrls: esVal(view, 'wiz-rss-feeds') || '',
            // Derive actual path properties that LibraryProvisioningService reads
            SyncPathMovies: basePath + '/movies',
            SyncPathShows: basePath + '/shows',
            SyncPathAnime: basePath + '/anime',
            LibraryNameMovies: esVal(view, 'wiz-library-name-movies') || 'Streamed Movies',
            LibraryNameSeries: esVal(view, 'wiz-library-name-series') || 'Streamed Series',
            LibraryNameAnime: esVal(view, 'wiz-library-name-anime') || 'Streamed Anime',
            EnableAnimeLibrary: esChk(view, 'wiz-enable-anime'),
            MetadataLanguage: esVal(view, 'wiz-meta-lang') || 'en',
            MetadataCountry: esVal(view, 'wiz-meta-country') || 'US',
            ImageLanguage: esVal(view, 'wiz-meta-img-lang') || 'en',
            EnableCinemetaCatalog: esChk(view, 'wiz-use-cinemeta'),
            EmbyBaseUrl: esVal(view, 'wiz-emby-base-url') || window.location.origin,
            IsFirstRunComplete: true
        };

        // Collect selected catalogs
        var selectedIds = [];
        var checkboxes = view.querySelectorAll('.es-catalog-checkbox');
        for (var i = 0; i < checkboxes.length; i++) {
            if (checkboxes[i].checked) {
                selectedIds.push(checkboxes[i].getAttribute('data-es-catalog-id'));
            }
        }
        config.SelectedCatalogIds = selectedIds.join(',');

        // Save configuration
        var pluginConfig = typeof ApiClient !== 'undefined' ? ApiClient.getPluginConfiguration(pluginId) : {};
        for (var key in config) {
            pluginConfig[key] = config[key];
        }

        // Show sync progress
        var progressEl = q(view, 'es-sync-progress');
        var msgEl = q(view, 'es-sync-msg');
        var barEl = q(view, 'es-sync-bar');
        var wizardNav = q(view, 'es-wizard-nav');

        if (progressEl) progressEl.style.display = 'block';
        if (wizardNav) wizardNav.style.display = 'none';
        if (msgEl) { msgEl.textContent = 'Saving configuration…'; msgEl.className = 'es-alert es-alert-info'; }
        if (barEl) barEl.style.width = '10%';

        ApiClient.updatePluginConfiguration(pluginId, pluginConfig)
            .then(function() {
                if (barEl) barEl.style.width = '30%';
                if (msgEl) msgEl.textContent = 'Creating Emby libraries…';

                // Create Emby library entries for all configured paths.
                return esFetch('/InfiniteDrive/Setup/ProvisionLibraries', {method:'POST'});
            })
            .then(function() {
                if (barEl) barEl.style.width = '55%';
                if (msgEl) msgEl.textContent = 'Starting sync…';

                // Trigger catalog sync.
                return esFetch('/InfiniteDrive/Trigger?task=catalog_sync', {method:'POST'});
            })
            .then(function() {
                if (barEl) barEl.style.width = '75%';

                // Animate sync progress then show completion screen.
                animateSyncProgress(view, function() {
                    setTimeout(function() {
                        if (progressEl) progressEl.style.display = 'none';
                        var completeDiv = q(view, 'es-wizard-complete');
                        if (completeDiv) {
                            completeDiv.style.display = 'block';
                            loadCompletionStats(view);
                        }
                    }, 500);
                });
            })
            .catch(function(err) {
                if (msgEl) {
                    msgEl.textContent = 'Error: ' + (err.message || 'Failed to save configuration');
                    msgEl.className = 'es-alert es-alert-error';
                }
                if (wizardNav) wizardNav.style.display = 'flex';
            });
    }

    // ── Quick-start guide toggle (Sprint 14: UX-ONBOARD-ZERO) ───────────────────
    function toggleQuickstartGuide(view) {
        var content = q(view, 'es-quickstart-content');
        var chevron = q(view, 'es-quickstart-chevron');
        if (content) {
            var isOpen = content.style.display !== 'none';
            content.style.display = isOpen ? 'none' : 'block';
            if (chevron) chevron.style.transform = isOpen ? 'rotate(0deg)' : 'rotate(90deg)';
            localStorage.setItem('es-quickstart-expanded', isOpen ? 'false' : 'true');
        }
    }

    // ── Settings reset (Sprint 14: UX-SETTINGS-RESET) ───────────────────────────
    function resetSettingsConfirm(view) {
        if (confirm('Reset all InfiniteDrive settings to defaults? Your Emby library and .strm files will not be deleted.\n\nThis cannot be undone.')) {
            resetSettings(view);
        }
    }
    function resetSettings(view) {
        var cfg = {
            PrimaryManifestUrl: '',
            SecondaryManifestUrl: '',
            BaseSyncPath: '/media/infinitedrive',
            SyncPathMovies: '/media/infinitedrive/catalog/movies',
            SyncPathShows: '/media/infinitedrive/catalog/shows',
            EmbyBaseUrl: window.location.origin,
            MetadataLanguage: 'en',
            MetadataCertificationCountry: 'US',
            MetadataImageLanguage: 'en',
            EnableDiscoverChannel: true,
            EnableAioStreamsCatalog: true,
            AioStreamsCatalogIds: '',
            IsFirstRunComplete: false
        };
        var btn = q(view, 'button[data-es-action="reset-settings-confirm"]');
        if (btn) btn.disabled = true;
        ApiClient.updatePluginConfiguration(pluginId, cfg)
            .then(function() {
                _loadedConfig = cfg;
                if (btn) { btn.disabled = false; btn.innerHTML = '✓ Reset complete!'; setTimeout(function() { window.location.reload(); }, 1500); }
            })
            .catch(function(err) {
                if (btn) { btn.disabled = false; btn.innerHTML = '🔄 Reset All Settings'; }
                var result = q(view, 'es-reset-result');
                if (result) { result.textContent = '✗ Reset failed: ' + err.message; result.className = 'es-alert es-alert-error'; result.style.display = 'block'; }
            });
    }



    // ── Advanced settings & debug collapsibles ────────────────────────────────

    function toggleAdvanced(view) {
        var inner  = q(view, 'cfg-advanced-inner');
        var arrow  = q(view, 'cfg-advanced-arrow');
        if (!inner) return;
        var open = inner.style.display !== 'none';
        inner.style.display = open ? 'none' : 'block';
        if (arrow) arrow.textContent = open ? '▼ show' : '▲ hide';
    }

    function toggleDebug(view) {
        var inner = q(view, 'es-debug-inner');
        var arrow = q(view, 'es-debug-arrow');
        if (!inner) return;
        var open = inner.style.display !== 'none';
        inner.style.display = open ? 'none' : 'block';
        if (arrow) arrow.textContent = open ? '▼ show' : '▲ hide';
    }

    function toggleConnDetails(view, prefix) {
        var inner = q(view, prefix + '-inner');
        var arrow = q(view, prefix + '-arrow');
        if (!inner) return;
        var open = inner.style.display !== 'none';
        inner.style.display = open ? 'none' : 'block';
        if (arrow) arrow.textContent = open ? '▶' : '▼';
    }

    // ── Danger Zone handlers ──────────────────────────────────────────────────

    function resetWizardConfirm(view) {
        if (!confirm('Reset the Setup Wizard?\n\nYour settings and library will not be changed — this only re-shows the wizard on next page load.\n\n"Mostly Harmless."')) return;
        esFetch('/InfiniteDrive/Trigger?task=reset_wizard', {method:'POST'})
            .then(function(r) { return r.json(); })
            .then(function(d) {
                if (d.Status === 'ok') {
                    Dashboard.alert('Wizard reset. Reload the plugin page to see it.');
                } else {
                    Dashboard.alert('Error: ' + (d.Message || 'unknown'));
                }
            })
            .catch(function(err) { Dashboard.alert('Request failed: ' + err.message); });
    }

    function purgeCatalogConfirm(view) {
        var el = q(view, 'es-purge-confirm');
        if (el) el.style.display = 'block';
    }

    function purgeCatalogCancel(view) {
        var el = q(view, 'es-purge-confirm');
        if (el) el.style.display = 'none';
        // also hide nuclear steps
        ['es-nuclear-step1','es-nuclear-step2'].forEach(function(id) {
            var e = q(view, id); if (e) e.style.display = 'none';
        });
    }

    function purgeCatalogExecute(view) {
        var el = q(view, 'es-purge-confirm');
        if (el) el.style.display = 'none';
        esFetch('/InfiniteDrive/Trigger?task=purge_catalog', {method:'POST'})
            .then(function(r) { return r.json(); })
            .then(function(d) {
                if (d.Status === 'ok') {
                    Dashboard.alert('Catalog purge started. All .strm files and catalog data will be removed.\n\nRun a Catalog Sync to rebuild. Run a Library Scan in Emby to clear orphaned entries.');
                } else {
                    Dashboard.alert('Error: ' + (d.Message || 'unknown'));
                }
            })
            .catch(function(err) { Dashboard.alert('Request failed: ' + err.message); });
    }

    function nuclearStep1(view) {
        var s1 = q(view, 'es-nuclear-step1');
        if (s1) { s1.style.display = 'block'; s1.scrollIntoView({behavior:'smooth',block:'center'}); }
    }

    function nuclearStep2(view) {
        var s1 = q(view, 'es-nuclear-step1');
        var s2 = q(view, 'es-nuclear-step2');
        var inp = q(view, 'es-nuclear-confirm-input');
        if (s1) s1.style.display = 'none';
        if (s2) { s2.style.display = 'block'; s2.scrollIntoView({behavior:'smooth',block:'center'}); }
        if (inp) { inp.value = ''; inp.focus(); }
    }

    function nuclearCancel(view) {
        ['es-nuclear-step1','es-nuclear-step2','es-purge-confirm'].forEach(function(id) {
            var e = q(view, id); if (e) e.style.display = 'none';
        });
    }

    function nuclearExecute(view) {
        var inp     = q(view, 'es-nuclear-confirm-input');
        var errEl   = q(view, 'es-nuclear-confirm-error');
        var doneEl  = q(view, 'es-nuclear-done');
        var confirm = inp ? inp.value.trim().toUpperCase() : '';

        if (confirm !== 'VOGON') {
            if (errEl) {
                errEl.textContent = '✗ That\'s not quite right. The Vogons are very particular about paperwork. Type VOGON (all caps).';
                errEl.style.display = 'block';
            }
            return;
        }
        if (errEl) errEl.style.display = 'none';

        var s2 = q(view, 'es-nuclear-step2');
        if (s2) s2.style.display = 'none';

        if (doneEl) {
            doneEl.style.display = 'block';
            doneEl.innerHTML = '<div class="es-alert es-alert-info" style="margin:0">⏳ The Vogon Constructor Fleet is at work… deleting everything… please wait…</div>';
        }

        esFetch('/InfiniteDrive/Trigger?task=nuclear_reset', {method:'POST'})
            .then(function(r) { return r.json(); })
            .then(function(d) {
                if (d.Status === 'ok') {
                    if (doneEl) {
                        doneEl.innerHTML =
                            '<div class="es-alert es-alert-success" style="margin:0">' +
                            '<strong>So Long, and Thanks for All the Streams.</strong><br/>' +
                            'All data and configuration have been wiped. The Vogon ships have moved on.<br/>' +
                            '<em>Your AIOStreams instance is unaffected.</em><br/><br/>' +
                            'Reload this page — the Setup Wizard is waiting to begin again.<br/>' +
                            'Remember to run a Library Scan in Emby to clear orphaned entries.' +
                            '</div>';
                    }
                } else {
                    if (doneEl) {
                        doneEl.innerHTML = '<div class="es-alert es-alert-error" style="margin:0">✗ ' + esc(d.Message || 'Reset failed') + '</div>';
                    }
                }
            })
            .catch(function(err) {
                if (doneEl) {
                    doneEl.innerHTML = '<div class="es-alert es-alert-error" style="margin:0">✗ Request failed: ' + esc(err.message) + '</div>';
                }
            });
    }

    // ── Improbability Drive functions ───────────────────────────────────────────
    function loadImprobabilityStatus(view) {
        // Fetch status from /InfiniteDrive/Status
        ApiClient.getJSON(ApiClient.getUrl('InfiniteDrive/Status')).then(function(status) {
            // Refresh Worker
            var refreshDot = q(view, 'es-refresh-dot');
            var refreshStatus = q(view, 'es-refresh-status');
            var refreshLastRun = q(view, 'es-refresh-last-run');

            if (status.RefreshHasRun && status.RefreshLastRunAt) {
                var refreshMinutesAgo = minutesAgo(status.RefreshLastRunAt);
                if (refreshMinutesAgo < 12) {
                    refreshDot.textContent = '🟢';
                } else if (refreshMinutesAgo < 18) {
                    refreshDot.textContent = '🟡';
                } else {
                    refreshDot.textContent = '🔴';
                }
                refreshLastRun.textContent = 'Last run: ' + formatTimeAgo(status.RefreshLastRunAt);
            } else {
                refreshDot.textContent = '⚪';
                refreshLastRun.textContent = 'Not yet run';
            }

            // Active step
            if (status.RefreshActiveStep) {
                refreshDot.textContent = '🔄';
                refreshStatus.textContent = 'Running: ' + status.RefreshActiveStep + ' (' + status.RefreshItemsProcessed + ' items)';
            } else {
                refreshStatus.textContent = 'Idle';
            }

            // Deep Clean
            var deepCleanDot = q(view, 'es-deepclean-dot');
            var deepCleanStatus = q(view, 'es-deepclean-status');
            var deepCleanLastRun = q(view, 'es-deepclean-last-run');

            if (status.MarvinHasRun && status.MarvinLastRunAt) {
                var marvinHoursAgo = hoursAgo(status.MarvinLastRunAt);
                if (marvinHoursAgo < 36) {
                    deepCleanDot.textContent = '🟢';
                } else if (marvinHoursAgo < 54) {
                    deepCleanDot.textContent = '🟡';
                } else {
                    deepCleanDot.textContent = '🔴';
                }
                deepCleanLastRun.textContent = 'Last run: ' + formatTimeAgo(status.MarvinLastRunAt);
            } else {
                deepCleanDot.textContent = '⚪';
                deepCleanLastRun.textContent = 'Not yet run';
            }

            deepCleanStatus.textContent = 'Idle';

            // Enrichment counts
            var needsEnrichEl = q(view, 'es-needs-enrich-count');
            var blockedEl = q(view, 'es-blocked-count');
            if (needsEnrichEl) needsEnrichEl.textContent = status.NeedsEnrichCount || 0;
            if (blockedEl) blockedEl.textContent = status.BlockedCount || 0;

            // Sprint 155: Cooldown badge
            var cooldownBadge = q(view, 'es-cooldown-badge');
            var cooldownMsg = q(view, 'es-cooldown-msg');
            var suggestPrivate = q(view, 'es-suggest-private');
            if (status.CooldownActive && cooldownBadge && cooldownMsg) {
                cooldownBadge.style.display = '';
                cooldownMsg.textContent = 'Upstream busy — pausing briefly to stay a good neighbour.';
                // Auto-clear badge when cooldown expires
                if (status.CooldownUntil) {
                    var remaining = new Date(status.CooldownUntil) - new Date();
                    if (remaining > 0) {
                        setTimeout(function() {
                            if (cooldownBadge) cooldownBadge.style.display = 'none';
                        }, remaining);
                    }
                }
            } else if (cooldownBadge) {
                cooldownBadge.style.display = 'none';
            }
            if (status.SuggestPrivateInstance && suggestPrivate) {
                suggestPrivate.style.display = '';
            } else if (suggestPrivate) {
                suggestPrivate.style.display = 'none';
            }
        }).catch(function(err) {
            console.error('Failed to load Improbability Drive status:', err);
        });
    }

    function triggerRefreshNow(view) {
        var btn = q(view, 'es-refresh-now-btn');
        if (btn) {
            btn.textContent = 'Starting...';
            btn.disabled = true;
        }

        // Find RefreshTask and trigger it
        ApiClient.getJSON(ApiClient.getUrl('ScheduledTasks')).then(function(tasks) {
            var refresh = (tasks || []).find(function(t) { return t.Key === 'InfiniteDriveRefresh'; });
            if (refresh) {
                ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('ScheduledTasks/Running/' + refresh.Id) });

                // Poll until active step appears (task started) then until it clears (task done)
                var pollInterval = setInterval(function() {
                    loadImprobabilityStatus(view);
                    ApiClient.getJSON(ApiClient.getUrl('InfiniteDrive/Status')).then(function(status) {
                        if (!status.RefreshActiveStep && btn) {
                            clearInterval(pollInterval);
                            btn.textContent = 'Refresh Now';
                            btn.disabled = false;
                        }
                    });
                }, 2000);

                // Safety: stop polling after 5 minutes max
                setTimeout(function() { clearInterval(pollInterval); if (btn) { btn.textContent = 'Refresh Now'; btn.disabled = false; } }, 300000);
            } else {
                if (btn) {
                    btn.textContent = 'Refresh Now';
                    btn.disabled = false;
                }
            }
        }).catch(function(err) {
            console.error('Failed to trigger refresh:', err);
            if (btn) {
                btn.textContent = 'Refresh Now';
                btn.disabled = false;
            }
        });
    }

    // Helper functions for time formatting
    function minutesAgo(isoString) {
        var now = new Date();
        var then = new Date(isoString);
        return Math.floor((now - then) / 60000);
    }

    function hoursAgo(isoString) {
        var now = new Date();
        var then = new Date(isoString);
        return Math.floor((now - then) / 3600000);
    }

    function formatTimeAgo(isoString) {
        var now = new Date();
        var then = new Date(isoString);
        var diffMs = now - then;
        var diffMins = Math.floor(diffMs / 60000);

        if (diffMins < 60) {
            return diffMins + 'm ago';
        }
        var diffHours = Math.floor(diffMins / 60);
        if (diffHours < 24) {
            return diffHours + 'h ago';
        }
        var diffDays = Math.floor(diffHours / 24);
        return diffDays + 'd ago';
    }

    function summonMarvin(view) {
        var btn = q(view, 'es-summon-marvin-btn');
        var statusEl = q(view, 'es-marvin-status');
        if (!btn || !statusEl) return;

        btn.textContent = 'Marvin is grumbling…';
        btn.disabled = true;
        statusEl.textContent = 'Starting deep clean…';

        ApiClient.getJSON(ApiClient.getUrl('ScheduledTasks')).then(function(tasks) {
            var task = (tasks || []).find(function(t) { return t.Key === 'InfiniteDriveMarvin'; });
            if (!task) {
                btn.textContent = 'Summon Marvin';
                btn.disabled = false;
                statusEl.textContent = 'Task not found.';
                return;
            }
            ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('ScheduledTasks/Running/' + task.Id) });
            statusEl.textContent = 'Deep clean running…';

            // Safety reset after 5 minutes
            setTimeout(function() {
                btn.textContent = 'Summon Marvin';
                btn.disabled = false;
                statusEl.textContent = '';
                loadImprobabilityStatus(view);
            }, 300000);
        }).catch(function() {
            btn.textContent = 'Summon Marvin';
            btn.disabled = false;
            statusEl.textContent = 'Failed to start.';
        });
    }

    function loadContentMgmtSources(view) {
        var el = view.querySelector('#es-sync-sources-list');
        if (!el) return;

        esFetch('/InfiniteDrive/Status')
            .then(function(r) { return r.json(); })
            .then(function(data) {
                // Use SyncStates instead of CatalogSources (API change compatibility)
                var sources = data.SyncStates || [];
                if (!sources.length) {
                    el.innerHTML = '<span style="opacity:.5">No sources configured yet.</span>';
                    return;
                }
                el.innerHTML = sources.map(function(src) {
                    // Use LastReachedAt instead of LastReachableAt (API change compatibility)
                    var ok = src.LastReachedAt ? '🟢' : '🔴';
                    var lastSync = src.LastSyncAt ? fmtRelative(new Date(src.LastSyncAt)) : 'never';
                    return '<div style="padding:.3em 0">' + ok + ' <strong>' + esc(src.SourceKey || '?') + '</strong>'
                        + ' &mdash; ' + (src.ItemCount || 0) + ' items, synced ' + lastSync + '</div>';
                }).join('');
            })
            .catch(function() {
                el.innerHTML = '<span style="color:#dc3545">Failed to load. Check server logs.</span>';
            });
    }

    // ── Cleanup on view hide ──────────────────────────────────────────────────
    function cleanup() {
        if (_dashInterval)     { clearInterval(_dashInterval);     _dashInterval     = null; }
        if (_catalogPollTimer) { clearInterval(_catalogPollTimer); _catalogPollTimer = null; }
    }

    // ── Wizard event handlers ──────────────────────────────────────────────────
    function bindWizardHandlers(view) {
        // Base path input for derived paths
        var basePathInput = q(view, 'wiz-base-path');
        if (basePathInput) {
            basePathInput.addEventListener('input', function() {
                var base = basePathInput.value || '/media/infinitedrive';
                var elM = q(view, 'wiz-derived-movies');
                var elS = q(view, 'wiz-derived-shows');
                if (elM) elM.textContent = base + '/catalog/movies';
                if (elS) elS.textContent = base + '/catalog/shows';
            });
        }
    }


    // ── Blocked Items tab (admin) ─────────────────────────────────────────────

    function loadBlockedItems(view) {
        var listEl = view.querySelector('#es-blocked-items-list');
        if (listEl) listEl.innerHTML = '<p style="opacity:.5;font-size:.85em">Loading…</p>';

        esFetch('/InfiniteDrive/Admin/BlockedItems')
            .then(function(r) { return r.json(); })
            .then(function(data) {
                if (!listEl) return;
                if (!data.Items || data.Items.length === 0) {
                    listEl.innerHTML = '<p style="opacity:.5;font-size:.85em">No blocked items.</p>';
                    return;
                }
                listEl.innerHTML = data.Items.map(function(item) {
                    return '<div style="display:flex;align-items:center;gap:.75em;padding:.5em 0;border-bottom:1px solid rgba(255,255,255,.06)">' +
                        '<label style="display:flex;align-items:center;gap:.5em;flex:1;cursor:pointer">' +
                        '<input type="checkbox" class="es-blocked-checkbox" data-id="' + esc(item.Id) + '" style="width:1em;height:1em">' +
                        '<span>' + esc(item.Title) + (item.Year ? ' (' + item.Year + ')' : '') + '</span>' +
                        '</label>' +
                        '<span style="opacity:.5;font-size:.8em">' + esc(item.ImdbId || '') + ' &bull; Retries: ' + (item.RetryCount || 0) + '</span>' +
                        '</div>';
                }).join('');
            })
            .catch(function() {
                if (listEl) listEl.innerHTML = '<p style="color:#dc3545;font-size:.85em">Failed to load blocked items.</p>';
            });
    }

    function initBlockedTab(view) {
        function unblockIds(ids) {
            esFetch('/InfiniteDrive/Admin/UnblockItems', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ItemIds: ids })
            })
            .then(function() {
                Dashboard.alert(ids.length + ' item(s) unblocked and queued for enrichment.');
                loadBlockedItems(view);
            })
            .catch(function() { Dashboard.alert('Unblock failed. Check server logs.'); });
        }

        var selBtn = view.querySelector('#es-unblock-selected-btn');
        if (selBtn && !selBtn._esBound) {
            selBtn._esBound = true;
            selBtn.addEventListener('click', function() {
                var checked = view.querySelectorAll('.es-blocked-checkbox:checked');
                if (!checked.length) { Dashboard.alert('No items selected.'); return; }
                unblockIds(Array.from(checked).map(function(cb) { return cb.getAttribute('data-id'); }));
            });
        }

        var allBtn = view.querySelector('#es-unblock-all-btn');
        if (allBtn && !allBtn._esBound) {
            allBtn._esBound = true;
            allBtn.addEventListener('click', function() {
                var checkboxes = view.querySelectorAll('.es-blocked-checkbox');
                if (!checkboxes.length) { Dashboard.alert('No blocked items.'); return; }
                unblockIds(Array.from(checkboxes).map(function(cb) { return cb.getAttribute('data-id'); }));
            });
        }

        // Block by IMDB ID
        var blockBtn = view.querySelector('#es-block-imdb-btn');
        var blockInput = view.querySelector('#es-block-imdb-input');
        var blockResult = view.querySelector('#es-block-result');
        if (blockBtn && !blockBtn._esBound) {
            blockBtn._esBound = true;
            blockBtn.addEventListener('click', function() {
                var imdbId = (blockInput.value || '').trim();
                if (!imdbId) { Dashboard.alert('Enter an IMDB ID.'); return; }
                if (!confirm('Block ' + imdbId + '? This will delete its .strm file and remove it from all user libraries.')) return;
                blockResult.textContent = 'Blocking…';
                blockResult.style.color = '';
                esFetch('/InfiniteDrive/Admin/BlockItems', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ ImdbIds: [imdbId] })
                })
                .then(function(r) { return r.json(); })
                .then(function(data) {
                    if (data.Success) {
                        blockResult.textContent = 'Blocked ' + imdbId + ' successfully.';
                        blockResult.style.color = '#28a745';
                        blockInput.value = '';
                        loadBlockedItems(view);
                    } else {
                        blockResult.textContent = 'Failed: ' + (data.Errors && data.Errors.length ? data.Errors.join(', ') : 'unknown error');
                        blockResult.style.color = '#dc3545';
                    }
                })
                .catch(function() {
                    blockResult.textContent = 'Block request failed.';
                    blockResult.style.color = '#dc3545';
                });
            });
        }
    }

    // ── Module export — simple function pattern (like HomeScreenCompanion) ──────
    return function (view) {
        // Bind all event listeners
        initView(view);

        // Load config on view show
        view.addEventListener('viewshow', function () {
            bindWizardHandlers(view);
            loadConfig(view);
        });

        // Cleanup on view hide
        view.addEventListener('viewhide', cleanup);
    };
});
