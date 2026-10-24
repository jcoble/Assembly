# Hand-written fixtures

The recorded fixtures in `../acp/` come from real Claude turns on Workbox Test. Two shapes never showed up there:

- **Thinking.** Claude's adapter sent no `agent_thought_chunk`, even at high effort.
- **Plans.** This Claude Code has no todo tool, so the adapter never sent a `plan` update.

These two files are written by hand in the same transcript format, using ACP v1's shapes for those updates. Replace them with recordings once an agent sends them (Codex streams both).
