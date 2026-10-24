/* Radio Meta DLL loader pointer; implementation is embedded in RadioMeta.dll. */
;(function () {
    var p = String(window.location && window.location.pathname || "/");
    var i = p.toLowerCase().lastIndexOf("/web/");
    window.require([(i >= 0 ? p.slice(0, i) : p.replace(/\/web$/i, "")) +
        "/web/ConfigurationPage?name=RadioMetaDisplayLoader.js"]);
}());
