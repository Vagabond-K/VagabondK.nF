# VagabondK.nF.WebUi

Build-time page composer for nanoGate-style device management UIs. One shared **shell**
(tabs, login, fetch, dialogs, point editor) plus one small **profile** per gateway are
merged, validated, minified and embedded as a single C# string resource - no runtime
reflection, no external assets.

## Files

| File | Role |
|---|---|
| `Assets/core.js` | Runtime library (injected at `<!--@core-->`): manifest access, form factory, value formatting, micro vDOM. |
| `VagabondK.nF.WebUi.targets` | MSBuild SDK hook (`WebUiGeneratePage`, runs before `CoreCompile`): merges, validates, minifies, gzips and emits `WebPage.g.cs`. |

## Markers

`shell.html` must contain, in this order:
`<!--@manifest-->` `<!--@core-->` `<!--@style-->` `<!--@views-->` `<!--@script-->`

`profile.html` must contain: `<!--@manifest-->` `<!--@style-->` `<!--@views-->` `<!--@script-->`
(`@core` in a profile is an error - the library provides it.)
A profile may also carry an optional `<!--@slots-->` section (see *Components & slots*).

## Components & slots

Two complementary ways for a profile to place its own UI at a fixed shell position,
Blazor-style, without the shell knowing the profile in advance.

**Runtime function components** (`Assets/core.js`) - a component is a function
`(props) -> vnode | array | string | null`. `WebUi.h(fn, props)` renders it inline;
`WebUi.component(name, fn)` registers it by name and `WebUi.slot(name, props)` renders
whatever is registered. The shell registers a *default* for each named slot it uses, so a
profile that does nothing keeps working, and a profile that calls `WebUi.component(name, ...)`
in its `@script` (which runs after the shell) transparently overrides the default.

The shell's cache table uses this: `renderCache` emits
`WebUi.slot('cacheRow', {p, cc})` for the leading columns and appends the shared
value/quality cells itself. The default `cacheRow` bridges the legacy `profileCacheRow(p)`
string-array hook (and the plain-ID fallback), so existing profiles are unaffected; a profile
can instead return `td` vnodes directly for full markup control.

**Build-time named slots** (`.targets`) - the shell reserves a position with a whole-line
token `<!--@slot:NAME-->`. A profile fills it in its `@slots` section:

```html
<!--@slots-->
<!--@slot:pointsNote-->
<span>이 프로필은 ... 포인트를 바꾼 후에는 재부팅해야 반영됩니다.</span>
```

At merge time the generator replaces each shell token with the matching profile block
(unknown-to-profile tokens are dropped, so a slot is optional per profile). Validation fails
the build if a profile fills a slot the shell never references. This replaces the old
`$('pNote').textContent = '...'` JS-injection pattern with static markup - smaller and no
runtime cost.

## Manifest (single source of truth)

The profile's `@manifest` section holds one JSON object:

```json
{"id":"modbus-to-mqtt","schema":5,
 "form":[{"name":"timeout","label":"응답 대기 시간","fmt":"ms"}, ...],
 "features":{"byteOrder":true,"access":["RO","RW"],"name":false,
             "addr":{...},"cacheCols":{"head":[...]},"points":false}}
```

* `id` - gateway profile string sent to `/api/settings` (`gatewayProfile`).
* `schema` - integer `ConfigurationSchemaVersion` used by save/backup payloads.
* `form` - ordered settings fields. `name` must match an input in the profile's `@views`
  form `#sf`. Optional `fmt`: `ms | pin | secret | bool` drives the change-dialog labels
  (selects and radio groups resolve their own display text automatically).
* `features` - whitelist: `points`, `scale`, `name`, `byteOrder`, `access`, `addr`,
  `cacheCols`. The shell reads them through `WebUi.feat(name, default)`.

Build-time validation fails the compile on: unknown manifest keys, bad id/schema,
duplicate or missing form names, form names not present in the views, legacy globals
(`PROFILE`, `KEYS`, `LABELS`, `profileAddr`, ...) still defined in the profile script,
and missing required hooks.

## Profile hooks

Required: `profileRender(status)`, `profileTick(uptimeSeconds)`, `checkSettings(values)`,
`syncForm()`. Optional: `profilePoint()` (new-point defaults), `profileCacheRow(p)`
(with `features.cacheCols`, bridged by the default `cacheRow` component - a profile may
instead register `WebUi.component('cacheRow', ...)`), `profileExport/profileImport` are
provided by the shell, `mapDirty()` for profile-owned editors (e.g. DNP3 range rules).

The shell exposes shared helpers built on the manifest: `formValues()`, `fillForm(s)`,
`profileDirty()`, `formState(errs)` (errors + change dots + save/undo state),
`formChanges(v)` (dialog diff lines), `settingsBody(v)` (POST body), `accessList()`,
`addrCfg(side)`, `feat()`, `MAN`, `PROFILE`, `KEYS`, `LABELS`.

## Micro vDOM

`WebUi.h(tag, props, ...children)` builds vnode trees (`{key: ...}` for stable diffing,
`onclick` handlers, `text`, `className`, `hidden`, `style` props supported); when `tag` is a
function it is treated as a component and called with `props`.
`WebUi.render(parentEl, vnodes)` patches the DOM in place with minimal mutations.
`WebUi.component(name, fn)` / `WebUi.slot(name, props)` add the named registry described above.
Used by the shell for the point and cache tables.

## Usage (consumer project)

```xml
<PropertyGroup>
  <WebUiShell>..\shared\shell.html</WebUiShell>
  <WebUiProfile>$(MSBuildProjectDirectory)\WebUi\profile.html</WebUiProfile>
  <WebUiNamespace>MyGateway.Management</WebUiNamespace>
</PropertyGroup>
<Import Project="..\..\VagabondK.nF\Source\Web\VagabondK.nF.WebUi\VagabondK.nF.WebUi.targets" />
```

Produces `$(IntermediateOutputPath)WebPage.g.cs` with `WebPage.Html` (the merged page) -
compile it into the management project like any other source file.
