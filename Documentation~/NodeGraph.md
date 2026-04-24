# SCAD Node Graph — Architecture

Visual, Shader-Graph-style authoring for OpenSCAD geometry inside Unity.
This document is the reference for the `Editor/Graph/` module: data model,
compilation pipeline, asset format, editor UX, and the node catalog.

![Node graph produced by reverse-importing a full .scad file](images/node-graph-car.jpg)

Pictured: the graph produced by reverse-importing the `car.scad` sample —
every user-defined module inlined, every `for`-loop unrolled, every
`color()` / `translate()` / primitive materialised as a typed node.

## Goals

- **Visual authoring** — build SCAD geometry by composing typed nodes.
- **Parametric & composable** — graph-level exposed parameters; nested
  subgraphs reused as nodes.
- **Type-safe connections** — ports typed (Number / Vector / Boolean /
  Solid / Shape / etc.) with explicit coercion rules.
- **Integrated with existing pipeline** — reuses `ScadLiveCompiler` for
  async OpenSCAD invocation; `.scadgraph` imports to `Mesh` just like
  `.scad`.
- **Editor UX** — open a graph, add nodes, connect, save; live preview
  (debounced) of the resulting geometry.

## Non-goals (initial phase)

- Full SCAD language coverage (user-defined `module`/`function`, list
  comprehensions, recursion). Surfaces as escape hatches ("Custom
  Expression" nodes) instead.
- Sub-second live editing. OpenSCAD compile time is the floor; UX
  follows the same debounced model as the Live Preview window.

## SCAD source import

Forward direction is still the primary path (graph → scad), but a subset
reverse importer is included:

- `Editor/Graph/Import/ScadSourceParser.cs` — hand-written lexer +
  recursive-descent parser over assignments, module calls, and simple
  arithmetic expressions (+, -, *, /, %, unary minus).
- `Editor/Graph/Import/ScadSourceToGraph.cs` — AST → graph builder.
  Recognised modules (`cube`, `sphere`, `cylinder`, `translate`,
  `rotate`, `scale`, `mirror`, `color`, `resize`, `offset`, `union`,
  `difference`, `intersection`, `hull`, `minkowski`, `linear_extrude`,
  `rotate_extrude`, `projection`, `square`, `circle`, `polygon`,
  `polyhedron`, `text`) map to typed nodes; positional args are
  normalised to keyword form via a per-module order table, then routed
  either to port defaults or to node fields. Unknown modules are
  preserved verbatim inside `CustomStatementNode`s.
- Top-level `x = …;` becomes an exposed parameter. Identifier
  references (`size`, `width`) in port arguments auto-wire a
  `ParameterNode` into the consuming port so live edits propagate.
- Multiple top-level geometry statements are wrapped in an implicit
  `UnionNode`, matching SCAD's top-level semantics.
- A simple columnar auto-layout places the output on the right, with
  each upstream hop one column to the left.

Entry points:
- Toolbar **Import** dropdown in `ScadGraphEditorWindow` (paste source
  or open a `.scad` file).
- Assets context menu **SCAD → Convert .scad to Graph** on any `.scad`
  asset produces a sibling `.scadgraph` asset.

Out of scope in this pass: `module` / `function` definitions,
`for`/`if`, list comprehensions, `include` / `use`. These land in
`CustomStatementNode`s verbatim.

## Data model

```
ScadGraph : ScriptableObject
├── List<ScadNode> nodes          ([SerializeReference], polymorphic)
├── List<ScadConnection> edges
├── List<ScadExposedParameter>
└── string outputNodeId

ScadNode (abstract)
├── id (GUID), position, title
├── List<ScadPort> inputs
├── List<ScadPort> outputs
└── Emit(outputPortId, compiler) → SCAD expression

ScadPort
├── id (stable within node, author-defined)
├── label (display)
├── type:  Number | Vector2 | Vector3 | Boolean | String | Color
│        | Solid | Shape | Any
└── defaultLiteral (SCAD text used when the input is unconnected)

ScadConnection
├── fromNodeId, fromPortId
└── toNodeId,   toPortId
```

Nodes are serialized via Unity's `[SerializeReference]` so concrete
subclasses survive save/load. Port identity is `(nodeId, portId)` —
GUID-free, because port IDs are stable within a node type.

## Compilation pipeline

```
Graph  ──►  SCAD source  ──►  OpenSCAD  ──►  STL  ──►  Mesh
```

1. Find the `OutputNode`. Everything reachable from its inputs is live;
   anything else is dead and excluded from emission.
2. For each input a node needs, the compiler resolves:
   - if a connection exists → recursively emit the source node's output
     expression,
   - otherwise → use the port's `defaultLiteral`.
3. Value nodes (literals, math) emit expressions (`"[10, 5, 2]"`,
   `"(size * 2)"`). Geometry nodes emit SCAD statements
   (`"cube([10,10,10], center=false)"`).
4. The output node writes the final top-level statement.
5. The full source (preamble + exposed-parameter variables + statement)
   is handed to `ScadLiveCompiler`, which runs OpenSCAD off-thread.
6. The resulting STL is loaded into a `Mesh`.

**Cycles** are rejected at compile time (topo sort surfaces them).
**Type mismatches** are checked during connection and again during
compilation (safety net).

## Asset pipeline

- **Extension:** `.scadgraph`
- **Format:** JSON via `EditorJsonUtility.ToJson(graph, prettyPrint)` —
  VCS-friendly, diffable, survives domain reload.
- **Importer:** `ScadGraphImporter : ScriptedImporter`
  - Reads JSON → materializes `ScadGraph`.
  - Invokes `ScadGraphCompiler` → emits `.scad` source to a temp path.
  - Invokes `ScadLiveCompiler.CompileAsync` (synchronously for import).
  - Produces:
    - **Main artifact:** `Mesh`
    - **Sub-asset:** the `ScadGraph` SO (for editing).
    - **Sub-asset:** the generated `.scad` text, as a `TextAsset`.
- **Create menu:** `Assets → Create → SCAD → Graph` emits a new empty
  `.scadgraph` containing just an `OutputNode`.
- **Open:** `OnOpenAsset` intercepts double-click on `.scadgraph` and
  routes to `ScadGraphEditorWindow`.

## Editor UI

- `ScadGraphEditorWindow : EditorWindow`
  - Hosts a `GraphView` (UnityEditor.Experimental.GraphView).
  - Top toolbar: Save, Compile, Frame All, Parameters panel toggle.
  - Side inspector: properties of the selected node (edit default port
    values and node-specific fields).
  - Optional live preview pane (reuses the preview rendering from
    `ScadLivePreviewWindow` — same `PreviewRenderUtility`, same orbit
    manipulator).
- Node creation: Space / right-click opens a `SearchWindow` populated
  by reflecting `[ScadNode(...)]`-attributed types.
- Connections enforce port compatibility rules (see §Type rules).
- Undo/redo via Unity's `Undo.RecordObject` on the graph SO.

## Type rules

- Equal types always connect.
- `Any` output connects to any input; `Any` input accepts any output.
- **Scalar → Vector broadcast:** `Number` → `Vector2`/`Vector3` expands
  to `[n, n, n]` (opt-in per port, matches SCAD's own behavior for
  `cube(10)` etc.).
- **Solid vs Shape:** separate types. Extrusion nodes consume `Shape`
  and produce `Solid`. Translate / Rotate / Scale accept either.
- **Color:** stored as `[r, g, b, a]` SCAD literal, consumed by
  `ColorNode` and `ColorField` editors.

## Node catalog (initial)

| Category      | Nodes                                                                        |
|---------------|------------------------------------------------------------------------------|
| IO            | Output, Parameter, Number / Vector3 / Vector2 / Boolean / String literal     |
| Primitive 3D  | Cube, Sphere, Cylinder, Polyhedron                                           |
| Primitive 2D  | Square, Circle, Polygon, Text                                                |
| Transform     | Translate, Rotate, Scale, Mirror, Color, Offset, Resize, MultMatrix          |
| Boolean       | Union, Difference, Intersection, Hull, Minkowski                             |
| Extrusion     | LinearExtrude, RotateExtrude, Projection                                     |
| Math (scalar) | Add, Sub, Mul, Div, Mod, Pow, Abs, Sqrt, Sin, Cos, Tan, Min, Max, Clamp, Lerp |
| Vector        | Combine3, Split3, Length, Normalize, Dot, Cross                              |
| Logic         | And, Or, Not, Greater, Less, Equal, IfElse                                   |
| Escape hatch  | Custom SCAD Expression, Custom SCAD Statement                                |

## Phases

1. **Foundation** *(this PR)* — data model, compiler, importer, editor
   shell, ~20 nodes covering primitives/transforms/booleans/extrusion/IO.
2. **Math & vector pack** — full scalar / vector / logic catalog.
3. **Inline property editors** — typed defaults (FloatField, Vector3Field,
   ColorField) rendered directly on unconnected input ports.
4. **Parameters / Customizer panel** — exposed graph inputs with ranges,
   drives a live customizer in the editor window.
5. **Live preview pane** embedded in `ScadGraphEditorWindow`.
6. **Subgraphs** — group selected nodes into a reusable node.
7. **Reverse importer (subset)** — *done, see §SCAD source import.*
8. **Per-node preview** (nice-to-have) — incremental OpenSCAD on
   selected subgraph, displayed in the node header.

## Open questions

- Should we expose SCAD's `children()` mechanism for custom modules, or
  only ship first-class node variants for the common cases?
- For `for` / list comprehension, is a dedicated "Pattern" node group
  (Array, Copy Along Curve) more useful than the raw primitive?
- For `.scadgraph`, should the generated `.scad` also be written to
  disk next to the graph for version-control friendliness, or kept as
  a sub-asset only?
