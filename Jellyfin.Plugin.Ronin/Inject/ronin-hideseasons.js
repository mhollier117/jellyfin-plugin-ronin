// Merged look: hides display-empty season cards/rows. Which ids qualify is
// decided server-side (GET /Ronin/HiddenSeasons): in-scope libraries only,
// never Season 1 or Specials, placeholder episodes count as content.
(function () {
    'use strict';
    if (!(window.RoninVariables && window.RoninVariables.RONIN_HIDE_EMPTY_SEASONS)) return;

    var BATCH = 100;
    var known = {};   // itemId -> true (hide) | false (leave)
    var pending = {};
    var timer = null;

    function normalize(id) {
        return id ? id.replace(/-/g, '').toLowerCase() : null;
    }

    function apply() {
        document.querySelectorAll('.card[data-id], .listItem[data-id]').forEach(function (el) {
            var id = normalize(el.getAttribute('data-id'));
            if (id && known[id] === true) el.classList.add('ronin-hidden-season');
        });
    }

    function flush() {
        timer = null;
        var ids = Object.keys(pending).slice(0, BATCH);
        if (!ids.length) return;
        ids.forEach(function (id) { delete pending[id]; });
        var client = window.ApiClient;
        if (!client) return;
        client.ajax({
            type: 'GET',
            url: client.getUrl('Ronin/HiddenSeasons', { ids: ids.join(',') }),
            dataType: 'json'
        }).then(function (hidden) {
            var hide = {};
            (hidden || []).forEach(function (id) { hide[normalize(id)] = true; });
            ids.forEach(function (id) { known[id] = !!hide[id]; });
            apply();
            if (Object.keys(pending).length) schedule();
        }).catch(function () {
            // leave unknown; a later render may retry
        });
    }

    function schedule() {
        if (timer) return;
        timer = setTimeout(flush, 150);
    }

    function scan() {
        var wanted = false;
        document.querySelectorAll('.card[data-id], .listItem[data-id]').forEach(function (el) {
            var id = normalize(el.getAttribute('data-id'));
            if (!id) return;
            if (Object.prototype.hasOwnProperty.call(known, id)) {
                if (known[id] === true) el.classList.add('ronin-hidden-season');
                return;
            }
            if (!pending[id]) { pending[id] = true; wanted = true; }
        });
        if (wanted) schedule();
    }

    new MutationObserver(scan).observe(document.body, { childList: true, subtree: true });
    scan();
})();
