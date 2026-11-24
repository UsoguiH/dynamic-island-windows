# Animations

How Bloub moves, and why. Read this before adding or changing any animation of his.

It has three parts: what Grok Bot does (measured), what the reference film does (measured), and the rules and per-state specs that follow from both.

Units: `R` is the body radius. Times are seconds unless marked ms. "Frame" means 1/60 s.

---

## 1. Sources

| Source | What it gave |
|---|---|
| xAI, "Designing Grok Bot for a world of persistent agents" (x.ai/news/designing-grok-bot) | The six states and what each should say. The page also ships the avatar's own script, which is where every Grok Bot number below comes from. |
| xAI, "Designing Grok Bot with Grok Bot" (x.ai/bot/guides/designing-grok-bot-with-grok-bot) | How they tune motion ("hold the look down-left longer", "bounce is too exaggerated"), a three-frame sheet of the promo move, a GIF of four avatars spinning. |
| github.com/jeremy-prt/bloub, `docs/measurements.md` | An independent frame-by-frame measurement of the same character. It agrees with the script (see 2.9). |
| `orb_aniamtionState.mp4` (project root, 60 fps) | Bloub's own reference film: Greeting, file, Error, Finished. |

Not available: the four videos on the guide page are blocked for download. The study is therefore of the script itself, replayed frame by frame (tables in 2.5 and 2.6), plus the frame sheet and the GIF. That is stronger evidence than video: the script is what draws every frame.

---

## 2. Grok Bot, measured

### 2.1 What xAI says the avatar must do

> "At rest, the Bot is calm and slightly curious. When work arrives, it acknowledges the task. As work begins, it kicks into gear. Its motion changes again when it is waiting or needs help, then settles once the work is done."

They rejected the "three animated dots" as the only signal (too little: working or stuck?) and a written step (people then want every step). The avatar's motion is the first reassurance; the current action is one hover away.

The lifecycle demo on their page plays: Idle 4.2 s, Working 4.6 s, Waiting 4.2 s, Blocked 4.2 s, Thinking 4.6 s, Done 4.2 s.

### 2.2 The engine in one paragraph

A body shape and two eyes. Every moving quantity is a spring that chases a target; the state only sets targets. Springs are stepped at 1/120 s whatever the screen rate, so motion is identical at 60 and 144 Hz. Nothing is a fixed-length tween except the blink keys, the hops and the spins.

### 2.3 The springs

`v += (-2·z·w·v - w²·(x - target))·dt; x += v·dt`

| Drives | w | z | 95% in | Overshoot |
|---|---|---|---|---|
| Tilt (degrees) | 5 | 0.9 | 0.81 s | 0.1% |
| Body x | 3.5 | 1 | 1.37 s | none |
| Body y | 4 | 1 | 1.20 s | none |
| Body scale | 10 | 0.8 | 0.34 s | 1% |
| Eyelids | 26 | 1 | 0.19 s | none |
| Eye size | 9 | 0.85 | 0.42 s | 0.3% |
| Gaze | 13 | 1 | 0.38 s | none |
| Eye pose blend | 7 (6 routine, 10 excited/searching) | 1 | 0.69 s | none |
| Morph in/out | 14 | 1 | 0.35 s | none |
| Morph to morph | 11 | 1 | 0.45 s | none |
| Spin | 6.2 | 1 | 0.77 s | none |
| Notification badge | 9 | 0.55 | 0.27 s | 12% |

The lesson: **the body is critically damped. It never overshoots.** Position is slow (over a second), scale and eyes are fast (a third of a second), lids are fastest. The only thing allowed to pop past its target is a small badge.

### 2.4 Easing curves they use

- cubic out `1-(1-t)³`: things arriving (radius, opacity).
- cubic in-out: shape-to-shape blends, one clean spin.
- back out `1 + 2.70158(t-1)³ + 1.70158(t-1)²`: only for small parts flying out to their place (10% overshoot).
- smoothstep: fades.

### 2.5 The blink

Lid targets: 0.05 at 0 ms, 0.05 at 70 ms, 1.08 at 150 ms, 1.0 at 300 ms, through the lid spring. One time in seven a second blink follows (shut at 370 ms, open at 480 ms).

