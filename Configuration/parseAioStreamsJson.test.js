#!/usr/bin/env node
// TEST-5 — Unit tests for _extractDebridKeys() pure function
// Run with: node parseAioStreamsJson.test.js
// No external dependencies required.

// ── Load the function under test ──────────────────────────────────────────────
// configurationpage.js is an AMD module; we extract _extractDebridKeys by
// temporarily patching the AMD `define` to call the factory synchronously and
// capture the exported object via the module.exports guard at the end.

// Simplest approach: inline the function definition (it's a pure, self-contained
// 30-line function) so the test has no module system dependency.

function _extractDebridKeys(text) {
    var providerMap = {
        'realdebrid': ['RealDebridApiKey', 'Real-Debrid', 'cfg-rd-api-key'],
        'torbox':     ['TorBoxApiKey',      'TorBox',      'cfg-torbox-api-key'],
        'premiumize': ['PremiumizeApiKey',   'Premiumize',  'cfg-premiumize-api-key'],
        'alldebrid':  ['AllDebridApiKey',    'AllDebrid',   'cfg-alldebrid-api-key'],
    };
    var found = [], notFound = [];
    try {
        var data = JSON.parse(text);
        var services = data.services || data.providers || data.debridOptions || [];
        if (!Array.isArray(services)) services = [];
        services.forEach(function(svc) {
            var id = String(svc.id || svc.name || svc.type || '').toLowerCase();
            if (!providerMap[id]) return;
            var creds = svc.credentials || svc.config || svc.settings || {};
            var key = creds.apiKey || creds.token || creds.key || creds.apikey || null;
            var info = providerMap[id];
            if (key) {
                found.push({ label: info[1], inputId: info[2], cfgKey: info[0], key: key });
            } else {
                notFound.push(info[1]);
            }
            delete providerMap[id];
        });
    } catch(e) {
        return { found: [], notFound: [], error: String(e.message || e) };
    }
    Object.keys(providerMap).forEach(function(k) { notFound.push(providerMap[k][1]); });
    return { found: found, notFound: notFound, error: null };
}

// ── Test harness ──────────────────────────────────────────────────────────────

var passed = 0, failed = 0;

function assert(condition, message) {
    if (condition) {
        console.log('  ✅ ' + message);
        passed++;
    } else {
        console.error('  ❌ FAIL: ' + message);
        failed++;
    }
}

function run(name, fn) {
    console.log('\n' + name);
    fn();
}

// ── Tests ─────────────────────────────────────────────────────────────────────

run('Credential field name variants', function() {
    var json = JSON.stringify({ services: [{ id: 'realdebrid', credentials: { apiKey: 'rd_apiKey_value' } }] });
    var r = _extractDebridKeys(json);
    assert(r.error === null, 'No error');
    assert(r.found.length === 1, 'One key found');
    assert(r.found[0].key === 'rd_apiKey_value', 'apiKey field extracted');

    json = JSON.stringify({ services: [{ id: 'torbox', credentials: { token: 'tbx_token_value' } }] });
    r = _extractDebridKeys(json);
    assert(r.found.length === 1 && r.found[0].key === 'tbx_token_value', 'token field extracted');

    json = JSON.stringify({ services: [{ id: 'premiumize', credentials: { key: 'pm_key_value' } }] });
    r = _extractDebridKeys(json);
    assert(r.found.length === 1 && r.found[0].key === 'pm_key_value', 'key field extracted');

    json = JSON.stringify({ services: [{ id: 'alldebrid', credentials: { apikey: 'ad_apikey_value' } }] });
    r = _extractDebridKeys(json);
    assert(r.found.length === 1 && r.found[0].key === 'ad_apikey_value', 'apikey (lowercase) field extracted');
});

run('Service identifier field variants', function() {
    var json = JSON.stringify({ services: [{ id: 'realdebrid', credentials: { apiKey: 'k1' } }] });
    assert(_extractDebridKeys(json).found.length === 1, 'id field matches');

    json = JSON.stringify({ services: [{ name: 'torbox', credentials: { apiKey: 'k2' } }] });
    assert(_extractDebridKeys(json).found.length === 1, 'name field matches');

    json = JSON.stringify({ services: [{ type: 'premiumize', credentials: { apiKey: 'k3' } }] });
    assert(_extractDebridKeys(json).found.length === 1, 'type field matches');
});

