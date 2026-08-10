using System;
using System.Collections.Generic;
using ImGuiNET;
using Num = System.Numerics;

namespace Voltage.Dialogue.Editor
{
	/// <summary>Pan/zoom canvas: draws the graph, moves nodes, and drags wires between ports.</summary>
	internal sealed class DialogueCanvas
	{
		private const float NodeWidth = 190f;
		private const float HeaderHeight = 24f;
		private const float RowHeight = 18f;
		private const float PortRadius = 5f;
		private const float GridStep = 24f;

		private const float MinZoom = 0.25f;
		private const float MaxZoom = 2.5f;

		private Num.Vector2 _pan;
		private float _zoom = 1f;

		/// <summary>Every selected node. <c>selectedId</c> is whichever one the inspector shows.</summary>
		private readonly HashSet<string> _selection = new(StringComparer.Ordinal);

		private bool _draggingNodes;
		private Num.Vector2 _dragLastWorld;

		private bool _panning;

		private bool _boxSelecting;
		private Num.Vector2 _boxAnchorWorld;
		private HashSet<string> _boxBaseSelection;

		private string _wireFromNode;
		private int _wireFromPort = -1;

		private Num.Vector2 _origin;
		private Num.Vector2 _size;

		/// <summary>Node whose context menu should open on the next frame, set when it is right-clicked.</summary>
		private string _contextNodeId;

		/// <summary>
		/// Where the canvas menu was opened, in graph coordinates. Captured on the click because the mouse
		/// keeps moving while the menu is up, and "Add Node" has to land where you right-clicked.
		/// </summary>
		private Num.Vector2 _menuWorld;

		public bool SnapToGrid;

		public float Zoom => _zoom;

		public int SelectionCount => _selection.Count;

		public void ResetZoom() => _zoom = 1f;

		public void ZoomBy(float factor) => SetZoomAbout(_zoom * factor, _origin + _size * 0.5f);

		public IReadOnlyCollection<string> Selection => _selection;

		public void ClearSelection() => _selection.Clear();

		public void SelectOnly(string id)
		{
			_selection.Clear();
			if (!string.IsNullOrEmpty(id))
				_selection.Add(id);
		}

		public void SelectAll(DialogueGraph graph)
		{
			_selection.Clear();
			if (graph == null)
				return;

			foreach (var node in graph.Nodes)
			{
				if (node?.Id != null)
					_selection.Add(node.Id);
			}
		}

		public void FrameAll(DialogueGraph graph)
		{
			_zoom = 1f;
			_pan = Num.Vector2.Zero;

			if (graph == null || graph.Nodes.Count == 0)
				return;

			float minX = float.MaxValue, minY = float.MaxValue;
			foreach (var node in graph.Nodes)
			{
				if (node == null) continue;
				minX = Math.Min(minX, node.EditorX);
				minY = Math.Min(minY, node.EditorY);
			}

			if (minX < float.MaxValue)
				_pan = new Num.Vector2(40f - minX, 40f - minY);
		}

		public void FrameNode(DialogueNode node)
		{
			if (node == null)
				return;

			_pan = new Num.Vector2(_size.X * 0.5f / _zoom - node.EditorX, _size.Y * 0.5f / _zoom - node.EditorY);
		}

		/// <summary>Centres on the selection, or on everything when nothing is selected.</summary>
		public void FrameSelection(DialogueGraph graph)
		{
			if (graph == null || _selection.Count == 0)
			{
				FrameAll(graph);
				return;
			}

			float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
			foreach (var node in graph.Nodes)
			{
				if (node?.Id == null || !_selection.Contains(node.Id))
					continue;

				minX = Math.Min(minX, node.EditorX);
				minY = Math.Min(minY, node.EditorY);
				maxX = Math.Max(maxX, node.EditorX + NodeWidth);
				maxY = Math.Max(maxY, node.EditorY + NodeHeight(node));
			}

			if (minX > maxX)
				return;

			_pan = new Num.Vector2(
				_size.X * 0.5f / _zoom - (minX + maxX) * 0.5f,
				_size.Y * 0.5f / _zoom - (minY + maxY) * 0.5f);
		}

