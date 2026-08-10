using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using Voltage.Editor.Inspectors;
using Voltage.Editor.Inspectors.TypeInspectors;
using Voltage.Editor.Plugins;
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

		public DialogueGraphWindow() => Title = "Dialogue Graph";

		public string OpenPath => _path;

		internal DialogueGraph Graph => _graph;

		internal bool HasClipboard => !string.IsNullOrEmpty(_clipboardJson);

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

		public override void Draw()
		{
			if (!IsOpen)
				return;

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

			if (_dirty && !anyActive)
				Save();

			if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) &&
			    (ImGui.GetIO().KeySuper || ImGui.GetIO().KeyCtrl) && ImGui.IsKeyPressed(ImGuiKey.S))
			{
				Save();
			}

			DrawStatus();
			ImGui.End();
		}

		private void DrawMenuBar()
		{
			if (!ImGui.BeginMenuBar())
				return;

			if (ImGui.BeginMenu("File"))
			{
				if (ImGui.MenuItem("New", _graph == null || _path != null))
					NewGraph();

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
				if (ImGui.MenuItem("Select All", "Ctrl+A"))
				{
					_canvas.SelectAll(_graph);
					_selectedId = null;
				}

				if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, _canvas.SelectionCount > 0))
					_canvas.DuplicateSelectionFromMenu(this);

				if (ImGui.MenuItem("Delete", "Del", false, _canvas.SelectionCount > 0))
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

				var snap = _canvas.SnapToGrid;
				if (ImGui.MenuItem("Snap To Grid", null, snap))
					_canvas.SnapToGrid = !snap;

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

			_graph.EntryNodeId = id;
			MarkDirty();
		}

		/// <summary>Clears this node's outgoing wires, leaving incoming ones alone.</summary>
		internal void DisconnectOutputs(string id)
		{
			var node = _graph?.FindNode(id);
			if (node == null)
				return;

			RemapOutputs(node, _ => null);
			MarkDirty();
		}

		internal void CopyToClipboard(IEnumerable<string> ids)
		{
			var json = SerializeNodes(ids);
			if (json != null)
				_clipboardJson = json;
		}

		internal List<string> PasteClipboard(Num.Vector2 worldPosition) =>
			InsertSerializedNodes(_clipboardJson, worldPosition, absolute: true, Num.Vector2.Zero);

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

		private void NewGraph()
		{
			_graph = DialogueGraphIO.CreateDefault();
			_path = null;
			_selectedId = _graph.EntryNodeId;
			_inspectors = null;
			_inspectedNode = null;
			_reportStale = true;
			_canvas.FrameAll(_graph);
			SetStatus("New graph - use File > Save once it has a path.");
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
