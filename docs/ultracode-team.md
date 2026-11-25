# Ultracode agent team guidelines

Operating guidelines for choosing models and reasoning effort, and for building agent teams, when using Ultracode (workflows) or subagents in Claude Code. These are the rules used while building this launcher.

These rules apply even when Ultracode is on. Lines tagged [n] come from official Anthropic material (researched 2026-11-19, sources cross-checked); lines tagged **(rule)** are operating rules agreed with the user.

The goal is not the cheapest combination but the one that fits the job. Too much and too little are both wrong. Model and effort are chosen separately: the model by the *kind* of work, the effort by its *difficulty*.

- Every agent call must state both the model (`model`) and the reasoning effort (`effort`). Never let it silently inherit the session model.

### 1. Decide whether to use agents at all
- Start with the simplest approach; add complexity only when it falls short. [1]
- Multi-agent runs use about 15× the tokens of chat (a single agent about 4×). They fit when several independent directions must be explored at once. [2]
- If the work is a chain where each step depends on the previous one, or it fits in one context without a long cost tail, do it in the main session instead of splitting it. In official measurements (earlier models), splitting paid off in two cases: work that does not fit in one context, and easy work with a long cost tail. [3]
- Subagents do not give smarter answers. Their benefit is parallelism and an independent viewpoint. [4] Work that needs frequent back-and-forth stays in the main session. [5]

### 2. Choosing a model: the kind of work
| Model | Good fit (per official material) | Poor fit |
|---|---|---|
| Haiku 5.5 | High-volume or latency-sensitive work, classification, extraction, routing, summaries, narrowly scoped subagent tasks (collecting files, checking links) [6][7][4] | Complex agentic coding (Sonnet/Opus are better) [7]. Requests over 100k tokens cost 5× for both input and output, so avoid feeding large material wholesale [6][3] |
| Sonnet 5.5 | Well-scoped everyday work: bug fixes, writing code, documents and tables, data analysis, bulk work. Officially recommended as the default teammate [8][9][10] | Open-ended work that needs sustained judgment (Opus is clearly stronger) [8] |
| Opus 5.5 | The starting point for most work. Long agentic coding and knowledge work, large migrations and audits, complex design and multi-step reasoning [11][12][10] | **(rule)** Don't use it for well-scoped simple repetition when a smaller model is enough |
| Fable 5.2 | Highest capability. Move up to Fable when Opus at xhigh/max is still not enough [11] | **(rule)** Never for subagents. Only for the single task the user asked for, or that Opus failed |

- Adjusting effort is usually a better lever than switching models. [11] So the **(rule)** order is:
  1. Pick the model by the kind of work.
  2. Pick the effort by difficulty.
  3. If the result falls short, raise the effort one step for that task only. If that still fails, move up one model.
- Reference figures (official internal measurements; read for direction only) [3]
  - Opus 5.5 (medium) with Haiku subagents: about 32% lower cost, about 4 points lower pass rate. A comparable alternative to lowering effort, not a better one.
  - For large defect hunts that exceed one context, an Opus lead with Sonnet subagents cost about a third less than Opus alone with similar scores.
- **(rule)** "It's more capable" is not a reason. Move up only when you can say why a smaller model or lower effort won't do.

### 3. Choosing effort: the difficulty of the task
In Claude Code all four models default to medium (API defaults differ: Sonnet and Fable default to high). The same effort name means different actual strength on different models. [13]

| Effort | When to use (per official material) | Notes |
|---|---|---|
| low | Fast back-and-forth where each result is checked (brainstorming, drafts, small changes such as renames) [13] | Saves many tokens but reduces capability somewhat [14] |
| medium | Well-scoped everyday work [13] | Default |
| high | Work where verification matters or edge cases abound (e.g. fixing bugs in existing code), complex reasoning, hard coding/agent tasks [13][14] | |
| xhigh | Long agentic/coding tasks over 30 minutes [14] | Sonnet runs several self-reviews at xhigh/max, costing more time and tokens [3]. For Opus, xhigh scored about +1.4 over high at 2.5× the cost [3]. **(rule)** Use for Sonnet/Haiku only when high falls short; almost never for Opus |
| max | Hard problems to be solved without a human [13] | Diminishing returns and a tendency to overthink [13]. In one evaluation (FrontierCode) Sonnet scored lower at max than at xhigh [8]. **(rule)** Only for the single task the user asked for, or that failed at xhigh |

