define([], function () {
    'use strict';

    var cache = new Map();
    var searchTimer = null;

    // ── Helpers ────────────────────────────────────────────────────────

    function esc(s) { var d = document.createElement('div'); d.textContent = s || ''; return d.innerHTML; }
    function escAttr(s) { return (s || '').replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/'/g, '&#39;').replace(/</g, '&lt;'); }

    function $(id) { return document.getElementById(id); }

    function show(el) { el.style.display = ''; }
    function hide(el) { el.style.display = 'none'; }

    function toast(msg, type) {
        var t = $('id-toast');
        t.textContent = msg;
        t.className = 'id-toast' + (type ? ' id-toast-' + type : '');
        show(t);
        setTimeout(function () { hide(t); }, 3000);
    }

    // ── Card HTML ─────────────────────────────────────────────────────

    function cardHtml(item) {
        cache.set(item.AioId, item);
        var poster = item.PosterUrl;
        var year = item.Year || '';
        var rating = item.ImdbRating ? item.ImdbRating.toFixed(1) : '';
        var cert = item.Certification || '';
        var inLib = item.InLibrary;

        var h = '<div class="id-card" data-id="' + escAttr(item.AioId) + '">';
        h += '<div class="id-card-poster">';
        if (poster) {
            h += '<img src="' + escAttr(poster) + '" alt="" loading="lazy" onerror="this.parentNode.innerHTML=\'<div class=id-card-no-poster>' + esc(item.Title || '?') + '</div>\'" />';
        } else {
            h += '<div class="id-card-no-poster">' + esc(item.Title || '?') + '</div>';
        }
        h += '</div><div class="id-card-info"><h3>' + esc(item.Title) + '</h3><div class="id-card-meta">';
        if (year) h += '<span class="id-card-year">' + esc(year) + '</span>';
        if (rating) h += '<span class="id-card-rating">&#9733; ' + esc(rating) + '</span>';
        if (cert) h += '<span class="id-card-cert">' + esc(cert) + '</span>';
        if (inLib) h += '<span class="id-card-in-lib">In Library</span>';
        h += '</div></div></div>';
        return h;
    }

    // ── Rails ─────────────────────────────────────────────────────────

    function loadRails() {
        ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl('InfiniteDrive/Discover/Rails'),
            dataType: 'json'
        }).then(function (data) {
            var container = $('id-rails');
            if (!data || !data.Rails || !data.Rails.length) {
                container.innerHTML = '<div class="id-empty"><p>No popular titles available yet.</p></div>';
                return;
            }
            var html = '';
            data.Rails.forEach(function (rail) {
                if (!rail.Items || !rail.Items.length) return;
                html += '<div class="id-rail-section">';
                html += '<h3 class="id-section-title">' + esc(rail.Title) + '</h3>';
                html += '<div class="id-rail-scroll">';
                rail.Items.forEach(function (item) { html += cardHtml(item); });
                html += '</div></div>';
            });
            container.innerHTML = html;
            hide($('id-loading'));
        }).catch(function () {
            $('id-rails').innerHTML = '<div class="id-empty"><p>Could not load popular titles.</p></div>';
            hide($('id-loading'));
        });
    }

    // ── Search ────────────────────────────────────────────────────────

    function doSearch(query) {
        var moviesRail = $('id-rail-movies'), seriesRail = $('id-rail-series'), animeRail = $('id-rail-anime');
        var moviesScroll = $('id-rail-movies-scroll'), seriesScroll = $('id-rail-series-scroll'), animeScroll = $('id-rail-anime-scroll');
        var empty = $('id-results-empty');
        var results = $('id-results');
        var title = $('id-results-title');

        if (!query) { hide(results); return; }

        title.textContent = 'Results for "' + query + '"';
        show(results);
        hide(moviesRail); hide(seriesRail); hide(animeRail); hide(empty);
        moviesScroll.innerHTML = '<div class="id-loading"><div class="spinner"></div></div>';
        seriesScroll.innerHTML = ''; animeScroll.innerHTML = '';

        ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl('InfiniteDrive/Discover/Search', { q: query }),
            dataType: 'json'
        }).then(function (data) {
            var items = data && data.Items ? data.Items : [];
            if (!items.length) {
                hide(moviesRail); hide(seriesRail); hide(animeRail); show(empty);
                return;
            }
            var movies = [], series = [], anime = [];
            items.forEach(function (item) {
                if (item.MediaType === 'anime') anime.push(item);
                else if (item.MediaType === 'series') series.push(item);
                else movies.push(item);
            });
            if (movies.length) {
                var mh = ''; movies.forEach(function (i) { mh += cardHtml(i); });
                moviesScroll.innerHTML = mh; show(moviesRail);
            } else { hide(moviesRail); }
            if (series.length) {
                var sh = ''; series.forEach(function (i) { sh += cardHtml(i); });
                seriesScroll.innerHTML = sh; show(seriesRail);
            } else { hide(seriesRail); }
            if (anime.length) {
                var ah = ''; anime.forEach(function (i) { ah += cardHtml(i); });
                animeScroll.innerHTML = ah; show(animeRail);
            } else { hide(animeRail); }
        }).catch(function () {
            hide(moviesRail); hide(seriesRail); hide(animeRail); show(empty);
            empty.querySelector('p').textContent = 'Search failed — check your connection.';
        });
    }

    // ── Detail Modal ──────────────────────────────────────────────────

    function openDetail(aioId) {
        var item = cache.get(aioId);
        if (!item) return;

        renderModal(item);

        // If no overview, fetch full meta from backend
        if (!item.Overview) {
            ApiClient.ajax({
                type: 'GET',
                url: ApiClient.getUrl('InfiniteDrive/Discover/Detail', { aioId: aioId }),
                dataType: 'json'
            }).then(function (data) {
                if (data && data.Item) {
                    // Update cache and re-render overview if we got one
                    var d = data.Item;
                    if (d.Overview) {
                        item.Overview = d.Overview;
                        if (d.Genres) item.Genres = d.Genres;
                        if (d.BackdropUrl) item.BackdropUrl = d.BackdropUrl;
                        if (d.Certification) item.Certification = d.Certification;
                        var el = document.querySelector('.id-detail-overview');
                        if (el) el.textContent = d.Overview;
                        var gEl = document.querySelector('.id-detail-genres');
                        if (gEl && d.Genres) gEl.textContent = d.Genres;
                    }
                }
            });
        }
    }

    function renderModal(item) {
        var aioId = item.AioId;
        var backdrop = item.BackdropUrl || '';
        var poster = item.PosterUrl || '';
        var year = item.Year || '';
        var rating = item.ImdbRating ? item.ImdbRating.toFixed(1) : '';
        var cert = item.Certification || '';
        var genres = item.Genres || '';
        var overview = item.Overview || 'Loading overview...';
        var inLib = item.InLibrary;
        var mediaType = item.MediaType || 'movie';

        var h = '';
        // Backdrop
        if (backdrop) {
            h += '<img class="id-modal-backdrop-img" src="' + escAttr(backdrop) + '" alt="" />';
        } else {
            h += '<div class="id-modal-no-backdrop"></div>';
        }
        h += '<button class="id-modal-close" id="id-modal-x">&times;</button>';

        // Body
        h += '<div class="id-modal-body">';
        if (poster) {
            h += '<div class="id-detail-poster"><img src="' + escAttr(poster) + '" alt="" /></div>';
        }
        h += '<div class="id-detail-info">';
        h += '<h2>' + esc(item.Title) + '</h2>';

        h += '<div class="id-detail-meta-row">';
        if (year) h += '<span>' + esc(year) + '</span>';
        if (rating) h += '<span class="id-detail-rating">&#9733; ' + esc(rating) + '</span>';
        if (cert) h += '<span class="id-detail-cert">' + esc(cert) + '</span>';
        var typeLabel = mediaType === 'anime' ? 'Anime' : mediaType === 'series' ? 'TV Series' : 'Movie';
        h += '<span>' + esc(typeLabel) + '</span>';
        h += '</div>';

        if (genres) h += '<div class="id-detail-genres">' + esc(genres) + '</div>';
        h += '<p class="id-detail-overview">' + esc(overview) + '</p>';
        h += '</div></div>';

        // Actions
        h += '<div class="id-modal-actions">';
        if (inLib) {
            h += '<button class="id-btn-secondary" disabled>In Library</button>';
        } else {
            h += '<button class="id-btn-primary" data-add-id="' + escAttr(aioId) + '">Add to Library</button>';
        }
        h += '<button class="id-btn-secondary" id="id-modal-close-btn">Close</button>';
        h += '</div>';

        $('id-modal-box').innerHTML = h;
        show($('id-modal'));
    }

    function closeModal() { hide($('id-modal')); }

    function addToLibrary(aioId) {
        var item = cache.get(aioId);
        if (!item) return;

        var btn = document.querySelector('[data-add-id="' + aioId + '"]');
        if (btn) { btn.disabled = true; btn.textContent = 'Adding...'; }

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('InfiniteDrive/Discover/AddToLibrary', {
                aioId: aioId,
                type: item.MediaType || 'movie',
                title: item.Title || '',
                year: item.Year || ''
            }),
            dataType: 'json'
        }).then(function (data) {
            if (data && data.Ok) {
                toast('Added "' + (item.Title || '') + '" to library', 'success');
                item.InLibrary = true;
                // Update any visible cards
                document.querySelectorAll('.id-card[data-id="' + aioId + '"]').forEach(function (card) {
                    var meta = card.querySelector('.id-card-meta');
                    if (meta && !meta.querySelector('.id-card-in-lib')) {
                        meta.innerHTML += '<span class="id-card-in-lib">In Library</span>';
                    }
                });
                // Update modal button
                if (btn) { btn.textContent = 'In Library'; btn.className = 'id-btn-secondary'; btn.disabled = true; }
            } else {
                toast(data && data.Error ? data.Error : 'Failed to add', 'error');
                if (btn) { btn.disabled = false; btn.textContent = 'Add to Library'; }
            }
        }).catch(function () {
            toast('Failed to add to library', 'error');
            if (btn) { btn.disabled = false; btn.textContent = 'Add to Library'; }
        });
    }

    // ── Event Binding ─────────────────────────────────────────────────

    function bindEvents(page) {
        var input = $('id-search-input');

        input.addEventListener('input', function () {
            clearTimeout(searchTimer);
            var q = input.value.trim();
            searchTimer = setTimeout(function () { doSearch(q); }, 300);
        });

        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                clearTimeout(searchTimer);
                doSearch(input.value.trim());
            }
        });

        // Card clicks (delegated)
        page.addEventListener('click', function (e) {
            var card = e.target.closest('.id-card');
            if (card) {
                openDetail(card.getAttribute('data-id'));
                return;
            }
        });

        // Modal events (delegated)
        document.addEventListener('click', function (e) {
            // Close button / backdrop
            if (e.target.id === 'id-modal-x' || e.target.id === 'id-modal-close-btn' || e.target.id === 'id-modal-bg') {
                closeModal();
                return;
            }
            // Add to library
            var addBtn = e.target.closest('[data-add-id]');
            if (addBtn) {
                addToLibrary(addBtn.getAttribute('data-add-id'));
                return;
            }
        });

        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') closeModal();
        });
    }

    // ── Init ──────────────────────────────────────────────────────────

    return function (view) {
        bindEvents(view);
        loadRails();
    };
});
