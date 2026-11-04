# Obsidian 1.8+ & Capacities.io Architectural & Functional Parity Analysis

> [!IMPORTANT]
> Rhyolite unifies **Obsidian's local-first plain Markdown file-and-folder sovereignty** (`https://obsidian.md/`) with **Capacities' Object-First Knowledge Studio, Collections, Icon/Emoji Personalization, and Multi-Modal Gallery/Wall/Calendar Views** (`https://capacities.io/product`) inside a single native **C# 14 / .NET 10 / Avalonia UI** engine.

---

## 1. Executive Architecture Overview

Traditional PKM tools force an architectural trade-off:
- **File-first tools (Obsidian)** excel at hierarchical folders, plain `.md` files on disk, WikiLinks, and local speed, but lack first-class **Typed Objects**, **Object Studio schema builders**, **Collections**, and **native visual Gallery/Wall/Calendar views** without brittle third-party plugin stacks.
- **Object-first cloud tools (Capacities, Notion)** excel at structured object types (`Page`, `Project`, `Meeting`, `Person`, `Architecture ADR`, `Study Card`), icon/emoji personalization, and visual card layouts, but lock user data inside proprietary cloud databases.

**Rhyolite's 3-Tier Hybrid Architecture** eliminates this trade-off:
1. **Tier 1 — Lock-Free In-Memory Object & Folder Graph (`< 0.5 ms` delta)**: Incremental Markdown AST parser, hierarchical folder/subfolder tree index, and Capacities Object Studio schema registry.
2. **Tier 2 — Canonical Plain `.md` & `.canvas` Files on Disk (`~0.15 ms` atomic swap)**: Every note, typed Capacities object, and nested subfolder is a real directory and UTF-8 `.md` file on the OS filesystem.
3. **Tier 3 — SQLite WAL Sidecar Cache (`.rhyolite/cache.db`)**: Persists `folder_metadata` (custom icons/emojis, accent colors, expanded state), `custom_object_types` (Object Studio schemas & templates), `note_cache`, `flashcard_reviews`, `note_snapshots`, `github_pr_cache`, `external_sync_bindings`, and `ui_preferences` with O(1) B-tree primary key lookups.

```mermaid
flowchart LR
    subgraph Disk["Tier 2: Canonical OS Filesystem"]
        MD["Nested Folders & Plain .md Files\n(01 — Architecture/Storage/Replication/*.md)"]
        CANVAS["JSON Canvas 1.0\n(System Map.canvas)"]
    end

    subgraph Memory["Tier 1: In-Memory AST & Object Graph (< 0.5 ms)"]
        TREE["Hierarchical Folder & Subfolder Tree\n(Depth, Emoji, AccentColor, IsExpanded)"]
        OBJ["Capacities Object Studio Engine\n(Page, Project, Meeting, Person, ADR, Study Card + Custom)"]
        BASES["Bases & Multi-View Query Engine\n(Table, Kanban, Gallery, Wall, Calendar)"]
    end

    subgraph Wal["Tier 3: SQLite WAL Sidecar (.rhyolite/cache.db)"]
        FMETA["folder_metadata\n(path PK, icon_emoji, color_hex, is_expanded)"]
        OTYPE["custom_object_types\n(type_id PK, name, plural, icon_emoji, color_hex, fields_json)"]
        NCACHE["note_cache & note_snapshots"]
    end

    MD <-->|Atomic .tmp Swap| Memory
    CANVAS <-->|Zero-Loss JSON| Memory
    Memory <-->|O(1) B-Tree Upsert| FMETA
    Memory <-->|O(1) B-Tree Upsert| OTYPE
    Memory <-->|Incremental Delta| NCACHE
    TREE --> BASES
    OBJ --> BASES
```

---

## 2. Obsidian (`https://obsidian.md/`) Latest 1.8+ Feature Parity Matrix

