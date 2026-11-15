// VagabondK.nF.WebUi 코어: <nf-core/> 마커 줄에 주입됩니다.
// PageComposer가 자동 생성한 MANIFEST 상수(id + form)를 읽어 WebUi 헬퍼를 노출합니다 —
// 기능 플래그(feat), 폼 팩토리(autoForm), 값 포맷(showValue), 가상 DOM(node/render). 의존성 없음.
var WebUi = (function () {
  var M = typeof MANIFEST === "undefined" ? {} : MANIFEST;
  var F = M.form || [],
    KEYS = [],
    LABELS = {},
    BY = {};
  for (var i = 0; i < F.length; i++) {
    KEYS.push(F[i].name);
    LABELS[F[i].name] = F[i].label;
    BY[F[i].name] = F[i];
  }
  // ---- i18n ----
  // 언어 런타임(uiLang/uiText/uiSetLang/uiAddTexts)은 공용 자산 /i18n.js가 먼저 로드합니다.
  // (사전 window.__wuiTx도 texts.js가 채웁니다 — PageComposer가 @core 앞에서 링크 순서를 지킵니다.)
  // 서버가 api/status의 capabilities로 자기 서술을 보내면 caps()로 받아 feat()의 출처가 됩니다.
  // (manifest.features는 폐기 — capabilities가 유일한 선언부입니다.)
  var CAPS = null;
  function caps(o) {
    if (o) CAPS = o;
    return CAPS;
  }
  function feat(name, dflt) {
    return CAPS && name in CAPS ? CAPS[name] : dflt;
  }
  // ---- form factory ----
  function values(form) {
    var e = form.elements,
      v = {};
    for (var i = 0; i < KEYS.length; i++)
      v[KEYS[i]] = String(e[KEYS[i]].value).trim();
    return v;
  }
  function fill(form, s) {
    var e = form.elements,
      base = {};
    for (var i = 0; i < KEYS.length; i++) {
      var k = KEYS[i];
      base[k] = String(s[k]);
      e[k].value = base[k];
    }
    return base;
  }
  function changed(base, v) {
    var c = [];
    if (!base) return c;
    for (var i = 0; i < KEYS.length; i++) {
      var k = KEYS[i];
      if (v[k] !== base[k]) c.push(k);
    }
    return c;
  }
  function showValue(form, k, v) {
    var f = BY[k],
      el = form.elements[k],
      o,
      i;
    if (f) {
      if (f.fmt === "ms") return v + " ms";
      if (f.fmt === "pin") return "GPIO " + v;
      if (f.fmt === "secret") return v ? "\u2022\u2022\u2022\u2022" : v;
      if (f.fmt === "bool" || f.fmt === "switch")
        return v === "1" ? uiText("val.yes") : uiText("val.no");
    }
    if (!f || !f.fmt || f.fmt === "seg") {
      if (el && el.querySelector) {
        o = el.querySelector('option[value="' + v + '"]');
        if (o) return o.text;
      } else if (el && !el.tagName) {
        for (i = 0; i < el.length; i++)
          if (el[i].value === v) {
            o = el[i].parentNode.querySelector
              ? el[i].parentNode.querySelector("span")
              : null;
            if (o) return o.textContent;
          }
      }
    }
    return v;
  }
  // ---- micro vDOM ----
  // 컴포넌트는 (props) -> vnode | 배열 | 문자열 | null 함수입니다. node()의 태그 자리에 함수가 오면
  // 호출해 인라인으로 렌더합니다(node(Comp, props)). component()/slot()은 이름 레지스트리를 둬서
  // 셸이 고정 위치에서 프로필이 준 컴포넌트를 그릴 수 있게 합니다(WebUi.slot('name', props)).
  var REG = {};
  function component(name, fn) {
    REG[name] = fn;
    return fn;
  }
  function norm(c) {
    return typeof c === "string" || typeof c === "number"
      ? { t: "#", v: String(c) }
      : c;
  }
  function slot(name, props) {
    var f = REG[name];
    return f ? f(props || {}) : null;
  }
  function node(tag, props) {
    if (typeof tag === "function") return tag(props || {});
    var n = { t: tag, p: props || {}, k: [] };
    for (var i = 2; i < arguments.length; i++) add(n, arguments[i]);
    return n;
  }
  function add(n, c) {
    if (c == null || c === false) return;
    if (Object.prototype.toString.call(c) === "[object Array]") {
      for (var i = 0; i < c.length; i++) add(n, c[i]);
      return;
    }
    n.k.push(norm(c));
  }
  function mk(n) {
    if (n.t === "#") return document.createTextNode(n.v);
    var e = document.createElement(n.t);
    prop(e, n);
    for (var i = 0; i < n.k.length; i++) e.appendChild(mk(n.k[i]));
    return e;
  }
  function prop(e, n) {
    var p = n.p;
    for (var k in p) {
      if (k === "key") continue;
      var v = p[k];
      if (k === "className") {
        if (e.className !== v) e.className = v || "";
      } else if (k === "text") {
        if (e.textContent !== v) e.textContent = v;
      } else if (k === "style") {
        for (var s in v) e.style[s] = v[s];
      } else if (k.slice(0, 2) === "on") e[k.toLowerCase()] = v;
      else if (v === null || v === false) e.removeAttribute(k);
      else e.setAttribute(k, v === true ? "" : v);
    }
  }
  function same(node, n) {
    return n.t === "#"
      ? node.nodeType === 3
      : node.nodeType === 1 && node.tagName === n.t.toUpperCase();
  }
  function patch(node, n) {
    if (!same(node, n)) {
      var r = mk(n);
      node.parentNode.replaceChild(r, node);
      return r;
    }
    if (n.t === "#") {
      if (node.data !== n.v) node.data = n.v;
      return node;
    }
    prop(node, n);
    // text 속성은 요소 전체 내용을 소유합니다(textContent가 자식을 전부 대체). 그래서 자식
    // diff를 건너뜁니다 — 아래 루프가 방금 설정한 텍스트 노드를 지워버리기 때문입니다.
    if (n.p.text !== undefined) return node;
    var kids = n.k,
      i;
    for (i = 0; i < kids.length; i++) {
      var c = node.childNodes[i];
      if (c) patch(c, kids[i]);
      else node.appendChild(mk(kids[i]));
    }
    while (node.childNodes.length > kids.length)
      node.removeChild(node.lastChild);
    return node;
  }
  // render(parent, vnodes): 자식 목록을 diff해 병합하되 key 속성이 있는 노드는 key로 짝짓습니다
  // (순서가 바뀌어도 포커스/상태가 유지됨). key 없는 노드는 위치로 짝짓고, 남은 자식은 제거합니다.
  function render(parent, list) {
    var old = [],
      i,
      j;
    for (i = 0; i < parent.childNodes.length; i++)
      old.push(parent.childNodes[i]);
    var used = new Array(old.length);
    for (i = 0; i < list.length; i++) {
      var n = list[i],
        key = n.p.key,
        node = null;
      if (key !== undefined) {
        for (j = 0; j < old.length; j++)
          if (!used[j] && old[j].__k === key) {
            node = old[j];
            break;
          }
      } else
        for (j = 0; j < old.length; j++)
          if (!used[j] && same(old[j], n)) {
            node = old[j];
            break;
          }
      if (node) {
        used[j] = 1;
        node = patch(node, n);
      } else node = mk(n);
      node.__k = key;
      var want = parent.childNodes[i];
      if (node !== want) parent.insertBefore(node, want || null);
    }
    while (parent.childNodes.length > list.length)
      parent.removeChild(parent.lastChild);
  }
  // ---- transport (Phase 1.5): api/csrf/post/toast ----
  // 셸마다 중복되던 fetch 래퍼의 단일 구현입니다. 401이면 err.unauth와 함께 /login으로 보냅니다:
  // 로그인 UI는 페이지(또는 login 컴포넌트)가 이벤트를 구독해 반응합니다.
  var csrfH = "";
  // ---- busy: API(변경 요청) 처리 중 컨트롤 잠금 ----
  // POST 등 장치 상태를 바꾸는 요청이 떠 있는 동안 모든 폼 컨트롤을 pointer-events로 막고
  // 상단 배지로 "처리 중"을 표시합니다. 주기적 GET 갱신은 포함하지 않아 화면이 주기적으로
  // 잠기는 일이 없습니다. first-use에 스타일/배지 DOM을 만듭니다(셸 마크업 수정 불필요).
  var busyN = 0,
    busyEl = null,
    blockEl = null;
  function busy(on) {
    busyN = Math.max(0, busyN + (on ? 1 : -1));
    if (!busyEl) {
      var s = document.createElement("style");
      s.textContent =
        "#wuiBusy{position:fixed;left:50%;top:8px;transform:translateX(-50%);z-index:90;padding:4px 12px;" +
        "border-radius:14px;font:12px system-ui;background:rgba(20,26,32,.92);color:#e8eef2;border:1px solid rgba(120,140,160,.5)}" +
        "#wuiBlock{position:fixed;inset:0;z-index:80;cursor:progress}" +
        "body.wui-busy button,body.wui-busy input,body.wui-busy select,body.wui-busy textarea," +
        "body.wui-busy wui-number,body.wui-busy wui-password,body.wui-busy wui-search{pointer-events:none;opacity:.6}";
      document.head.append(s);
      busyEl = document.createElement("div");
      busyEl.id = "wuiBusy";
      busyEl.hidden = true;
      busyEl.textContent = uiText("core.busy");
      blockEl = document.createElement("div"); // shadow DOM 내부 포함 전체 클릭 차단
      blockEl.id = "wuiBlock";
      blockEl.hidden = true;
      document.body.append(blockEl, busyEl);
    }
    document.body.classList.toggle("wui-busy", busyN > 0);
    busyEl.hidden = blockEl.hidden = busyN === 0;
  }
  var authRedirect = true; // WebUi.authRedirect=false로 바꾸면 wui:unauth 이벤트 방식(구형 오버레이)
  async function api(path, opt, ms) {
    var mut = !!(opt && opt.method && opt.method !== "GET");
    if (mut) busy(true);
    var ac = new AbortController(),
      timer = setTimeout(function () {
        ac.abort();
      }, ms || 8000);
    try {
      var r = await fetch(
        path,
        Object.assign({ cache: "no-store", signal: ac.signal }, opt),
      );
      var json = (r.headers.get("Content-Type") || "").indexOf("json") >= 0;
      var body = json
        ? await r.json().catch(function () {
            return {};
          })
        : await r.text();
      if (!r.ok) {
        var err = new Error((json && body.error) || "HTTP " + r.status);
        if (r.status === 401) {
          err.unauth = true;
          if (authRedirect) location.replace("/login" + location.hash);
          else fire("wui:unauth", {});
        }
        throw err;
      }
      return body;
    } catch (e) {
      if (e.name === "AbortError") throw new Error(uiText("core.timeout"));
      if (e instanceof TypeError) throw new Error(uiText("core.offline"));
      throw e;
    } finally {
      clearTimeout(timer);
      if (mut) busy(false);
    }
  }
  function csrf(h) {
    if (h) csrfH = h;
    if (csrfH) return Promise.resolve(csrfH);
    return api("/api/auth/status", null, 4000).then(
      function (a) {
        csrfH = a.csrfHeader || "";
        return csrfH;
      },
      function () {
        return csrfH;
      },
    );
  }
  function post(path, body, type, ms) {
    return csrf().then(function (h) {
      var o = { method: "POST", headers: {}, body: body || "" };
      if (h) o.headers[h] = "1";
      o.headers["Content-Type"] = type || "application/x-www-form-urlencoded";
      return api(path, o, ms);
    });
  }
  function toast(text, kind) {
    var box = document.getElementById("toasts");
    if (!box) return;
    var t = document.createElement("div");
    t.className = "toast" + (kind ? " " + kind : "");
    t.textContent = text;
    box.appendChild(t);
    var life = kind === "bad" ? 7000 : 4000;
    setTimeout(function () {
      t.classList.add("out");
    }, life - 500);
    setTimeout(function () {
      t.remove();
    }, life);
  }
  // ---- events (Phase 1.5): wui:status / wui:tick ----
  // 루트 페이지의 폴러가 status()/tick()을 호출하고, 컴포넌트는 on()으로 구독합니다.
  // ST/SEEN은 최신 상태와 수신 시각: age는 클라이언트에서 외삽합니다.
  var ST = null,
    SEEN = 0;
  function fire(name, detail) {
    document.dispatchEvent(new CustomEvent(name, { detail: detail }));
  }
  function on(name, fn) {
    document.addEventListener(name, function (e) {
      fn(e.detail);
    });
  }
  function status(s) {
    ST = s;
    SEEN = Date.now();
    if (s && s.capabilities) CAPS = s.capabilities;
    fire("wui:status", s);
    return s;
  }
  function tick() {
    if (!ST) return;
    var age = (Date.now() - SEEN) / 1000;
    fire("wui:tick", { uptime: (ST.uptime || 0) + age, age: age, status: ST });
  }
  // ---- 마크업 선언형 컨트롤 업그레이드 (Phase 1.5) ----
  // data-fmt가 렌더링의 단일 출처: bool/switch → .sw 토글(라디오 0/1), seg → .sg 세그먼트(select options 이식).
  // 값은 라디오 그룹 form.elements[k].value로 왕복되므로 bindForm 저장/dirty 기치는 그대로 재사용한다.
  // 스타일은 themes.css가 프로젝트에 의해 통째로 교체될 수 있어 최초 업그레이드 시 한 번 직접 주입한다.
  var CTL_CSS =
    ".sw{position:relative;display:inline-block;width:44px;height:24px;vertical-align:middle;cursor:pointer}" +
    ".sw input{position:absolute;opacity:0;width:1px;height:1px;pointer-events:none}" +
    ".sw i{position:absolute;left:0;top:0;right:0;bottom:0;border-radius:12px;background:var(--line,#334155);transition:background .15s;pointer-events:none}" +
    '.sw i::after{content:"";position:absolute;top:3px;left:3px;width:18px;height:18px;border-radius:50%;background:var(--tx,#e2e8f0);transition:left .15s}' +
    '.sw:has(input[value="1"]:checked) i{background:var(--in,#38bdf8)}' +
    '.sw:has(input[value="1"]:checked) i::after{left:23px}' +
    ".sw:focus-visible{outline:2px solid var(--acc,#38bdf8);border-radius:12px}" +
    ".sg{display:inline-flex;border:1px solid var(--line,#334155);border-radius:8px;overflow:hidden;vertical-align:middle}" +
    ".sg label{margin:0}" +
    ".sg input{position:absolute;opacity:0;width:1px;height:1px}" +
    ".sg span{display:inline-block;padding:6px 14px;color:var(--mut,#94a3b8);cursor:pointer;white-space:nowrap}" +
    ".sg input:checked+span{background:var(--in,#38bdf8);color:#04252f;font-weight:600}" +
    ".sg input:focus-visible+span{outline:2px solid var(--acc,#38bdf8)}";
  function ctlCss() {
    if (ctlCss.on) return;
    ctlCss.on = 1;
    var s = document.createElement("style");
    s.textContent = CTL_CSS;
    document.head.appendChild(s);
  }
  function switchCtl(name, el) {
    var on =
      el.tagName === "SELECT"
        ? el.value === "1"
        : el.type === "checkbox"
          ? el.checked
          : String(el.value) === "1";
    var w = document.createElement("span");
    w.className = "sw";
    w.setAttribute("role", "switch");
    w.tabIndex = 0;
    var a = document.createElement("input"),
      b = document.createElement("input");
    a.type = b.type = "radio";
    a.name = b.name = name;
    a.value = "0";
    b.value = "1";
    b.checked = on;
    w.appendChild(a);
    w.appendChild(b);
    w.appendChild(document.createElement("i"));
    w.setAttribute("aria-checked", on ? "true" : "false");
    function tog() {
      var t = b.checked ? a : b;
      t.checked = true;
      w.setAttribute("aria-checked", b.checked ? "true" : "false");
      t.dispatchEvent(new Event("input", { bubbles: true }));
    }
    // preventDefault: .sw가 <label> 안에 있으면 라벨 활성 동작이 첫 라디오(value=0)로
    // click을 재전달해 tog의 반전을 되돌립니다(클릭이 무효과). 기본 동작을 막아야 토글이 유지됩니다.
    w.addEventListener("click", function (e) {
      e.preventDefault();
      tog();
    });
    w.addEventListener("keydown", function (e) {
      if (e.key === " " || e.key === "Enter") {
        e.preventDefault();
        tog();
      }
    });
    return w;
  }
  function segCtl(name, sel) {
    var g = document.createElement("span");
    g.className = "sg";
    for (var j = 0; j < sel.options.length; j++) {
      var o = sel.options[j],
        l = document.createElement("label"),
        r = document.createElement("input"),
        s = document.createElement("span");
      r.type = "radio";
      r.name = name;
      r.value = o.value;
      if (o.value === sel.value) r.checked = true;
      s.textContent = o.textContent;
      l.appendChild(r);
      l.appendChild(s);
      g.appendChild(l);
    }
    // 라벨 안 공백 클릭이 바깥 <label> 활성 동작으로 첫 라디오에 전달되는 것만 차단(선택은 그대로).
    g.addEventListener("click", function (e) {
      if (e.target === g) e.preventDefault();
    });
    return g;
  }
  function secretCtl(el) {
    // markup의 <input type="password" data-fmt="secret"> → <password> 컴포넌트로 승격.
    // 값 왕복은 호스트의 .value(formAssociated)가 form.elements[name]를 통해 그대로 작동합니다.
    // 런타임 생성이므로 wui- 접두사(빌드 시 FACE가 정의하는 실제 등록명)를 직접 씁니다.
    var w = document.createElement(window.customElements && customElements.get("wui-password") ? "wui-password" : "password");
    var at = ["data-fmt", "data-label", "name", "maxlength", "placeholder", "autocomplete"];
    for (var j = 0; j < at.length; j++)
      if (el.getAttribute(at[j]) != null) w.setAttribute(at[j], el.getAttribute(at[j]));
    if (el.disabled) w.setAttribute("disabled", "");
    return w;
  }
  function upgradeCtls(f) {
    if (!f || !f.querySelectorAll) return;
    var list = f.querySelectorAll("[data-fmt]"),
      i,
      el,
      fmt;
    for (i = 0; i < list.length; i++) {
      el = list[i];
      fmt = el.getAttribute("data-fmt");
      if (!el.name || !el.parentNode) continue;
      if (fmt === "bool" || fmt === "switch") {
        ctlCss();
        el.parentNode.replaceChild(switchCtl(el.name, el), el);
      } else if (fmt === "seg" && el.tagName === "SELECT") {
        ctlCss();
        el.parentNode.replaceChild(segCtl(el.name, el), el);
      } else if (fmt === "secret" && el.tagName === "INPUT") {
        el.parentNode.replaceChild(secretCtl(el), el);
      }
    }
  }
  // bindForm을 타지 않는 정적 페이지용: name 있는 secret input을 <password>로 승격.
  function upgradeSecrets(root) {
    var list = root.querySelectorAll('input[data-fmt="secret"]'),
      i,
      el;
    for (i = 0; i < list.length; i++) {
      el = list[i];
      if (el.name && el.parentNode) el.parentNode.replaceChild(secretCtl(el), el);
    }
  }
  // 부트: core 인라인 스크립트보다 뒤에 파싱되는 마크업도 잡으도록 DOM 준비 후 1회 스캔.
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", function () {
      upgradeSecrets(document);
    });
  } else upgradeSecrets(document);

  // ---- bindForm (Phase 1.5): 설정 폼의 저장/되돌리기/dirty 기계장치 ----
  // opts는 {check(v)->errs, save(v,changed)->Promise, errors(form,errs), mark(form,changedKeys),
  //   confirm(bool|fn(v,changed)->bool), stateEl, saveBtn, undoBtn, dotEl, keys, texts} 형태의 설정 객체입니다.
  // fill(s)로 장치를 채우고 sync()로 버튼/점/상태문구를 갱신합니다. handle.dirty로 beforeunload 처리.
  function bindForm(form, opts) {
    var o = opts || {},
      base = null,
      src = null;
    if (typeof form === "string") form = document.getElementById(form);
    // data-fmt 마크업을 .sw/.sg 컨트롤로 승격 (opts.upgrade:false로 끄기 가능)
    if (o.upgrade !== false) upgradeCtls(form);
    var keys = o.keys || KEYS;
    function node(x) {
      return typeof x === "string" ? document.getElementById(x) : x;
    }
    var stateEl = node(o.stateEl),
      saveBtn = node(o.saveBtn),
      undoBtn = node(o.undoBtn),
      dotEl = node(o.dotEl);
    var T = Object.assign(
      {
        loading: uiText("form.loading"),
        invalid: uiText("form.invalid"),
        pending: uiText("form.pending"),
        clean: uiText("form.clean"),
        saved: uiText("form.saved"),
        confirm: uiText("form.confirm"),
      },
      o.texts,
    );
    function vals() {
      var e = form.elements,
        v = {};
      for (var i = 0; i < keys.length; i++)
        v[keys[i]] = String(e[keys[i]].value).trim();
      return v;
    }
    function diff(v) {
      var c = [];
      if (!base) return c;
      for (var i = 0; i < keys.length; i++) {
        var k = keys[i];
        if (v[k] !== base[k]) c.push(k);
      }
      return c;
    }
    function changes(v) {
      var c = diff(v),
        out = [];
      for (var i = 0; i < c.length; i++) {
        var k = c[i];
        out.push(
          (LABELS[k] || k) +
            ": " +
            showValue(form, k, base[k]) +
            " → " +
            showValue(form, k, v[k]),
        );
      }
      return out;
    }
    function sync() {
      var v = vals(),
        ch = diff(v),
        er = o.check ? o.check(v) || {} : {},
        bad = 0,
        k;
      for (k in er) bad++;
      if (o.errors) o.errors(form, er);
      if (o.mark) o.mark(form, ch);
      if (saveBtn) saveBtn.disabled = !base || !ch.length || bad > 0;
      if (undoBtn) undoBtn.disabled = !ch.length;
      if (stateEl)
        stateEl.textContent = !base
          ? T.loading
          : bad
            ? T.invalid
            : ch.length
              ? T.pending.replace("{n}", ch.length)
              : T.clean;
      if (dotEl) dotEl.classList.toggle("dot", ch.length > 0);
      return { v: v, changed: ch, errs: er };
    }
    function fill(s) {
      src = s;
      base = fill2(form, s);
      syncAria(form);
      return sync();
    }
    // 프로그램 checked(예: 상태 폴링로 인한 채움)는 input 이벤트를 발생시키지 않으므로
    // .sw 래퍼의 aria-checked가 어긋난 채로 남습니다. fill 후 한 번 맞춰줍니다.
    function syncAria(f) {
      var sws = f.querySelectorAll(".sw"),
        i,
        ins;
      for (i = 0; i < sws.length; i++) {
        ins = sws[i].getElementsByTagName("input");
        if (ins.length > 1)
          sws[i].setAttribute("aria-checked", ins[1].checked ? "true" : "false");
      }
    }
    function fill2(f, s) {
      var e = f.elements,
        b = {};
      for (var i = 0; i < keys.length; i++) {
        var k = keys[i];
        b[k] = String(s[k]);
        e[k].value = b[k];
      }
      return b;
    }
    form.addEventListener("input", sync);
    if (saveBtn)
      saveBtn.onclick = function (ev) {
        if (ev) ev.preventDefault();
        var r = sync();
        if (r.changed.length && Object.keys(r.errs).length === 0) {
          var go = function () {
            Promise.resolve(o.save ? o.save(r.v, r.changed) : null).then(
              function () {
                // src도 함께 갱신: 저장 후 "되돌리기"가 페이지 로드 시점의 낡은 스냅샷으로 되돌아가 기기와 UI가 어긋나는 것을 막습니다.
                src = Object.assign({}, src, r.v);
                base = r.v;
                sync();
                toast(T.saved, "ok");
              },
              function (e) {
                toast(e && e.message ? e.message : String(e), "bad");
              },
            );
          };
          if (o.confirm === false) go();
          else if (
            confirm(
              changes(r.v).join("\n")
                ? T.confirm + "\n" + changes(r.v).join("\n")
                : T.confirm,
            )
          )
            go();
        } else sync();
      };
    if (undoBtn)
      undoBtn.onclick = function () {
        if (src) fill(src);
      };
    // 최초 wui:status 수신 시 자동 채움 (구 autoForm의 구독 경로 대체, opts.fillOnStatus:false로 끄기 가능)
    if (o.fillOnStatus !== false)
      on("wui:status", function (s) {
        if (!base) fill(s);
      });
    return {
      fill: fill,
      values: vals,
      changed: diff,
      changes: changes,
      sync: sync,
      dirty: function () {
        return !!base && diff(vals()).length > 0;
      },
      get base() {
        return base;
      },
    };
  }
  return {
    M: M,
    KEYS: KEYS,
    LABELS: LABELS,
    feat: feat,
    caps: caps,
    values: values,
    fill: fill,
    changed: changed,
    showValue: showValue,    node: node,
    render: render,
    component: component,
    slot: slot,
    api: api,
    csrf: csrf,
    post: post,
    toast: toast,
    fire: fire,
    on: on,
    busy: busy,
    status: status,
    tick: tick,
    bindForm: bindForm,
    text: uiText,
    addTexts: uiAddTexts,
    setLang: uiSetLang,
    get lang() {
      return uiLang;
    },
    get ST() {
      return ST;
    },
    // 401 처리 방식: true(기본)는 /login 페이지로 리다이렉트, false는 wui:unauth 이벤트(오버레이 셸용).
    get authRedirect() {
      return authRedirect;
    },
    set authRedirect(v) {
      authRedirect = !!v;
    },
  };
})();
// ---- WUI: 인스턴스 컴포넌트(Web Components) ----
// html에 <!--@instance--> 마커가 있는 컴포넌트는 <template id="wui-t-NAME">으로 발행되고,
// 참조된 위치에는 <wui-name> 태그가 들어갑니다. def()가 커스텀 엘리먼트로 등록하면,
// 태그마다 shadow root를 갖고 템플릿을 복제해 넣으므로 id가 인스턴스별로 격리되고
// init이 인스턴스당 1회 실행됩니다(init.call(host, root, scopedDocument, $)).
// 페이지의 병합 <style>은 adopted base sheet로 따라 들어가 전역 규칙이 shadow 안에서도
// 적용되고, 컴포넌트 자기 css가 그 뒤를 이습니다.
var WUI = (function () {
  var roots = [],
    base = null,
    baseCss = "";
  function makeSheet(text) {
    try {
      var s = new CSSStyleSheet();
      s.replaceSync(text);
      return s;
    } catch (e) {
      return null;
    }
  }
  function scope(root) {
    return {
      getElementById: function (id) {
        return root.getElementById(id);
      },
      querySelector: function (s) {
        return root.querySelector(s);
      },
      querySelectorAll: function (s) {
        return root.querySelectorAll(s);
      },
      createElement: function (t) {
        return document.createElement(t);
      },
      body: document.body,
      addEventListener: function () {
        return document.addEventListener.apply(document, arguments);
      },
    };
  }
  // opts.form: 폼 연관 커스텀 엘리먼트(FACE). 호스트가 name으로 form.elements에 참여하고
  // 값은 this.__internals.setFormValue(v)로 공개합니다. (@instance form 매니페스트 참조)
  function def(name, init, css, opts) {
    var fa = !!(opts && opts.form);
    var own = css == null ? null : { tried: false, sheet: null };
    var cls = class extends HTMLElement {
      connectedCallback() {
        if (this.__wui) return;
        this.__wui = 1;
        if (fa) {
          try {
            this.__internals = this.attachInternals();
          } catch (e) {
            this.__internals = null;
          }
        }
        var sr = this.attachShadow({ mode: "open" });
        var t = document.getElementById("wui-t-" + name);
        if (t) sr.appendChild(t.content.cloneNode(true));
        if (base === null) {
          var s = document.querySelector("style");
          baseCss = s ? s.textContent : "";
          base = makeSheet(baseCss) || false;
        }
        if (own && !own.tried) {
          own.tried = true;
          own.sheet = makeSheet(css);
        }
        var ok = base !== null && base !== false && (!own || own.sheet);
        if (ok) {
          try {
            sr.adoptedStyleSheets = own ? [base, own.sheet] : [base];
          } catch (e) {
            ok = false;
          }
        }
        if (!ok) {
          var st = document.createElement("style");
          st.textContent = baseCss + (css ? "\n" + css : "");
          sr.insertBefore(st, sr.firstChild);
        }
        roots.push(sr);
        this.shadow = sr;
        if (init)
          init.call(this, sr, scope(sr), function (id) {
            return sr.getElementById(id);
          });
      }
    };
    if (fa) cls.formAssociated = true;
    customElements.define("wui-" + name.toLowerCase(), cls);
  }
  return { def: def, roots: roots };
})();
