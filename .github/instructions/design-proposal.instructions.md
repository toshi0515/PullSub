---
description: "Use when proposing software design, architecture options, class structure, dependency relationships, or implementation plans. Hard rule: design proposals must be written in Japanese and follow a concept-to-detail flow with Mermaid dependency diagrams."
name: "Design Proposal Structure"
---
# Design Proposal Structure

- This is a mandatory rule for design proposals in this workspace.
- Write design proposals in Japanese.
- Start with conceptual design before detailed design.
- Explain the design in a way that is detailed and easy to understand.
- State each class responsibility explicitly. Do not leave role boundaries implicit.
- Include a Mermaid diagram that shows dependency relationships between classes/components.
- After the conceptual section, provide a concrete detailed design section.

## Required Output Order

1. Conceptual Design
2. Detailed Design
3. Risks and Open Questions

## Conceptual Design Requirements

- Describe the goal, scope, and non-goals.
- Explain major components and their responsibilities.
- Describe key interactions and data flow at a high level.
- Keep this section implementation-light.

## Detailed Design Requirements

- Break down concrete classes/modules and their public responsibilities.
- Clarify lifecycle, state transitions, and error handling where relevant.
- Include key interfaces/method signatures when needed.
- Map conceptual components to concrete code-level structures.

## Quality Bar

- Avoid vague summaries; prefer concrete and verifiable statements.
- If assumptions are needed, list them explicitly.
- If trade-offs exist, state alternatives and why one was chosen.
