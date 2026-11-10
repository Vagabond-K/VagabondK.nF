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
(with `features.cacheCols`), `profileExport/profileImport` are provided by the shell,
`mapDirty()` for profile-owned editors (e.g. DNP3 range rules).

The shell exposes shared helpers built on the manifest: `formValues()`, `fillForm(s)`,
`profileDirty()`, `formState(errs)` (errors + change dots + save/undo state),
`formChanges(v)` (dialog diff lines), `settingsBody(v)` (POST body), `accessList()`,
`addrCfg(side)`, `feat()`, `MAN`, `PROFILE`, `KEYS`, `LABELS`.

## Micro vDOM

`WebUi.h(tag, props, ...children)` builds vnode trees (`{key: ...}` for stable diffing,
`onclick` handlers, `text`, `className`, `hidden`, `style` props supported);
`WebUi.render(parentEl, vnodes)` patches the DOM in place with minimal mutations.
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
