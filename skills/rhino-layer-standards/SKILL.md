---
name: rhino-layer-standards
description: Detect, choose, apply and audit layer naming schemes in Rhino (AIA/US NCS, ISO 13567, UK ISO 19650, German office conventions, or a custom JSON scheme). Use when organising layers, setting up a new project, auditing non-conforming layers, preparing drawings or exports, or asked about layer standards.
---

# Layer standards

Layer structure drives drawing output, plotting and exports. Decide the scheme once, persist it, apply it consistently.

## Workflow

1. **Detect.** `layers_list` (tree with counts). Look at the existing names: `A-WALL-FULL` style means AIA/NCS; `00_Model`-style trees mean an office convention; German words (`Wand`, `Decke`) mean a German house standard.
2. **Read the stored choice.** Check document user text key `rhino-mcp.layer-scheme`:

   ```python
   import scriptcontext as sc
   result = sc.doc.Strings.GetValue("rhino-mcp.layer-scheme")
   ```

   If present, use it and stop asking.
3. **If absent or unclear, ask the user** which scheme applies. Offer: AIA/NCS, ISO 13567, UK ISO 19650, German office convention, or custom JSON. Mention the project country and the recipient (consultant, client, authority). Do not guess for deliverables.
4. **Persist** the answer (only after the user confirms):

   ```python
   import scriptcontext as sc
   sc.doc.Strings.SetString("rhino-mcp.layer-scheme", "AIA-NCS")
   ```

   Use a short id (`AIA-NCS`, `ISO-13567`, `UK-ISO-19650`, `DE-OFFICE`, or the custom scheme `name`).
5. **Audit before changing.** `layers_audit` reports layers that do not conform. Present counts and a few examples, not the full list.
6. **Apply.** `layers_apply_scheme` with the scheme id or a custom JSON (format in [references/scheme-format.md](references/scheme-format.md)). Prefer creating missing layers and moving objects over renaming.
7. **Verify.** `layers_audit` again; `layers_list` counts should match the totals from before (no objects lost).

## Rules

- **Never rename or delete existing layers without explicit confirmation.** Renaming breaks references from the user's other files, scripts and plot settings. Propose a mapping table first (old to new, with object counts).
- Colour, linetype and print width set ByLayer. Objects should not carry overrides unless the user wants them.
- Separate layers for cut and projection geometry, for annotation, and for non-plotting helpers. Mark helper layers non-plotting.
- Keep a phase tree if useful: `00_Model`, `01_Make2D`, `02_Annotation`.
- Layer states for phases and sheets are better than duplicating layers.
- Non-ASCII layer names (German umlauts) work in Rhino but break some exports. When exporting to DWG for others, prefer ASCII (`ae`, `oe`, `ue`) unless the recipient wants otherwise.

## Scheme references

| Scheme | When | File |
|---|---|---|
| AIA / US National CAD Standard | US projects, many international consultants | [references/aia-ncs.md](references/aia-ncs.md) |
| ISO 13567 | Public clients and projects requiring ISO CAD layering | [references/iso13567.md](references/iso13567.md) |
| UK BS 1192 to ISO 19650, Uniclass 2015 | UK projects | [references/uk-iso19650.md](references/uk-iso19650.md) |
| German office conventions | Projects in Germany, Austria, Switzerland | [references/de-conventions.md](references/de-conventions.md) |
| Custom JSON | Anything else, or a client template | [references/scheme-format.md](references/scheme-format.md) |

Codes are quoted from general knowledge. Where a precise code matters (contract deliverable), ask the user for the client's current standard and verify.

Return to `rhino-mcp-efficient-use` for the general loop.
