# Feature ideas (to build in a later session)

Saved 2026-11-10. Nothing here is built yet. Pick one and start a new session with it.

## The big bets (the "10k stars" list)

1. **The island becomes the screen every AI agent talks to.**
   Ship it as an MCP server, so any agent (Claude Code, Cursor, Codex, Gemini CLI, anything that speaks MCP)
   can call `island.ask`, `island.approve`, `island.progress` and `island.show_diff` with zero per-agent
   integration. The pitch: "agents have no face; this is the face". Every agent's community becomes the audience.

2. **Swarm mode: one sentence, a team of agents.**
   You type a goal and Bloub splits it. Five coloured Bloubs each take a piece in their own git worktree and
   work in parallel while you watch. When they finish you review each diff in the island and merge the best
   with one click. The island already launches agents and shows them as a team, so this is the natural step up,
   and it makes a ten-second video people share.

3. **Away mode: your agents never wait for you.**
   When an agent needs permission and you're not at the PC, the request goes to your phone. You approve, deny
   or answer by voice, and the agent continues. Stalled agents are the biggest daily pain of anyone running them.

4. **Talk to them.**
   Hold a key and say "tell the shark tank agent to run the tests and fix what fails". Bloub listens and routes
   it to the right agent. When the agent finishes, Bloub tells you the result in one spoken sentence.

5. **Mac and Linux.**
   Not a feature, but the ceiling: most developers who run AI agents and star repos are on a Mac. It means
   moving the core off Direct2D, which is a big rewrite.

**Recommendation:** make 1 and 2 the product ("the control room for AI agents"), add 3 as the feature people
tell their friends about, and plan 5 once the idea is proven on Windows. Voice (4) is the cheapest to demo
but the easiest for others to copy.

## The strong features (the "1k stars" list)

1. **Mission control for every AI agent.**
   Claude Code, Codex, Gemini CLI, Cursor and Aider all appear as Bloubs. Approve or deny any agent's
   permission request from the island, and when one finishes, type the next instruction right there.

2. **Ask the island (Ctrl+Space).**
   One line that understands plain language and acts: "what's using my CPU", "kill port 3000",
   "open yesterday's project", "summarise what my agents did today". Replaces the current command bar.

3. **Live Activities for anything.**
   One command, `island push "Deploying" --progress 0.4`, and any script, build, CI run, download or render
   becomes a live activity with progress and a done celebration. Ship ready-made ones for GitHub Actions,
   Docker and `npm` / `dotnet`.

4. **The shelf.**
   Drag files, images, text or a screenshot onto the island and it holds them; drag them back out anywhere,
   or drop them on an agent's Bloub to hand it the file as context.

5. **Rewind.**
   A private local timeline of your day: which agent changed which files, what you copied, what finished and
   when. Scrub back to "what was I doing at 3 pm?" and jump straight back into that project or session.

## Already rejected (don't propose again)

- Volume / brightness HUD and mixer.
- Playtime: Bloub jumping out to roam the desktop.
