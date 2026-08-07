# Stream Tuning Spec — per-monitor Moonlight options

Status: **IMPLEMENTED.** All decisions closed (owner's rulings on T1–T10, 2026-08-05,
and OPEN-1..4), and **all sub-slices 2.1–2.6 have shipped** — see §12. The one
remaining item is the end-to-end run of the whole stack, tracked in `TODO.md`.

> **Now design rationale only.** The canonical homes are: behavior →
> `ARCHITECTURE.md` (connect flow step 5); values → `SPEC.md` ("Moonlight launch",
> "Bitrate sizing", "Config files", "New CLI surface"); slices → `PLAN.md` (Phase 3
> Slice 2). When this file disagrees with those, **ARCHITECTURE/SPEC win** (per
> CLAUDE.md). Same lifecycle as `tool-orchestration.md`.
>
> Passages below written in the future tense ("gains", "will") describe work that has
> since landed; they are kept as the record of *why* each choice was made.

## 1. Why

Lance launches one Moonlight per monitor but gives every one **identical options**.
Measured on the owner's setup (2026-08-05): three monitors (1080p60 / 1440p144 /
4K60) all receive `--bitrate 80000` — **240 Mbps payload, ~290 Mbps with FEC** —
across an agent uplink that is Wi-Fi 6 at **292.5 Mbps PHY** (~150–200 Mbps usable),
encoded by **one Intel Iris Xe QSV engine** at ~840 Mpixel/s (~1.7x a 4K60 load).
Client logs show `Network dropped 1 frame` and `Video decode unit queue overflow`.

The 1080p stream consumes bandwidth the 4K stream needs. This spec makes per-monitor
budgets expressible and adds a derivation so sane values are the default.

**Scope: client-side launch arguments only.** No agent, slot, session or Apollo
change. (Apollo-side tuning is blocked by a separate defect — §9.)

## 2. Rename — **SETTLED (T1, OPEN-1)**

"Flags" becomes "options". Lance is prerelease, so this is a **hard rename with no
compatibility shim**:

| Old | New |
|---|---|
| `remoteClient.defaultFlags` | `remoteClient.defaultOptions` |
| *(new)* | `remoteClient.monitorOptions` |

No compatibility shim and **no migration guard**: a config still saying
`defaultFlags` is simply ignored by the deserializer, and that config's streams
launch with no options. Prerelease, owner's call.

> **Stale defaults fixed in the same change (slice 2.1, done).** `ClientConfig.cs`
> had hardcoded a fallback `DefaultFlags` containing `--yuv444`, `--no-vsync` and
> `--bitrate 80000`, so a config omitting the key reintroduced exactly the flags that
> caused the Vulkan-decode fallback. The same stale values sat in `samples/lance.json`
> and `dist/client/lance.json`. All three were corrected.
>
> **`--bitrate` and `--fps` were removed outright**, not lowered. With no explicit
> bitrate and no mode set, §4.2 row 1 applies and every stream derives at `balanced`;
> `--fps` is dropped because the derivation reads the monitor's refresh rate (§7)
> rather than a single shared value. Shipped `defaultOptions` is therefore just
> `["--video-codec", "HEVC", "--capture-system-keys", "fullscreen"]`.
>
> **The interim gap is closed.** Between slices 2.1 and 2.4 nothing set bitrate or
> fps, so Moonlight applied its own per-resolution defaults (~0.16 bits/pixel →
> ~135 Mbps across the owner's three monitors). Since 2.4 shipped, every stream
> derives its own budget and the `balanced` default lands at the ~84 Mbps target.

## 3. Layers — **SETTLED (T1)**

Moonlight's rule is later-argument-wins. Lance layers on top: config first, then
CLI; within each, general then specific.

```
0  --resolution <native WxH>       generated  lowest — a default, overridable (§5)
0  --fps <min(refresh, 60)>        generated  lowest — a default, overridable (§7)
1  defaultOptions                  config     all monitors
2  monitorOptions[id]              config     one monitor
3  --options                       CLI        all monitors
4  --monitor-options               CLI        one monitor
   --bitrate <derived>             generated  injected per §4.2
```

Layers 0, 1 and 3 exist today; 2 and 4 are new.

**Lance strips nothing.** Because `--bitrate-mode` is global and lives outside these
lists (§4), every token in layers 1–4 is passed to Moonlight verbatim. Lance only
*reads* three of them — `--bitrate`, `--fps`, `--resolution` — all of which are real
Moonlight flags. The passthrough contract is fully preserved.

## 4. Bitrate

### 4.1 `--bitrate-mode` — **SETTLED (T2, T4, OPEN-2)**

**Global, not per monitor.** A config field `remoteClient.bitrateMode` and a CLI flag
`--bitrate-mode`; **CLI overrides config**.

| Value | Behavior | bits/pixel |
|---|---|---|
| `high` | auto | 0.16 |
| `balanced` | auto — **default** | 0.10 |
| `conservative` | auto | 0.06 |
| `<number>` | auto, caller-supplied | as given |
| `manual` | no derivation; bitrate comes from the options | — |

Anchor: Moonlight's own defaults sit at ~0.16 bits/pixel (1080p60→20 Mbps and
4K60→80 Mbps both land there). Those are gaming defaults with deliberate headroom;
desktop content compresses far better, hence the lower default.

