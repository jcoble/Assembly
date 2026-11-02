# Hand-written fixtures

The recorded fixtures in `../acp/` come from real Claude turns on Workbox Test. Two shapes never showed up there:

- **Thinking.** Claude's adapter sent no `agent_thought_chunk`, even at high effort.
- **Plans.** This Claude Code has no todo tool, so the adapter never sent a plan update.

These two files are written by hand in the same transcript format, using ACP v2 chunks, `plan_update.plan`, message insertion responses and idle state updates. They are synthetic contract examples, not recorded provider evidence. Replace them with recordings once an agent sends them (Codex streams both). The recorded v1 files in `../acp/` remain pending re-recording by Engine plan 2 Task 5.
