---
name: rhino-architectural-massing
description: Build and check building massing and architectural layouts in Rhino. Use for levels, column grids, floor plates, walls, slabs, openings, stairs, facade grids, area schedules, GFA, FAR, or floor-to-floor heights. State the assumed region (EU/DE or US) and propose defaults to confirm.
---

# Architectural massing

Work from intent: levels, grid, plates, then openings and stairs. Use the `arch_*` operations (M3, check `tools/list`) and fall back to `geometry_*` plus short `run_python` snippets. All code-related values below are defaults to propose, not law: **verify against local code**.

## 0. Region first

State the assumed region in your first message ("Assuming EU/DE, metric: tell me if US/imperial"). Ask when the project country is unknown. Use region-appropriate defaults from [references/dimensions-eu-us.md](references/dimensions-eu-us.md). Prefer values the user gives over defaults.

## 1. Setup

1. `context_get` (units, tolerance). Architecture: mm or m (see `rhino-units-tolerances`).
2. Layer scheme (`rhino-layer-standards`).
3. Origin: put the building near the origin; keep the site georeference in document user text.

## 2. Levels and grid

- `arch_levels`: levels registry with names and elevations (for example EG/OG1 or L0/L1), relative to a stated datum. Elevation table: level, elevation, floor-to-floor.
- Floor-to-floor defaults by use (see reference): residential about 2.75 to 3.0 m, office about 3.3 to 4.0 m (EU); US 8 to 9 ft clear residential, 12 to 14 ft office floor-to-floor. Clear height = floor-to-floor minus slab and finishes.
- Structural grid: office 6.0 to 8.4 m, housing around 5.4 m (EU); 25 to 30 ft (US). Choose grid and storey height together with the user.
- Tag objects with `ai.level` user text (via `objects_set_props`) so queries by level work: `objects_query` with `user_text`.

## 3. Intent operations

| Intent | Tool | Post-condition to verify |
|---|---|---|
| Wall from centreline and thickness | `arch_wall` | Length, height, volume = length x thickness x height |
| Floor plate from outline | `arch_slab` | Area equals `measure` area; thickness |
| Door or window opening | `arch_opening` | Opening size, sill height; wall volume decreased by expected amount |
| Stair between levels | `arch_stair` | Rise, going, count, rule check (below) |
| Facade subdivision | `arch_facade_grid` | Panel count, unique panel count, planarity deviation |

If an `arch_*` tool is not available: `geometry_create` (extrusion of an offset curve for walls, planar surface plus extrusion for slabs), `geometry_boolean` for openings. Check every result numerically before moving on.

## 4. Stair calculator

Given floor-to-floor height H and maximum rise r_max: number of risers n = ceil(H / r_max); actual rise r = H / n; going g from the rule.

**Stair rule 2r + g = 59 to 65 cm** (DIN 18065 practice: verify), rise 16 to 19 cm, tread 26 to 30 cm (EU). Example: H = 3000 mm, r_max = 190 mm gives n = 16, r = 187.5 mm, so g = 625 - 2r = 250 mm, which is below the 260 mm minimum tread. Use n = 17 instead: r = 176.5 mm, g = 625 - 353 = 272 mm. US: rise up to about 7.75 in (IRC) or 7 in (IBC) and tread at least 10 in (IRC) or 11 in (IBC): verify against local code.

Total run = (n - 1) x g. Report rise, going, n, run, headroom (at least about 2.0 m EU: verify).

## 5. Area schedules

`area_schedule` (M3) groups `AreaMassProperties` by `ai.space` (and by `ai.level`). Without it, tag rooms as closed planar curves or surfaces on a `Areas` layer, then `measure` area grouped by user text.

- **GFA (Bruttogeschossflaeche, gross floor area):** sum of the areas enclosed by outer wall faces per level. DE uses DIN 277 (BGF, NUF, NRF) and WoFlV for residential: ask which; definitions differ by country: verify.
- **FAR / GFZ / GRZ:** FAR = GFA / site area. State which GFA definition you used.
- Report as a small table: space, level, area m2, count, target, delta. Do not list all rooms if there are more than 30: group by use.

## 6. Verification

1. `clash_check` between walls, slabs and columns of the same level.
2. `measure` volumes and areas against expectations.
3. Section check: one `view_capture` of an elevation or section only after numbers pass.
4. Accessibility spot checks (door 90 cm clear, corridor 120 cm, turning circle 150 cm in DE DIN 18040; ADA 32 in door, 36 in route, 60 in turn): verify local code.

Return to `rhino-mcp-efficient-use` for the general loop.