| Tier | 1080p60 | 1440p60 | 4K60 | Owner's three monitors |
|---|---|---|---|---|
| `high` | 20 Mbps | 35 Mbps | 80 Mbps | ~135 Mbps |
| `balanced` | 12 Mbps | 22 Mbps | 50 Mbps | **~84 Mbps** |
| `conservative` | 7 Mbps | 13 Mbps | 30 Mbps | ~50 Mbps |

**Numeric guard.** `--bitrate-mode 20000` (meaning "20 Mbps") would otherwise be read
as 20000 bits/pixel. Numeric values are validated to **0.01–1.0**; outside that range
fails fast with *"expected bits-per-pixel (e.g. 0.10), not a kbps value"*.

### 4.2 Which bitrate wins — **SETTLED (T3, point 4)**

Mode is resolved once per session; the derived value is then computed per monitor.
Because CLI must override config, the derived `--bitrate` cannot simply be appended
last — that would clobber a CLI-supplied explicit value. The rule:

> **The derived bitrate applies unless an explicit `--bitrate` survives the merge at
> a layer greater than or equal to the layer that set the mode.** Explicit wins ties.

Mode source layers: `--bitrate-mode` CLI = **3** · `bitrateMode` config = 1 ·
default/inferred = 0. Both settings are *global*, so each sits at the general level of
its source — the CLI flag at layer 3 (`--options`), the config field at layer 1
(`defaultOptions`). *(An earlier draft put the CLI flag at 4; that contradicted the
last row of the table below, which requires an explicit `--bitrate` in `--options`
— layer 3 — to beat a command-line mode.)*

| `--bitrate-mode` | `bitrateMode` | explicit `--bitrate` | Result |
|---|---|---|---|
| — | — | — | auto `balanced` (default) |
| — | — | present | **manual inferred** — explicit used, nothing derived |
| — | set | absent | auto at the config mode |
| — | set | present (any layer) | **explicit wins** + warn |
| set | any | absent | auto at the CLI mode |
| set | any | present in config (1–2) | **CLI mode wins**, derived overrides + warn |
| set | any | present in CLI (3–4) | **explicit wins** + warn |

Every case where one value supersedes another emits a single warning naming both, so
it is never silent.

**This preserves the owner's current config untouched.** It has `--bitrate 80000`
and no mode, so row 2 applies: `manual` is inferred and nothing is derived. Auto is
opted into by *deleting* the flag, not by migrating.

**Inference granularity — SETTLED.** When no mode is set anywhere, the inference in
row 2 is evaluated **per monitor** — so putting an explicit `--bitrate` on only the
4K monitor leaves the other two on auto. When a mode *is* set explicitly it applies
uniformly to all monitors, and per-monitor explicit values still win by the table
above. This is what lets a single global mode cover per-monitor intent.

### 4.3 Derivation

```
fpsForMath = min(fps, 60)                                            (§6)
kbps       = round1000(streamW * streamH * fpsForMath * bpp / 1000)
```

- `streamW x streamH` — the **streamed** resolution after layer merging (§5), not
  the panel's native size.
- `fps` — last `--fps` across layers 1–4, else the monitor's refresh rate (§7),
  else 60.

## 5. Resolution — **SETTLED (T7)**