run('Config wrapper variants', function() {
    var key = { id: 'realdebrid', credentials: { apiKey: 'k' } };

    assert(_extractDebridKeys(JSON.stringify({ services:      [key] })).found.length === 1, 'services[] wrapper');
    assert(_extractDebridKeys(JSON.stringify({ providers:     [key] })).found.length === 1, 'providers[] wrapper');
    assert(_extractDebridKeys(JSON.stringify({ debridOptions: [key] })).found.length === 1, 'debridOptions[] wrapper');
});

run('All four providers found', function() {
    var json = JSON.stringify({ services: [
        { id: 'realdebrid', credentials: { apiKey: 'rd'  } },
        { id: 'torbox',     credentials: { apiKey: 'tbx' } },
        { id: 'premiumize', credentials: { apiKey: 'pm'  } },
        { id: 'alldebrid',  credentials: { apiKey: 'ad'  } },
    ]});
    var r = _extractDebridKeys(json);
    assert(r.found.length === 4,    'Four keys found');
    assert(r.notFound.length === 0, 'Nothing missing');
    assert(r.error === null,        'No error');
});

run('Missing services', function() {
    var r = _extractDebridKeys(JSON.stringify({ services: [] }));
    assert(r.found.length === 0,    'No keys when services empty');
    assert(r.notFound.length === 4, 'All four providers listed as not found');

    r = _extractDebridKeys(JSON.stringify({}));
    assert(r.found.length === 0,    'No keys when no services key');

    r = _extractDebridKeys(JSON.stringify({ services: [{ id: 'unknown_service', credentials: { apiKey: 'x' } }] }));
    assert(r.found.length === 0,    'Unknown provider ignored');
    assert(r.notFound.length === 4, 'All four listed as not found');
});

run('Service with no credential key', function() {
    var json = JSON.stringify({ services: [{ id: 'realdebrid', credentials: {} }] });
    var r = _extractDebridKeys(json);
    assert(r.found.length === 0,      'No key extracted when credentials empty');
    assert(r.notFound.includes('Real-Debrid'), 'Provider in notFound');
});

run('Malformed JSON', function() {
    var r = _extractDebridKeys('this is not json');
    assert(r.error !== null,         'Error returned for invalid JSON');
    assert(r.found.length === 0,     'No keys on error');

    r = _extractDebridKeys('');
    assert(r.error !== null,         'Error returned for empty string');

    r = _extractDebridKeys('[1,2,3]');
    assert(r.error === null,          'Array JSON parses without error (services defaults to [])');
    assert(r.found.length === 0,      'No keys from top-level array');
});

run('cfgKey mapping is correct', function() {
    var json = JSON.stringify({ services: [{ id: 'torbox', credentials: { apiKey: 'tbx_secret' } }] });
    var r = _extractDebridKeys(json);
    assert(r.found[0].cfgKey === 'TorBoxApiKey', 'cfgKey is TorBoxApiKey for torbox');
    assert(r.found[0].label  === 'TorBox',       'label is TorBox');
});

// ── parseManifestUrl guard — https:// pre-fill defence ───────────────────────

// Inline the sentinel guard logic from configurationpage.js parseManifestUrl():
//   if (!url || url === 'https://' || url === 'http://') return;
function parseManifestUrlGuard(url) {
    if (!url || url === 'https://' || url === 'http://') return false;
    return true;
}

run('Manifest URL guard — no-op on bare scheme sentinels', function() {
    assert(parseManifestUrlGuard('https://') === false,  'https:// sentinel is skipped');
    assert(parseManifestUrlGuard('http://')  === false,  'http:// sentinel is skipped');
    assert(parseManifestUrlGuard('')         === false,  'empty string is skipped');
    assert(parseManifestUrlGuard(null)       === false,  'null is skipped');
});

run('Manifest URL guard — real URLs proceed', function() {
    assert(parseManifestUrlGuard('https://my.host/stremio/uuid/token/manifest.json') === true,
           'full HTTPS manifest URL proceeds');
    assert(parseManifestUrlGuard('http://localhost:8080/stremio/uuid/token/manifest.json') === true,
           'localhost HTTP manifest URL proceeds');
});

// ── Summary ───────────────────────────────────────────────────────────────────

console.log('\n─────────────────────────────────────────────');
console.log('Results: ' + passed + ' passed, ' + failed + ' failed');
if (failed > 0) process.exit(1);