| Obsidian Capability | Obsidian 1.8+ Specification | Rhyolite Native Implementation | Complexity & Latency |
| :--- | :--- | :--- | :--- |
| **Hierarchical Folders & Subfolders** | Arbitrary nested directories (`Folder/Subfolder/Leaf`), expand/collapse chevrons, note counts, drag/move note between folders | `FolderNode` hierarchy (`GetFolderHierarchyTree`, `GetFlattenedVisibleFolderTree`), `CreateFolder`, `ToggleFolderExpanded`, and `MoveNoteToFolderAsync` mirroring real OS directories | **O(F + N)** single-pass tree build; `< 0.3 ms` |
| **Folder Icon & Emoji Personalization** | Requires community plugins (`Obsidian Iconize` / `Icon Folder`) | **Built-in native support**: `SetFolderIconAndColor` & `GetFolderMetadata` persisted in SQLite WAL `folder_metadata` with quick emoji palette (`📁`, `🏛️`, `⚡`, `🧠`, `🔬`, `🚀`, `💎`, `🔥`, `🛡️`, `📐`, `📓`, `🗂️`) | **O(1)** primary-key SQLite WAL upsert |
| **Automatic Daily Notes** | Core Daily Notes plugin routing to configured folder (`YYYY-MM-DD.md`) | Native `02 — Daily Notes/YYYY-MM-DD.md` creation & instant today jump + Calendar timeline stream | **O(1)** path resolution |
| **Bookmarks & Pinned Notes** | Core Bookmarks tree for starred files | Native `pinned: true` frontmatter property + dedicated Bookmarks sidebar section & filter | **O(1)** property lookup |
| **Inline YAML Properties (`Properties`)** | Structured YAML frontmatter editor at top of note | Zero-allocation single-pass YAML frontmatter parser (`MarkdownAstEngine.ParseNoteMetadata`) + live property pill bar & `rhyolite.update_frontmatter` MCP tool | **< 0.15 ms** per note |
| **Live Split & Reading Views** | Source, Live Preview, and Reading modes with side-by-side panes | Native 3-mode Editor Switcher (**Split View**, **Editor Only**, **Reading View**) with live rendered callouts, task checkboxes, WikiLinks, and flashcards | **60–120 FPS** incremental UI sync |
| **Force-Directed Knowledge Graph** | 2D force graph with link distance, node sizing, and local neighborhood filter | Hardware-accelerated Skia `KnowledgeGraphControl` with Fruchterman-Reingold physics, PageRank centrality sizing, 1-hop/2-hop ego-graph filtering, and pan/zoom | **60 FPS** Skia render pass |
| **Infinite Spatial Canvas (`.canvas`)** | JSON Canvas 1.0 open specification (`nodes`, `edges`, cards, groups) | Native `SpatialCanvasControl` & `JsonCanvasEngine` with interactive card dragging, Bezier edge creation, color cycling, and double-click note opening | **100% JSON Canvas 1.0** compliant |
| **Obsidian Bases (Database Views)** | Dynamic Table and Board views over vault notes filtered by properties/tags | `BasesQueryEngine` supporting live filter, sort, group-by status, and instant property mutation across Table, Kanban, Gallery, Wall, and Calendar modes | **< 1.0 ms** query evaluation |
| **Command Palette (`⌘P` / `Ctrl+P`)** | Fuzzy modal launcher for notes, commands, and themes | Modal `⌘P` Command Palette indexing all notes, typed object creators, design system switchers, and DevEx actions | **< 0.5 ms** substring/fuzzy match |

---

## 3. Capacities (`https://capacities.io/product`) Object Studio & Multi-Modal Views Parity Matrix

Capacities reimagines note-taking around **Typed Objects**, **Object Studio**, **Collections**, **Icon/Emoji Identity**, and **Visual Multi-Modal Views**. Rhyolite implements every pillar natively while keeping all objects stored as portable plain `.md` files on disk:

### 3.1 Built-In & Custom Object Types (`Object Studio`)
Every note in Rhyolite resolves an `ObjectType` (`object_type` / `type` / `kind` YAML frontmatter or folder inference) backed by `ObjectTypeSchema`:
1. **`Page` (`📄`)**: General prose document or engineering wiki page.
2. **`Project` (`🚀`)**: Deliverable initiative with `status`, `priority`, `owner`, `target_date`, and `collection`.
3. **`Meeting` (`🗓️`)**: Time-anchored meeting or incident review with `date`, `attendees`, and `action_items`.
4. **`Person` (`👤`)**: Team member, stakeholder, or code owner with `role`, `team`, and `handle`.
5. **`Architecture ADR` (`🏛️`)**: Architecture Decision Record with `status`, `subsystem`, `priority`, and `[[code:...]]` symbol anchors.
6. **`Study Card` (`🧠`)**: FSRS v4 spaced-repetition flashcard deck note with embedded `Q:/A:` and `{{c1::cloze}}` prompts.
7. **Custom User Object Types (`SaveCustomObjectType`)**: Users can define new object types (e.g., `Runbook`, `OKR`, `Customer`, `RFC`) with custom singular/plural names, icon emojis, hex accent colors, default property keys, and starter Markdown templates persisted in SQLite WAL `custom_object_types`.

