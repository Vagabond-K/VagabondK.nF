// VagabondK.nF.WebUi core (injected at the <!--@core--> marker, after <!--@manifest-->).
// Reads the MANIFEST const and exposes the WebUi helpers: feature flags, form factory,
// value formatting and a tiny keyed virtual DOM. No dependencies.
var WebUi = (function () {
var M = (typeof MANIFEST === 'undefined') ? {} : MANIFEST;
var F = M.form || [], KEYS = [], LABELS = {}, BY = {};
for (var i = 0; i < F.length; i++) { KEYS.push(F[i].name); LABELS[F[i].name] = F[i].label; BY[F[i].name] = F[i]; }
function feat(name, dflt) { var x = M.features; return (x && name in x) ? x[name] : dflt; }
// ---- form factory ----
function values(form) { var e = form.elements, v = {}; for (var i = 0; i < KEYS.length; i++) v[KEYS[i]] = String(e[KEYS[i]].value).trim(); return v; }
function fill(form, s) { var e = form.elements, base = {}; for (var i = 0; i < KEYS.length; i++) { var k = KEYS[i]; base[k] = String(s[k]); e[k].value = base[k]; } return base; }
function changed(base, v) { var c = []; if (!base) return c; for (var i = 0; i < KEYS.length; i++) { var k = KEYS[i]; if (v[k] !== base[k]) c.push(k); } return c; }
function showValue(form, k, v) {
var f = BY[k], el = form.elements[k], o, i;
if (f) { if (f.fmt === 'ms') return v + ' ms'; if (f.fmt === 'pin') return 'GPIO ' + v; if (f.fmt === 'secret') return v ? '\u2022\u2022\u2022\u2022' : v; if (f.fmt === 'bool') return v === '1' ? '\uc608' : '\uc544\ub2c8\uc694'; }
if (!f || !f.fmt) {
if (el && el.querySelector) { o = el.querySelector('option[value="' + v + '"]'); if (o) return o.text; }
else if (el && !el.tagName) { for (i = 0; i < el.length; i++) if (el[i].value === v) { o = el[i].parentNode.querySelector ? el[i].parentNode.querySelector('span') : null; if (o) return o.textContent; } }
}
return v;
}
// ---- micro vDOM ----
// A component is a function (props) -> vnode | array | string | null. h() calls it when the
// tag is a function, so h(Comp, props) renders inline. component()/slot() add a name registry
// so the shell can render a profile-provided component at a fixed position (WebUi.slot('name', props)).
var REG = {};
function component(name, fn) { REG[name] = fn; return fn; }
function norm(c) { return (typeof c === 'string' || typeof c === 'number') ? { t: '#', v: String(c) } : c; }
function slot(name, props) { var f = REG[name]; return f ? f(props || {}) : null; }
function h(tag, props) {
if (typeof tag === 'function') return tag(props || {});
var n = { t: tag, p: props || {}, k: [] }; for (var i = 2; i < arguments.length; i++) add(n, arguments[i]); return n;
}
function add(n, c) {
if (c == null || c === false) return;
if (Object.prototype.toString.call(c) === '[object Array]') { for (var i = 0; i < c.length; i++) add(n, c[i]); return; }
n.k.push(norm(c));
}
function mk(n) {
if (n.t === '#') return document.createTextNode(n.v);
var e = document.createElement(n.t);
prop(e, n);
for (var i = 0; i < n.k.length; i++) e.appendChild(mk(n.k[i]));
return e;
}
function prop(e, n) {
var p = n.p;
for (var k in p) {
if (k === 'key') continue;
var v = p[k];
if (k === 'className') { if (e.className !== v) e.className = v || ''; }
else if (k === 'text') { if (e.textContent !== v) e.textContent = v; }
else if (k === 'style') { for (var s in v) e.style[s] = v[s]; }
else if (k.slice(0, 2) === 'on') e[k.toLowerCase()] = v;
else if (v === null || v === false) e.removeAttribute(k);
else e.setAttribute(k, v === true ? '' : v);
}
}
function same(node, n) { return n.t === '#' ? node.nodeType === 3 : node.nodeType === 1 && node.tagName === n.t.toUpperCase(); }
function patch(node, n) {
if (!same(node, n)) { var r = mk(n); node.parentNode.replaceChild(r, node); return r; }
if (n.t === '#') { if (node.data !== n.v) node.data = n.v; return node; }
prop(node, n);
// A text prop owns the element's whole content (textContent replaces all children),
// so skip the child diff; otherwise the loop below would delete the text node just set.
if (n.p.text !== undefined) return node;
var kids = n.k, i;
for (i = 0; i < kids.length; i++) { var c = node.childNodes[i]; if (c) patch(c, kids[i]); else node.appendChild(mk(kids[i])); }
while (node.childNodes.length > kids.length) node.removeChild(node.lastChild);
return node;
}
// render(parent, vnodes): keyed diff of a child list. Nodes with a key prop match by key
// (so focus/state survives reorder); the rest match positionally. Extra children are removed.
function render(parent, list) {
var old = [], i, j;
for (i = 0; i < parent.childNodes.length; i++) old.push(parent.childNodes[i]);
var used = new Array(old.length);
for (i = 0; i < list.length; i++) {
var n = list[i], key = n.p.key, node = null;
if (key !== undefined) { for (j = 0; j < old.length; j++) if (!used[j] && old[j].__k === key) { node = old[j]; break; } }
else for (j = 0; j < old.length; j++) if (!used[j] && same(old[j], n)) { node = old[j]; break; }
if (node) { used[j] = 1; node = patch(node, n); } else node = mk(n);
node.__k = key;
var want = parent.childNodes[i];
if (node !== want) parent.insertBefore(node, want || null);
}
while (parent.childNodes.length > list.length) parent.removeChild(parent.lastChild);
}
return { M: M, KEYS: KEYS, LABELS: LABELS, feat: feat, values: values, fill: fill, changed: changed, showValue: showValue, h: h, render: render, component: component, slot: slot };
})();
// ---- WUI: instance components (Web Components) ----
// A component whose html contains <!--@instance--> is emitted as a <template id="wui-t-NAME"> plus
// <wui-name> tags at each @use position. def() registers the custom element: every tag gets its own
// shadow root with the template cloned in, so ids are scoped per instance and init runs once per
// instance as init.call(host, root, scopedDocument, $). The page's merged <style> is adopted as a
// base sheet so global rules still apply inside the shadow; the component's own css follows it.
var WUI = (function () {
var roots = [], base = null, baseCss = '';
function makeSheet(text) { try { var s = new CSSStyleSheet(); s.replaceSync(text); return s; } catch (e) { return null; } }
function scope(root) {
return {
getElementById: function (id) { return root.getElementById(id); },
querySelector: function (s) { return root.querySelector(s); },
querySelectorAll: function (s) { return root.querySelectorAll(s); },
createElement: function (t) { return document.createElement(t); },
addEventListener: function () { return document.addEventListener.apply(document, arguments); }
};
}
function def(name, init, css) {
var own = css == null ? null : { tried: false, sheet: null };
customElements.define('wui-' + name.toLowerCase(), class extends HTMLElement {
connectedCallback() {
if (this.__wui) return;
this.__wui = 1;
var sr = this.attachShadow({ mode: 'open' });
var t = document.getElementById('wui-t-' + name);
if (t) sr.appendChild(t.content.cloneNode(true));
if (base === null) { var s = document.querySelector('style'); baseCss = s ? s.textContent : ''; base = makeSheet(baseCss) || false; }
if (own && !own.tried) { own.tried = true; own.sheet = makeSheet(css); }
var ok = base !== null && base !== false && (!own || own.sheet);
if (ok) { try { sr.adoptedStyleSheets = own ? [base, own.sheet] : [base]; } catch (e) { ok = false; } }
if (!ok) { var st = document.createElement('style'); st.textContent = baseCss + (css ? '\n' + css : ''); sr.insertBefore(st, sr.firstChild); }
roots.push(sr);
this.shadow = sr;
if (init) init.call(this, sr, scope(sr), function (id) { return sr.getElementById(id); });
}
});
}
return { def: def, roots: roots };
})();
