// Rewrites every <time class="local-time" datetime="..."> element in the page to the operator's
// browser-local time. The server emits the canonical UTC value in `datetime`, plus a UTC text
// fallback so the page is still legible with JavaScript disabled.
(function () {
    "use strict";

    function format(date) {
        // Keep the same shape as the server's UTC fallback (yyyy-MM-dd HH:mm:ss) but in local
        // time, with a short UTC-offset suffix so the timezone is unambiguous in screenshots.
        var pad = function (n) { return n < 10 ? "0" + n : "" + n; };
        var iso = date.getFullYear() + "-" + pad(date.getMonth() + 1) + "-" + pad(date.getDate())
            + " " + pad(date.getHours()) + ":" + pad(date.getMinutes()) + ":" + pad(date.getSeconds());
        var offsetMinutes = -date.getTimezoneOffset();
        var sign = offsetMinutes >= 0 ? "+" : "-";
        var abs = Math.abs(offsetMinutes);
        var offset = sign + pad(Math.floor(abs / 60)) + ":" + pad(abs % 60);
        return iso + " " + offset;
    }

    function rewrite(el) {
        var iso = el.getAttribute("datetime");
        if (!iso) return;
        var d = new Date(iso);
        if (isNaN(d.getTime())) return;
        el.textContent = format(d);
        el.setAttribute("title", el.getAttribute("datetime"));
    }

    function rewriteAll() {
        var nodes = document.querySelectorAll("time.local-time[datetime]");
        for (var i = 0; i < nodes.length; i++) rewrite(nodes[i]);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", rewriteAll);
    } else {
        rewriteAll();
    }
})();
