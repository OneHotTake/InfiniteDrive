define(['loading', 'emby-input', 'emby-button', 'emby-select', 'emby-scrollpanel'],
function (loading) {
    'use strict';

    // ── Module-level state ────────────────────────────────────────────────────
    var _currentTab = 'discover';
    var _searchTimeout = null;
    var _browseOffset = 0;
    var _browseLimit = 50;
    var _totalItems = 0;
    var _currentDetailItem = null;
    var _currentUserId = null;
    var _userListLimit = -1; // -1 = not yet fetched

    // ── Authenticated fetch ───────────────────────────────────────────────────
    function idFetch(url, opts) {
        opts = opts || {};
        var token = (typeof ApiClient !== 'undefined' && ApiClient.accessToken) ? ApiClient.accessToken() : '';
        opts.headers = Object.assign({ 'X-Emby-Token': token }, opts.headers || {});
        return fetch(url, opts);
    }

    // ── DOM helpers ───────────────────────────────────────────────────────────
    function q(id) { return document.getElementById(id); }
    function esc(s) {
        if (!s) return '';
        return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
    }

    // ── Notification helper ───────────────────────────────────────────────────
    function showToast(message, type) {
        type = type || 'success';
        var toast = q('id-toast');
        if (!toast) return;

        toast.textContent = message;
        toast.className = 'id-toast id-toast-' + type;
        toast.style.display = 'block';

        setTimeout(function() {
            toast.style.display = 'none';
        }, 3000);
    }

    // ── Loading state helpers ────────────────────────────────────────────────
    function setGridLoading(gridId, loadingElId, show) {
        var grid = q(gridId);
        var loadingEl = q(loadingElId);
        if (grid) grid.style.display = show ? 'none' : '';
        if (loadingEl) loadingEl.style.display = show ? 'flex' : 'none';
    }

    // ── Tab switching ────────────────────────────────────────────────────────
    function switchTab(tabName) {
        // Guard: if lists are disabled, redirect to discover
        if (tabName === 'lists' && _userListLimit === 0) {
            tabName = 'discover';
        }

        // Update active tab button
        document.querySelectorAll('.id-tab').forEach(function(tab) {
            tab.classList.remove('active');
            if (tab.getAttribute('data-tab') === tabName) {
                tab.classList.add('active');
            }
        });

        // Update active panel
        document.querySelectorAll('.id-tab-panel').forEach(function(panel) {
            panel.classList.remove('active');
        });
        var activePanel = q('id-tab-' + tabName);
        if (activePanel) activePanel.classList.add('active');

        _currentTab = tabName;

        // Load content for the active tab
        if (tabName === 'discover') {
            loadBrowse();
        } else if (tabName === 'picks') {
            loadPicks();
        } else if (tabName === 'lists') {
            loadLists();
        }
    }

    // ── Discover Tab: Browse ──────────────────────────────────────────────────
    function loadBrowse() {
        setGridLoading('id-discover-grid', 'id-loading', true);
        q('id-pagination').style.display = 'none';
        q('id-empty-discover').style.display = 'none';

        var url = '/InfiniteDrive/Discover/Browse?limit=' + _browseLimit + '&offset=' + _browseOffset;
        idFetch(url)
            .then(function(res) { return res.json(); })
            .then(function(data) {
                _totalItems = data.total || 0;
                renderGrid('id-discover-grid', data.items || []);
                setGridLoading('id-discover-grid', 'id-loading', false);
                renderPagination();
            })
            .catch(function(err) {
                console.error('Failed to load browse:', err);
                showToast('Failed to load catalog', 'error');
                setGridLoading('id-discover-grid', 'id-loading', false);
            });
    }

    // ── Discover Tab: Search ──────────────────────────────────────────────────
    function performSearch(query) {
        setGridLoading('id-discover-grid', 'id-loading', true);
        q('id-pagination').style.display = 'none';
        q('id-empty-discover').style.display = 'none';

        var url = '/InfiniteDrive/Discover/Search?q=' + encodeURIComponent(query) + '&limit=50';
        idFetch(url)
            .then(function(res) { return res.json(); })
            .then(function(data) {
                var items = data.items || [];
                renderGrid('id-discover-grid', items);
                setGridLoading('id-discover-grid', 'id-loading', false);

                if (items.length === 0) {
                    q('id-empty-discover').style.display = 'block';
                }
            })
            .catch(function(err) {
                console.error('Failed to search:', err);
                showToast('Search failed', 'error');
                setGridLoading('id-discover-grid', 'id-loading', false);
            });
    }

    function setupSearch() {
        var searchInput = q('id-search-input');
        var searchBtn = q('id-search-btn');

        // Debounced search on input
        searchInput.addEventListener('input', function(e) {
            clearTimeout(_searchTimeout);
            var query = e.target.value.trim();

            if (query.length === 0) {
                // Reset to browse
                _browseOffset = 0;
                loadBrowse();
                return;
            }

            if (query.length >= 2) {
                _searchTimeout = setTimeout(function() {
                    performSearch(query);
                }, 300);
            }
        });

        // Search button click
        searchBtn.addEventListener('click', function() {
            var query = searchInput.value.trim();
            if (query.length >= 2) {
                performSearch(query);
            }
        });
    }

    // ── Grid rendering ───────────────────────────────────────────────────────
    function renderGrid(gridId, items) {
        var grid = q(gridId);
        if (!grid) return;

        if (items.length === 0) {
            grid.innerHTML = '';
            return;
        }

        var html = items.map(function(item) {
            return renderCard(item);
        }).join('');
        grid.innerHTML = html;

        // Attach click handlers
        grid.querySelectorAll('.id-card').forEach(function(card) {
            card.addEventListener('click', function() {
                var imdbId = this.getAttribute('data-imdb');
                var mediaType = this.getAttribute('data-type');
                if (imdbId) {
                    openDetailModal(imdbId, mediaType);
                }
            });
        });
    }

    function renderCard(item) {
        var year = item.year ? '<span class="id-card-year">' + esc(item.year) + '</span>' : '';
        var rating = item.imdbRating ? '<span class="id-card-rating">★ ' + item.imdbRating.toFixed(1) + '</span>' : '';
        var cert = item.certification ? '<span class="id-card-cert">' + esc(item.certification) + '</span>' : '';
        var inLib = item.inLibrary ? '<span class="id-card-in-lib">In Library</span>' : '';

        return '<div class="id-card" data-imdb="' + esc(item.imdbId) + '" data-type="' + esc(item.mediaType) + '">' +
            '<div class="id-card-poster">' +
                '<img src="' + esc(item.posterUrl || '') + '" alt="' + esc(item.title) + '" loading="lazy" />' +
            '</div>' +
            '<div class="id-card-info">' +
                '<h3>' + esc(item.title) + '</h3>' +
                '<div class="id-card-meta">' +
                    year + rating + cert + inLib +
                '</div>' +
            '</div>' +
        '</div>';
    }

    // ── Pagination ───────────────────────────────────────────────────────────
    function renderPagination() {
        var pagination = q('id-pagination');
        if (_totalItems <= _browseLimit) {
            pagination.style.display = 'none';
            return;
        }

        pagination.style.display = 'flex';

        var totalPages = Math.ceil(_totalItems / _browseLimit);
        var currentPage = Math.floor(_browseOffset / _browseLimit) + 1;

        var html = '';
        html += '<button id="id-prev-page"' + (_browseOffset === 0 ? ' disabled' : '') + '>Previous</button>';
        html += '<span class="id-page-info">Page ' + currentPage + ' of ' + totalPages + '</span>';
        html += '<button id="id-next-page"' + (_browseOffset + _browseLimit >= _totalItems ? ' disabled' : '') + '>Next</button>';

        pagination.innerHTML = html;

        q('id-prev-page').addEventListener('click', function() {
            if (_browseOffset > 0) {
                _browseOffset -= _browseLimit;
                loadBrowse();
            }
        });

        q('id-next-page').addEventListener('click', function() {
            if (_browseOffset + _browseLimit < _totalItems) {
                _browseOffset += _browseLimit;
                loadBrowse();
            }
        });
    }

    // ── Detail Modal ─────────────────────────────────────────────────────────
    function openDetailModal(imdbId, mediaType) {
        var modal = q('id-detail-modal');
        modal.style.display = 'flex';

        // Reset modal state
        q('id-detail-add-btn').style.display = '';
        q('id-detail-remove-btn').style.display = 'none';
        q('id-detail-poster-img').src = '';
        q('id-detail-title').textContent = '';
        q('id-detail-meta').textContent = '';
        q('id-detail-overview').textContent = '';
        q('id-detail-rating').textContent = '';
        q('id-detail-certification').textContent = '';

        // Fetch detail
        var url = '/InfiniteDrive/Discover/Detail?imdbId=' + encodeURIComponent(imdbId) + '&type=' + encodeURIComponent(mediaType);
        idFetch(url)
            .then(function(res) { return res.json(); })
            .then(function(item) {
                _currentDetailItem = item;

                // Populate modal
                q('id-detail-poster-img').src = item.posterUrl || '';
                q('id-detail-title').textContent = item.title || '';
                q('id-detail-meta').textContent = (item.year || '') + ' • ' + (item.mediaType || '');
                q('id-detail-overview').textContent = item.overview || '';

                var ratingHtml = item.imdbRating ? '<span class="id-detail-rating">★ ' + item.imdbRating.toFixed(1) + '</span>' : '';
                var certHtml = item.certification ? '<span class="id-detail-certification">' + esc(item.certification) + '</span>' : '';
                q('id-detail-rating').innerHTML = ratingHtml;
                q('id-detail-certification').innerHTML = certHtml;

                // Update button based on library status
                if (item.inLibrary) {
                    q('id-detail-add-btn').style.display = 'none';
                    q('id-detail-remove-btn').style.display = '';
                }
            })
            .catch(function(err) {
                console.error('Failed to load detail:', err);
                showToast('Failed to load item details', 'error');
                closeModal('id-detail-modal');
            });
    }

    function setupDetailModal() {
        // Close buttons
        q('id-modal-close').addEventListener('click', function() { closeModal('id-detail-modal'); });
        q('id-detail-close-btn').addEventListener('click', function() { closeModal('id-detail-modal'); });

        // Backdrop click
        q('id-detail-modal').addEventListener('click', function(e) {
            if (e.target.classList.contains('id-modal-backdrop')) {
                closeModal('id-detail-modal');
            }
        });

        // Add to library
        q('id-detail-add-btn').addEventListener('click', function() {
            if (!_currentDetailItem) return;

            var btn = this;
            btn.disabled = true;
            btn.textContent = 'Adding...';

            var url = '/InfiniteDrive/Discover/AddToLibrary';
            var params = new URLSearchParams({
                imdbId: _currentDetailItem.imdbId,
                type: _currentDetailItem.mediaType,
                title: _currentDetailItem.title,
                year: _currentDetailItem.year || ''
            });

            idFetch(url + '?' + params.toString(), { method: 'POST' })
                .then(function(res) {
                    if (res.ok) {
                        showToast('Added to library', 'success');
                        closeModal('id-detail-modal');
                        // Refresh current view
                        if (_currentTab === 'discover') loadBrowse();
                        else if (_currentTab === 'picks') loadPicks();
                    } else {
                        throw new Error('Add failed');
                    }
                })
                .catch(function(err) {
                    console.error('Failed to add to library:', err);
                    showToast('Failed to add to library', 'error');
                    btn.disabled = false;
                    btn.textContent = 'Add to Library';
                });
        });

        // Remove from library
        q('id-detail-remove-btn').addEventListener('click', function() {
            if (!_currentDetailItem) return;

            var btn = this;
            btn.disabled = true;
            btn.textContent = 'Removing...';

            var url = '/InfiniteDrive/Discover/RemoveFromLibrary?imdbId=' + encodeURIComponent(_currentDetailItem.imdbId);

            idFetch(url, { method: 'POST' })
                .then(function(res) {
                    if (res.ok) {
                        showToast('Removed from library', 'success');
                        closeModal('id-detail-modal');
                        // Refresh current view
                        if (_currentTab === 'discover') loadBrowse();
                        else if (_currentTab === 'picks') loadPicks();
                    } else {
                        throw new Error('Remove failed');
                    }
                })
                .catch(function(err) {
                    console.error('Failed to remove from library:', err);
                    showToast('Failed to remove from library', 'error');
                    btn.disabled = false;
                    btn.textContent = 'Remove from Library';
                });
        });
    }

    // ── My Picks Tab ─────────────────────────────────────────────────────────
    function loadPicks() {
        setGridLoading('id-picks-grid', 'id-picks-loading', true);
        q('id-picks-empty').style.display = 'none';

        idFetch('/InfiniteDrive/User/Pins')
            .then(function(res) { return res.json(); })
            .then(function(data) {
                var items = data.items || [];

                if (items.length === 0) {
                    q('id-picks-empty').style.display = 'block';
                    q('id-browse-discover').addEventListener('click', function() {
                        switchTab('discover');
                    });
                } else {
                    renderPicksGrid(items);
                }

                setGridLoading('id-picks-grid', 'id-picks-loading', false);
            })
            .catch(function(err) {
                console.error('Failed to load picks:', err);
                showToast('Failed to load your picks', 'error');
                setGridLoading('id-picks-grid', 'id-picks-loading', false);
            });
    }

    function renderPicksGrid(items) {
        var grid = q('id-picks-grid');
        if (!grid) return;

        var html = items.map(function(item) {
            var year = item.year ? '<span class="id-card-year">' + esc(item.year) + '</span>' : '';
            var rating = item.imdbRating ? '<span class="id-card-rating">★ ' + item.imdbRating.toFixed(1) + '</span>' : '';

            return '<div class="id-card" data-imdb="' + esc(item.imdbId) + '" data-type="' + esc(item.mediaType) + '">' +
                '<div class="id-card-poster">' +
                    '<img src="' + esc(item.posterUrl || '') + '" alt="' + esc(item.title) + '" loading="lazy" />' +
                '</div>' +
                '<div class="id-card-info">' +
                    '<h3>' + esc(item.title) + '</h3>' +
                    '<div class="id-card-meta">' + year + rating + '</div>' +
                '</div>' +
                '<div class="id-card-actions">' +
                    '<button class="id-pick-remove" data-imdb="' + esc(item.imdbId) + '">Remove</button>' +
                '</div>' +
            '</div>';
        }).join('');

        grid.innerHTML = html;

        // Card click handlers (open detail)
        grid.querySelectorAll('.id-card').forEach(function(card) {
            card.addEventListener('click', function(e) {
                if (e.target.classList.contains('id-pick-remove')) return;
                var imdbId = this.getAttribute('data-imdb');
                var mediaType = this.getAttribute('data-type');
                if (imdbId) {
                    openDetailModal(imdbId, mediaType);
                }
            });
        });

        // Remove button handlers
        grid.querySelectorAll('.id-pick-remove').forEach(function(btn) {
            btn.addEventListener('click', function(e) {
                e.stopPropagation();
                var imdbId = this.getAttribute('data-imdb');
                removePick(imdbId, this);
            });
        });
    }

    function removePick(imdbId, btnElement) {
        btnElement.disabled = true;
        btnElement.textContent = 'Removing...';

        var url = '/InfiniteDrive/Discover/RemoveFromLibrary?imdbId=' + encodeURIComponent(imdbId);

        idFetch(url, { method: 'POST' })
            .then(function(res) {
                if (res.ok) {
                    showToast('Removed from picks', 'success');
                    loadPicks(); // Reload to update UI
                } else {
                    throw new Error('Remove failed');
                }
            })
            .catch(function(err) {
                console.error('Failed to remove pick:', err);
                showToast('Failed to remove', 'error');
                btnElement.disabled = false;
                btnElement.textContent = 'Remove';
            });
    }

    // ── My Lists Tab ─────────────────────────────────────────────────────────
    function loadLists() {
        setGridLoading('id-lists-grid', 'id-lists-loading', true);
        q('id-lists-empty').style.display = 'none';

        idFetch('/InfiniteDrive/User/Catalogs')
            .then(function(res) { return res.json(); })
            .then(function(data) {
                var catalogs = data.Catalogs || [];
                var limit = data.Limit != null ? data.Limit : 5;

                // Admin-reduced-limit warning
                var overLimitEl = q('id-lists-overlimit-warning');
                var addListBtn = q('id-add-list-btn');
                if (overLimitEl) {
                    if (catalogs.length > limit && limit > 0) {
                        overLimitEl.textContent = 'Your server\'s per-user list limit is ' + limit + '. You have ' + catalogs.length + ' lists. You can keep your existing lists, but won\'t be able to add new ones until you remove some.';
                        overLimitEl.style.display = 'block';
                    } else {
                        overLimitEl.style.display = 'none';
                    }
                }
                if (addListBtn) {
                    addListBtn.disabled = catalogs.length >= limit && limit > 0;
                }

                if (catalogs.length === 0) {
                    q('id-lists-empty').style.display = 'block';
                    var limitEl = q('id-lists-limit');
                    if (limitEl) limitEl.textContent = '';
                } else {
                    renderListsGrid(catalogs);
                    var limitEl = q('id-lists-limit');
                    if (limitEl) limitEl.textContent = catalogs.length + ' of ' + limit + ' lists used';
                }

                setGridLoading('id-lists-grid', 'id-lists-loading', false);
            })
            .catch(function(err) {
                console.error('Failed to load lists:', err);
                showToast('Failed to load your lists', 'error');
                setGridLoading('id-lists-grid', 'id-lists-loading', false);
            });
    }

    function renderListsGrid(catalogs) {
        var grid = q('id-lists-grid');
        if (!grid) return;

        var html = catalogs.map(function(cat) {
            var providerIcons = { trakt: '🎬', mdblist: '📋', tmdb: '🎬', anilist: '🎌' };
            var icon = providerIcons[cat.Provider] || '📋';
            var lastSync = cat.LastSyncedAt ? new Date(cat.LastSyncedAt).toLocaleDateString() : 'Never';
            var syncStatus = cat.LastSyncStatus || '';

            return '<div class="id-list-card" data-id="' + esc(cat.Id) + '">' +
                '<div class="id-list-header">' +
                    '<div class="id-list-icon">' + icon + '</div>' +
                    '<div class="id-list-info">' +
                        '<h3>' + esc(cat.DisplayName || cat.Id) + '</h3>' +
                        '<div class="id-list-url">' + esc(cat.Provider || '') + ' &middot; ' + esc((cat.ListUrl || '').substring(0, 50)) + (cat.ListUrl && cat.ListUrl.length > 50 ? '…' : '') + '</div>' +
                    '</div>' +
                '</div>' +
                '<div class="id-list-meta">' +
                    '<span>' + (cat.ItemCount || 0) + ' items</span>' +
                    '<span>Last sync: ' + lastSync + '</span>' +
                    (syncStatus && syncStatus !== 'ok' ? '<span style="color:#dc3545">' + esc(syncStatus) + '</span>' : '') +
                '</div>' +
                '<div class="id-list-actions">' +
                    '<button class="id-list-refresh" data-id="' + esc(cat.Id) + '">Refresh</button>' +
                    '<button class="id-list-remove" data-id="' + esc(cat.Id) + '">Remove</button>' +
                '</div>' +
            '</div>';
        }).join('');

        grid.innerHTML = html;

        // Refresh button handlers
        grid.querySelectorAll('.id-list-refresh').forEach(function(btn) {
            btn.addEventListener('click', function() {
                var catalogId = this.getAttribute('data-id');
                refreshList(catalogId, this);
            });
        });

        // Remove button handlers
        grid.querySelectorAll('.id-list-remove').forEach(function(btn) {
            btn.addEventListener('click', function() {
                var catalogId = this.getAttribute('data-id');
                if (confirm('Are you sure you want to remove this list?')) {
                    removeList(catalogId, this);
                }
            });
        });

        // Refresh all button
        q('id-refresh-all-btn').addEventListener('click', function() {
            refreshAllLists();
        });
    }

    function refreshList(catalogId, btn) {
        btn.disabled = true;
        btn.textContent = 'Refreshing...';

        var url = '/InfiniteDrive/User/Catalogs/Refresh?catalogId=' + encodeURIComponent(catalogId);

        idFetch(url, { method: 'POST' })
            .then(function(res) { return res.json(); })
            .then(function(data) {
                if (data.Ok) {
                    showToast((data.Fetched || 0) + ' items refreshed', 'success');
                } else {
                    showToast(data.Error || 'Refresh failed', 'error');
                }
                loadLists();
            })
            .catch(function(err) {
                console.error('Failed to refresh list:', err);
                showToast('Failed to refresh list', 'error');
                btn.disabled = false;
                btn.textContent = 'Refresh';
            });
    }

    function refreshAllLists() {
        var btn = q('id-refresh-all-btn');
        btn.disabled = true;
        btn.textContent = 'Refreshing all...';

        idFetch('/InfiniteDrive/User/Catalogs/Refresh', { method: 'POST' })
            .then(function(r) { return r.json(); })
            .then(function(data) {
                if (data.Ok) {
                    showToast(data.Lists + ' list(s) refreshed — ' + (data.Fetched || 0) + ' items', 'success');
                } else {
                    showToast(data.Error || 'Refresh failed', 'error');
                }
                loadLists();
            })
            .catch(function(err) {
                console.error('Failed to refresh all lists:', err);
                showToast('Failed to refresh lists', 'error');
            })
            .finally(function() {
                btn.disabled = false;
                btn.textContent = 'Refresh All';
            });
    }

    function removeList(catalogId, btn) {
        var url = '/InfiniteDrive/User/Catalogs/Remove?catalogId=' + encodeURIComponent(catalogId);

        idFetch(url, { method: 'POST' })
            .then(function(res) { return res.json(); })
            .then(function(data) {
                if (data.Ok) {
                    showToast('List removed', 'success');
                    loadLists();
                } else {
                    showToast(data.Error || 'Failed to remove list', 'error');
                }
            })
            .catch(function(err) {
                console.error('Failed to remove list:', err);
                showToast('Failed to remove list', 'error');
            });
    }

    // ── Add List Modal ───────────────────────────────────────────────────────
    function openAddListModal() {
        var modal = q('id-add-list-modal');
        modal.style.display = 'flex';

        // Reset form
        q('id-list-name-input').value = '';
        q('id-list-url-input').value = '';
        q('id-add-list-error').textContent = '';

        // Load enabled providers
        var sel = q('id-list-provider-input');
        sel.innerHTML = '<option value="">Loading...</option>';
        idFetch('/InfiniteDrive/User/Catalogs/Providers')
            .then(function(r) { return r.json(); })
            .then(function(data) {
                var providers = data.EnabledProviders || [];
                var current = data.CurrentCount || 0;
                var limit = data.Limit || 5;
                sel.innerHTML = '';
                if (current >= limit) {
                    sel.innerHTML = '<option value="">List limit reached (' + limit + ')</option>';
                    var errEl = q('id-add-list-error');
                    if (errEl) errEl.textContent = 'Remove an existing list to add a new one.';
                    return;
                }
                if (providers.indexOf('mdblist') !== -1)
                    sel.innerHTML += '<option value="mdblist">MDBList</option>';
                if (providers.indexOf('trakt') !== -1)
                    sel.innerHTML += '<option value="trakt">Trakt</option>';
                if (providers.indexOf('tmdb') !== -1)
                    sel.innerHTML += '<option value="tmdb">TMDB</option>';
                if (providers.indexOf('anilist') !== -1)
                    sel.innerHTML += '<option value="anilist">AniList</option>';
                if (!sel.innerHTML) sel.innerHTML = '<option value="">No providers available</option>';
            })
            .catch(function() { sel.innerHTML = '<option value="">Error loading providers</option>'; });
    }

    function setupAddListModal() {
        // Close button
        q('id-add-list-close').addEventListener('click', function() { closeModal('id-add-list-modal'); });

        // Cancel button
        q('id-add-list-cancel').addEventListener('click', function() { closeModal('id-add-list-modal'); });

        // Backdrop click
        q('id-add-list-modal').addEventListener('click', function(e) {
            if (e.target.classList.contains('id-modal-backdrop')) {
                closeModal('id-add-list-modal');
            }
        });

        // Add button
        q('id-add-list-btn').addEventListener('click', function() { openAddListModal(); });
        q('id-add-list-empty-btn').addEventListener('click', function() { openAddListModal(); });

        // Submit
        q('id-add-list-submit').addEventListener('click', function() {
            var name = q('id-list-name-input').value.trim();
            var url = q('id-list-url-input').value.trim();
            var errorEl = q('id-add-list-error');

            errorEl.textContent = '';

            if (!url) {
                errorEl.textContent = 'Please enter a list URL';
                return;
            }

            var btn = this;
            btn.disabled = true;
            btn.textContent = 'Validating...';

            var apiUrl = '/InfiniteDrive/User/Catalogs/Add?' +
                new URLSearchParams({ listUrl: url, displayName: name }).toString();

            idFetch(apiUrl, { method: 'POST' })
                .then(function(res) { return res.json(); })
                .then(function(data) {
                    if (data.Ok) {
                        showToast('List added — ' + (data.Fetched || 0) + ' items found', 'success');
                        closeModal('id-add-list-modal');
                        loadLists();
                    } else {
                        errorEl.textContent = data.Error || 'Failed to add list.';
                        btn.disabled = false;
                        btn.textContent = 'Validate & Save';
                    }
                })
                .catch(function(err) {
                    console.error('Failed to add list:', err);
                    errorEl.textContent = 'Failed to add list: ' + err.message;
                    btn.disabled = false;
                    btn.textContent = 'Validate & Save';
                });
        });
    }

    // ── Modal helpers ────────────────────────────────────────────────────────
    function closeModal(modalId) {
        var modal = q(modalId);
        if (modal) {
            modal.style.display = 'none';
        }
        if (modalId === 'id-detail-modal') {
            _currentDetailItem = null;
        }
    }

    // ── List limit check ──────────────────────────────────────────────────
    function checkListLimit() {
        idFetch('/InfiniteDrive/User/Catalogs/Providers')
            .then(function(r) { return r.json(); })
            .then(function(data) {
                var limit = data.Limit != null ? data.Limit : 5;
                _userListLimit = limit;

                var listsTab = document.querySelector('.id-tab[data-tab="lists"]');
                if (listsTab) {
                    listsTab.style.display = limit === 0 ? 'none' : '';
                }

                // If currently on lists tab but it's now disabled, redirect
                if (limit === 0 && _currentTab === 'lists') {
                    switchTab('discover');
                }
            })
            .catch(function() {});
    }

    // ── Tab setup ───────────────────────────────────────────────────────────
    function setupTabs() {
        document.querySelectorAll('.id-tab').forEach(function(tab) {
            tab.addEventListener('click', function() {
                var tabName = this.getAttribute('data-tab');
                switchTab(tabName);
            });
        });
    }

    // ── Initialize ───────────────────────────────────────────────────────────
    return function (view, params) {
        view.addEventListener('viewshow', function() {
            // Get current user
            if (typeof ApiClient !== 'undefined') {
                _currentUserId = ApiClient.getCurrentUserId();
                var welcomeEl = q('id-welcome');
                if (welcomeEl) {
                    var user = ApiClient.getCurrentUser();
                    if (user && user.Name) {
                        welcomeEl.textContent = 'Welcome back, ' + user.Name;
                    }
                }
            }

            // Setup UI
            setupTabs();
            setupSearch();
            setupDetailModal();
            setupAddListModal();
            checkListLimit();

            // Load initial content
            switchTab('discover');
        });
    };
});
