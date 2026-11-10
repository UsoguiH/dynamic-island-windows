# Changelog

What changed in each version of Island. Installed copies update themselves, so you normally get these without doing anything.

## 0.2.0

**Install and update**
- A real installer: `Setup.exe` installs without admin rights, updates itself in the background and uninstalls cleanly. A portable zip is still there if you prefer it.

**New**
- **Describe a tab:** say what you want to keep an eye on ("bitcoin price with a chart", "today's Premier League matches") and Bloub builds a live tab for it, with pictures, charts or score rows.
- **Change it by asking:** on a tab you just described, say what should be different and the same tab comes back changed.
- **New agent form:** the task comes first in a box that wraps, the chosen project shows its full path, and the button says where the agent will run.
- **Tab library:** 17 tabs to pin, unpin and reorder, including Servers (every dev server on localhost) and Battery.
- A new intro, a new app icon, and Bloub says hello when you come back.
- The limits check shows its progress in a small toast.

**Smoother and lighter**
- The island redraws only what changed: about 20× less CPU and GPU when nothing moves, 2.5–4× less while animating.
- Network sampling and file writes moved off the drawing thread; text layouts are cached.
- Folded over a window, only the line itself opens the island.
- The island no longer hides when Start, Search or the notification centre open.

**Removed**
- The list view of the agent team (the grid and each agent's own screen do the same job).

## 0.1.0

First public release: the island and its spring motion, Bloub, the Claude Code agent team (live activity, plan, changed files, side questions), agents launched from the island with Allow / Deny, usage and plan limits, the "done" celebration, the tray icon, auto-retract and the demo mode (`--showcase`).