What the lid actually does (replayed):

| ms | 0 | 33 | 67 | 100 | 133 | 150 | 183 | 217 | 250 | 283 | 317 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| open | 1.00 | 0.73 | 0.46 | 0.28 | 0.18 | 0.15 | 0.40 | 0.67 | 0.85 | 0.95 | 1.00 |

So a blink is 0.3 s, never fully hard-shut, slower to close (150 ms) than a tween would make it, and it opens without a visible bounce.

Blink interval by state: idle 6–14 s, thinking 3.5–7 s, working 2.8–5.5 s, searching 1.6–4 s, waiting 4–8 s, done 2.2–4.5 s. Busier states blink more.

### 2.6 Thinking: the avatar becomes three dots

This is the animation to match. In Grok Bot, thinking is not a face expression. The whole avatar turns into a typing indicator.

- The body shrinks to a dot (radius 0.193 R) on the morph spring. Its outline is a circle by 62% of the morph. The eyes are gone at 50%.
- Two more dots fly out sideways to ±0.543 R on the back-out curve, the right one 12% behind the left. They overshoot to 0.597 R and settle.
- A wave then runs left to right, 1.4 s per cycle. Each dot in its turn rises 0.079 R (0.41 of its own radius), swells from 0.84 to 1.06 of its size and brightens from 50% to 100%. The turn is a bell curve `exp(-d²/0.045)`, `d` being how far the wave is from that dot (in cycles), so each dot is lit for about a third of the cycle and they overlap softly.
- Leaving is the same spring back to zero: the dots return, the body regrows, the eyes come back.
- At small sizes (under about 44 px) the picture zooms 1.5× so the dots stay readable; at 134 px and above it does not zoom.

Entry, frame by frame (replayed at 60 fps):

| Frame | ms | Morph | Body size | Eyes | Left dot x / r | Right dot x / r |
|---|---|---|---|---|---|---|
| 0 | 0 | 0.00 | 1.00 | on | 0 / 0 | 0 / 0 |
| 2 | 33 | 0.11 | 0.91 | on | 0.23 / 0.06 | 0 / 0 |
| 4 | 67 | 0.28 | 0.78 | on | 0.47 / 0.12 | 0.35 / 0.07 |
| 6 | 100 | 0.44 | 0.64 | on | 0.58 / 0.15 | 0.54 / 0.12 |
| 7 | 117 | 0.52 | 0.58 | off | 0.59 / 0.16 | 0.58 / 0.14 |
| 9 | 150 | 0.64 | 0.48 | off | 0.59 / 0.17 | 0.60 / 0.16 |
| 12 | 200 | 0.78 | 0.38 | off | 0.57 / 0.17 | 0.58 / 0.16 |
| 18 | 300 | 0.92 | 0.27 | off | 0.55 / 0.17 | 0.55 / 0.17 |
| 21 | 350 | 0.95 | 0.24 | off | 0.55 / 0.17 | 0.55 / 0.17 |
| 30 | 500 | 0.99 | 0.20 | off | 0.54 / 0.17 | 0.54 / 0.18 |

Read it as: gone in 0.12 s, three dots by 0.2 s, settled by 0.35 s. Fast in, no bounce.

One wave cycle (lift in R / size / brightness):

| ms | Left | Centre | Right |
|---|---|---|---|
| 0 | 0.057 / 1.00 / 0.87 | 0.028 / 0.92 / 0.68 | 0.001 / 0.84 / 0.51 |
| 300 | 0.007 / 0.86 / 0.54 | 0.079 / 1.06 / 1.00 | 0.007 / 0.86 / 0.54 |
| 700 | 0.003 / 0.85 / 0.52 | 0.013 / 0.88 / 0.58 | 0.075 / 1.05 / 0.98 |
| 1200 | 0.078 / 1.06 / 0.99 | 0.005 / 0.85 / 0.53 | 0.009 / 0.87 / 0.56 |

### 2.7 The other states

Body numbers are spring targets. Lengths are in R (their unit is R/114); `t` is seconds.

