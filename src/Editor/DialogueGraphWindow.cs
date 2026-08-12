using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using Voltage.Editor.FilePickers;
using Voltage.Editor.Inspectors;
using Voltage.Editor.Inspectors.TypeInspectors;
using Voltage.Editor.Persistence;
using Voltage.Editor.Plugins;
using Voltage.Editor.ProjectFile;
using Num = System.Numerics;

namespace Voltage.Dialogue.Editor
{
	/// <summary>Node-graph editor for <c>.vdialogue</c> assets.</summary>
	public sealed class DialogueGraphWindow : EditorPluginWindow
	{
		private readonly DialogueCanvas _canvas = new();

		private DialogueGraph _graph;
		private string _path;
		private string _selectedId;

		private List<AbstractTypeInspector> _inspectors;
		private object _inspectedNode;

		private bool _dirty;
		private bool _wasAnyItemActive;
		private string _status;
		private double _statusClearAt;

		private DialogueValidationReport _report;
		private bool _reportStale = true;

		/// <summary>Nodes copied or cut, held as graph JSON so a paste round-trips through the real reader.</summary>
		private string _clipboardJson;

		/// <summary>
		/// Whole-graph snapshots, as the same JSON the asset is written in. Snapshots rather than a command
		/// log because every edit here already round-trips through that reader and writer, so a snapshot is
		/// exact by construction - including for an UnknownNode, whose fields no command in this editor
		/// could describe well enough to invert.
		/// </summary>
		private readonly List<string> _undo = new();
		private readonly List<string> _redo = new();

		/// <summary>
		/// State captured when an interaction began, committed only if it actually changed something. A
		/// drag and a text edit both have to become one undo step rather than one per frame, and neither
		/// announces itself up front.
		/// </summary>
		private string _pendingSnapshot;

		/// <summary>Deep graphs are still only tens of KB of JSON; this is a memory ceiling, not a limit anyone reaches.</summary>
		private const int MaxHistory = 64;

		/// <summary>Where the path of the last graph opened is kept, so the next session starts on it.</summary>
		private const string LastGraphSetting = "DialogueMaker.LastGraph";

		private bool _restoredLastGraph;

		public DialogueGraphWindow() => Title = "Dialogue Graph";

		public string OpenPath => _path;

		internal DialogueGraph Graph => _graph;

		internal bool HasClipboard => !string.IsNullOrEmpty(CurrentClipboard());

		public void Open(string absolutePath)
		{
			if (string.IsNullOrEmpty(absolutePath))
				return;

			try
			{
				var graph = DialogueGraphIO.Load(absolutePath);
				if (graph == null)
				{
					SetStatus($"'{Path.GetFileName(absolutePath)}' could not be read - see the console.");
					IsOpen = true;
					return;
				}

				_graph = graph;
				_path = absolutePath;
				_selectedId = graph.EntryNodeId;
				_dirty = false;
				_reportStale = true;
				_inspectors = null;
				_inspectedNode = null;
				_canvas.FrameAll(graph);
				RememberLastGraph(absolutePath);
				SetStatus($"Opened {Path.GetFileName(absolutePath)}");
			}
			catch (Exception ex)
			{
				SetStatus($"Could not open: {ex.Message}");
			}

			IsOpen = true;
		}

		public void Close()
		{
			_graph = null;
			_path = null;
			_selectedId = null;
			_inspectors = null;
			_inspectedNode = null;
			IsOpen = false;
		}

		public void Save()
		{
			if (_graph == null || string.IsNullOrEmpty(_path))
				return;

			try
			{
				DialogueGraphIO.Save(_graph, _path);
				_dirty = false;
				SetStatus($"Saved {Path.GetFileName(_path)}");
			}
			catch (Exception ex)
			{
				SetStatus($"Save failed: {ex.Message}");
			}
		}

		/// <summary>Any structural edit; the index and the validation report both go stale.</summary>
		public void MarkDirty()
		{
			_dirty = true;
			_reportStale = true;
			_graph?.InvalidateIndex();
		}

		#region Undo history

		internal bool CanUndo => _undo.Count > 0;

		internal bool CanRedo => _redo.Count > 0;