		/// <summary>Centre of the visible area, in graph coordinates - where a new node should land.</summary>
		public Num.Vector2 ViewCentreInWorld() =>
			new(_size.X * 0.5f / _zoom - _pan.X, _size.Y * 0.5f / _zoom - _pan.Y);

		private Num.Vector2 ToScreen(float x, float y) =>
			new(_origin.X + (x + _pan.X) * _zoom, _origin.Y + (y + _pan.Y) * _zoom);

		private Num.Vector2 ToWorld(Num.Vector2 screen) =>
			new((screen.X - _origin.X) / _zoom - _pan.X, (screen.Y - _origin.Y) / _zoom - _pan.Y);

		public void Draw(DialogueGraphWindow window, DialogueGraph graph, ref string selectedId)
		{
			_origin = ImGui.GetCursorScreenPos();
			_size = ImGui.GetContentRegionAvail();
			if (_size.X < 1f || _size.Y < 1f)
				return;

			PruneSelection(graph);
			if (!string.IsNullOrEmpty(selectedId) && !_selection.Contains(selectedId))
				SelectOnly(selectedId);

			var draw = ImGui.GetWindowDrawList();
			draw.PushClipRect(_origin, _origin + _size, true);
			draw.AddRectFilled(_origin, _origin + _size, ImGui.GetColorU32(new Num.Vector4(0.11f, 0.11f, 0.13f, 1f)));
			DrawGrid(draw);

			// Independent of any item, so the wheel and middle-drag work over a node as well as over
			// empty space - which is what every other node editor does.
			var overCanvas = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)
			                 && ImGui.IsMouseHoveringRect(_origin, _origin + _size);

			HandleZoom(overCanvas);
			HandlePan(overCanvas);

			DrawWires(draw, graph);
			DrawNodeVisuals(draw, graph, selectedId);

			// Hit-testing runs front-to-back while drawing ran back-to-front: in ImGui the FIRST item to
			// claim the mouse wins, so the topmost node has to be submitted first. This is also why the
			// canvas-wide button below is submitted last - as the first item it used to swallow every
			// click, which is what stopped nodes being selectable or draggable at all.
			for (var i = graph.Nodes.Count - 1; i >= 0; i--)
				HandleNodeInteraction(window, graph.Nodes[i], ref selectedId);

			DrawPendingWire(draw, graph);

			ImGui.SetCursorScreenPos(_origin);
			ImGui.InvisibleButton("canvas", _size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight |
			                                       ImGuiButtonFlags.MouseButtonMiddle);
			var backgroundHovered = ImGui.IsItemHovered();
			var backgroundActive = ImGui.IsItemActive();

			HandleBoxSelect(draw, graph, backgroundActive, ref selectedId);

			if (backgroundHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && !_panning)
			{
				_menuWorld = ToWorld(ImGui.GetIO().MousePos);
				ImGui.OpenPopup("canvas-menu");
			}

			DrawCanvasMenu(window, graph, ref selectedId);
			DrawNodeMenu(window, graph, ref selectedId);
			HandleShortcuts(window, graph, ref selectedId);

			if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				_draggingNodes = false;
				if (_wireFromNode != null)
					FinishWire(window, graph);
			}

			DrawOverlay(draw, graph);