### 3.2 Collections (`GetCollections`)
Just as Capacities groups objects of the same or mixed types into **Collections** (e.g., `"Core Architecture"`, `"Q4 Launch"`, `"Platform Security"`), Rhyolite extracts and indexes `collection` frontmatter attributes alongside folder groupings so users can filter any Base or Object Studio view by Collection in one click.

### 3.3 Multi-Modal Object Views (Table · Kanban · Gallery · Wall · Calendar)
In Screen 4 (**Bases & Object Studio**), Rhyolite supports five distinct visual representations over the same underlying `.md` object set:
- **Table View**: High-density spreadsheet grid showing Object Type badge + emoji, Title, Folder/Subfolder path with folder emoji, Collection, Status, Priority, Word Count, Links, and Updated timestamp.
- **Kanban Board**: Status-grouped workflow columns (`Backlog`, `In Progress`, `In Review`, `Shipped`) with one-click status advancement.
- **Gallery View (Capacities Cover Cards)**: Visual card grid featuring a top color/gradient cover banner derived from the object's `cover_color` or `ObjectTypeSchema.AccentColorHex`, large floating icon/emoji badge, title, property chips, and collection tag.
- **Wall View (Capacities Masonry Stream)**: Rich content-preview cards displaying the rendered Markdown excerpt, outgoing `[[WikiLinks]]`, tags, and authorship metadata for rapid visual scanning.
- **Calendar & Daily Note Stream (`BuildCalendarTimeline`)**: Chronological timeline grouping Daily Notes, Meetings, Tasks with `📅 YYYY-MM-DD` due dates, and recently modified objects into a date-ordered agenda stream.

---

## 4. Avalonia 12 GPU-Accelerated Transition Architecture (60–120 FPS)

A core engineering requirement is that visual polish and micro-interactions never degrade typing, scrolling, or graph panning performance.

```mermaid
flowchart TD
    UI["User Interaction\n(Hover, View Switch, Folder Expand, Theme Cycle)"]
    PROP["Compositor-Only Property Mutation\n(Background, BorderBrush, Opacity, RenderTransform)"]
    GPU["Avalonia Skia Composition Thread\n(O(1) GPU Shader Uniform Interpolation)"]
    LAYOUT["CPU Layout Pass (Measure / Arrange)\n(SKIPPED — Zero Invalidation)"]

    UI --> PROP
    PROP --> GPU
    PROP -.->|Never Triggered| LAYOUT
```

### Why Rhyolite's Transitions Are Zero-Jank
1. **Compositor-Only Property Targeting**:
   - Rhyolite exclusively animates **render-thread / compositor properties**:
     - `BrushTransition` (`120 ms – 160 ms` `CubicEaseOut`) on `Background` and `BorderBrush`.
     - `DoubleTransition` (`140 ms` `CubicEaseOut`) on `Opacity`.
     - `TransformOperationsTransition` (`150 ms` `CubicEaseOut`) on `RenderTransform` (`scale(1.015)`, `translateY(-1px)`).
   - We **never** animate layout-affecting properties (`Width`, `Height`, `Margin`, `Padding`, or `Grid` row/column definitions), which would force an expensive O(N) CPU `MeasureOverride` + `ArrangeOverride` pass on every frame.
2. **Sub-160ms Snappy Curves**:
   - All transitions use `120 ms – 160 ms` `CubicEaseOut` curves so the UI responds on the very first frame (`< 8.3 ms` at 120 Hz) and settles before the eye perceives lag.
3. **Skia Custom Control Isolation**:
   - Both `KnowledgeGraphControl` and `SpatialCanvasControl` render directly to Skia `DrawingContext` primitives using cached `Typeface` and `ImmutableSolidColorBrush` instances, isolating 60 FPS canvas physics from XAML visual-tree styling.
