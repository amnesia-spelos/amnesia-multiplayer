# Domain Docs

How engineering skills should consume this repository's domain documentation.

## Before exploring, read these

- `CONTEXT.md` at the repository root.
- Relevant ADRs under `docs/adr/`.
- For work involving the Game Interaction Protocol or its implementation, also read `../amnesia-tdd-tcp/CONTEXT.md` and relevant ADRs in that repository when the sibling checkout is available.

If these files do not exist, proceed silently. The domain-modeling skill creates them lazily when terminology or decisions are resolved.

## File structure

This is a single-context repository:

```
/
├── CONTEXT.md
├── docs/adr/
└── src/
```

## Use the glossary's vocabulary

When output names a domain concept—in an issue title, proposal, hypothesis, or test—use the term defined in `CONTEXT.md`. Do not drift to synonyms the glossary explicitly avoids.

For concepts owned by `amnesia-tdd-tcp`, follow that repository's domain language rather than redefining it locally. Document multiplayer-specific interpretations or integration concepts in this repository.

If a required concept is absent, reconsider whether it belongs to established language or note the gap for domain modeling.

## Flag ADR conflicts

Explicitly surface output that contradicts an existing ADR instead of silently overriding it. Check both this repository and `amnesia-tdd-tcp` when a decision crosses their boundary.
