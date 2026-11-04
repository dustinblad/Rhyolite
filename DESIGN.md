---
product: Rhyolite
version: "0.1.6"
architecture: "Local-First Hybrid Markdown (.md) + SQLite WAL + Capacities Object Studio"
platforms: ["macOS", "Windows", "Linux", "iOS", "Android"]
breakpoints:
  mobile: "390px - 767px"
  tablet: "768px - 1199px"
  desktop: "1200px - 2560px+"
sidebar:
  ribbon_width: "42px"
  left_explorer_width: "268px"
  right_inspector_width: "280px"
  tree_row_height: "28px"
  tree_indent_step: "16px"
motion:
  hover_duration: "140ms"
  easing: "CubicEaseOut (0.22, 1.0, 0.36, 1.0)"
  compositor_only_properties: ["Opacity", "RenderTransform"]
---

# DESIGN.md — Rhyolite Design System & Folder/Workspace Architecture

> Structured following the [AI UX Playground `create-design-md` Specification](https://aiuxplayground.com/prompts/create-design-md) and benchmarked against **Obsidian 1.8**, **Capacities.io**, **Craft.do**, **Bear**, **Apple Notes**, and **Linear**.

---

## 1. Purpose and Scope
This document is the canonical source of truth for Rhyolite's visual hierarchy, folder/file navigation architecture, Capacities Object Studio layouts, and multi-design token system (`feimacode/open-design-agent-kit`). Every UI view, sidebar tree, and component must adhere strictly to the rules and anti-patterns defined here.

---

## 2. Product and User Principles
- **Target Users**: Staff/Principal engineers, systems architects, researchers, and knowledge workers who demand local file sovereignty (`.md` + `.canvas` on disk) paired with structured object thinking.
- **Main Jobs to Be Done**:
  1. Navigate nested folders, subfolders, and notes effortlessly without visual clutter.
  2. Personalize folders, subfolders, and typed objects (`ADR`, `Project`, `Meeting`, `Daily Note`, `Study Card`) with meaningful icons/emojis and color accents.
  3. Switch seamlessly between prose writing (Source / Split / Reading), spatial thinking (Graph / Canvas), and structured databases (Capacities Gallery, Wall/Kanban, Bases Table, Calendar).
- **Core Design Principles**:
  1. **Calm Spatial Hierarchy over Toolbar Clutter**: Never stack multiple competing lists or permanent form inputs inside narrow sidebars.
  2. **Unified Tree Mental Model (Obsidian + Craft + Notion)**: Subfolders and notes belong *inside* their parent folders in a single unified tree with subtle vertical indentation guide lines — never split into a separate "Folders" list and a disconnected "Notes" list in the same column.
  3. **Progressive Disclosure**: Creation forms (New Folder/Subfolder) and customization palettes (Folder Icon/Emoji Picker) appear in a clean collapsible popover/drawer when invoked, staying tucked away during normal reading and browsing.
  4. **Zero-Layout-Thrash Motion**: Animate only GPU compositor properties (`Opacity`, `RenderTransform`).

---

## 3. Visual Hierarchy and Layout Rules

### 3.1 Desktop 3-Pane Master-Detail Shell (`1440×900` Standard — Mirroring Reference Architecture)
```text
+-----------------------+----------------------------+--------------------------------------------------------------+
| 205px                 | 285px                      | * (Fluid Right Editorial & Workspace Stage)                  |
| Pane 1: Navigation,   | Pane 2: Middle Note Feed   | Top Minimal Bar: Folder Dropdown · Add Tags · View Icons · •••|
| Quick Links, Folders, | Pill Search + Circular (+) | Hero Banner / Templates Pill                                 |
| Subfolders, Tags &    | "All notes ▾" Heading      | ● Colored Dot + Large Display Title                          |
| User Profile Footer   | Colored Dot Note Cards     | Editorial Body + Embedded Attachment Card + Bottom "/" Bar   |
+-----------------------+----------------------------+--------------------------------------------------------------+
```

### 3.2 Three-Pane Master-Detail Architecture (Reference Screenshots 1, 2 & 3)
1. **Pane 1 — Left Navigation, Folders & Tags Sidebar (`205px`, deepest surface)**:
   - **Top Header**: macOS window traffic lights (`🔴 🟡 🟢`) or Workspace Avatar (`P` / `R` badge + `"Private"` / `"Rhyolite"`), plus subtle sync `⟳` and collapse `<` buttons.
   - **Section 1: `Quick links`**:
     - Active item renders with a **full-pill `CornerRadius="999"` solid blue/accent background (`#0D6EFD` / `AppleAccentBrush`) and white icon + text**.
     - Items: `📄 All notes`, `🔔 Reminders`, `☑️ Tasks`, `♡ Favorites`, `🖊️ Highlights`, `🕒 Activity` (with red notification dot), `🔍 Saved search` (`Test search 12`, `My notes 10`).
   - **Section 2: `Folders`** (header row with `Folders` on the left, sort `≡` and `+` button on the right to toggle the Folder & Emoji Studio popover):
     - Hierarchical folder and indented subfolder tree with colored folder icons/emojis and muted `direct/total` note counts (`4/6`, `3/19`, `2/345`, `1`, `52`):
       - `🏛️ 01 — Architecture  4/6`
         - `💾 Storage  1/1` (indented subfolder)
         - `🔌 Protocols  0/0` (indented subfolder)
       - `📅 02 — Daily Notes  1/1`
       - `🧠 03 — Study & Research  1/1`
       - `🚀 04 — Capacities Studio  0/2`
         - `🚀 Projects  1/1` (indented subfolder)
         - `🤝 Meetings  1/1` (indented subfolder)
   - **Section 3: `Tags`** (header row with `+` button): `🏷️ Design 12`, `🏷️ Architecture 6`, `🏷️ Screenshots 250`.
   - **Section 4: `Trash`**: `🏷️ Silver / Snapshots 28`.
   - **Bottom User Profile Footer**: Avatar circle + `"Dustin Blad"` / `"Personal"`.
2. **Pane 2 — Middle Note List / Feed Column (`285px`, mid-tone surface)**:
   - **Top Search & Quick-Create Row**: Pill-shaped Search input (`🔍 Search` with filter icon `≡`) + **Circular Accent `+` Button** (`30×30px`, `CornerRadius="999"`).
   - **Feed Header**: `All notes ▾` (or active folder/filter title).
   - **Note Feed Cards**:
     - Selected note card has a crisp `1px` border (`#3E3E44` dark / `#CBD5E1` light) and elevated surface fill; unselected cards have subtle `1px` bottom dividers.
     - **Line 1**: `9px` colored status/category dot (`●` green, orange, purple, red, blue) + **Bold Note Title**.
     - **Line 2**: Date (`18/11/2026`) + single-line excerpt preview (`TextTrimming="CharacterEllipsis"`).
     - **Line 3**: Tinted rounded reminder/metadata pill (`🔔 13 sep 15:00` / `🔔 20 FEB 15:00` in translucent blue `#1E3A5F` / `#38BDF8`).
3. **Pane 3 — Right Main Editorial Studio & Multi-Screen Stage (`*` width)**:
   - **Top Minimal Header Bar (`42px`)**:
     - Left: `📁 My notes / Folder ▾` + `🏷️ Add tags` + compact screen/theme controls.
     - Right: Minimal action icons (`📎` Attach · `☑️` Tasks · `🔔` Reminder · `🔗` Share · `⤢` Focus/Split · `•••` More Actions menu trigger).
   - **Floating `•••` Note Context Menu Popover (`IsNoteMenuOpen`)**:
     - Mirrors Reference Screenshot 1: `♡ Add to favorites`, `📌 Pin note`, `🎨 Set color`, `⛶ Focus mode` (with toggle switch), `⚙️ Appearance`, `🔍 Find in note`, `📁 Move to`, `📋 Copy to`, `💬 View resolved comments`, `ℹ️ Note info`, `↩ Undo`, `↪ Redo`, `🗑️ Delete`.
   - **Editorial Note Canvas**:
     - Optional **Hero Cover Banner** (Screenshot 1) or top-right `✨ Templates` pill (Screenshot 3).
     - **Colored Accent Dot + Large `26px` Display Title** (`● Fusion energy` / `● Hybrid Storage Engine`).
     - Generous editorial prose with inline highlight spans, embedded diagram/visual preview, bordered **Attachment / Google Drive / SQLite Sidecar Card**, and bottom slash-command bar (`Type "/" or add  ☑️  ☰  🔢  ⊞  🖼️  📎  |  ⌄`).

---

## 4. Token and Visual-System Guidance
Rhyolite supports **9 switchable design systems** from `feimacode/open-design-agent-kit`, each updating 25+ `DynamicResource` tokens at runtime:
- **Typography Tokens**: `AppleUiFont`, `AppleDisplayFont`, `AppleMonoFont`, `AppleHeadingFontWeight`, `AppleBtnFontWeight`, `AppleBtnFontSize`.
- **Button Geometry & Bevel Tokens**: `ApplePrimaryBtnCornerRadius` (`0px` Vercel to `999px` Stripe), `AppleSecondaryBtnCornerRadius`, `ApplePrimaryBtnBorderThickness` (`1,1,1,3` Raycast 3D keycap, `1,2,1,1` Linear top-lit, `1,1,1,2` GitHub Primer), `ApplePrimaryBtnShadow`.
- **Card, Input & Pill Tokens**: `AppleCardCornerRadius`, `AppleCardBorderThickness`, `AppleCardShadow`, `AppleInputCornerRadius`, `ApplePillCornerRadius`.

---

## 5. Component Selection and Usage Rules
- **Primary Button (`Classes="apple-btn-primary"`)**: Maximum **1–2 primary buttons per toolbar region** (e.g., `+ Note` in sidebar, `Save` in editor bar, `+ Create Object` in Object Studio).
- **Secondary / Ghost Tree Row**: Folder and note rows inside the Unified Explorer Tree must use a borderless transparent background at rest, revealing a subtle surface tint on `:pointerover` and `AppleCalloutBgBrush` when `:selected`.
- **Capacities Folder Cards (in Screen 4 Object Studio)**: Display a top horizontal strip of **Subfolder Cards** showing each folder's assigned emoji (`🏛️`, `💾`, `🔌`, `🚀`, `🤝`), name, subfolder path, and item count so users can navigate folders visually inside the main stage as well as the sidebar.

---

## 6. Interaction States and Behavior
- **Folder Row Click**: Selects the folder (`SelectedFolderNode`), expands/collapses its children (`ToggleFolderExpandCommand`), and filters the Object Studio view when Screen 4 is active.
- **Note Row Click**: Opens the note immediately in the editor (`SelectedNote`) and updates breadcrumbs (`🏛️ 01 — Architecture › 💾 Storage › 📄 SQLite WAL Indexer`).
- **Emoji Assignment**: Clicking any emoji in the Folder Studio Drawer immediately updates `folder_metadata` in SQLite WAL and refreshes both the Unified Explorer Tree and the Breadcrumb bar in `< 1 ms`.

---

## 7. Content and Microcopy Guidance
- Keep sidebar labels to **1–3 words** (`EXPLORER`, `OBJECT TYPES`, `BOOKMARKS`).
- Never repeat folder paths on note rows that are already visually nested inside their parent folder in the tree.

---

## 8. Accessibility Requirements
- Minimum `4.5:1` contrast ratio for body text and `3:1` for active folder/note selection borders across all 18 Light/Dark palettes.
- Keyboard shortcuts: `⌘P` / `Ctrl+P` (Command Palette), `⌘S` / `Ctrl+S` (Save Note), `⌘N` / `Ctrl+N` (New Note), `Esc` (Close modal/drawer).

---

## 9. Responsive Behavior
- Collapsible Left Explorer (`◧`, `268px` ↔ `0px`) and Collapsible Right Inspector (`◨`, `280px` ↔ `0px`) allow distraction-free full-bleed writing or 3-pane studio layouts.
- Dedicated Mobile adaptive view (`390px` viewport) with bottom navigation and 1-tap Daily Note capture.

---

## 10. AI-Specific UI Patterns
- Local Vault RAG Copilot lives in the Right Inspector, citing exact `[[WikiLinks]]` from the vault.
- Headless `--mcp-stdio` JSON-RPC 2.0 server exposes vault tools to external AI agents in `< 0.5 ms`.

---

## 11. Anti-Patterns and Prohibited Choices
- ❌ **NO Split "Folders List" + "Notes List" in the Same Sidebar Column**: Never render a flat list of folders above a separate flat list of notes. Always use the **Unified Hierarchical Explorer Tree** where subfolders and notes are nested directly inside their parent folders.
- ❌ **NO Multi-Line Card Clutter in File Trees**: Tree rows must be single-line (`28px` height) with clean ellipsis truncation. Do not stack subtitles and word counts under every tree item.
- ❌ **NO Permanent Form/Emoji Strip Clutter**: Keep the Folder Creation & Emoji Picker inside the collapsible `IsFolderStudioOpen` drawer (or inside the dedicated Folder Studio header in Screen 4) so the default sidebar remains clean.
- ❌ **NO Duplicate Design-System Button Rows**: Put the Design System selector (`◀ [ComboBox] ▶`) in the top tab bar rather than adding a third full-width toolbar row of 9 duplicate buttons across the editor header.

---

## 12. Canonical Design References
1. **Obsidian (`https://obsidian.md/`)**: Left ribbon, unified folder/subfolder/note tree with vertical indentation guides, multi-tab header, inline YAML `Properties`, backlinks inspector, bottom status bar.
2. **Capacities (`https://capacities.io/product`)**: Object Types sidebar filter, custom folder/object icon & emoji identity, Object Studio schema builder, Collections, and 4-mode visual views (Gallery cover cards, Wall/Kanban, Table, Calendar).
3. **Craft.do & Bear**: Generous whitespace, single-line tree typography, progressive disclosure of folder management controls.

---

## 13. Preflight Checklist Before Generating UI
1. Are subfolders (`💾 Storage`, `🔌 Protocols`, `🚀 Projects`, `🤝 Meetings`) and notes (`📄 Storage Architecture`) rendered inside a single unified tree with depth indentation and vertical guide lines?
2. Are all tree rows single-line and scannable without horizontal clipping?
3. Is the top header consolidated into at most 2 clean bars (Tab Bar + Breadcrumb/Mode Bar)?

---

## 14. Final UI QA Checklist
1. Verify all 12 rendered mockups (`docs/mockups/01_*.png` through `12_*.png`) via visual inspection.
2. Confirm zero text overlap, zero clipped buttons, and clear visual hierarchy in both Light and Dark themes across all 9 design systems.
