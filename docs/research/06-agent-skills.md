# 06 — Agent skills: domain knowledge and packaging

> Source: Sonnet 5.5 research subagent, 2026-10-15.
>
> Key: **[V]** fetched this session · **[K]** background knowledge (well-known standards, not re-fetched) · **[U]** unverified.
>
> docs.mcneel.com returned 403, so Part A is mostly [K]. Check the code tables (DIN 18065, DIN 18101, ASR A2.3, IBC/IRC, NCS) against primary sources before shipping.

## Bottom line
- Ship **one Claude Code plugin** that bundles the MCP server (`.mcp.json`) and ~6 skills (`skills/<name>/SKILL.md`).
- Generate the matching MCP **instructions / resources / prompts** from the same markdown, so non-Claude clients get the same guidance.
- Keep each skill body under 200 lines. Put tables in `references/`.

## B. Packaging [V]
- **Format** ([overview](https://platform.claude.com/docs/en/agents-and-tools/agent-skills/overview)):
  - A skill is a directory with `SKILL.md` and frontmatter.
  - `name`: ≤64 chars, lowercase/digits/hyphens, and must not contain "claude" or "anthropic".
  - `description`: ≤1024 chars, saying what the skill does and when to use it.
- **Progressive disclosure:** metadata (~100 tokens, always loaded) → body (<5k tokens, loaded when the skill triggers) → bundled files (free until read). Script output enters context; script source does not.
- **Surfaces:**
  - Claude Code: `~/.claude/skills`, `.claude/skills`, or via plugins.
  - claude.ai / Desktop: zip upload, per user.
  - API: `/v1/skills`.
  - Skills don't sync between surfaces.
- **Plugins** ([docs](https://code.claude.com/docs/en/plugins)):
  - Layout: `.claude-plugin/plugin.json` + `skills/` + `.mcp.json` + `hooks/`.
  - Marketplace via `.claude-plugin/marketplace.json`; users run `/plugin install`.
  - Every enabled skill description sits in context every turn, so keep them tight.
- **Non-Claude clients** [K]:
  - Server `instructions`: a ~300-token "how to use this server" note.
  - Skill references → MCP resources.
  - Skill workflows → MCP prompts.
  - Fallback tool `guidance.get(topic)`.
- **Token efficiency:**
  - 30–60-word descriptions with trigger phrases.
  - "Cheap before expensive": units/tolerance/layers/counts → targeted act → numeric check → screenshot only for visual checks.
  - Prefer counts over lists. Batch work.
  - Say what *not* to load.
  - Document failure modes along with a cheap diagnostic for each.

## A1. Units & tolerances
McNeel guidance [V via search] ([R7 help](https://docs.mcneel.com/rhino/7/help/en-us/documentproperties/units.htm), [wiki FAQ](https://wiki.mcneel.com/rhino/faqtolerances), [thread](https://discourse.mcneel.com/t/tolerance-question/101616), [templates](https://discourse.mcneel.com/t/large-vs-small-objects-templates/178199)):
- Absolute tolerance is typically 0.01–0.0001 model units. Never go below ~1e-5; switch to smaller units instead.
- Tolerance should be ≥10× smaller than the smallest detail.
- Set tolerance once at project start.
- R7 template defaults: Large Objects 0.01, Small Objects 0.001, angle 1°, relative 1%.

Recommended starting points [K/U]:

| Use | Unit | Abs. tol. |
|---|---|---|
| Architecture | mm (EU) / m | 0.01–0.1 mm / 0.001 m |
| Urban/site | m | 0.01–0.001; use a local origin |
| Product | mm | 0.001 |
| Jewelry | mm | 0.001–0.0001 |
| 3D print / CNC / laser | mm | 0.01 |

**Failure modes:**
- Too loose: slivers, collapsed features, wrong booleans.
- Too tight: naked edges, failed intersections, bloated control points.
- Far-from-origin or extreme size ratios cause numerical problems.
- Imported files can carry a tolerance mismatch.
- Unit changes: "scale objects" and "don't scale" are different operations.
- Blocks and linked files carry their own units.

## A2. Geometry hygiene [K]
- **Diagnose:** `Check`, `SelBadObjects`, `ShowEdges` (naked/non-manifold), `SelOpenPolysrf`. API: `IsValid`, `IsSolid`, `IsManifold`, `GetValidityLog`.
- **Fix:** `Join`, `ExtractBadSrf` + `Rebuild`/`Patch`, `MergeAllFaces`, `UnifyNormals`/`Flip`, `RemoveAllNakedMicroEdges`, `ShrinkTrimmedSrf`, `Purge`, `Audit3dmFile`.
- **Booleans:** inputs must be closed and manifold with consistent normals. Avoid coplanar or coincident faces.
- **Rebuild vs FitCrv:** `Rebuild` changes shape and sets degree. `FitCrv` keeps shape within tolerance.
- **Degree:** 1 for straight edges, 2 for arcs, 3 by default; avoid anything above 5.
- **Mesh export:** set max edge length and edge-to-surface distance per process; check closedness.
- **Diagnostic ladder:** counts by type → bad-object count → naked edges per brep → `IsSolid` → only then fix.

## A3. Layer standards [K/U]
- **AIA / US NCS:** `Discipline-Major-Minor-Status`, e.g. `A-WALL-FULL`, `A-DOOR`, `A-GLAZ`, `A-ANNO-DIMS`, `A-ANNO-NPLT`, `S-COLS`.
- **ISO 13567:** agent / element / presentation fields. Heavier; offer it as an option.
- **UK BS 1192 → ISO 19650 + Uniclass 2015:** Role-Element-Presentation.
- **Germany:** DIN 1356-1 covers drawing content, not a layer scheme. Offices use house standards, translated AIA, or schemes keyed to Leistungsphase. Common names: `Wand`, `Decke`, `Stuetze`, `Fenster`, `Tuer`, `Treppe`, `Achsen`, `Bemassung`, `Text`. **Ask the user.**
- **Rhino practice:**
  - Sublayer trees such as `00_Model / 01_Make2D / 02_Annotation`.
  - Separate cut and projection layers.
  - Colour, linetype and print width set ByLayer.
  - Layer states for phases and sheets.
  - Store the chosen scheme in document user text (`rhino-mcp.layer-scheme`).
  - Never rename existing layers without confirmation.

## A4. Drawing standards [K]
- **Scales:**
  - Metric: site 1:500/1:1000; overview 1:200; plans 1:100/1:50; details 1:20/1:10/1:5/1:1.
  - Imperial: 1/16″ (1:192), 1/8″ (1:96), 1/4″ (1:48), 1/2″ (1:24), 1″ (1:12), 3″ (1:4).
- **ISO 128 pens:** 0.13 / 0.18 / 0.25 / 0.35 / 0.5 / 0.7 / 1.0 / 1.4 / 2.0 mm (steps of √2). Hierarchy:

  | Line | Width (mm) | Linetype |
  |---|---|---|
  | Section cut | 0.5–0.7 | continuous |
  | Beyond / elevation edges | 0.25–0.35 | continuous |
  | Projection | 0.18–0.25 | continuous |
  | Hidden | 0.13–0.18 | dashed |
  | Hatch / fine detail | 0.13 | continuous |
  | Grids | 0.13–0.18 | chain |

  Use lighter weights at small scales and heavier at large ones.
- **Linetypes:** continuous (visible), dashed (hidden), dash-dot (axes), long dash double-dot (cut lines).
- **Section hatches** (DIN 1356 practice):
  - concrete: aggregate pattern; reinforced concrete adds diagonals
  - masonry: 45° lines
  - insulation: zigzag
  - timber: grain
  - metal: dense diagonals
  - ≥1:200: solid poché
  - Rhino 7 ships few architectural hatches; use custom `.pat` files [U].
- **Text (printed height):** notes 2.5 mm (minimum ~1.8), labels 3.5 mm, titles 5–7 mm. Model height = printed height × scale denominator, e.g. 3.5 mm at 1:100 = 350 mm.
- **Dimensions:** DE uses ticks with text above the line, in m with a decimal comma or in mm. US uses feet-inches with fractions.
- **Paper:**
  - ISO: A0 841×1189 · A1 594×841 · A2 420×594 · A3 297×420 · A4 210×297 mm.
  - ANSI A–E, from 8.5×11 to 34×44 in.
  - ARCH A–E, from 9×12 to 36×48 in.
- **Title block** (ISO 7200): project/number, client, sheet title/number, scale, date, revision, author/checker, phase (HOAI LPH), north arrow, office, file name.

## A5. Typical dimensions [K/U — propose as defaults, state the region, prefer user values]

| Item | EU/DE | US |
|---|---|---|
| Floor-to-floor, residential | 2.75–3.0 m (clear ≥2.4–2.5 m) | 8–9′ clear |
| Floor-to-floor, office | 3.3–4.0 m | 12–14′ |
| Interior door | 0.885/1.01 × 2.01/2.135 m (DIN 18101) | 30–36″ × 80″ |
| Sill | 0.8–0.9 m | 30–36″ |
| Stair | rise 16–19 cm, tread 26–30 cm, 2r+g = 59–65 cm (DIN 18065) | IRC rise ≤7¾″, tread ≥10″; IBC rise ≤7″, tread ≥11″ |
| Corridor | 1.0–1.2 m (ASR A2.3) | 36″ residential, 44″ commercial |
| Accessible | DIN 18040: 90 cm door, 120 cm corridor | ADA 32″ door, 36″ route, 60″ turning circle |
| Structural grid | 6.0–8.4 m office, 5.4 m housing | 25–30′ |

## C. Proposed skill set
1. **`rhino-mcp-efficient-use`** (hub). Query → plan → act (batched, by GUID) → numeric verify → screenshot only when the check is visual. Covers token rules, undo discipline, a failure playbook and a tool cheat sheet.
2. **`rhino-units-tolerances`**. Checklist, tolerance table, origin/scale rules, import mismatch handling, never changing tolerance silently.
3. **`rhino-geometry-hygiene`**. Diagnostic ladder, repair recipes, degree guidance, mesh export presets, and a `diagnose` script returning compact JSON.
4. **`rhino-layer-standards`**. Detect or ask for the scheme and persist it. References for AIA/NCS, ISO 13567, UK ISO 19650 and German conventions. Includes an audit of non-conforming layers.
5. **`rhino-drawing-production`**. Views → clipping → clean Make2D → layers/weights → layout/detail scale → title block → plot. References for line weights, hatches, paper sizes and title blocks.
6. **`rhino-architectural-massing`**. Grid, levels, floor plates, area/GFA/FAR reports, a stair calculator. Region-aware defaults.
7. *(phase 2)* **`rhino-fabrication-export`**. Closedness, mesh density per process, DXF/DWG units and layers, pre-flight checks.

**Rules:**
- Keep trigger phrases distinct; skill 1 links to the others.
- Single-source the markdown and generate MCP resources/prompts from it.
- Add a small eval set per skill ([plugin evals](https://code.claude.com/docs/en/plugin-evals)).
