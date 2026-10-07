# Dynamic Island for Windows — Master Plan (v2)

> Working name: **Island** · Mascot: **Bloub** (MIT, github.com/jeremy-prt/bloub)
> Target machine: Windows 10 Pro 19045 · **1920×1080 @ 144 Hz**, single monitor · RTX 2070 Super
> Type: **native desktop app** (C# / .NET 10 + DirectX). No web view, no Electron, no browser.
> Goal: an iPhone-accurate Dynamic Island at the top of the screen, built for **desktop productivity**, where **Bloub *is* the island**.

### Decisions locked (from Q&A)
| Topic | Decision |
|---|---|
| App type | Native Windows desktop app (.exe), GPU-rendered |
| Screen | One monitor, 1920×1080, 144 Hz → render loop at the display's vsync |
| Features | Desktop-first productivity (§5), not a copy of the phone's features |
| Games | Fully hidden in fullscreen games; alerts are queued and summarized after you exit |
| Mascot | The island *is* Bloub: its body, eyes, moods and morphs |
| Windows OSD | Coexist: the native volume/brightness flyouts stay; the island adds its own |
| Top space (tabs/title bars) | **Auto-retract (default):** when a window is under the island, it folds into a thin line glowing in the live activity's color. Hover → pops out; click → opens the Full island; alerts pop out by themselves. *Reserved bar* (a 40px AppBar strip) stays available as a setting. |
| Click | Clicking the island always opens it (idle/line → Full; compact → Expanded). |
| Priority | **Animation and UI quality first.** Every UI piece is prototyped and shown to you before the real feature is wired. |

---

## 1. Design Principles

1. **One living creature.** The island is Bloub's body. It never "opens a window"; it stretches, squishes, buds off and merges back like ink.
2. **Black is the material.** A pure `#000` body reads as hardware. All content lives *inside* the black.
3. **Springs, never durations.** Every motion is a physical spring that can be interrupted and retargeted mid-flight while keeping its velocity. That is the core of the Apple feel.
4. **Glance → interact → work.** Each feature has 3 sizes: *compact* (glance), *expanded* (quick actions), *full* (a small workspace).
5. **Never steal focus, never block clicks.** Clicks pass through everywhere except the black shape. It takes keyboard focus only when you summon it.
6. **Keyboard-first.** It's a desktop: every feature is reachable by a global hotkey, not only by mouse.
7. **Invisible when it should be.** It's hidden in games and quiet in Focus.

---

## 2. Bloub = the Island (the core concept)

Bloub's engine describes its body as a **radial profile** (64 radii around a center) plus pose (offset, rotation, squash/stretch), and its eyes as **holes** in the body. We extend this so the **island shape and Bloub's body are the same black surface**:

| Island state | What Bloub is doing |
|---|---|
| **Dormant** | The island *is* Bloub: a small black capsule (≈ 120×34) with two eyes. It blinks, looks around, and follows your cursor when the cursor is near the top of the screen. |
| **Compact** | Bloub stretches sideways into a wider pill. Its eyes slide to the leading edge and shrink into a "mini Bloub" (the leading slot); content appears in the rest of the body. |
| **Split** | A second activity **buds off Bloub's side** as a droplet (gooey bridge, then a clean separation). Bloub glances at the bubble when it appears. |
| **Alert** | Bloub "inhales": the body drops down and widens with a bouncy spring, and its eyes react to the event (`notify`, `exclaim`, `alert`…). |
| **Expanded / Full** | The body becomes the panel; Bloub's face lives in the top-left corner (40–56 px) and reacts to everything you hover or click. |
| **Thinking / busy** | Bloub's body ripples or orbits while background work runs (`thinking`, `orbit`). |
| **Sleep** | The body shrinks to a small dark pebble, eyes closed, with slow breathing. |

Eyes on the island are **light cut-outs** (soft white `#F5F5F7` or an accent tint), since real holes would show the wallpaper.

### Bloub engine assets we reuse
- **15 animation states:** `idle, thinking, wink, wide, alert, notify, exclaim, sleep, egg, hexagon, play, orbit, swirl, burst, comet`
- **16 moods:** neutral, happy, excited, curious, proud, shy, angry, sad, scared, confused, hilarious, sleepy, suspicious, unimpressed, attentive, surprised
- **8 body skins:** capsule, circle, pebble, drop, hexagon, cloud, squircle, triangle
- Gaze tracking, blinking, and blending between states without jumps

Port: `src/bot/*.ts` (~2.7k lines, pure functions of time) → C# `Island.Mascot` with identical math. Its tests are ported too, so we can prove the C# output matches the original frame by frame.

### Event → Bloub mapping
| Event | State | Mood |
|---|---|---|
| Nothing happening | `idle` + blinks + gaze | neutral |
| Work running (build, export, AI answer, copy) | `thinking` | attentive |
| Long background task (download, sync, render) | `orbit` | neutral |
| New notification / message | `notify` | curious |
| Urgent (meeting in 1 min, disk almost full, mic live while muted) | `exclaim` | surprised |
| Error / failure | `alert` | sad / angry |
| Success (copied, saved, captured) | `wink` | happy / proud |
| Timer / focus session done, big task finished | `burst` | excited |
| Something arrives (file on Shelf, download done) | `comet` | excited |
| Music playing | `play`, bobbing to the beat (audio FFT) | happy |
| Mode/tab switch | `swirl` | — |
| Hover | `wide` | curious |
| Idle >5 min, night, Do-Not-Disturb | `sleep` | sleepy |
| Easter eggs (poke 7×, Konami code) | `egg`, `hexagon` | hilarious |

### Personality
- **The time of day sets the mood:** fresh in the morning, sleepy after midnight with yawns, and at 2 AM it nudges you to sleep.
- **The PC's health sets the mood:** a cool PC → happy; a hot GPU → sweating/scared; RAM full → unimpressed.
- **Productivity sets the mood:** proud after a long focus streak, and it gives you a disappointed glance when you open YouTube during a focus session.
- **Poking:** a double-click on the dormant island → giggle; on the 5th poke → annoyed.

> Bloub recreates the x.ai bot avatar. It's fine for personal use; if you ever publish the app, we customize it into your own character.

---

## 3. Animation Spec (Apple-exact)

### 3.1 Spring model
A real damped-spring integrator (analytic solution, stable at any `dt`), with SwiftUI-style parameters:
```
stiffness k = (2π / response)²            mass = 1
damping   c = 4π · dampingFraction / response
```
| Token | response | damping | Used for |
|---|---|---|---|
| `spring.island` | 0.50 | 0.78 | Shape width/height/radius on expand (≈3–4% overshoot) |
| `spring.islandSnappy` | 0.38 | 0.86 | Collapse (it collapses faster than it expands) |
| `spring.bounce` | 0.45 | 0.62 | Alert pop-in, bubble bud-off |
| `spring.content` | 0.42 | 1.00 | Content opacity/blur/scale (no overshoot) |
| `spring.press` | 0.25 | 0.90 | Press squish |
| `spring.gentle` | 0.70 | 1.00 | Ambient glow, dormant fade |

Each property (x, y, w, h, radius, blur, opacity, scale) has its own spring, so retargeting mid-animation is seamless. All values are tunable live in the prototype (§9).

### 3.2 Expand choreography
1. **t=0:** old content fades out: opacity→0, blur 0→8 px, scale→0.92.
2. **t=0:** the shape springs to the new size. Width leads and height lags ~30 ms, giving a "stretch then drop".
3. **t≈90 ms:** new content fades in: opacity 0→1, blur 10→0 px, scale 0.88→1, anchored at the top center.
4. **Squash & stretch** from the shape's velocity (liquid feel); Bloub's eyes lag slightly behind the body (secondary motion).
5. **Collapse:** the content leaves *first*, then the shape snaps closed.

### 3.3 Gooey split/merge
The black shape is rendered as a **signed distance field (SDF) in an HLSL pixel shader**. Each blob is a rounded rect with continuous squircle corners or a Bloub radial profile, and blobs are combined with **smooth-min** (k ≈ 10–14 px) so liquid bridges form when they're close. Edges are anti-aliased with `fwidth`, so they stay crisp. The same shader gives us click ripples, a "drip" when a file is dropped in, and Bloub emerging from the surface.

### 3.4 Micro-interactions
- **Hover:** a 1.02 "breath" immediately; Expanded after a 250 ms hover intent.
- **Press:** squish to 0.96, then a bouncy release.
- **Scroll on island:** volume (or scrubbing/timer minutes), with a rubber-band wobble at the limits.
- **Dragging a file toward the top:** the island *reaches* down toward the cursor (up to +14 px) and opens the Shelf.
- **Glow:** a soft halo under the island in the album/app color (25–35% opacity).
- **Digits roll** like iOS numeric transitions (timers, %, speeds).
- **Reduced motion:** honor Windows' "Show animations" setting.

### 3.5 Performance budget
- The render loop runs at **144 Hz vsync** only while something moves; when everything has settled, the loop sleeps (0 frames).
- Targets: **idle CPU ≈ 0%**, **< 60 MB RAM**, **< 1 ms GPU per frame**.

---

## 4. Island States & Sizes (1920×1080, 100% scale)

| State | Size (w×h) | Radius | Trigger |
|---|---|---|---|
| Dormant (Bloub) | 120×34 | capsule | Nothing happening |
| Compact | 260–360×36 | capsule | 1 live activity |
| Split | main + 36×36 bubble | capsule + circle | 2 live activities |
| Alert | 380–440×76 | 28 | Transient event, 2.5–4 s |
| Expanded | 420–480×170–210 | 38 | Hover / click |
| Full (Workspace) | 680–760×300–440 | 42 | Click while expanded, or `Win+Alt+Space` |
| Command bar | 600×54 → grows with results | 27 | `Alt+Space` |

Position: top center, 6 px from the edge (configurable; "flush notch" mode is optional).

---

## 5. Features — Desktop Productivity

**P0** = MVP · **P1** = v1 · **P2** = later. All of these respect the arbiter (§6).

### A. Focus & Time
1. **Focus sessions (Pomodoro / deep work) — P0.** Compact view: ring + rolling `mm:ss`. Optional **distraction blocking**: if you open a blocked app or site (YouTube, Reddit…) during focus, Bloub gives you the side-eye and the island shows "Back to work?" with a [Close it] button. End of session → `burst` + stats.
2. **Today's tasks — P0.** A tiny to-do list. Your *current task* stays pinned in the compact island ("✎ Finish report"). `Alt+T` adds a task; check it off from Expanded.
3. **Timers, stopwatch, alarms, natural-language reminders — P0.** Type `remind me in 20m to check the oven` or `t 10`.
4. **Time tracking (automatic) — P1.** Logs foreground app/window usage locally. The Full view shows today's timeline ("VS Code 3h12, Chrome 1h40, Discord 25m"), plus a weekly summary.
5. **Break & health nudges — P1.** The 20-20-20 eye rule, posture, water, and a breathing break where the island expands and contracts with your breath.
6. **Day progress — P2.** A thin progress line in Dormant showing how much of your workday is left; end-of-day review: "Done: 6 tasks, 3h focus."

### B. Capture & Clipboard
7. **Clipboard history — P0.** The last 50 items (text, images, files, colors), with pin, search, "paste as plain text" and snippets. A copied hex color shows a swatch in the island; a copied link shows the domain.
8. **Screenshot catcher — P1.** Catches `Win+Shift+S`/PrintScreen; the thumbnail flies into the island. Actions: Copy · Save · **OCR text** · Annotate · Drag out.
9. **OCR anywhere — P1.** A hotkey to select a screen region → text copied (uses the built-in `Windows.Media.Ocr`, offline). It also reads **QR codes**.
10. **Color picker / eyedropper — P1.** A hotkey with a magnifier loupe in the island; copy as HEX/RGB/HSL.
11. **Quick screen recording — P2.** Region or window capture (Windows.Graphics.Capture) with a red recording dot and a rolling timer in compact view, like iOS's screen-recording island.
12. **Quick notes — P1.** `Alt+N` → the island becomes a mini notepad (markdown saved to a folder you choose). You can drop selected text on the island to save it as a note.

### C. Windows & Workspace (desktop-only power)
13. **Window PiP (live mini-window) — P1.** Pin a live thumbnail of *any* window inside the island (DWM thumbnails, zero cost), e.g. watch a build terminal, a download, a stream or a game lobby while working. Hover to enlarge; click to jump to it.
14. **Workspace layouts — P1.** Save and restore window arrangements: "Coding" opens VS Code + browser + terminal in their exact positions; "Study", "Stream", etc. One click from the island or `Alt+1..9`.
15. **Window actions — P1.** Pin the current window always-on-top, make it transparent, center it, or send it to another virtual desktop. The active window's title/icon appears in Expanded.
16. **Virtual desktop indicator — P2.** On desktop switch, the island briefly shows the desktop's name and number.
17. **Recent files & quick folders — P1.** Recent docs plus pinned folders (Downloads, Projects…) in the Workspace view; drag out to any app.

### D. Files
18. **File Shelf — P0.** Drag any file to the top → the island reaches for it → it's held. Later, drag it out anywhere. A "pocket" for moving files between apps and folders.
19. **Shelf actions — P1.** Compress to ZIP, convert images (PNG/JPG/WEBP), resize, rename in bulk, copy path, **send to your phone over Wi-Fi** (LocalSend protocol, no cloud).
20. **Downloads tracker — P0.** Watches the Downloads folder (`.crdownload`, `.part`); progress ring in the bubble; when a download finishes, the file *drops* into the Shelf (`comet`). One click: open / show in folder / extract.
21. **Long file operations — P2.** Large copy/move progress and ETA.

### E. Communication & Schedule
22. **Notifications mirror — P1.** A single, beautiful notification stream (Discord, WhatsApp, Mail, Teams…) with per-app rules (allow / mute / Expanded-only). Needs package identity → we ship a sparse MSIX package.
23. **Calendar & meetings — P1.** Google Calendar / Outlook. Countdown in compact view starting 15 min before; at T-1 min, `exclaim` + **Join** (Meet/Zoom/Teams links). The Full view shows today's agenda as a timeline.
24. **Meeting mode — P1.** A global **mic-mute hotkey** with a big clear indicator in the island ("MUTED" red pill), plus camera/mic privacy dots (green/orange) showing *which app* is using them. Warns you if you're talking while muted.
25. **Discord voice — P2.** Who's speaking in your voice channel; mute/deafen buttons.
26. **Mail count — P2.** Unread Gmail/Outlook count in the bubble.

### F. Media & Audio
27. **Now Playing — P0.** Spotify, YouTube in the browser, any app that reports to Windows media controls (SMTC). Album art, live waveform (WASAPI loopback + FFT), scrubber, controls, glow tinted by the album colors.
28. **Audio device switcher — P0.** Speakers ↔ headset in one click (or a hotkey); per-app volume mixer in Expanded.
29. **Volume / brightness / keyboard-language / Caps Lock indicators — P0.** They coexist with the Windows flyouts. Monitor brightness is controlled over DDC/CI (scroll on the island while holding Shift).

### G. System & Hardware
30. **System monitor — P1.** CPU/GPU load and temps, RAM, VRAM, network up/down, disk. A sparkline mode is available in compact view.
31. **Resource hog alerts — P1.** "Chrome is using 7.2 GB RAM" → [Show] [End task]. "Disk C: < 10 GB" → [Clean up].
32. **Network status — P1.** Internet down/up alerts, ping indicator, VPN on/off, Wi-Fi/Ethernet changes.
33. **Devices — P2.** Bluetooth headphones/controller connect card with a battery ring; low-battery warnings for wireless mouse/keyboard.
34. **Game mode — P0.** Fullscreen games are detected → the island hides completely and alerts are queued. When you exit: "While you were gaming: 4 messages, download finished, 1 h 52 m played."

### H. Quick Tools & AI
35. **Command bar — P0.** `Alt+Space` → the island morphs into a search field. Apps, files (Everything SDK if installed), calculator, unit/currency conversion, timers, system commands (lock, sleep, restart, empty recycle bin), web search.
36. **Ask Claude — P1.** Prefix `?` to stream an answer inside the island (Bloub `thinking`). **Selected-text actions:** select text anywhere → hotkey → summarize / rewrite / translate / explain.
37. **Quick translate — P1.** Select text + hotkey → translation appears in the island (EN ↔ AR ↔ FR…).
38. **Macro buttons — P2.** A stream-deck-style grid in the Workspace view: run scripts, open URLs, toggle settings.

### I. Developer
39. **Local API + CLI — P1.** `island push --title "Build" --progress 40 --state thinking`: any script can create a live activity.
40. **Claude Code integration — P1.** Hooks report session state: "Claude is working…" (`thinking`), "Needs permission" (`exclaim` + Allow), "Done" (`burst`).
41. **Git / CI status — P2.** GitHub Actions results for your repos; PR review requests.

### J. Ambient & fun
42. Weather in Dormant (Bloub gets raindrops when it rains), daily greeting, easter eggs, seasonal skins.

---

## 5b. Researched Top 20 — Vibe Coder · Gamer · Productivity

★ = killer feature. These come on top of §5; where one deepens an existing item, it replaces it.

**Key enablers (verified in the Claude Code docs):**
- Claude Code hooks can be `type: "http"`: Claude Code POSTs JSON straight to the island's local API, so no scripts are needed.
- The `PermissionRequest` hook can **return** `allow`/`deny` plus `updatedPermissions`. Answering from the island is real two-way control.
- The statusline JSON exposes `rate_limits.five_hour/seven_day.used_percentage` + `resets_at`, `context_window.used_percentage`, `cost.total_cost_usd` and lines added/removed. The rate-limit fields are Pro/Max only.

### Vibe coder (Claude Code)
| # | Feature | What it does | Island & Bloub | Source/API | P |
|---|---|---|---|---|---|
| 1 ★ | **Approve/Deny from the island** | Permission prompt → the island expands with the tool + command and **Allow / Always / Deny** buttons (+ hotkeys). No alt-tab. | Red flash → expanded card · `exclaim` → `wink` on allow | `PermissionRequest` HTTP hook; `permission_suggestions` → `updatedPermissions` | P0 |
| 2 ★ | **Multi-session agent board** | One dot per running Claude session (repo, state, elapsed time, subagents); click to focus that terminal. | Dots in compact view, list in Full · `orbit` | Hooks keyed by `session_id`/`cwd`; `SessionStart/End`, `SubagentStart/Stop`; parent-PID → console window | P0 |
| 3 ★ | **Usage-limit & context gauge** | Ring for 5h/7d limits, "resets in 42m", context % per session, cost; warns at 80% context; pings when the limit resets. | Ring around the pill · `hexagon`, `sleep` when rate-limited, `swirl` on compaction | statusline JSON → island; `PreCompact/PostCompact` | P0 |
| 4 | **Turn-done digest + away escalation** | On turn end: a 1-line summary, lines changed, elapsed time. If you're away and Claude is waiting → push to your phone. | Toast · `notify` / `burst` | `Stop` (`last_assistant_message`), Notification `idle_prompt`; `GetLastInputInfo` | P0 |
| 5 | **Live activity ticker + failure radar** | "Editing Program.cs → running dotnet test", todo progress 3/7; a failed tool flashes red with the error. | Marquee in compact view · `thinking` / `alert` | `PreToolUse/PostToolUse`, `TaskCreated/Completed`, `PostToolUseFailure`, `StopFailure` (async hooks) | P1 |
| 6 | **Dev-server & port radar** | Detects running dev servers (vite :5173, dotnet :5000…) as chips: Open / Copy URL / Kill; alerts if one crashes. | Chip row in Expanded · `play` / `alert` | `GetExtendedTcpTable` → PID → process name + command line | P1 |
| 7 | **Build/test result card** | Pass/fail counts, duration, first failing test, whenever tests or a build run. | ✓/✗ badge · `burst` / `exclaim` | `PostToolUse` matcher `Bash` + output parsing | P1 |
| 8 | **Ship tracker (push → CI → deploy)** | Follows the pushed commit through CI and the deploy, then hands you the preview URL. | Progress comet · `comet` → `burst` | `gh run watch` / GitHub Actions API, Vercel/Netlify API | P1 |
| 9 | **Daily vibe log** | Per repo: what Claude built, cost, lines changed, time spent waiting on prompts. | Full view · `egg` | Stored `Stop` + statusline data per session | P2 |

### Gamer
| # | Feature | What it does | Island & Bloub | Source/API | P |
|---|---|---|---|---|---|
| 10 ★ | **Game update center** | Steam/Epic download, update and shader pre-cache progress, a "Ready to play" ping, NVIDIA driver updates. | Progress arc · `comet` → `play` | Steam `appmanifest_*.acf`, Epic `Manifests\*.item`, NVML driver version | P0 |
| 11 ★ | **Agents-while-gaming bridge** | Claude keeps working while you play: auto-allow safe read-only tools, hold the rest, push critical prompts to your phone; the post-game summary lists what the agents did. | Hidden → summary after the game · `sleep` → `wide` | `PermissionRequest` policy + game detector + phone bridge | P0 |
| 12 | **Free-games & wishlist-sale radar** | Weekly Epic freebies, Steam wishlist discounts. | Dot → cover art · `egg` | Epic `freeGamesPromotions`, Steam store API | P1 |
| 13 | **Post-game report card** | Play time, average and 1%-low FPS, peak GPU temp and power, clips saved, queued alerts. | Full view · `burst` | PresentMon (ETW), NVML, ShadowPlay/Captures folder | P1 |
| 14 | **Friends online & party ping** | "3 friends in CS2", one-click join; a ping when a favorite friend comes online. | Avatars · `notify` | Steam Web API (your key) | P2 |

### Productivity
| # | Feature | What it does | Island & Bloub | Source/API | P |
|---|---|---|---|---|---|
| 15 | **Long-command watcher** | Any terminal command > 30 s (winget, docker, ffmpeg) becomes a live activity with a done/failed ping + exit code. | Spinner · `thinking` → `burst`/`alert` | Windows Terminal shell integration (OSC 133) / PowerShell prompt hook | P0 |
| 16 | **OTP / 2FA code catcher** | Spots one-time codes in notifications → one-tap copy that auto-expires. | Big digits · `wink` | Notification listener + regex | P1 |
| 17 | **AI notification triage** | During focus/games/meetings, notifications are batched; Claude (Haiku) summarizes and ranks them, and only the important ones break through. | Count → digest · `notify` | Notification listener + Claude API | P1 |
| 18 | **Morning brief / shutdown ritual** | On wake: meetings, overnight agent results, usage reset time, free games, weather. At end of day: unfinished tasks → pick tomorrow's top 3. | Full view · `wide` / `sleep` | `WTSRegisterSessionNotification` + existing sources | P1 |
| 19 | **Phone bridge** | Two-way: island alerts reach your phone with action buttons (Allow/Deny Claude from the couch); phone battery and OTPs come back. | "Sent to phone" badge · `comet` | ntfy.sh (self-hostable) or KDE Connect | P1 |
| 20 | **System update center** | Pending winget upgrades, Windows Update "reboot required"; never during games, nudges you during idle time. | Badge · `egg` | `winget upgrade`, `RebootRequired` key, WUApiLib | P2 |

Sources: code.claude.com/docs/en/hooks · code.claude.com/docs/en/statusline · CodeIsland, Claude Island/Vibe Notch, Notchy (Mac agent-notch apps) · steam-appmanifest docs · Epic freeGamesPromotions.

---

## 6. Activity System (the brain)

```csharp
record Activity(
  string Id, string Module, int Priority,     // 0..100
  ActivityKind Kind,                           // Live | Alert
  TimeSpan? Ttl,
  MascotCue? Mascot,                           // Bloub state + mood
  Color? Accent,                               // glow tint
  IActivityViews Views);                       // Compact / Minimal / Expanded / Full renderers
```
**Arbitration**
- The highest-priority live activity → **Compact**; the 2nd → **Split bubble**; the rest → a list in Expanded.
- **Alerts** take over temporarily, then the island returns to the previous layout.
- Default priorities: Meeting now / mic live-while-muted 95 · Timer ending 90 · Alerts 80 · Privacy 75 · Focus session 70 · Downloads 60 · Media 50 · Current task 40 · System monitor 20.
- **Game running** → the island is hidden; everything is queued for the post-game summary.
- **Focus session** → only priority ≥ 80 interrupts; the rest become silent badges.

---

## 7. Technical Architecture (native)

### 7.1 Stack
| Layer | Choice | Why |
|---|---|---|
| Language/runtime | **C# / .NET 10** (LTS), `net10.0-windows10.0.19041.0` | Direct access to all Windows/WinRT APIs (media, OCR, notifications) and the best ecosystem for Windows utilities |
| Window | Raw **Win32** via `Microsoft.Windows.CsWin32` | Full control over transparency, topmost, click-through, no-activate |
| Composition | **DirectComposition** + DXGI flip-model swap chain with premultiplied alpha | True per-pixel transparency, vsync at 144 Hz, tear-free |
| Rendering | **Direct3D 11** (HLSL SDF island shader) + **Direct2D/DirectWrite** (content, text, icons, Bloub paths), via **Vortice.Windows** | GPU everything; we own every pixel, so the animation is exactly what we design |
| UI framework | Our own small **retained scene graph** (nodes, layout, hit-test, springs) | Apple-like motion is impossible to get exact through a stock UI toolkit's animation system |
| Fonts/icons | Bundled **Inter** (tabular numbers) + **Fluent System Icons** / Lucide paths | Windows 10 lacks Segoe UI Variable and Fluent icons |
| Audio | **NAudio** (WASAPI loopback, endpoint volume) + FFT | Waveform, volume events, device switching |
| Media | `Windows.Media.Control` (SMTC) | Now Playing for every app |
| Hardware | **LibreHardwareMonitorLib** (optional elevated helper for CPU temps) | Temps/loads |
| Brightness | DDC/CI via `dxva2.dll` | Desktop monitor brightness |
| OCR | `Windows.Media.Ocr` | Offline, built-in |
| Settings UI | Drawn by our own engine in the same style (a separate window) | Consistent look |
| Storage | JSON + SQLite (time tracking, clipboard) in `%APPDATA%\Island` | |
| Packaging | Self-contained exe + **sparse MSIX** package (for the notification listener), autostart | |

### 7.2 Window strategy
- One borderless window, `WS_EX_NOREDIRECTIONBITMAP | TOPMOST | TOOLWINDOW | NOACTIVATE`, fixed at the size of the **largest** state (≈ 800×480) at the top center. **It is never resized during animation**; only the shader's shape animates.
- **Click-through:** the window is `WS_EX_TRANSPARENT` by default. A 120 Hz cursor check against the live SDF shape flips it to interactive only when the cursor is over the black.
- Topmost is re-asserted on foreground changes (`SetWinEventHook`).
- Fullscreen game detection: `SHQueryUserNotificationState` (D3D fullscreen / busy) + a check for a foreground window that covers the whole monitor → hide.
- Hotkeys via `RegisterHotKey`; OLE drag-drop target for the Shelf (enabled when a drag enters the top zone).
- Per-monitor DPI v2 aware (ready for future scaling changes).

### 7.3 Render pipeline (per frame, only while animating)
1. Step all springs (`dt` from the vsync clock).
2. The island arbiter and layout resolve target geometry → shape blobs (rects/profiles).
3. **D3D pass:** a full-quad SDF shader draws the black body (+ glow, ripple).
4. **D2D pass:** content layers render into offscreen bitmaps → blur/opacity/scale effects → composited inside the body clip. Bloub's eyes are drawn last.
5. `Present(1)` on a waitable swap chain → DirectComposition.

### 7.4 Solution layout
```
Island.sln
├─ Island.App/            entry, tray, hotkeys, settings, single-instance
├─ Island.Platform/       Win32 window, DComp/D3D/D2D device, click-through, fullscreen detect, DPI
├─ Island.Motion/         Spring, SpringVector, choreographer, easing for reduced-motion
├─ Island.Render/         SDF shader (HLSL), scene graph, text, icons, effects
├─ Island.Mascot/         C# port of Bloub (profiles, states, face, gaze, expressions)
├─ Island.Core/           Activity, Arbiter, event bus, settings, storage
├─ Island.Modules/        Media, Audio, Focus, Tasks, Clipboard, Shelf, Downloads, Calendar,
│                         Notifications, SysMon, Network, Capture/OCR, Windows/Workspaces, CommandBar, AI
├─ Island.Lab/            Motion Lab: the prototype & tuning tool (§9)
└─ Island.Tests/          Spring math, Bloub port parity, arbiter rules
```

---

## 8. Visual Design System
- **Colors:** body `#000`; text `#FFF` / `rgba(255,255,255,.62)` / `.38`; fills `rgba(255,255,255,.10–.14)`; dynamic accent (album/app color, else your Windows accent). Semantic: red `#FF453A`, green `#30D158`, orange `#FF9F0A`, blue `#0A84FF` (Apple's dark-mode palette).
- **Type:** Inter with tabular numbers. Sizes 11 / 13 / 15 / 17 / 22; timer digits 30 semibold.
- **Corners:** continuous squircle everywhere, with inner radius = outer radius − padding.
- **Spacing:** 8-pt grid; padding 12 px (compact) / 18–22 px (expanded).
- **Progress:** 4 px rounded tracks; rings with a 3 px stroke and round caps.
- **Icons:** 1.75 px stroke, 18/20/24 px.
- The island stays black in both OS themes; only the glow and accents adapt.

---

## 9. Prototype-First Process (your priority)

Each feature goes **design → prototype → your review → implementation**. The tool for this is **Island Lab**: a real native app running the real engine over your desktop, with a side panel to trigger states and tune springs live.

| Prototype | What you'll see & judge |
|---|---|
| **P-1 Shape & springs** | Dormant ↔ Compact ↔ Expanded ↔ Alert ↔ Full transitions, interrupt mid-animation, live spring sliders |
| **P-2 Goo** | The bubble buds off and merges back (two activities); the shape reaching toward a dragged file |
| **P-3 Content choreography** | The blur/fade/scale of content during morphs, rolling digits, progress rings, marquee text |
| **P-4 Bloub** | The ported mascot as the island body: blinking, gaze following your cursor, all 15 states and 16 moods |
| **P-5 Screens** | Mock UIs of each feature: Now Playing, Focus, Tasks, Shelf, Clipboard, Command bar, Meeting mode, System monitor |
| **P-6 Real data** | The same screens driven by real Windows data |

**Status (2026-10-06):** Island Lab (`src/Island.Lab`, native C# + D3D11/D2D/DirectComposition) runs at 144 Hz.
- **Done:**
  - P-1 shape & springs
  - P-2 gooey split
  - P-3 content choreography
  - P-4 Bloub, ported 1:1 to C# with parity checked against the TypeScript (max error 7.6e-6); all 15 states, 16 moods and 8 shapes; Bloub gallery
  - Auto-retract top mode
  - Interactive workspace with 5 tabs
  - Full-screen welcome (replica of welcome-orb.mp4 with a lit 3D Bloub) that ends with Bloub flying into the island (Ctrl+Alt+W replays)
  - Agent team (replica of agent-widget.mp4): the dormant pill shows Bloub plus four coloured agent Bloubs. Hovering opens the team widget with grid, focus (live log, badge, glow), list and done/needs-you views (Ctrl+Alt+A)
  - Fixed half-drawn frames: D2D now renders offscreen and the finished frame is copied to the swap chain
  - The team is now your real Claude Code sessions. The data comes from Claude Code's own registry (`~/.claude/sessions/*.json`: busy/idle/waiting) plus each session's transcript (AI title, last prompt, tool calls turned into a live log). Nothing is added to `settings.json`.
    - "Open terminal" brings the session's window to the front.
    - Hovering the centre of the pill opens the dashboard; hovering the team opens the team.
  - Clicking Bloub (dashboard, media, or the team header) opens the team grid.
  - Agent card: click any agent to open it.
    - Shows what it's doing now, its task, and progress and stats.
    - One Activity feed (steps, replies, your messages; click a reply to copy it), with Plan and Files tabs that appear when there is something to show.
    - A needs-you card sits inside the feed.
    - Below: a message box and a few actions (Go to terminal or Stop, the editor, Folder, Close).
  - Usage tab (replaces Shelf):
    - Real plan limits (5-hour and weekly) come from Claude Code's `rate_limit_event`: from island agents for free, otherwise from a tiny Haiku probe (≈$0.001) at start, every 10 min, and every 2 min while the dashboard is open.
    - Banners at 80% and 95%.
    - Today: tokens, active time, files, commands, plus hour, project, week and model charts from `~/.claude/projects/**/*.jsonl`.
  - New intro ("born from the island"): Bloub peeks out of the island and drops onto the screen, growing as he falls. He lands with a squash, a ripple and dust, bounces, looks around, says hi in a speech bubble and leaps home. About 4 s (Ctrl+Alt+W replays).
  - Done celebration: when a session finishes and you're not looking at the island, Bloub leaps out and lands beside your cursor. You get a confetti burst in the agent's colour and a "✓ name is done" card; he hops twice, then flies home and the island shows the banner. At most one every 12 s; never during fullscreen games. Ctrl+Alt+D previews it.
  - Shared `StageScene`: squash and stretch, motion blur, shadow, confetti, dust, ripples, speech bubble.
  - Dev: `ISLAND_DEMO=focus:<n>[:plan|files]`, `team` or `new` opens a screen at startup (screenshots without driving the mouse).
  - Island agents ("+ New agent"): headless `claude -p` speaking stream-json with `--permission-prompt-tool stdio`.
    - Setup: pick a recent project, describe the task (or use a template), choose permissions (Your default / Ask first / Read-only / Auto edits).
    - From the island you can message them anytime, answer Allow/Deny, answer their questions (options become buttons), Stop, Restart, continue in a terminal (`claude --resume`) or Close.
  - Terminal sessions take side questions: a read-only fork (`--resume --fork-session --permission-mode plan`), so the original keeps working. Forks never leave plan mode.
- **Not done (needs your OK):** typing straight into a session already running in your terminal (console input injection) was blocked by the safety check. It would need an explicit permission rule from you.
- **Next:** P-6 SMTC media.

## 10. Roadmap
| Phase | Deliverable |
|---|---|
| **0. Motion Lab** | P-1 → P-5 prototypes; animation signed off by you |
| **1. Shell** | Production overlay: click-through, topmost, game hide, tray, autostart, settings |
| **2. MVP (P0)** | Arbiter + Now Playing + audio switcher + indicators + Focus/Tasks/Timers + Clipboard + Shelf + Downloads + Command bar + Game mode |
| **3. v1 (P1)** | Notifications, Calendar, Meeting mode, Screenshot/OCR/Color picker, Notes, Window PiP, Workspaces, SysMon, Network, Ask Claude, Translate, API/CLI, Claude Code |
| **4. Polish** | Per-app rules, themes and Bloub colors, sounds, reduced motion, performance pass, installer and auto-update |
| **5. P2 & fun** | Time-tracking insights, screen recording, devices, Discord, Git/CI, macros, easter eggs |