- Thinking is billed as output tokens, and output costs 5× input. [15][16]
- On the API, changing effort between requests can break the cache. [15] **(rule)** So avoid changing it within a conversation; change it at task boundaries.
- A subagent on a different model from the session does not inherit the session's effort. [5] **(rule)** So always state both model and effort when spawning.

### 4. Building a team
- Size: one agent for simple research, 2–4 for comparisons, 10+ only for complex research. [2] Start teams at 3–5; three focused agents often beat five scattered ones. [18]
- Shape: pick from a lead that hands out work, independent parts run in parallel, or a build–evaluate loop when evaluation criteria are clear. [1]
- The official examples use a higher-tier model for the lead and Sonnet for workers. [2][10]
- Review is done by a fresh agent that did not produce the result. [4]
- Instructions must include the goal, output format, material to look at, and the boundaries of the task. Keep results short; leave long results in a file and return only its location. [2][5]
- End finished teammates immediately; they keep using tokens while alive. [10]
- Worktrees: two agents editing the same file overwrite each other. Split files between agents, and give each its own worktree if they must edit concurrently. [18][19] Choose model and effort by the same rules.
- **(rule)** Judge the lead (the main session) by the same standard. If its model or effort is too much or too little for the job, propose a change first and switch only after approval.

### 5. Report and approve before spawning (rule)
- Before spawning, scout the work yourself, make a task list, and spawn only as many agents as needed. If a few tool calls will do, do it yourself without agents.
- **Always report to the user and get approval before spawning.** This applies to workflows and subagents alike, one or many. Never spawn without approval. The report is a table containing:
  - For each role: what it does, headcount, model, effort
  - For each role: why this model and why this effort (never omit the reason)
  - Total agent count and rough scale
- Always present the team as one clean table, with columns in this order: stage | task | headcount | model | effort | reason, one role per row. Add one or two lines below the table with the total headcount and the order of work. Don't write long role descriptions outside the table.
- If the approved model, effort or headcount must change, report the reason and get approval again before changing it.
- An approval covers only the work in that report. The team for the next stage is reported again.
- Verification is one agent per item by default. Use several only where a mistake would be costly.
- When done, report the number of agents used and their models in one line.

### Sources
[1] https://www.anthropic.com/engineering/building-effective-agents
[2] https://www.anthropic.com/engineering/multi-agent-research-system
[3] https://platform.claude.com/docs/en/about-claude/models/optimizing-for-cost-and-intelligence
[4] https://code.claude.com/docs/en/best-practices
[5] https://code.claude.com/docs/en/sub-agents
[6] https://platform.claude.com/docs/en/models/haiku-5-5/overview
[7] https://www.anthropic.com/claude-haiku-5-5
[8] https://www.anthropic.com/claude-sonnet-5-5
[9] https://platform.claude.com/docs/en/models/sonnet-5-5/overview
[10] https://code.claude.com/docs/en/costs
[11] https://platform.claude.com/docs/en/about-claude/models/choosing-a-model
[12] https://www.anthropic.com/claude-opus-5-5
[13] https://code.claude.com/docs/en/model-config
[14] https://platform.claude.com/docs/en/build-with-claude/effort
[15] https://platform.claude.com/docs/en/build-with-claude/thinking-steering-and-cost
[16] https://platform.claude.com/docs/en/about-claude/pricing
[18] https://code.claude.com/docs/en/agent-teams
[19] https://code.claude.com/docs/en/worktrees