| State | Body | Eyes |
|---|---|---|
| **Idle** | Tilt `1.5·sin(0.5t) + 0.6·sin(0.17t)`°. Drift x ±0.009 R, y ±0.011 R. Breath ±0.7%. | Looks straight ahead, re-aims every 2.5–5.5 s. Pose changes every 9–16 s. Follows the pointer up to 0.12 R sideways and 0.07 R up/down, catching up 16% per frame. |
| **Work arrives** (listening) | Tilts 8°, lifts 0.018 R, 1.5% larger. A nod every 1.8–3.2 s lasting 0.38 s. | Small gaze moves every 2.2–4.2 s. |
| **Thinking** | Three dots (2.6). | Hidden. |
| **Working** | A bob at 1.6 Hz: tilt 4° ± 2.5°, dips 0.026 R and squashes 2% on each beat. One full spin every 6–9 s. | Looks down at the work, re-aims every 1.2–2.4 s. Eyes soften toward the rest shape. |
| **Searching** | Sways ±13° at 0.2 Hz, sliding ±0.06 R with it. | Darts side to side every 0.55–1.15 s. |
| **Waiting** | Sits 0.044 R lower, 1% smaller, leans −3° ± 4° very slowly. A sigh every 4–7 s: 0.6 s, 5% larger, sinks 0.026 R. | Lids 60% open. Looks far to one side and down, re-aims every 3–6 s. |
| **Blocked** | Becomes the dot of an exclamation mark. The bar drops in from above and rattles ±2.2° at 6.7 Hz, dying out in half a second, again every 2.2 s. | Hidden. |
| **Done** | Nine fast turns over 5.5 s with comet trails, small hops of 0.022 R. Repeats every 6.2 s. | 10% larger, lids wide. |
| **Notifying** | A small kick upward and a blink. The badge pops in on the one bouncy spring. | 5% larger for a third of a second. |

Other morphs in the same engine: orbit (five dots circling in depth), radar (three rings), progress (a ring that fills), pencil (writes a line and lifts back), send, receive, upload, standby. Every one is the body shrinking to a small dot plus a few simple parts. No morph is a detailed picture.

### 2.8 Hops and the promo move

A bounce is four hops, each a parabola: heights 0.42, 0.25, 0.12, 0.05 R; lengths 0.50, 0.38, 0.27, 0.18 s (1.33 s in all). Height falls by about 0.55× each hop and time by the square root of that, which is what real bouncing does.

The promo move ("appears, notices the app window, bounces into place") is three poses: large in the middle already looking down-left at the window; small; seated in the window's corner. He looks first, then moves. xAI's two corrections to it were "hold the look longer" and "bounce is too exaggerated".

### 2.9 Cross-check

The independent measurement found: body never overshoots; the one spring is the badge's pop (they measured +14%, the script gives +12%); at rest the centre is still to ±0.003 R and life comes from gaze and blinks; every shape change hides behind a 0.2 s blink; eyes are decals on a sphere. All of it matches the script.

---

## 3. The reference film, measured

`orb_aniamtionState.mp4`: Greeting 0–2.9 s, file 2.9–4.6 s, Error 4.7–5.6 s, Finished 5.6–8.6 s.

- The film is drawn **on twos**: each pose is held two frames (sometimes one or three). In Finished, 42 of 98 frames repeat the one before. Replayed frame for frame on a large Bloub this reads as about ten frames a second. **Never replay film frames directly.** Turn the poses into keys and draw a curve through them (see 5.2).
- The eye roll at the start of Finished is an exponential ease-out with a time constant of about four frames (67 ms): 1.09 R of travel, 95% done in 0.2 s.
- His eyes are decals on a ball: they slide over the surface, narrow toward the edge and go out of sight over the rim. They do not shrink in place.
- The step colour lives only in the round light on his upper-left shoulder, with a gap cut round it.
- Hands are small balls of what he is made of. They come out from behind him and go back.
- A shape change is covered by a blink.

---

## 4. The rules