			draw.PopClipRect();
		}

		/// <summary>A node deleted elsewhere must not linger in the selection and resurrect on the next edit.</summary>
		private void PruneSelection(DialogueGraph graph)
		{
			if (_selection.Count == 0)
				return;

			if (graph == null)
			{
				_selection.Clear();
				return;
			}

			List<string> gone = null;
			foreach (var id in _selection)
			{
				if (graph.FindNode(id) == null)
					(gone ??= new List<string>()).Add(id);
			}

			if (gone == null)
				return;

			foreach (var id in gone)
				_selection.Remove(id);
		}

		private void DrawGrid(ImDrawListPtr draw)
		{
			var step = GridStep * 2f * _zoom;
			if (step < 8f)
				return;

			var colour = ImGui.GetColorU32(new Num.Vector4(1f, 1f, 1f, 0.04f));
			var offsetX = (_pan.X * _zoom) % step;
			var offsetY = (_pan.Y * _zoom) % step;

			for (var x = offsetX; x < _size.X; x += step)
				draw.AddLine(new Num.Vector2(_origin.X + x, _origin.Y), new Num.Vector2(_origin.X + x, _origin.Y + _size.Y), colour);

			for (var y = offsetY; y < _size.Y; y += step)
				draw.AddLine(new Num.Vector2(_origin.X, _origin.Y + y), new Num.Vector2(_origin.X + _size.X, _origin.Y + y), colour);
		}

		private void SetZoomAbout(float target, Num.Vector2 screenPivot)
		{
			var before = ToWorld(screenPivot);
			_zoom = Math.Clamp(target, MinZoom, MaxZoom);
			var after = ToWorld(screenPivot);
			_pan += after - before;
		}

		private void HandleZoom(bool overCanvas)
		{
			var wheel = ImGui.GetIO().MouseWheel;
			if (!overCanvas || Math.Abs(wheel) < 0.001f)
				return;

			// Zoom about the cursor, so the point under the mouse stays put.
			SetZoomAbout(_zoom * (1f + wheel * 0.1f), ImGui.GetIO().MousePos);
		}

		/// <summary>
		/// Middle-drag anywhere, or Alt+left-drag. Left-drag on empty space is box-select, so it cannot
		/// also be pan.
		/// </summary>
		private void HandlePan(bool overCanvas)
		{
			var io = ImGui.GetIO();

			if (!_panning && overCanvas && !_draggingNodes && _wireFromNode == null &&
			    (ImGui.IsMouseClicked(ImGuiMouseButton.Middle) || (io.KeyAlt && ImGui.IsMouseClicked(ImGuiMouseButton.Left))))
			{
				_panning = true;
			}

			if (!_panning)
				return;

			if (ImGui.IsMouseDown(ImGuiMouseButton.Middle) || ImGui.IsMouseDown(ImGuiMouseButton.Left))
				_pan += io.MouseDelta / _zoom;
			else
				_panning = false;
		}

		private void HandleBoxSelect(ImDrawListPtr draw, DialogueGraph graph, bool backgroundActive, ref string selectedId)
		{
			var io = ImGui.GetIO();

			if (!_boxSelecting && backgroundActive && !_panning && _wireFromNode == null &&
			    ImGui.IsMouseDragging(ImGuiMouseButton.Left))
			{
				_boxSelecting = true;
				// The press position, not this frame's: IsMouseDragging only fires past a threshold, so
				// anchoring on the current mouse would lose the first few pixels of the box.
				_boxAnchorWorld = ToWorld(io.MouseClickedPos[0]);
				// Additive drag keeps what was already selected; a plain drag starts fresh.
				_boxBaseSelection = io.KeyShift || io.KeyCtrl || io.KeySuper
					? new HashSet<string>(_selection, StringComparer.Ordinal)
					: new HashSet<string>(StringComparer.Ordinal);
			}

			if (!_boxSelecting)
				return;

			var anchor = ToScreen(_boxAnchorWorld.X, _boxAnchorWorld.Y);
			var current = io.MousePos;
			var min = new Num.Vector2(Math.Min(anchor.X, current.X), Math.Min(anchor.Y, current.Y));
			var max = new Num.Vector2(Math.Max(anchor.X, current.X), Math.Max(anchor.Y, current.Y));

			draw.AddRectFilled(min, max, ImGui.GetColorU32(new Num.Vector4(0.4f, 0.6f, 1f, 0.15f)));
			draw.AddRect(min, max, ImGui.GetColorU32(new Num.Vector4(0.5f, 0.7f, 1f, 0.8f)));

			var worldMin = ToWorld(min);
			var worldMax = ToWorld(max);

			_selection.Clear();
			foreach (var id in _boxBaseSelection)
				_selection.Add(id);

			foreach (var node in graph.Nodes)
			{
				if (node?.Id == null)
					continue;

				var nodeMaxX = node.EditorX + NodeWidth;
				var nodeMaxY = node.EditorY + NodeHeight(node);

				if (node.EditorX <= worldMax.X && nodeMaxX >= worldMin.X &&
				    node.EditorY <= worldMax.Y && nodeMaxY >= worldMin.Y)
				{
					_selection.Add(node.Id);
				}
			}

			if (_selection.Count > 0 && (selectedId == null || !_selection.Contains(selectedId)))
			{
				foreach (var id in _selection)
				{
					selectedId = id;
					break;
				}
			}

			if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				_boxSelecting = false;
				_boxBaseSelection = null;
				if (_selection.Count == 0)
					selectedId = null;
			}
		}

		private void DrawNodeVisuals(ImDrawListPtr draw, DialogueGraph graph, string selectedId)
		{
			foreach (var node in graph.Nodes)
			{
				if (node == null)
					continue;

				var height = NodeHeight(node);
				var min = ToScreen(node.EditorX, node.EditorY);
				var max = min + new Num.Vector2(NodeWidth * _zoom, height * _zoom);

				var isSelected = node.Id != null && _selection.Contains(node.Id);
				var isPrimary = string.Equals(selectedId, node.Id, StringComparison.Ordinal);
				var isEntry = string.Equals(graph.EntryNodeId, node.Id, StringComparison.Ordinal);

				draw.AddRectFilled(min, max, ImGui.GetColorU32(new Num.Vector4(0.17f, 0.18f, 0.21f, 0.97f)), 5f);
				draw.AddRectFilled(min, new Num.Vector2(max.X, min.Y + HeaderHeight * _zoom), HeaderColour(node), 5f);

				var outline = isPrimary ? new Num.Vector4(1f, 0.8f, 0.35f, 1f)
					: isSelected ? new Num.Vector4(0.95f, 0.7f, 0.3f, 0.75f)
					: new Num.Vector4(0f, 0f, 0f, 0.6f);
				draw.AddRect(min, max, ImGui.GetColorU32(outline), 5f, ImDrawFlags.None, isSelected ? 2f : 1f);

				if (isEntry)
					draw.AddCircleFilled(new Num.Vector2(min.X - 8f * _zoom, min.Y + 12f * _zoom), 4f * _zoom,
						ImGui.GetColorU32(new Num.Vector4(0.5f, 0.9f, 0.5f, 1f)));

				var font = ImGui.GetFont();
				var fontSize = ImGui.GetFontSize() * _zoom;
				draw.AddText(font, fontSize, min + new Num.Vector2(8f * _zoom, 5f * _zoom),
					ImGui.GetColorU32(new Num.Vector4(1f, 1f, 1f, 0.95f)), Truncate(node.DisplayName, 24));

				DrawNodeBody(draw, node, min, font, fontSize);

				// Input port.
				draw.AddCircleFilled(new Num.Vector2(min.X, min.Y + HeaderHeight * 0.5f * _zoom), PortRadius * _zoom,
					ImGui.GetColorU32(new Num.Vector4(0.7f, 0.7f, 0.75f, 1f)));

				var count = OutputCount(node);
				for (var i = 0; i < count; i++)
				{
					draw.AddCircleFilled(OutputPortScreen(node, min, height, i), PortRadius * _zoom,
						ImGui.GetColorU32(new Num.Vector4(0.95f, 0.75f, 0.35f, 1f)));
				}
			}
		}

		private void DrawNodeBody(ImDrawListPtr draw, DialogueNode node, Num.Vector2 min, ImFontPtr font, float fontSize)
		{
			var colour = ImGui.GetColorU32(new Num.Vector4(0.75f, 0.76f, 0.8f, 1f));
			var y = min.Y + (HeaderHeight + 4f) * _zoom;

			void Line(string text)
			{
				draw.AddText(font, fontSize * 0.9f, new Num.Vector2(min.X + 8f * _zoom, y), colour, Truncate(text, 26));
				y += RowHeight * _zoom;
			}

			switch (node)
			{
				case LineNode line:
					Line(string.IsNullOrEmpty(line.SpeakerId) ? "(no speaker)" : line.SpeakerId);
					break;
				case ChoiceNode choice:
					for (var i = 0; i < choice.Options.Count; i++)
						Line($"{i + 1}. {choice.Options[i]?.TextKey ?? "(unset)"}");
					break;
				case ConditionNode condition:
					Line(condition.Condition?.ToString() ?? "(no condition)");
					Line("true / false");
					break;
				case SetVariableNode set:
					Line(set.Assignment?.ToString() ?? "(no assignment)");
					break;
				case JumpNode:
					Line("goto");
					break;
				case UnknownNode unknown:
					Line(unknown.UnknownTypeId ?? "unknown");
					break;
			}
		}

		private void HandleNodeInteraction(DialogueGraphWindow window, DialogueNode node, ref string selectedId)
		{
			if (node?.Id == null)
				return;

			var io = ImGui.GetIO();
			var height = NodeHeight(node);
			var min = ToScreen(node.EditorX, node.EditorY);
			var max = min + new Num.Vector2(NodeWidth * _zoom, height * _zoom);

			// Ports first so they win the mouse where they overlap the body's edge.
			var count = OutputCount(node);
			for (var i = 0; i < count; i++)
			{
				var pos = OutputPortScreen(node, min, height, i);
				var radius = PortRadius * 2f * _zoom;
				ImGui.SetCursorScreenPos(pos - new Num.Vector2(radius, radius));
				ImGui.InvisibleButton($"port-{node.Id}-{i}", new Num.Vector2(radius * 2f, radius * 2f));

				if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left) && _wireFromNode == null && !_panning)
				{
					_wireFromNode = node.Id;
					_wireFromPort = i;
				}

				if (ImGui.IsItemHovered())
					ImGui.SetTooltip(PortTooltip(node, i));
			}

			ImGui.SetCursorScreenPos(min);
			ImGui.InvisibleButton($"node-{node.Id}", max - min,
				ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);

			var additive = io.KeyShift || io.KeyCtrl || io.KeySuper;

			if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !_panning)
			{
				if (additive)
				{
					if (!_selection.Add(node.Id))
					{
						_selection.Remove(node.Id);
						if (string.Equals(selectedId, node.Id, StringComparison.Ordinal))
							selectedId = FirstSelected();
					}
					else
					{
						selectedId = node.Id;
					}
				}
				else if (!_selection.Contains(node.Id))
				{
					// Clicking inside an existing multi-selection keeps it, so a drag moves the whole group.
					SelectOnly(node.Id);
					selectedId = node.Id;
				}
				else
				{
					selectedId = node.Id;
				}
			}

			if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
			{
				if (!_selection.Contains(node.Id))
					SelectOnly(node.Id);

				selectedId = node.Id;
				_contextNodeId = node.Id;
				ImGui.OpenPopup("node-menu");
			}

			if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left) &&
			    _wireFromNode == null && !_panning && !io.KeyAlt)
			{
				var world = ToWorld(io.MousePos);
				if (!_draggingNodes)
				{
					_draggingNodes = true;
					_dragLastWorld = world;
				}

				var delta = world - _dragLastWorld;
				_dragLastWorld = world;

				if (delta.X != 0f || delta.Y != 0f)
				{
					MoveSelection(window, node, delta);
					window.MarkDirty();
				}
			}
		}

		private void MoveSelection(DialogueGraphWindow window, DialogueNode grabbed, Num.Vector2 delta)
		{
			var graph = window.Graph;
			if (graph == null)
				return;

			foreach (var node in graph.Nodes)
			{
				if (node?.Id == null)
					continue;

				var moves = _selection.Contains(node.Id) || ReferenceEquals(node, grabbed);
				if (!moves)
					continue;

				node.EditorX += delta.X;
				node.EditorY += delta.Y;

				if (SnapToGrid)
				{
					node.EditorX = (float)Math.Round(node.EditorX / GridStep) * GridStep;
					node.EditorY = (float)Math.Round(node.EditorY / GridStep) * GridStep;
				}
			}
		}

		private string FirstSelected()
		{
			foreach (var id in _selection)
				return id;
			return null;
		}

		private static string PortTooltip(DialogueNode node, int index) => node switch
		{
			ConditionNode => index == 0 ? "true" : "false",
			ChoiceNode choice => index < choice.Options.Count
				? $"option {index + 1}: {choice.Options[index]?.TextKey ?? "(unset)"}"
				: "option",
			JumpNode => "jump target",
			_ => "next",
		};

		private void DrawWires(ImDrawListPtr draw, DialogueGraph graph)
		{
			var colour = ImGui.GetColorU32(new Num.Vector4(0.8f, 0.8f, 0.85f, 0.7f));

			foreach (var node in graph.Nodes)
			{
				if (node == null)
					continue;

				var min = ToScreen(node.EditorX, node.EditorY);
				var height = NodeHeight(node);
				var targets = OutputTargets(node);

				for (var i = 0; i < targets.Count; i++)
				{
					var target = graph.FindNode(targets[i]);
					if (target == null)
						continue;

					var from = OutputPortScreen(node, min, height, i);
					var toMin = ToScreen(target.EditorX, target.EditorY);
					var to = new Num.Vector2(toMin.X, toMin.Y + HeaderHeight * 0.5f * _zoom);
					Bezier(draw, from, to, colour);
				}
			}
		}

		private void DrawPendingWire(ImDrawListPtr draw, DialogueGraph graph)
		{
			if (_wireFromNode == null)
				return;

			var node = graph.FindNode(_wireFromNode);
			if (node == null)
			{
				_wireFromNode = null;
				return;
			}

			var min = ToScreen(node.EditorX, node.EditorY);
			var from = OutputPortScreen(node, min, NodeHeight(node), _wireFromPort);
			Bezier(draw, from, ImGui.GetIO().MousePos, ImGui.GetColorU32(new Num.Vector4(1f, 0.85f, 0.4f, 0.9f)));
		}

		private void Bezier(ImDrawListPtr draw, Num.Vector2 from, Num.Vector2 to, uint colour)
		{
			var offset = Math.Max(30f, Math.Abs(to.X - from.X) * 0.5f) * _zoom;
			draw.AddBezierCubic(from, from + new Num.Vector2(offset, 0f), to - new Num.Vector2(offset, 0f), to,
				colour, 2f * _zoom);
		}

		private void FinishWire(DialogueGraphWindow window, DialogueGraph graph)
		{
			var fromId = _wireFromNode;
			var port = _wireFromPort;
			_wireFromNode = null;
			_wireFromPort = -1;

			var source = graph.FindNode(fromId);
			if (source == null)
				return;

			var world = ToWorld(ImGui.GetIO().MousePos);
			foreach (var candidate in graph.Nodes)
			{
				if (candidate == null || candidate.Id == fromId)
					continue;

				if (world.X >= candidate.EditorX && world.X <= candidate.EditorX + NodeWidth &&
				    world.Y >= candidate.EditorY && world.Y <= candidate.EditorY + NodeHeight(candidate))
				{
					Connect(source, port, candidate.Id);
					window.MarkDirty();
					return;
				}
			}

			// Released over empty space: clear the wire, which is how a connection is removed.
			Connect(source, port, null);
			window.MarkDirty();
		}

		private static void Connect(DialogueNode node, int port, string targetId)
		{
			switch (node)
			{
				case LineNode line: line.NextId = targetId; break;
				case SetVariableNode set: set.NextId = targetId; break;
				case JumpNode jump: jump.TargetId = targetId; break;
				case ConditionNode condition:
					if (port == 0) condition.TrueNextId = targetId;
					else condition.FalseNextId = targetId;
					break;
				case ChoiceNode choice:
					if (port >= 0 && port < choice.Options.Count && choice.Options[port] != null)
						choice.Options[port].NextId = targetId;
					break;
			}
		}

		private void DrawCanvasMenu(DialogueGraphWindow window, DialogueGraph graph, ref string selectedId)
		{
			if (!ImGui.BeginPopup("canvas-menu"))
				return;

			var world = _menuWorld;

			if (ImGui.BeginMenu("Add Node"))
			{
				foreach (var (label, factory) in DialogueGraphWindow.NodeFactories)
				{
					if (ImGui.MenuItem(label))
					{
						var added = window.AddNode(factory(), world);
						if (added != null)
						{
							SelectOnly(added.Id);
							selectedId = added.Id;
						}
					}
				}

				ImGui.EndMenu();
			}

			if (ImGui.MenuItem("Paste", "Ctrl+V", false, window.HasClipboard))
				PasteFromClipboard(window, ref selectedId, world);

			ImGui.Separator();

			if (ImGui.MenuItem("Select All", "Ctrl+A"))
			{
				SelectAll(graph);
				selectedId = FirstSelected();
			}

			if (ImGui.MenuItem("Frame All", "Shift+F"))
				FrameAll(graph);

			if (ImGui.MenuItem("Reset Zoom", "Ctrl+0"))
				ResetZoom();

			var snap = SnapToGrid;
			if (ImGui.MenuItem("Snap To Grid", null, snap))
				SnapToGrid = !snap;

			ImGui.EndPopup();
		}

		private void DrawNodeMenu(DialogueGraphWindow window, DialogueGraph graph, ref string selectedId)
		{
			if (!ImGui.BeginPopup("node-menu"))
				return;

			var node = graph.FindNode(_contextNodeId);
			if (node == null)
			{
				ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
				return;
			}

			ImGui.TextDisabled(_selection.Count > 1 ? $"{_selection.Count} nodes" : node.DisplayName);
			ImGui.Separator();

			var isEntry = string.Equals(graph.EntryNodeId, node.Id, StringComparison.Ordinal);
			if (ImGui.MenuItem("Make Entry Node", null, isEntry, !isEntry))
				window.SetEntryNode(node.Id);

			if (ImGui.MenuItem("Duplicate", "Ctrl+D"))
				DuplicateSelection(window, ref selectedId);

			if (ImGui.MenuItem("Copy", "Ctrl+C"))
				window.CopyToClipboard(_selection);

			if (ImGui.MenuItem("Disconnect Outputs"))
			{
				foreach (var id in _selection)
					window.DisconnectOutputs(id);
			}

			ImGui.Separator();

			if (ImGui.MenuItem("Delete", "Del"))
			{
				window.DeleteNodes(_selection);
				_selection.Clear();
				selectedId = null;
			}

			ImGui.EndPopup();
		}

		private void HandleShortcuts(DialogueGraphWindow window, DialogueGraph graph, ref string selectedId)
		{
			if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || ImGui.IsAnyItemActive())
				return;

			var io = ImGui.GetIO();
			var command = io.KeyCtrl || io.KeySuper;

			if (ImGui.IsKeyPressed(ImGuiKey.Delete) || ImGui.IsKeyPressed(ImGuiKey.Backspace))
			{
				if (_selection.Count > 0)
				{
					window.DeleteNodes(_selection);
					_selection.Clear();
					selectedId = null;
				}
			}

			if (command && ImGui.IsKeyPressed(ImGuiKey.A))
			{
				SelectAll(graph);
				selectedId = FirstSelected();
			}

			if (command && ImGui.IsKeyPressed(ImGuiKey.D))
				DuplicateSelection(window, ref selectedId);

			if (command && ImGui.IsKeyPressed(ImGuiKey.C))
				window.CopyToClipboard(_selection);

			if (command && ImGui.IsKeyPressed(ImGuiKey.V))
				PasteFromClipboard(window, ref selectedId, ViewCentreInWorld());

			if (command && ImGui.IsKeyPressed(ImGuiKey._0))
				ResetZoom();

			if (!command && ImGui.IsKeyPressed(ImGuiKey.F))
			{
				if (io.KeyShift)
					FrameAll(graph);
				else
					FrameSelection(graph);
			}
		}

		/// <summary>Menu-bar entry point, where there is no selectedId to write back through.</summary>
		public void DuplicateSelectionFromMenu(DialogueGraphWindow window)
		{
			string ignored = null;
			DuplicateSelection(window, ref ignored);
		}

		private void DuplicateSelection(DialogueGraphWindow window, ref string selectedId)
		{
			var added = window.DuplicateNodes(_selection, new Num.Vector2(GridStep, GridStep));
			if (added == null || added.Count == 0)
				return;

			_selection.Clear();
			foreach (var id in added)
				_selection.Add(id);

			selectedId = added[0];
		}

		private void PasteFromClipboard(DialogueGraphWindow window, ref string selectedId, Num.Vector2 world)
		{
			var added = window.PasteClipboard(world);
			if (added == null || added.Count == 0)
				return;

			_selection.Clear();
			foreach (var id in added)
				_selection.Add(id);

			selectedId = added[0];
		}

		/// <summary>Zoom level and the controls that are not discoverable by looking at the canvas.</summary>
		private void DrawOverlay(ImDrawListPtr draw, DialogueGraph graph)
		{
			var font = ImGui.GetFont();
			var size = ImGui.GetFontSize();
			var colour = ImGui.GetColorU32(new Num.Vector4(1f, 1f, 1f, 0.35f));

			var text = $"{_zoom * 100f:0}%   {graph.Nodes.Count} nodes" +
			           (_selection.Count > 0 ? $"   {_selection.Count} selected" : "");
			draw.AddText(font, size, new Num.Vector2(_origin.X + 8f, _origin.Y + _size.Y - size * 2.4f), colour, text);

			draw.AddText(font, size, new Num.Vector2(_origin.X + 8f, _origin.Y + _size.Y - size * 1.2f), colour,
				"drag: move   wheel: zoom   middle/alt-drag: pan   right-click: menu   del: delete");
		}

		private Num.Vector2 OutputPortScreen(DialogueNode node, Num.Vector2 min, float height, int index)
		{
			var count = Math.Max(1, OutputCount(node));
			var spacing = (height - HeaderHeight) / (count + 1);
			var y = min.Y + (HeaderHeight + spacing * (index + 1)) * _zoom;
			return new Num.Vector2(min.X + NodeWidth * _zoom, y);
		}

		private static int OutputCount(DialogueNode node) => node switch
		{
			LineNode or SetVariableNode or JumpNode => 1,
			ConditionNode => 2,
			ChoiceNode choice => Math.Max(1, choice.Options.Count),
			_ => 0,
		};

		private static List<string> OutputTargets(DialogueNode node)
		{
			var list = new List<string>();
			switch (node)
			{
				case LineNode line: list.Add(line.NextId); break;
				case SetVariableNode set: list.Add(set.NextId); break;
				case JumpNode jump: list.Add(jump.TargetId); break;
				case ConditionNode condition:
					list.Add(condition.TrueNextId);
					list.Add(condition.FalseNextId);
					break;
				case ChoiceNode choice:
					foreach (var option in choice.Options)
						list.Add(option?.NextId);
					break;
			}

			return list;
		}

		private static float NodeHeight(DialogueNode node)
		{
			var rows = node switch
			{
				ChoiceNode choice => Math.Max(1, choice.Options.Count),
				ConditionNode => 2,
				EndNode => 0,
				_ => 1,
			};

			return HeaderHeight + 10f + rows * RowHeight;
		}

		private static uint HeaderColour(DialogueNode node) => ImGui.GetColorU32(node switch
		{
			LineNode => new Num.Vector4(0.24f, 0.38f, 0.55f, 1f),
			ChoiceNode => new Num.Vector4(0.45f, 0.34f, 0.55f, 1f),
			ConditionNode => new Num.Vector4(0.5f, 0.42f, 0.2f, 1f),
			SetVariableNode => new Num.Vector4(0.24f, 0.45f, 0.35f, 1f),
			JumpNode => new Num.Vector4(0.35f, 0.35f, 0.4f, 1f),
			EndNode => new Num.Vector4(0.45f, 0.25f, 0.25f, 1f),
			UnknownNode => new Num.Vector4(0.5f, 0.3f, 0.1f, 1f),
			_ => new Num.Vector4(0.3f, 0.3f, 0.34f, 1f),
		});

		// ASCII only: the editor's font atlas has no glyph for an ellipsis, and it renders as '?'.
		private static string Truncate(string text, int max) =>
			string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text.Substring(0, max - 3) + "...";
	}
}
