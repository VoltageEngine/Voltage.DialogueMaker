# DialogueMaker

Branching narrative graphs for the [Voltage Engine](https://github.com/VoltageEngine/VoltageEngine),
shipped as an installable plugin.

> **Status: in development.** The runtime — graph model, file format, variables, conditions and playback
> — is implemented and covered by a round-trip and playback harness. The node-graph editor window, the
> timeline track and the localisation tables are not written yet.

## What it is

A `.vdialogue` asset holds a graph of nodes: lines, choices, conditions, variable assignments, jumps and
ends. A `DialogueRunner` component plays one in a scene; a `DialogueSession` plays one anywhere, with no
engine dependency at all, which is how the whole thing is tested headlessly.

```csharp
var session = new DialogueSession();
session.LineStarted     += line    => ShowLine(line.SpeakerId, Localise(line.TextKey));
session.ChoicesPresented += (_, opts) => ShowChoices(opts);
session.Finished        += tag     => Debug.Log($"conversation ended: {tag}");

session.Start(graph);
session.Advance();   // past a line
session.Choose(0);   // pick an option
```

## Design notes

**Nodes are stored by stable id, never by CLR name.** Each node type carries `[NodeTypeId("line")]` and
registers with `DialogueNodeRegistry` from a `[ModuleInitializer]`. Renaming or moving a node class does
not break existing graphs, and an unknown id fails loudly with the list of registered ids rather than
silently dropping a node. This mirrors `TimelineTrackRegistry` in the engine.

**A graph is its own file format, not a `.vasset`.** Data assets have no polymorphic-field support, so a
heterogeneous node list could not round-trip through one. `.vdialogue` is a `JsonAssetFile<DialogueGraph>`
registered with `AssetFileRegistry` — which also means it gets its own editor rather than a generic
property grid.

**Conditions are structured, not an expression language.** A condition is a variable, an operator and a
value. The editor builds them from dropdowns, so a condition cannot be malformed and there is no parser
to get wrong. Compound logic chains `ConditionNode`s.

**The graph is shared; nothing playable lives on it.** Every runner reading the same file gets the same
`DialogueGraph` instance, so all mutable state — position, variables — belongs to the session.
`DialogueVariables.Snapshot()` / `Restore()` is the save-game surface.

**Text lives outside the graph.** Nodes store keys, not prose, so translation and structure version
separately.

## Runtime behaviour worth knowing

- Condition, set-variable and jump nodes are walked *through* — playback only stops on a line or a choice.
- An option whose condition is false is hidden, and `Choose(i)` indexes the **visible** list, so a hidden
  option can never shift what the player clicked.
- Authoring mistakes degrade rather than throw: a broken wire, a choice with every option gated off, or a
  jump cycle ends the conversation and raises `Warning` with the cause. `DialogueGraph.Validate()` catches
  all of these at author time.

## Building

The plugin binds to a built engine rather than a NuGet package.

```bash
dotnet build Voltage.DialogueMaker.csproj -c Release
dotnet msbuild Voltage.DialogueMaker.csproj -t:PackagePlugin
```

The engine is found via `-p:VoltageEnginePath=<dir>`, `$VOLTAGE_ENGINE_PATH`, or a sibling `VoltageEngine`
checkout, in that order. The editor project additionally needs `Voltage.Editor.dll`; because the editor
builds self-contained per RID, its path defaults to `bin/<Configuration>/<rid>` and can be steered with
`-p:VoltageEditorRid=` or `-p:VoltageEditorPath=`.

`PackagePlugin` stages `plugin.json` plus `lib/` (game build), `editor-lib/` (EDITOR flavour) and
`editor/` (the graph window). Those folders are gitignored — they are release artifacts, produced by CI
and attached to a tagged release.

## Installing

Once released, install it from the Voltage editor: **Plugins ▸ Browse Plugins**, or add the release zip
URL by hand.