1. **The body never overshoots.** Critically damped springs or exponential ease-outs. No rubber, no wobble after landing.
2. **Fast in, calm after.** A change of state is over in 0.35 s. What follows is a slow loop, 1.4 s or longer.
3. **Eyes are fast, the body is slow.** Lids 0.2 s, gaze 0.4 s, size 0.35 s, position over a second.
4. **A shape change hides behind shut eyes.** Lids shut first (0.09 s), then he changes.
5. **Look, then move.** The gaze goes to the target before the body does, and holds.
6. **At rest he is still.** Life is gaze and blinks, not floating.
7. **Only small parts may pop.** A badge or a dot flying to its place may pass it by 10% and settle. Never the body.
8. **A state is the whole of him, not a prop beside him.** No thought bubble, no question mark over his head. Either his face says it or he becomes it.
9. **A morph is a dot plus a few simple parts.** If it needs detail to be read, it is the wrong morph.
10. **One idea per animation**, readable at 22 px and at 132 px.
11. **Smooth at any screen rate.** Motion is a function of time, never of frame number. Film poses become curves.
12. **Hops are parabolas** whose time shrinks with the square root of their height.

House rules that always apply: no border or ring on him, no "^^" eyes, no glints, no mouth, colour only in the shoulder light, keep the ✓ notch in the done celebration.

---

## 5. Bloub's states

### 5.1 Thinking: three dots (new)

Grok Bot's thinking, made of what he is made of. Built in `design/new-agent.html`, act `Think`.

| Time | What happens |
|---|---|
| 0–0.09 s | His lids shut. |
| 0.07 s | The morph starts (spring w 14, critically damped). |
| 0.07–0.25 s | He draws in toward a dot. Two more swell out of his sides. For a moment he is one body with two bulges. |
| 0.25–0.4 s | The bulges pinch off; a thread holds each, thins and lets go. His shut eyes fade out by the time the morph is half done. |
| 0.4 s on | Three balls, each lit as he is lit. The wave runs left to right every 1.4 s: rise 0.41 of a dot's radius, size 0.84 → 1.06, brightness 50% → 100%. |
| Leaving, 0–0.35 s | The two flow back into him and he grows to full size. |
| Leaving, 0.36–0.64 s | His lids open, 8% wide for an instant, then settle. |

Sizes: dot radius 0.25 R, spacing 0.66 R, so the three sit inside the space he filled. Under 44 px they are drawn up to 25% larger. Both side dots go out on the back-out curve, the right one 12% late, as in xAI's. The pinch uses the same joined-balls shape as the split launch, so the two animations speak one language.

### 5.2 Done: he flies to your pointer, backflips on the way, and the film's Finished plays

Real island: `DoneFlight.cs` (full-screen stage; Ctrl+Alt+G, Ctrl+Alt+D, `IslandLab.exe --done-demo`, and every session that finishes). No card opens in the island. Rebuilt 2026-11-24 over four rounds with the user: "very bad and not engaging", then a stronger Done with "a flip on his eyes from down to up", then "after he flip exactly like the done animation before", a better bubble animation, and the agent's own colour "so i can differentiate".

