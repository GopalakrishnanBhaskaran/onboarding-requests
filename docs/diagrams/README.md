# Diagrams

Excalidraw sources for the diagrams in `docs/ARCHITECTURE.md` (and its HTML copy). Drawn 24 Oct 2026.

| Source (edit this) | Image | Shows |
|---|---|---|
| `pmo-architecture-v2.excalidraw` | `pmo-architecture-v2.svg` | Section 2: components of the pilot, as deployed |
| `pmo-request-workflow-v2.excalidraw` | `pmo-request-workflow-v2.svg` | Section 4: the request workflow, step by step |
| `pmo-target-architecture.excalidraw` | `pmo-target-architecture.svg` | Section 12: target in the actual tenant (design, not built) |

Colours: blue = people and Microsoft services; yellow = processing (our compute, the AI agent); purple = data and private endpoints; green = approvals, completion or what changes; red = guardrails and stops; grey = neutral, notes, or (dashed) coded but off.

## Edit a diagram
1. Go to excalidraw.com (free, no account).
2. Menu (top left) → *Open* → pick the `.excalidraw` file. Every box, arrow and label can be moved and edited.
3. Save the source: menu → *Save to…* → overwrite the `.excalidraw` file.
4. Export the image: menu → *Export image* → *SVG*, background on, dark mode off → overwrite the `.svg` file.
5. Rebuild `docs/ARCHITECTURE.html` from `docs/ARCHITECTURE.md` with pandoc, with the SVGs inlined so the page works on its own.

No real data in diagrams: no user ids, tenant ids, passwords, TAPs or connection strings.