**`--resolution` is not a Lance option.** It is a *Moonlight* argument Lance
**generates** from the mapped monitor's dimensions and injects at launch
(`SessionDaemon.TryLaunchMoonlight`). It tells Apollo what resolution to serve so
each stream matches the panel it will be fullscreened on; without it every stream
arrives at Apollo's default and is scaled. Confirmed working on the owner's agent —
the three slots log `Capture size: 3840x2160 / 2560x1440 / 1920x1080`.

Placing the generated value at **layer 0** makes it a default rather than a mandate:

```json
"monitorOptions": { "3": ["--resolution", "2560x1440"] }
```

streams the 4K panel at 1440p and lets Moonlight upscale to fill it — dropping that
stream from 497 to 221 Mpixel/s, **~55% off the most expensive stream's encoder load
and bandwidth** for a modest sharpness cost. On a Wi-Fi laptop with an iGPU this is
plausibly the single most effective knob in this document.

Consequence, handled in §4.3: auto-bitrate derives from the streamed resolution.

## 6. Clamping — **SETTLED (T5, OPEN-3)**

There are **two independent 60 fps ceilings**; keeping them distinct matters.

**1. The generated `--fps` ceiling (§7).** Always applied, in every mode. Lance emits
`min(refreshRate, 60)`, so the default stream rate is conservative on a
high-refresh panel. Overridden by stating `--fps` in any layer 1–4.

**2. The derivation ceiling.** Applies **in auto modes only** — `manual` is never
clamped, because an explicit bitrate is an explicit instruction.
`fpsForMath = min(fps, 60)`, so auto modes cannot produce a 4K144-scale number even
when the user has explicitly raised `--fps`. Testing an exotic configuration is done
by stating the bitrate explicitly, which infers `manual`.

- **No max-Hz config field** — both ceilings are fixed at 60.
- **Warn** when effective fps > 60 in an auto mode, so the stream is not silently
  under-provisioned: *"monitor 2 streams at 144fps; auto bitrate is capped at
  60fps-equivalent — set --bitrate explicitly for a full-rate budget."* This can only
  trigger when the user has overridden the generated `--fps`, since ceiling 1 would
  otherwise have kept it at 60.

## 7. Refresh rate and generated `--fps` — **SETTLED (T6b; OPEN-4 reversed)**

`MonitorInfo` gains a refresh rate. `DEVMODE.dmDisplayFrequency` sits at **offset
184**, immediately after the `dmPelsHeight`@176 that `MonitorEnumerator` already
reads — a two-line struct addition on Windows; Xrandr has an equivalent on Linux. It
gains a column in `lance monitors`.

**Lance emits `--fps min(refreshRate, 60)` at layer 0**, exactly as it emits
`--resolution` — a generated default that any later layer overrides. So the owner's
panels receive `--fps 60` (1080p@60), `--fps 60` (1440p@144, clamped) and `--fps 59`
(4K@59).

> An earlier draft had Lance emit nothing here, on the reasoning that emitting a
> 144 Hz panel's true rate would roughly double the load on an already-saturated
> iGPU. That reasoning ignored the 60 clamp, which is what makes emission safe.
> Emitting is strictly better than leaving it to Moonlight's own setting, because
> Lance knows the panel and Moonlight does not.

Streaming above 60 stays possible by stating it — `--fps 120` in any layer 1–4
overrides the generated value (and then §6's separate math clamp applies, with its
warning).

**Rounding caveat.** `dmDisplayFrequency` reports whole numbers, so a 59.94 Hz panel
reads as 59 and receives `--fps 59`. That is closer to the truth than 60 and Apollo
adapts to it (older agent logs show `Adjusted capture rate to 59.94fps to better
match display`). No snapping to "standard" rates.

## 8. Monitor keys — **SETTLED and implemented (T9)**

> **Shipped in slice 2.5.** Names work on Windows via the CCD API and on Linux via the
> Xrandr output name. Canonical description now lives in SPEC "Monitor keys"; the
> design rationale below is retained.
>
> **Extended in slice 2.6.** Naming applies to **`--monitors`** as well, not just the
> option maps — one resolver (`MonitorKey`) serves all three so the rules cannot drift.
> Duplicate detection consequently moved *after* resolution: `--monitors 1,U28E590`
> naming one screen is a fast-fail rather than a request for two slots.


Keys in `monitorOptions` / `--monitor-options` resolve as **all-digits → monitor id;
otherwise → name match**, case-insensitive.