| Time | What happens |
|---|---|
| 0–0.11 s | In his seat he looks at your pointer (gaze first) and gathers himself (squash 0.8). |
| 0.11 s | He flies out fast on an arc (a quadratic Bézier bowed above the straight line, or out to his side when the way is straight down), 0.34–0.52 s by distance, eased 0.6·smootherstep + 0.4·linear (no jolt off the mark, still moving as he touches down), growing to radius 48 in the first 87% of the trip. His tail is Grok Bot's rainbow ribbons (the avatar's own trails from x.ai/news/designing-grok-bot, looked at in Playwright), laid behind him along the way he has come and never over him: four ribbons side by side, 0.26 R apart (a small gap between), thrown out one after another from under him 55–105 ms apart, each growing in over 0.34 s and fading in over 0.26 s; each 0.19 R wide at its head (in his radius as he is now) and half at its tail, round at both ends, smooth along his curve (Catmull-Rom through the last 0.26 s); coloured hsl(hue + a·span, 56%, 56→67%) along it, span 45–95°, the four a quarter of the wheel apart, drifting 18–42°/s; heads a little staggered under him. It comes out while he flies fast (> 420 px/s, in 0.35 s) and is drawn back in, shorter and thinner (×(1 − .72ret²)), over 0.5 s as he slows; each new flight throws a new one. Stretch along his speed, at most 13%. |
| touch-down A | He touches down 80 px short of his spot, on the side he comes from (squash 0.9, a glance down), and is on the ground only 0.08 s. His spot: below your pointer and to its left (to its right when his words would run off the screen), 66 px across and 70 px down from its tip, so it is never covered. |
| A+0.08 | The backflip, on the way: he springs up 0.95 R (a parabola over 0.5 s) and the flip carries him the last 80 px onto his spot. A real flip: his eyes go right round, up over the top, behind him and back to the front (pitch rising, cubic in-out over 0.62 s). No light, no sparks. |
| A+0.58 | He lands on his spot hard (squash) and bounces once, 0.16 R high for 0.21 s (rule 12). Then he follows your pointer (critically damped, w 9). |
| A+0.82 | A blink hands him to the film's Finished, played exactly as after a file: from its first frame, its own speed, its own paint (he eases from white to the film's grey over 0.3 s), its blue filling him from below (`DrawOut(wash: true)`), its light, its eyes (they shut and go down, then come up from under through the blue and look up, nod and blink). Not turned toward your pointer (`OrbFilm.Turn = false`): only mirrored when your pointer is on his other side. |
| Finished's eyes in | A drop of him buds off his rim on the side away from your pointer, on a thread (the paper.js metaball). The thread lets go at 0.2 s and the drop swells straight into the message in one critically damped move, turning to the bubble's white. Then the session's name (bold 17 px, full black, after a 9 px dot of its colour) and "I'm done" (15 px, 55%) fade in and rise 4 px, 0.08 s apart. A soft shadow under it, no outline. |
| + 1.75 s | He winks: the eye on the side of his words, shut 0.09 s, held 0.15 s, open 0.22 s. |
| + 4.6 s | The words go, the bubble folds to a drop and flows back into him on its thread, he swallows it (a little squish), looks home, crouches (0.88) and flies back on a curve (smootherstep, 0.52–0.76 s), shrinking, the light going out over the last stretch. The island catches him with a squish. |

His paint is one solid white, lit from the upper left (#FFF → #F5F5F8 → #CCCDD6), the same for every agent: the user tried him in each agent's colour and said "MAKE THEM ALL COLOR WHITE". The agent's colour is only the small dot before the session name. The user had called the old grey-bottomed shading "not solid".

Tried and dropped the same day: anything of ours on top of the film's Finished (white paint over it, no blue, entering it at frame 15, 1.25x speed, a wink, the light bursting with sparks); his eyes only sliding down in the flip ("not very fliping"); Finished's nod made 1.5x with a body squash ("exactly like the done from after adding file"); four thin separate rainbow lines; a white comet's tail; a yaw spin before the film; the flat film grey at the pointer; a ✓ inside the light; a bubble that opened width first like a loading bar; three typing dots before the message; a third line with the folder, time and what it did; a rainbow trail behind him (four bands, then one ribbon); Grok's ribbons whirling round his body (on him, not in the tail); three ribbons braided round his path ("DNA looking"); the session's little coloured Bloub in the bubble ("like before exactly").

### 5.3 The split: a new agent

He hops to the middle, shuts his eyes, draws out sideways to a thread and parts in two. The new one wakes, its light comes on in the agent's colour, it hops, crouches and leaps out through the top. He watches it go and hops home. Real island: `SendOff.cs` (grow 1.5, part at 0.92 s, born 1.34 s, home 2.94 s).

### 5.4 Still to bring up to this standard

Search (magnifier), Write (page), Wait (hourglass), Lock, Bell, Ask. Each should be re-checked against rules 8–10. Grok Bot's own answers for the same jobs: radar or orbit for searching, a pencil that writes one line for writing, a filling ring for progress, an exclamation mark for blocked.

---

## 6. Making a new one

1. Say the one idea in a sentence.
2. Pick the parts: his face only, or a dot plus at most three simple parts.
3. Timing from the tables above, not by feel: entry on the morph spring, loop no faster than 1.4 s.
4. Write it as a function of time.
5. Shoot it frame by frame in slow motion (the design page's `?slow=1`, in headless Chrome) and look at every frame.
6. Measure what can be measured (steps per frame, held frames) before calling it smooth.
7. Design page first, then the real island.
