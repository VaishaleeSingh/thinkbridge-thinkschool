# Day 22 — Capstone kickoff: design + scaffold (task prompt)

## Task, as given

> **Capstone kickoff: design + scaffold**
>
> Pick a real product slice. Design it as a modular monolith (clean
> architecture by default — not microservices), scaffold the solution
> structure, and write the one-page design: bounded contexts, the core
> aggregate, and the async flows.

## Exercise, as given

> Paste the repo URL + the one-page design (contexts, aggregate, async flows)
> and the scaffolded solution layout.

## What the task is asking for, read carefully

Four deliverables, and the parenthetical in the first line is doing real work:

1. **A real product slice** — one workflow end to end, not a whole product and
   not a toy.
2. **A modular monolith, clean architecture, explicitly NOT microservices.**
   The instruction is worth taking literally: one process and one database,
   with the module boundaries drawn inside it. Splitting into services at
   kickoff would mean paying for network hops, distributed transactions and
   independent deployments before anyone knows where the boundaries actually
   belong.
3. **A scaffolded solution structure** — the project graph and the dependency
   rules, not the feature set.
4. **A one-page design** naming three specific things: bounded contexts, the
   core aggregate, the async flows.

What it does **not** ask for: working endpoints, a UI, persistence, or
migrations. Those start on Day 23. "Kickoff" is the word that sets the scope.

## Answers

- The one-page design: [`capstone-design.md`](capstone-design.md)
- The submission, including the layout and the repo URL:
  [`day22-capstone-submission.md`](day22-capstone-submission.md)
- The layout and how to run it: [`../README.md`](../README.md)