		/// <summary>
		/// Records the state to come back to, before a command changes it. Commands call this themselves;
		/// drags and field edits are captured by the interaction snapshot in <see cref="Draw"/> instead,
		/// because they have no single moment to call it from.
		/// </summary>
		internal void PushUndo()
		{
			if (_graph == null)
				return;

			Remember(DialogueGraphIO.ToJson(_graph));
		}

		private void Remember(string snapshot)
		{
			if (snapshot == null)
				return;

			// A click that changed nothing, or a command that also tripped the interaction snapshot, would
			// otherwise leave an undo step that appears to do nothing when used.
			if (_undo.Count > 0 && string.Equals(_undo[^1], snapshot, StringComparison.Ordinal))
				return;

			_undo.Add(snapshot);
			if (_undo.Count > MaxHistory)
				_undo.RemoveAt(0);

			// A new edit after undoing abandons the redo branch, as everywhere else.
			_redo.Clear();
		}

		internal void Undo() => Step(_undo, _redo, "Nothing to undo.");

		internal void Redo() => Step(_redo, _undo, "Nothing to redo.");

		private void Step(List<string> from, List<string> to, string emptyMessage)
		{
			if (_graph == null)
				return;

			if (from.Count == 0)
			{
				SetStatus(emptyMessage);
				return;
			}

			var current = DialogueGraphIO.ToJson(_graph);
			var target = from[^1];
			from.RemoveAt(from.Count - 1);

			if (!ApplyState(target))
				return;

			to.Add(current);
			if (to.Count > MaxHistory)
				to.RemoveAt(0);
		}

		/// <summary>
		/// Replaces the graph wholesale. The canvas is handed the graph afresh every frame and prunes a
		/// selection whose nodes are gone, so nothing outside here has to be told - except the inspector,
		/// which caches the node instance it built its field list from.
		/// </summary>
		private bool ApplyState(string json)
		{
			DialogueGraph parsed;
			try
			{
				parsed = DialogueGraphIO.FromJson(json);
			}
			catch (Exception ex)
			{
				SetStatus($"Could not restore that state: {ex.Message}");
				return false;
			}

			if (parsed == null)
				return false;

			_graph = parsed;
			_inspectors = null;
			_inspectedNode = null;

			// The restored graph may not contain what was selected, and a half-finished interaction
			// snapshot describes a graph that no longer exists.
			if (_graph.FindNode(_selectedId) == null)
				_selectedId = null;
			_pendingSnapshot = null;

			MarkDirty();
			return true;
		}

