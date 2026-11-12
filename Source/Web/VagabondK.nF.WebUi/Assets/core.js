// VagabondK.nF.WebUi core (injected at the <!--@core--> marker, after <!--@manifest-->).
// Reads the MANIFEST const and exposes the WebUi helpers: feature flags, form factory,
// value formatting and a tiny keyed virtual DOM. No dependencies.
var WebUi = (function () {
var M = (typeof MANIFEST === 'undefined') ? {} : MANIFEST;
var F = M.form || [], KEYS = [], LABELS = {}, BY = {};
for (var i = 0; i < F.length; i++) { KEYS.push(F[i].name); LABELS[F[i].name] = F[i].label; BY[F[i].name] = F[i]; }
// 서버가 api/status의 capabilities로 자기 서술을 보내면 caps()로 받아 feat()의 1순위 출처가 된다.
// manifest.features는 마이그레이션 기간의 폴백이다 (Phase 1.5 설계).
var CAPS = null;
function caps(o) { if (o) CAPS = o; return CAPS; }
function feat(name, dflt) { var x = CAPS || M.features; return (x && name in x) ? x[name] : dflt; }
// ---- form factory ----
function values(form) { var e = form.elements, v = {}; for (var i = 0; i < KEYS.length; i++) v[KEYS[i]] = String(e[KEYS[i]].value).trim(); return v; }
function fill(form, s) { var e = form.elements, base = {}; for (var i = 0; i < KEYS.length; i++) { var k = KEYS[i]; base[k] = String(s[k]); e[k].value = base[k]; } return base; }
function changed(base, v) { var c = []; if (!base) return c; for (var i = 0; i < KEYS.length; i++) { var k = KEYS[i]; if (v[k] !== base[k]) c.push(k); } return c; }
function showValue(form, k, v) {
var f = BY[k], el = form.elements[k], o, i;
if (f) { if (f.fmt === 'ms') return v + ' ms'; if (f.fmt === 'pin') return 'GPIO ' + v; if (f.fmt === 'secret') return v ? '\u2022\u2022\u2022\u2022' : v; if (f.fmt === 'bool' || f.fmt === 'switch') return v === '1' ? '\uc608' : '\uc544\ub2c8\uc694'; }
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
// ---- transport (Phase 1.5): api/csrf/post/toast ----
// 셸마다 중복되던 fetch 래퍼의 단일 구현. 401이면 err.unauth와 함께 wui:unauth를 발행한다:
// 로그인 UI는 페이지(또는 login 컴포넌트)가 이벤트를 구독해 반응한다.
var csrfH = '';
async function api(path, opt, ms) {
var ac = new AbortController(), timer = setTimeout(function () { ac.abort(); }, ms || 8000);
try {
var r = await fetch(path, Object.assign({cache: 'no-store', signal: ac.signal}, opt));
var json = (r.headers.get('Content-Type') || '').indexOf('json') >= 0;
var body = json ? await r.json().catch(function () { return {}; }) : await r.text();
if (!r.ok) {
var err = new Error((json && body.error) || 'HTTP ' + r.status);
if (r.status === 401) { err.unauth = true; fire('wui:unauth', {}); }
throw err;
}
return body;
} catch (e) {
if (e.name === 'AbortError') throw new Error('장치 응답 시간이 초과되었습니다.');
if (e instanceof TypeError) throw new Error('장치에 연결할 수 없습니다.');
throw e;
} finally { clearTimeout(timer); }
}
function csrf(h) {
if (h) csrfH = h;
if (csrfH) return Promise.resolve(csrfH);
return api('/api/auth/status', null, 4000).then(function (a) { csrfH = a.csrfHeader || ''; return csrfH; }, function () { return csrfH; });
}
function post(path, body, type, ms) {
return csrf().then(function (h) {
var o = {method: 'POST', headers: {}, body: body || ''};
if (h) o.headers[h] = '1';
o.headers['Content-Type'] = type || 'application/x-www-form-urlencoded';
return api(path, o, ms);
});
}
function toast(text, kind) {
var box = document.getElementById('toasts');
if (!box) return;
var t = document.createElement('div');
t.className = 'toast' + (kind ? ' ' + kind : '');
t.textContent = text;
box.appendChild(t);
var life = kind === 'bad' ? 7000 : 4000;
setTimeout(function () { t.classList.add('out'); }, life - 500);
setTimeout(function () { t.remove(); }, life);
}
// ---- events (Phase 1.5): wui:status / wui:tick / wui:unauth ----
// 루트 페이지의 폴러가 status()/tick()을 호출하고, 컴포넌트는 on()으로 구독한다.
// ST/SEEN은 최신 상태와 수신 시각: age는 클라이언트에서 외삽한다.
var ST = null, SEEN = 0;
function fire(name, detail) { document.dispatchEvent(new CustomEvent(name, {detail: detail})); }
function on(name, fn) { document.addEventListener(name, function (e) { fn(e.detail); }); }
function status(s) { ST = s; SEEN = Date.now(); if (s && s.capabilities) CAPS = s.capabilities; fire('wui:status', s); return s; }
function tick() {
if (!ST) return;
var age = (Date.now() - SEEN) / 1000;
fire('wui:tick', {uptime: (ST.uptime || 0) + age, age: age, status: ST});
}
// ---- bindForm (Phase 1.5): 설정 폼의 저장/되돌리기/dirty 기계장치 ----
// opts = {check(v)->errs, save(v,changed)->Promise, errors(form,errs), mark(form,changedKeys),
//   confirm(bool|fn(v,changed)->bool), stateEl, saveBtn, undoBtn, dotEl, keys, texts}
// fill(s)로 장치를 채우고 sync()로 버튼/점/상태문구를 갱신한다. handle.dirty로 beforeunload 처리.
function bindForm(form, opts) {
var o = opts || {}, base = null, src = null;
var keys = o.keys || KEYS;
function node(x) { return typeof x === 'string' ? document.getElementById(x) : x; }
var stateEl = node(o.stateEl), saveBtn = node(o.saveBtn), undoBtn = node(o.undoBtn), dotEl = node(o.dotEl);
var T = Object.assign({loading: '설정을 불러오는 중…', invalid: '입력값을 확인하세요.',
pending: '변경 {n}건 · 아직 저장하지 않음', clean: '장치에 저장된 설정과 같습니다.', saved: '저장했습니다.', confirm: '설정을 저장할까요?'}, o.texts);
function vals() { var e = form.elements, v = {}; for (var i = 0; i < keys.length; i++) v[keys[i]] = String(e[keys[i]].value).trim(); return v; }
function diff(v) { var c = []; if (!base) return c; for (var i = 0; i < keys.length; i++) { var k = keys[i]; if (v[k] !== base[k]) c.push(k); } return c; }
function changes(v) {
var c = diff(v), out = [];
for (var i = 0; i < c.length; i++) { var k = c[i]; out.push((LABELS[k] || k) + ': ' + showValue(form, k, base[k]) + ' → ' + showValue(form, k, v[k])); }
return out;
}
function sync() {
var v = vals(), ch = diff(v), er = o.check ? (o.check(v) || {}) : {}, bad = 0, k;
for (k in er) bad++;
if (o.errors) o.errors(form, er);
if (o.mark) o.mark(form, ch);
if (saveBtn) saveBtn.disabled = !base || !ch.length || bad > 0;
if (undoBtn) undoBtn.disabled = !ch.length;
if (stateEl) stateEl.textContent = !base ? T.loading : bad ? T.invalid : ch.length ? T.pending.replace('{n}', ch.length) : T.clean;
if (dotEl) dotEl.classList.toggle('dot', ch.length > 0);
return {v: v, changed: ch, errs: er};
}
function fill(s) { src = s; base = fill2(form, s); return sync(); }
function fill2(f, s) { var e = f.elements, b = {}; for (var i = 0; i < keys.length; i++) { var k = keys[i]; b[k] = String(s[k]); e[k].value = b[k]; } return b; }
form.addEventListener('input', sync);
if (saveBtn) saveBtn.onclick = function (ev) {
if (ev) ev.preventDefault();
var r = sync();
if (r.changed.length && Object.keys(r.errs).length === 0) {
var go = function () {
Promise.resolve(o.save ? o.save(r.v, r.changed) : null).then(function () {
// src도 함께 갱신: 저장 후 "되돌리기"가 페이지 로드 시점의 낡은 스냅샷으로 되돌아가 기기와 UI가 어긋나는 것을 막는다.
src = Object.assign({}, src, r.v); base = r.v; sync(); toast(T.saved, 'ok');
}, function (e) { toast(e && e.message ? e.message : String(e), 'bad'); });
};
if (o.confirm === false) go();
else if (confirm(changes(r.v).join('\n') ? T.confirm + '\n' + changes(r.v).join('\n') : T.confirm)) go();
} else sync();
};
if (undoBtn) undoBtn.onclick = function () { if (src) fill(src); };
return {fill: fill, values: vals, changed: diff, changes: changes, sync: sync,
dirty: function () { return !!base && diff(vals()).length > 0; },
get base() { return base; }};
}
return { M: M, KEYS: KEYS, LABELS: LABELS, feat: feat, caps: caps, values: values, fill: fill, changed: changed, showValue: showValue, h: h, render: render, component: component, slot: slot, api: api, csrf: csrf, post: post, toast: toast, fire: fire, on: on, status: status, tick: tick, bindForm: bindForm, get ST() { return ST; }};
})();
// ---- WUI: instance components (Web Components) ----
// A component whose html contains <!--@instance--> is emitted as a <template id="wui-t-NAME"> plus
// <wui-name> tag wherever referenced. def() registers the custom element: every tag gets its own
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
body: document.body,
addEventListener: function () { return document.addEventListener.apply(document, arguments); }
};
}
// opts.form: form-associated custom element (FACE). The host joins form.elements by name and
// the component publishes its value with this.__internals.setFormValue(v). See @instance form.
function def(name, init, css, opts) {
var fa = !!(opts && opts.form);
var own = css == null ? null : { tried: false, sheet: null };
var cls = class extends HTMLElement {
connectedCallback() {
if (this.__wui) return;
this.__wui = 1;
if (fa) { try { this.__internals = this.attachInternals(); } catch (e) { this.__internals = null; } }
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
};
if (fa) cls.formAssociated = true;
customElements.define('wui-' + name.toLowerCase(), cls);
}
return { def: def, roots: roots };
})();