**Ids ship first.** They are the same 1-indexed ids `--monitors` and `lance monitors`
already use, so no new API surface is needed. Their weakness is that the enumeration
index shifts if displays are unplugged or reordered — a per-monitor bitrate then
lands on the wrong screen, silently, since nothing is invalid. `lance monitors` makes
re-checking a one-command job.

**Names are designed here and scheduled at the end of the spec's implementation**,
per the owner. Design, so it is not re-derived later:

- The useful names are the EDID friendly names ("GW2480", "Optix MAG27CQ"), not the
  GDI device name (`\\.\DISPLAY2`), which is opaque and needs `"\\\\.\\DISPLAY2"` in
  JSON.
- Obtaining them AOT-safely means the CCD API: `GetDisplayConfigBufferSizes` →
  `QueryDisplayConfig` → `DisplayConfigGetDeviceInfo(GET_TARGET_NAME)` for
  `monitorFriendlyDeviceName`, joined back to the existing enumeration via
  `GET_SOURCE_NAME`'s `viewGdiDeviceName`. Pure P/Invoke — WMI is unavailable because
  `System.Management` is not AOT-safe. On Linux the Xrandr output name (`HDMI-1`,
  `DP-2`) already serves.
- **Exact** match, not substring — substring is friendlier but silently ambiguous
  across similar models.
- **Every key resolves to an id first.** Two keys resolving to the same monitor →
  fast-fail naming both. A key matching no monitor → warn and skip (the existing
  `--monitors` precedent). Two identical panels share a friendly name → fast-fail,
  telling the user to key by id.

## 9. Apollo-side configuration — restated (out of scope, blocked)

**The defect.** `SlotAllocator.Allocate` clones the template into
`sunshine_{id}.conf` **once**, then skips any id whose file already exists — a clone
is written at allocation and never refreshed. Meanwhile **Apollo rewrites those same
files itself** whenever settings change in its web UI. Proof on the owner's agent:
`sunshine_1.conf` / `sunshine_2.conf` are alphabetically sorted and omit
`server_cmd`, which is Apollo's serializer style (it drops default-valued keys), not
Lance's (Lance preserves template line order and appends). Consequences:

1. Encoder tuning applied to the template (slot 0) **never reaches** slots 1..N.
2. Per-clone tuning done in Apollo's web UI persists but is invisible to Lance and
   cannot be reproduced or version-controlled.
3. Changes to Lance's own mutation rules never reach existing clones — visible on the
   dev box, whose `sunshine_1.conf` predates the `file_state` / `credentials_file`
   rules and still lacks them.

**Is Apollo-side optimisation available?** In principle. The owner's template carries
only five lines, so every encoder setting is at its default, and the agent logs
`Active GPU has HAGS disabled`. Plausible candidates: the QSV preset, the capture
backend (Desktop Duplication vs Windows Graphics Capture), HAGS.

**None should be attempted yet**, because it is (a) unmanageable until the drift
defect is fixed — web-UI tuning is precisely what Lance neither knows about nor
preserves — and (b) second-order: cutting the 4K stream to 1440p (§5) removes ~55% of
the most expensive stream, which no encoder preset will match. Presets are worth
trying *after* the pixel and bandwidth budgets are right, each with its own
before/after measurement.

Fixing drift needs its own decision (re-clone on allocate / merge-and-reconcile / an
explicit `lance slots sync` / document-and-leave) and belongs in a **separate spec**.

## 10. Withdrawn — the slot-to-monitor mapping (T10)

**v1 claimed a defect here. That claim was wrong; it is withdrawn.**

v1 argued that because `SessionDaemon.LaunchMoonlights` zips `slots[i]` with
`targetMonitors[i]`, a failed slot would shift the mapping and give later monitors
the wrong resolution. It does not. Slots are **fungible** — a pool where order is
irrelevant (ARCHITECTURE, "State management"). Resolution and window placement both
derive from `targetMonitors[i]`, which keeps its position; only the interchangeable
slot paired with it changes. Pairing the first N surviving slots with the first N
monitors is correct, and the trailing monitor simply gets no stream — the documented
partial-success policy. Per-monitor options are keyed by **monitor**, so they follow
the monitor regardless of which slot serves it.

Residual polish only: nothing tells the user *which* monitor was dropped. Worth a
warning — *"3 monitors requested, 2 slots started; monitor 3 not connected."*