		/// <summary>
		/// Opens an undo step at the moment a press could start an edit - before the canvas moves anything
		/// this frame, so the first few pixels of a drag are inside the step rather than outside it.
		/// </summary>
		private void BeginInteractionSnapshot()
		{
			if (_graph == null || _pendingSnapshot != null)
				return;

			if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right))
				_pendingSnapshot = DialogueGraphIO.ToJson(_graph);
		}

		/// <summary>
		/// Closes it once nothing is being pressed and no field is still focused. Both conditions matter: a
		/// drag ends on mouse-up, while a text field stays active long after it.
		/// </summary>
		private void EndInteractionSnapshot(bool anyItemActive)
		{
			if (_pendingSnapshot == null || _graph == null)
				return;

			if (anyItemActive || ImGui.IsMouseDown(ImGuiMouseButton.Left) || ImGui.IsMouseDown(ImGuiMouseButton.Right))
				return;

			var snapshot = _pendingSnapshot;
			_pendingSnapshot = null;

			if (!string.Equals(DialogueGraphIO.ToJson(_graph), snapshot, StringComparison.Ordinal))
				Remember(snapshot);
		}

		#endregion

		public override void Draw()
		{
			if (!IsOpen)
				return;

			RestoreLastGraphOnce();

			ImGui.SetNextWindowSize(new Num.Vector2(1100, 680), ImGuiCond.FirstUseEver);

			var open = IsOpen;
			if (!ImGui.Begin(Title, ref open, ImGuiWindowFlags.MenuBar))
			{
				ImGui.End();
				IsOpen = open;
				return;
			}
			IsOpen = open;

			DrawMenuBar();

			if (_graph == null)
			{
				ImGui.TextColored(new Num.Vector4(0.6f, 0.6f, 0.6f, 1f),
					"No dialogue graph open.\n\nUse File > New, or double-click a .vdialogue in the Asset Browser.");
				DrawStatus();
				ImGui.End();
				return;
			}

			// Before anything is drawn, so a press is recorded ahead of the edit it starts.
			BeginInteractionSnapshot();

			var side = 320f;
			var avail = ImGui.GetContentRegionAvail();
			var canvasWidth = Math.Max(240f, avail.X - side - 8f);

			if (ImGui.BeginChild("canvas-host", new Num.Vector2(canvasWidth, avail.Y - 24f), true,
				    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
			{
				_canvas.Draw(this, _graph, ref _selectedId);
			}
			ImGui.EndChild();

			ImGui.SameLine();

			if (ImGui.BeginChild("side", new Num.Vector2(side, avail.Y - 24f), true))
			{
				DrawSidePanel();
			}
			ImGui.EndChild();

			// Same rule the Data Asset window uses: persist when an interaction ends, not per frame -
			// serializing on every slider tick would be far too costly.
			var anyActive = ImGui.IsAnyItemActive();
			if (_wasAnyItemActive && !anyActive)
				_dirty = true;
			_wasAnyItemActive = anyActive;

			EndInteractionSnapshot(anyActive);

			if (_dirty && !anyActive)
				Save();

			HandleWindowShortcuts();

			DrawStatus();
			ImGui.End();
		}

		/// <summary>
		/// Shortcuts that belong to the window rather than the canvas: they must work while a side-panel
		/// field has focus, which is exactly when the canvas stops listening.
		/// </summary>
		private void HandleWindowShortcuts()
		{
			if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
				return;

			var io = ImGui.GetIO();
			var command = io.KeyCtrl || io.KeySuper;
			if (!command)
				return;

			if (ImGui.IsKeyPressed(ImGuiKey.S))
				Save();

			// Ctrl+Shift+Z and Ctrl+Y both redo: the first is the cross-platform habit, the second the
			// Windows one, and getting the wrong one is a destructive surprise rather than a no-op.
			if (ImGui.IsKeyPressed(ImGuiKey.Z))
			{
				if (io.KeyShift)
					Redo();
				else
					Undo();
			}

			if (ImGui.IsKeyPressed(ImGuiKey.Y))
				Redo();

			// Handled here rather than on the canvas: the canvas ignores shortcuts while a field has focus,
			// which is precisely the state the find box puts you in.
			if (ImGui.IsKeyPressed(ImGuiKey.F))
				_canvas.ToggleFind();
		}

		private void DrawMenuBar()
		{
			if (!ImGui.BeginMenuBar())
				return;

			if (ImGui.BeginMenu("File"))
			{
				// New asks for the file up front, so there is never a graph without a path - which used to leave
				// Save, Reload and New all disabled at once, with no way out of it.
				if (ImGui.MenuItem("New"))
					NewGraph();

				if (ImGui.MenuItem("Load"))
					LoadGraph();

				if (ImGui.MenuItem("Save", "Ctrl+S", false, _graph != null && _path != null))
					Save();

				if (ImGui.MenuItem("Reload", _path != null))
					Open(_path);

				ImGui.EndMenu();
			}

			if (_graph != null && ImGui.BeginMenu("Add Node"))
			{
				foreach (var (label, factory) in NodeFactories)
				{
					if (ImGui.MenuItem(label))
						AddNode(factory(), _canvas.ViewCentreInWorld());
				}

				ImGui.EndMenu();
			}

			if (_graph != null && ImGui.BeginMenu("Edit"))
			{
				if (ImGui.MenuItem("Undo", "Ctrl+Z", false, CanUndo))
					Undo();

				if (ImGui.MenuItem("Redo", "Ctrl+Shift+Z", false, CanRedo))
					Redo();

				ImGui.Separator();

				var hasSelection = _canvas.SelectionCount > 0;

				if (ImGui.MenuItem("Cut", "Ctrl+X", false, hasSelection))
					_canvas.CutSelectionFromMenu(this);

				if (ImGui.MenuItem("Copy", "Ctrl+C", false, hasSelection))
					CopyToClipboard(_canvas.Selection);

				if (ImGui.MenuItem("Paste", "Ctrl+V", false, HasClipboard))
					_canvas.PasteFromMenu(this);

				if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, hasSelection))
					_canvas.DuplicateSelectionFromMenu(this);

				ImGui.Separator();

				if (ImGui.MenuItem("Select All", "Ctrl+A"))
				{
					_canvas.SelectAll(_graph);
					_selectedId = null;
				}

				if (ImGui.MenuItem("Delete", "Del", false, hasSelection))
				{
					DeleteNodes(_canvas.Selection);
					_canvas.ClearSelection();
					_selectedId = null;
				}

				ImGui.EndMenu();
			}

			if (_graph != null && ImGui.BeginMenu("View"))
			{
				if (ImGui.MenuItem("Frame Selection", "F"))
					_canvas.FrameSelection(_graph);
				if (ImGui.MenuItem("Frame All", "Shift+F"))
					_canvas.FrameAll(_graph);
				if (ImGui.MenuItem("Zoom In"))
					_canvas.ZoomBy(1.2f);
				if (ImGui.MenuItem("Zoom Out"))
					_canvas.ZoomBy(1f / 1.2f);
				if (ImGui.MenuItem("Reset Zoom", "Ctrl+0"))
					_canvas.ResetZoom();

				if (ImGui.MenuItem("Find", "Ctrl+F"))
					_canvas.ToggleFind();

				var snap = _canvas.SnapToGrid;
				if (ImGui.MenuItem("Snap To Grid", null, snap))
					_canvas.SnapToGrid = !snap;

				var minimap = _canvas.ShowMinimap;
				if (ImGui.MenuItem("Minimap", null, minimap))
					_canvas.ShowMinimap = !minimap;

				ImGui.EndMenu();
			}

			if (_graph != null && ImGui.BeginMenu("Help"))
			{
				if (ImGui.MenuItem("Manual"))
					DialogueManualWindow.Instance.IsOpen = true;
				ImGui.EndMenu();
			}

			ImGui.EndMenuBar();
		}

		internal static readonly (string Label, Func<DialogueNode> Factory)[] NodeFactories =
		{
			("Line", () => new LineNode()),
			("Choice", () => new ChoiceNode { Options = { new DialogueChoiceOption() } }),
			("Condition", () => new ConditionNode()),
			("Set Variable", () => new SetVariableNode()),
			("Jump", () => new JumpNode()),
			("End", () => new EndNode()),
		};

		internal DialogueNode AddNode(DialogueNode node, Num.Vector2 worldPosition)
		{
			if (_graph == null || node == null)
				return null;

			PushUndo();

			node.EditorX = worldPosition.X;
			node.EditorY = worldPosition.Y;
			_graph.AddNode(node);
			_selectedId = node.Id;
			MarkDirty();
			return node;
		}

		internal void DeleteNode(string id) => DeleteNodes(new[] { id });

		/// <summary>
		/// Snapshotted before the loop: the canvas passes its live selection set, and RemoveNode also
		/// clears every wire pointing at the node, so iterating the caller's collection while it changes
		/// would be a modification-during-enumeration bug.
		/// </summary>
		internal void DeleteNodes(IEnumerable<string> ids)
		{
			if (_graph == null || ids == null)
				return;

			var doomed = new List<string>(ids);

			// Recorded before the first removal, and only when there is one to make: an undo step for a
			// delete that deleted nothing is a step that does nothing.
			var willRemove = false;
			foreach (var id in doomed)
			{
				if (!string.IsNullOrEmpty(id) && _graph.FindNode(id) != null)
				{
					willRemove = true;
					break;
				}
			}

			if (!willRemove)
				return;

			PushUndo();
			var removed = false;

			foreach (var id in doomed)
			{
				if (string.IsNullOrEmpty(id) || !_graph.RemoveNode(id))
					continue;

				removed = true;
				if (_selectedId == id)
					_selectedId = null;
			}

			if (removed)
				MarkDirty();
		}

		internal void SetEntryNode(string id)
		{
			if (_graph == null || _graph.FindNode(id) == null)
				return;

			PushUndo();
			_graph.EntryNodeId = id;
			MarkDirty();
		}

		/// <summary>Clears this node's outgoing wires, leaving incoming ones alone.</summary>
		internal void DisconnectOutputs(string id)
		{
			var node = _graph?.FindNode(id);
			if (node == null)
				return;

			PushUndo();
			RemapOutputs(node, _ => null);
			MarkDirty();
		}

		/// <summary>
		/// Also goes to the system clipboard, which is what makes copying nodes from one graph into another
		/// work: the two windows share no state, but they do share the desktop's clipboard.
		/// </summary>
		internal void CopyToClipboard(IEnumerable<string> ids)
		{
			var json = SerializeNodes(ids);
			if (json == null)
				return;

			_clipboardJson = json;

			try
			{
				ImGui.SetClipboardText(ClipboardMarker + json);
			}
			catch
			{
				// No system clipboard on this platform - the in-window copy above still works.
			}
		}

		internal void CutToClipboard(IEnumerable<string> ids)
		{
			if (ids == null)
				return;

			var doomed = new List<string>(ids);
			CopyToClipboard(doomed);
			DeleteNodes(doomed);
		}

		/// <summary>
		/// Tags our own clipboard payload, so pasting while some unrelated text is on the clipboard falls
		/// back to the last in-window copy instead of trying to parse a shopping list as a graph.
		/// </summary>
		private const string ClipboardMarker = "voltage.dialoguemaker/nodes\n";

		/// <summary>The system clipboard when it holds nodes, otherwise whatever was last copied here.</summary>
		private string CurrentClipboard()
		{
			try
			{
				var text = ImGui.GetClipboardText();
				if (!string.IsNullOrEmpty(text) && text.StartsWith(ClipboardMarker, StringComparison.Ordinal))
					return text.Substring(ClipboardMarker.Length);
			}
			catch
			{
				// Fall through to the in-window copy.
			}

			return _clipboardJson;
		}

		internal List<string> PasteClipboard(Num.Vector2 worldPosition) =>
			InsertSerializedNodes(CurrentClipboard(), worldPosition, absolute: true, Num.Vector2.Zero);

		internal List<string> DuplicateNodes(IEnumerable<string> ids, Num.Vector2 offset) =>
			InsertSerializedNodes(SerializeNodes(ids), Num.Vector2.Zero, absolute: false, offset);

		/// <summary>
		/// Nodes are cloned by round-tripping them through the asset's own reader and writer rather than a
		/// hand-written copy per type. That is what keeps an UnknownNode - a node whose plugin is not
		/// loaded - intact instead of quietly losing the fields this editor cannot see.
		/// </summary>
		private string SerializeNodes(IEnumerable<string> ids)
		{
			if (_graph == null || ids == null)
				return null;

			var scratch = new DialogueGraph();
			foreach (var id in ids)
			{
				var node = _graph.FindNode(id);
				if (node != null)
					scratch.Nodes.Add(node);
			}

			return scratch.Nodes.Count == 0 ? null : DialogueGraphIO.ToJson(scratch);
		}

		private List<string> InsertSerializedNodes(string json, Num.Vector2 worldPosition, bool absolute, Num.Vector2 offset)
		{
			if (_graph == null || string.IsNullOrEmpty(json))
				return null;

			DialogueGraph parsed;
			try
			{
				parsed = DialogueGraphIO.FromJson(json);
			}
			catch (Exception ex)
			{
				SetStatus($"Could not paste: {ex.Message}");
				return null;
			}

			if (parsed == null || parsed.Nodes.Count == 0)
				return null;

			PushUndo();

			// Anchor a paste at the cursor by moving the group's top-left there; a duplicate just nudges.
			var shift = offset;
			if (absolute)
			{
				float minX = float.MaxValue, minY = float.MaxValue;
				foreach (var node in parsed.Nodes)
				{
					if (node == null) continue;
					minX = Math.Min(minX, node.EditorX);
					minY = Math.Min(minY, node.EditorY);
				}

				if (minX < float.MaxValue)
					shift = new Num.Vector2(worldPosition.X - minX, worldPosition.Y - minY);
			}

			// Wires inside the copied set must follow the copies; wires leaving it keep pointing at the
			// originals, which is what every node editor does and what a designer expects.
			var remap = new Dictionary<string, string>(StringComparer.Ordinal);
			var added = new List<string>(parsed.Nodes.Count);

			foreach (var node in parsed.Nodes)
			{
				if (node == null)
					continue;

				var oldId = node.Id;
				node.Id = null;
				node.EditorX += shift.X;
				node.EditorY += shift.Y;
				_graph.AddNode(node);

				if (!string.IsNullOrEmpty(oldId))
					remap[oldId] = node.Id;

				added.Add(node.Id);
			}

			foreach (var node in parsed.Nodes)
			{
				if (node != null)
					RemapOutputs(node, target => target != null && remap.TryGetValue(target, out var to) ? to : target);
			}

			if (added.Count > 0)
			{
				_selectedId = added[0];
				MarkDirty();
			}

			return added;
		}

		/// <summary>Rewrites every outgoing link of a node through <paramref name="map"/>.</summary>
		private static void RemapOutputs(DialogueNode node, Func<string, string> map)
		{
			switch (node)
			{
				case LineNode line:
					line.NextId = map(line.NextId);
					break;
				case SetVariableNode set:
					set.NextId = map(set.NextId);
					break;
				case JumpNode jump:
					jump.TargetId = map(jump.TargetId);
					break;
				case ConditionNode condition:
					condition.TrueNextId = map(condition.TrueNextId);
					condition.FalseNextId = map(condition.FalseNextId);
					break;
				case ChoiceNode choice:
					foreach (var option in choice.Options)
					{
						if (option != null)
							option.NextId = map(option.NextId);
					}

					break;
			}
		}

		/// <summary>
		/// Asks where the graph should live, then creates it there. The file is written immediately: a graph held
		/// only in memory has no path, and without a path Save, Reload and New all disable themselves - which is a
		/// corner with no way out of it.
		/// </summary>
		private void NewGraph()
		{
			var suggested = Path.Combine(DefaultDialogueFolder(), "NewDialogue" + DialogueGraphIO.FileExtension);

			if (!NativeFileDialogs.TrySaveFile("New Dialogue Graph", suggested,
				    new[] { "*" + DialogueGraphIO.FileExtension }, "Voltage Dialogue", out var path) ||
			    string.IsNullOrEmpty(path))
			{
				return;
			}

			if (!path.EndsWith(DialogueGraphIO.FileExtension, StringComparison.OrdinalIgnoreCase))
				path += DialogueGraphIO.FileExtension;

			try
			{
				_graph = DialogueGraphIO.CreateAndSave(path);
				_path = path;
				_selectedId = _graph.EntryNodeId;
				_inspectors = null;
				_inspectedNode = null;
				_dirty = false;
				_reportStale = true;
				_canvas.FrameAll(_graph);

				RememberLastGraph(path);
				SetStatus($"Created {Path.GetFileName(path)}");
			}
			catch (Exception ex)
			{
				SetStatus($"Could not create: {ex.Message}");
			}
		}

		/// <summary>Opens an existing graph from anywhere on disk, for assets outside the Asset Browser's view.</summary>
		private void LoadGraph()
		{
			if (!NativeFileDialogs.TryOpenFile("Load Dialogue Graph", DefaultDialogueFolder(),
				    new[] { "*" + DialogueGraphIO.FileExtension }, "Voltage Dialogue", out var path) ||
			    string.IsNullOrEmpty(path))
			{
				return;
			}

			Open(path);
		}

		private static string DefaultDialogueFolder()
		{
			if (ProjectManager.Instance != null && ProjectManager.Instance.HasActiveProject)
			{
				var dir = Path.Combine(ProjectManager.Instance.CurrentProject.DataFolder, "Dialogues");

				try { Directory.CreateDirectory(dir); } catch { /* fall through to the base directory */ }

				if (Directory.Exists(dir))
					return dir;
			}

			return AppContext.BaseDirectory;
		}

		private static void RememberLastGraph(string absolutePath)
		{
			if (!string.IsNullOrWhiteSpace(absolutePath))
				EditorSettingsLoader.SaveSetting(LastGraphSetting, absolutePath);
		}

		/// <summary>
		/// Re-opens whatever was open when the editor last closed, the first time this window draws. Only when
		/// nothing else has been opened in the meantime - a graph opened from the Asset Browser wins.
		/// </summary>
		private void RestoreLastGraphOnce()
		{
			if (_restoredLastGraph)
				return;

			_restoredLastGraph = true;

			if (_graph != null)
				return;

			var last = EditorSettingsLoader.LoadSetting(LastGraphSetting, string.Empty);

			// A graph that has been moved or deleted since is simply forgotten rather than reported: the window
			// opening with an error nobody asked for is worse than it opening empty.
			if (!string.IsNullOrWhiteSpace(last) && File.Exists(last))
				Open(last);
		}

		private void DrawSidePanel()
		{
			if (ImGui.CollapsingHeader("Node", ImGuiTreeNodeFlags.DefaultOpen))
				DrawNodeInspector();

			if (ImGui.CollapsingHeader("Variables", ImGuiTreeNodeFlags.DefaultOpen))
				DrawVariables();

			if (ImGui.CollapsingHeader("Validation", ImGuiTreeNodeFlags.DefaultOpen))
				DrawValidation();
		}

		private void DrawNodeInspector()
		{
			var node = _graph.FindNode(_selectedId);
			if (node == null)
			{
				ImGui.TextColored(new Num.Vector4(0.6f, 0.6f, 0.6f, 1f), "Nothing selected.");
				return;
			}

			ImGui.TextDisabled($"{node.DisplayName}  ({node.Id})");

			if (node is UnknownNode unknown)
			{
				ImGui.TextColored(new Num.Vector4(1f, 0.7f, 0.3f, 1f),
					$"Type '{unknown.UnknownTypeId}' is not registered.\nIts content is preserved untouched.");
				return;
			}

			var isEntry = string.Equals(_graph.EntryNodeId, node.Id, StringComparison.Ordinal);
			if (isEntry)
				ImGui.TextColored(new Num.Vector4(0.5f, 0.85f, 0.5f, 1f), "Entry node");
			else if (ImGui.SmallButton("Make entry node"))
			{
				_graph.EntryNodeId = node.Id;
				MarkDirty();
			}

			ImGui.Separator();

			// The same call the entity and data-asset inspectors use, so every existing type inspector
			// works on a node with no editor code written for it.
			if (!ReferenceEquals(_inspectedNode, node))
			{
				_inspectors = TypeInspectorUtils.GetInspectableProperties(node);
				_inspectedNode = node;
			}

			if (_inspectors == null || _inspectors.Count == 0)
			{
				ImGui.TextDisabled("No editable fields.");
				return;
			}

			foreach (var inspector in _inspectors)
				inspector.Draw();

			if (node is ChoiceNode choice)
				DrawChoiceOptions(choice);
		}

		private void DrawChoiceOptions(ChoiceNode choice)
		{
			ImGui.Separator();
			ImGui.TextDisabled("Options");

			for (var i = 0; i < choice.Options.Count; i++)
			{
				ImGui.PushID(i);
				var option = choice.Options[i] ?? (choice.Options[i] = new DialogueChoiceOption());

				var key = option.TextKey ?? string.Empty;
				if (ImGui.InputText($"Text key##{i}", ref key, 256))
				{
					option.TextKey = key;
					MarkDirty();
				}

				var gated = option.Condition != null;
				if (ImGui.Checkbox($"Conditional##{i}", ref gated))
				{
					option.Condition = gated ? new DialogueCondition() : null;
					MarkDirty();
				}

				if (option.Condition != null)
					DrawConditionEditor(option.Condition);

				if (ImGui.SmallButton("Remove option"))
				{
					choice.Options.RemoveAt(i);
					MarkDirty();
					ImGui.PopID();
					break;
				}

				ImGui.PopID();
				ImGui.Separator();
			}

			if (ImGui.SmallButton("Add option"))
			{
				choice.Options.Add(new DialogueChoiceOption());
				MarkDirty();
			}
		}

		internal void DrawConditionEditor(DialogueCondition condition)
		{
			var names = VariableNames();
			var current = Math.Max(0, Array.IndexOf(names, condition.Variable ?? string.Empty));

			if (names.Length > 0 && ImGui.Combo("Variable", ref current, names, names.Length))
			{
				condition.Variable = names[current];
				MarkDirty();
			}

			var op = (int)condition.Op;
			if (ImGui.Combo("Operator", ref op, ConditionOperatorNames, ConditionOperatorNames.Length))
			{
				condition.Op = (ConditionOperator)op;
				MarkDirty();
			}

			if (condition.NeedsValue)
			{
				condition.Value ??= DialogueValue.Bool(false);
				DrawValueEditor(condition.Value);
			}
		}

		private static readonly string[] ConditionOperatorNames =
			{ "is true", "is false", "==", "!=", ">", ">=", "<", "<=" };

		private static readonly string[] ValueKindNames = { "Bool", "Int", "Float", "String" };

		internal void DrawValueEditor(DialogueValue value)
		{
			var kind = (int)value.Kind;
			if (ImGui.Combo("Type", ref kind, ValueKindNames, ValueKindNames.Length))
			{
				value.Kind = (DialogueValueKind)kind;
				MarkDirty();
			}

			switch (value.Kind)
			{
				case DialogueValueKind.Bool:
					var b = value.BoolValue;
					if (ImGui.Checkbox("Value", ref b)) { value.BoolValue = b; MarkDirty(); }
					break;
				case DialogueValueKind.Int:
					var i = value.IntValue;
					if (ImGui.InputInt("Value", ref i)) { value.IntValue = i; MarkDirty(); }
					break;
				case DialogueValueKind.Float:
					var f = value.FloatValue;
					if (ImGui.InputFloat("Value", ref f)) { value.FloatValue = f; MarkDirty(); }
					break;
				case DialogueValueKind.String:
					var s = value.StringValue ?? string.Empty;
					if (ImGui.InputText("Value", ref s, 512)) { value.StringValue = s; MarkDirty(); }
					break;
			}
		}

		private string[] VariableNames()
		{
			var names = new string[_graph.Variables.Count];
			for (var i = 0; i < _graph.Variables.Count; i++)
				names[i] = _graph.Variables[i]?.Name ?? string.Empty;
			return names;
		}

		private void DrawVariables()
		{
			for (var i = 0; i < _graph.Variables.Count; i++)
			{
				var variable = _graph.Variables[i];
				if (variable == null)
					continue;

				ImGui.PushID(1000 + i);

				var name = variable.Name ?? string.Empty;
				if (ImGui.InputText("Name", ref name, 128))
				{
					variable.Name = name;
					MarkDirty();
				}

				variable.Default ??= DialogueValue.Bool(false);
				DrawValueEditor(variable.Default);

				if (ImGui.SmallButton("Remove"))
				{
					_graph.Variables.RemoveAt(i);
					MarkDirty();
					ImGui.PopID();
					break;
				}

				ImGui.PopID();
				ImGui.Separator();
			}

			if (ImGui.SmallButton("Add variable"))
			{
				_graph.Variables.Add(new DialogueVariableDef
				{
					Name = "variable" + _graph.Variables.Count,
					Default = DialogueValue.Bool(false),
				});
				MarkDirty();
			}
		}

		private void DrawValidation()
		{
			if (_reportStale)
			{
				_report = _graph.Validate();
				_reportStale = false;
			}

			if (_report == null || _report.Count == 0)
			{
				ImGui.TextColored(new Num.Vector4(0.5f, 0.85f, 0.5f, 1f), "No problems found.");
				return;
			}

			ImGui.TextColored(new Num.Vector4(1f, 0.7f, 0.3f, 1f), $"{_report.Count} problem(s)");

			for (var i = 0; i < _report.Count; i++)
			{
				var issue = _report[i];
				ImGui.PushID(2000 + i);

				if (ImGui.Selectable(issue.ToString()) && !string.IsNullOrEmpty(issue.NodeId))
				{
					_selectedId = issue.NodeId;
					_canvas.FrameNode(_graph.FindNode(issue.NodeId));
				}

				ImGui.PopID();
			}
		}

		private void SetStatus(string message)
		{
			_status = message;
			_statusClearAt = ImGui.GetTime() + 5.0;
		}

		private void DrawStatus()
		{
			if (_status == null)
				return;

			if (ImGui.GetTime() > _statusClearAt)
			{
				_status = null;
				return;
			}

			ImGui.TextDisabled(_status);
		}
	}
}