## 11. Non-goals

- Apollo/agent-side encoder tuning (§9 — blocked, separate spec).
- Session-wide bandwidth budget (T6) — **abandoned**; achievable in practice via
  `--bitrate-mode` plus explicit per-monitor values.
- Dynamic/adaptive bitrate mid-session — Moonlight already adapts; Lance sets the
  ceiling once at launch.

## 12. Implementation slices

Ordered so each lands reviewable and useful on its own. Sub-slice 3 depends on 2
(needs the merged options) and on 4 (needs the fps fallback). **All shipped.**

| # | Slice | Contents |
|---|---|---|
| 2.1 | **Rename + stale-flag cleanup** | `defaultFlags`→`defaultOptions` (hard, no guard); strip `--yuv444` / `--no-vsync` / `--bitrate 80000` from `ClientConfig.cs`, `samples/`, `dist/`; rename references in README, SPEC, ARCHITECTURE |
| 2.2 | **Per-monitor options** | `monitorOptions` config + `--monitor-options` CLI, keyed by id; the layer-merge in §3; dropped-monitor warning (§10) |
| 2.3 | **Refresh rate + generated `--fps`** | `dmDisplayFrequency` in `MonitorInfo`; Xrandr equivalent; `lance monitors` column; emit `--fps min(refresh, 60)` at layer 0. Linux rate deferred — `[DEFER-LINUX-REFRESH]` |
| 2.4 | **Auto-bitrate** | `bitrateMode` config + `--bitrate-mode` CLI; §4.2 precedence table; §4.3 derivation; clamp + warnings |
| 2.5 | **Monitor names** *(deferred — scheduled at the end)* | CCD API friendly names; key resolution rules (§8) |
| 2.6 | **Name references everywhere** | `--monitors` accepts names too; one shared resolver (`MonitorKey`) for all three references; duplicate detection moved after resolution |

## 13. Docs impact on acceptance

| Doc | Change |
|---|---|
| `ARCHITECTURE.md` | Connect flow step 5: the layer model; auto-bitrate as behavior; dropped-monitor warning |
| `SPEC.md` | "Moonlight launch": layer order + bits-per-pixel table + precedence table. "Config files": `defaultOptions`, `monitorOptions`, `bitrateMode`. "New CLI surface": `--monitor-options`, `--bitrate-mode`. `MonitorInfo` gains refresh rate (+ friendly name at 2.5) |
| `PLAN.md` | Phase 3 Slice 2 gains sub-slices 2.1–2.5 |
| `TODO.md` | P1 line points here; record the shipped `--yuv444` fix; add the T6 budget as backlog |
| `README.md` | Client config block (~L308) and the `--options` example (~L143) |
| `samples/lance.json`, `dist/client/lance.json`, `ClientConfig.cs` | New field names; drop the stale flags (§2) |

`CONVENTIONS.md` needs no change.

## 14. Decision log

| Id | Item | Outcome |
|---|---|---|
| T1 | Layer order; `defaultOptions` / `monitorOptions` rename | config → CLI, general → specific |
| T2 | `--bitrate-mode` single knob | `high`/`balanced`/`conservative`/`<number>`/`manual`; numeric guard 0.01–1.0 |
| T3 | Mode inference and precedence | §4.2 table; warn on every supersede |
| T4 | Default mode | `balanced`; existing configs infer `manual` and are unaffected |
| T5 | Clamps | auto modes only; fixed 60 fps ceiling |
| T6 | Session bandwidth budget | *Abandoned* |
| T6b | Refresh rate | fetched; feeds the math **and** a generated `--fps` |
| T7 | Per-monitor `--resolution` override | free via layer 0 |
| T8 | Config + CLI both exist and merge | subsumed by T1 |
| T9 | Key by id or name | ids at 2.2; names designed in §8, scheduled at 2.5 |
| T10 | Slot-to-monitor mapping | **withdrawn** — v1 was wrong |
| OPEN-1 | Rename migration | hard rename (prerelease); no guard, no shim |
| OPEN-2 | `--bitrate-mode` scope | global; outside the options lists; Lance strips nothing |
| OPEN-3 | Warn on fps > 60 in auto | yes |
| OPEN-4 | Emit `--fps` from refresh rate | **yes** — `min(refresh, 60)` at layer 0 (reversed; the clamp makes it safe) |
