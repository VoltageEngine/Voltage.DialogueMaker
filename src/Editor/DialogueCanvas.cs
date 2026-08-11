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

		/// <summary>
		/// The wire under the cursor and the wire that is selected, each identified by the port it leaves
		/// rather than by an object: a connection is a field on the source node, not a thing that exists.
		/// </summary>
		private string _hoverWireNode;
		private int _hoverWirePort = -1;
		private string _selectedWireNode;
		private int _selectedWirePort = -1;

		/// <summary>A wire released over empty space, waiting for the node-type menu to say what to build there.</summary>
		private string _dropFromNode;
		private int _dropFromPort = -1;
		private Num.Vector2 _dropWorld;

		/// <summary>Set when a press lands on a wire, so the same press does not also start a box-select.</summary>
		private bool _suppressBoxSelect;

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

			// Before the wires are drawn, so the one under the cursor can be drawn as such. Hit-tests the
			// node rectangles itself rather than asking ImGui, which does not know yet - the node items are
			// submitted further down.
			UpdateWireHover(graph, overCanvas);

			DrawWires(draw, graph);
			DrawNodeVisuals(draw, graph, selectedId);

			// Hit-testing runs front-to-back while drawing ran back-to-front: in ImGui the FIRST item to
			// claim the mouse wins, so the topmost node has to be submitted first. This is also why the
			// canvas-wide button below is submitted last - as the first item it used to swallow every
			// click, which is what stopped nodes being selectable or draggable at all.
			for (var i = graph.Nodes.Count - 1; i >= 0; i--)
				HandleNodeInteraction(window, graph.Nodes[i], ref selectedId);

			DrawPendingWire(draw, graph);

			// Both are overlays, and both are submitted here rather than after the canvas button below:
			// the first item to claim the mouse wins, so anything meant to be clickable on top of the
			// canvas has to come first.
			DrawMinimap(draw, graph);
			DrawFindPanel(graph, ref selectedId);

			ImGui.SetCursorScreenPos(_origin);
			ImGui.InvisibleButton("canvas", _size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight |
			                                       ImGuiButtonFlags.MouseButtonMiddle);
			var backgroundHovered = ImGui.IsItemHovered();
			var backgroundActive = ImGui.IsItemActive();

			HandleWireClicks(backgroundHovered, ref selectedId);
			HandleBoxSelect(draw, graph, backgroundActive && !_suppressBoxSelect, ref selectedId);

			// A right-click on a wire is about that wire, so the canvas menu stays out of the way.
			if (backgroundHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && !_panning && _hoverWireNode == null)
			{
				_menuWorld = ToWorld(ImGui.GetIO().MousePos);
				ImGui.OpenPopup("canvas-menu");
			}

			DrawCanvasMenu(window, graph, ref selectedId);
			DrawNodeMenu(window, graph, ref selectedId);
			DrawWireMenu(window, graph);
			DrawWireDropMenu(window, ref selectedId);
			HandleShortcuts(window, graph, ref selectedId);

			if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				_draggingNodes = false;
				_suppressBoxSelect = false;
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
		/// Middle-drag anywhere, or Alt+left-drag, or space+left-drag. Left-drag on empty space is
		/// box-select, so it cannot also be pan - which is why the alternatives exist at all: a trackpad
		/// often has no middle button, and space-drag is the habit every canvas tool shares.
		/// </summary>
		private void HandlePan(bool overCanvas)
		{
			var io = ImGui.GetIO();

			var panModifier = io.KeyAlt || ImGui.IsKeyDown(ImGuiKey.Space);

			if (!_panning && overCanvas && !_draggingNodes && _wireFromNode == null &&
			    (ImGui.IsMouseClicked(ImGuiMouseButton.Middle) || (panModifier && ImGui.IsMouseClicked(ImGuiMouseButton.Left))))
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

		/// <summary>
		/// Selecting a wire, which is what makes one deletable. A connection has no node of its own to
		/// carry a menu, so clicking the curve is the only way to name it.
		/// </summary>
		private void HandleWireClicks(bool backgroundHovered, ref string selectedId)
		{
			if (!backgroundHovered || _panning)
				return;

			if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				if (_hoverWireNode != null)
				{
					_selectedWireNode = _hoverWireNode;
					_selectedWirePort = _hoverWirePort;
					_selection.Clear();
					selectedId = null;

					// This press belongs to the wire; without this it would also open a selection box.
					_suppressBoxSelect = true;
				}
				else
				{
					ClearWireSelection();
				}
			}

			if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && _hoverWireNode != null)
			{
				_selectedWireNode = _hoverWireNode;
				_selectedWirePort = _hoverWirePort;
				ImGui.OpenPopup("wire-menu");
			}
		}

		private void ClearWireSelection()
		{
			_selectedWireNode = null;
			_selectedWirePort = -1;
		}

		private void DrawWireMenu(DialogueGraphWindow window, DialogueGraph graph)
		{
			if (!ImGui.BeginPopup("wire-menu"))
				return;

			var source = graph.FindNode(_selectedWireNode);
			if (source == null)
			{
				ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
				return;
			}

			ImGui.TextDisabled($"{source.DisplayName} - {PortTooltip(source, _selectedWirePort)}");
			ImGui.Separator();

			if (ImGui.MenuItem("Delete Connection", "Del"))
				DeleteSelectedWire(window);

			ImGui.EndPopup();
		}

		private void DeleteSelectedWire(DialogueGraphWindow window)
		{
			var graph = window.Graph;
			var source = graph?.FindNode(_selectedWireNode);
			if (source == null)
			{
				ClearWireSelection();
				return;
			}

			window.PushUndo();
			Connect(source, _selectedWirePort, null);
			window.MarkDirty();
			ClearWireSelection();
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
			HandleInputPort(window, node);

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
				ClearWireSelection();

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
			    _wireFromNode == null && !_panning && !io.KeyAlt && !ImGui.IsKeyDown(ImGuiKey.Space))
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

		/// <summary>
		/// Dragging off a node's input port picks the incoming wire back up, the way every node editor lets
		/// you. Without it a connection can only be changed from the end that made it, so rerouting one
		/// means finding whichever node upstream happens to own it.
		/// </summary>
		private void HandleInputPort(DialogueGraphWindow window, DialogueNode node)
		{
			var centre = InputPortScreen(node);
			var radius = PortRadius * 2f * _zoom;

			ImGui.SetCursorScreenPos(centre - new Num.Vector2(radius, radius));
			ImGui.InvisibleButton($"in-{node.Id}", new Num.Vector2(radius * 2f, radius * 2f));

			var incoming = FindIncoming(window.Graph, node.Id);

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(incoming.Node == null
					? "in"
					: "in - drag off to move this connection");
			}

			if (!ImGui.IsItemActive() || !ImGui.IsMouseDragging(ImGuiMouseButton.Left) ||
			    _wireFromNode != null || _panning || incoming.Node == null)
			{
				return;
			}

			// Detached immediately, so the wire follows the cursor rather than staying drawn to a node the
			// user is in the middle of taking it away from. Letting go over nothing therefore removes it,
			// which is the other half of the same gesture.
			window.PushUndo();
			Connect(incoming.Node, incoming.Port, null);
			window.MarkDirty();

			_wireFromNode = incoming.Node.Id;
			_wireFromPort = incoming.Port;
		}

		/// <summary>The first wire pointing at a node, as the source node and the port it leaves.</summary>
		private static (DialogueNode Node, int Port) FindIncoming(DialogueGraph graph, string targetId)
		{
			if (graph == null || string.IsNullOrEmpty(targetId))
				return (null, -1);

			foreach (var node in graph.Nodes)
			{
				if (node?.Id == null)
					continue;

				var targets = OutputTargets(node);
				for (var i = 0; i < targets.Count; i++)
				{
					if (string.Equals(targets[i], targetId, StringComparison.Ordinal))
						return (node, i);
				}
			}

			return (null, -1);
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
			var hovered = ImGui.GetColorU32(new Num.Vector4(1f, 1f, 1f, 0.95f));
			var selected = ImGui.GetColorU32(new Num.Vector4(1f, 0.8f, 0.35f, 1f));

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

					var isSelected = IsWire(_selectedWireNode, _selectedWirePort, node.Id, i);
					var isHovered = IsWire(_hoverWireNode, _hoverWirePort, node.Id, i);

					var from = OutputPortScreen(node, min, height, i);
					var to = InputPortScreen(target);
					Bezier(draw, from, to, isSelected ? selected : isHovered ? hovered : colour,
						isSelected || isHovered ? 3.5f : 2f);

					// An arrow at the end: with several wires converging on one node, which way each runs is
					// otherwise only inferable from which end happens to be on the left.
					DrawArrowHead(draw, to, isSelected ? selected : isHovered ? hovered : colour);
				}
			}
		}

		private static bool IsWire(string a, int aPort, string b, int bPort) =>
			a != null && aPort == bPort && string.Equals(a, b, StringComparison.Ordinal);

		private void DrawArrowHead(ImDrawListPtr draw, Num.Vector2 tip, uint colour)
		{
			var size = 5f * _zoom;
			if (size < 2f)
				return;

			draw.AddTriangleFilled(
				tip,
				new Num.Vector2(tip.X - size * 1.6f, tip.Y - size * 0.8f),
				new Num.Vector2(tip.X - size * 1.6f, tip.Y + size * 0.8f),
				colour);
		}

		/// <summary>
		/// The wire nearest the cursor, if the cursor is close enough to one and not over a node - nodes sit
		/// on top of their wires, so a wire under a node must not answer for it.
		/// </summary>
		private void UpdateWireHover(DialogueGraph graph, bool overCanvas)
		{
			_hoverWireNode = null;
			_hoverWirePort = -1;

			if (!overCanvas || _wireFromNode != null || _panning || _boxSelecting || _draggingNodes)
				return;

			var mouse = ImGui.GetIO().MousePos;
			if (NodeAt(graph, ToWorld(mouse)) != null)
				return;

			// In screen pixels, so the grab area does not shrink to nothing when zoomed out.
			var best = 8f;

			foreach (var node in graph.Nodes)
			{
				if (node?.Id == null)
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
					var to = InputPortScreen(target);
					var distance = DistanceToWire(from, to, mouse);

					if (distance >= best)
						continue;

					best = distance;
					_hoverWireNode = node.Id;
					_hoverWirePort = i;
				}
			}
		}

		/// <summary>Node whose rectangle contains a graph-space point, topmost first.</summary>
		private static DialogueNode NodeAt(DialogueGraph graph, Num.Vector2 world)
		{
			for (var i = graph.Nodes.Count - 1; i >= 0; i--)
			{
				var node = graph.Nodes[i];
				if (node == null)
					continue;

				if (world.X >= node.EditorX && world.X <= node.EditorX + NodeWidth &&
				    world.Y >= node.EditorY && world.Y <= node.EditorY + NodeHeight(node))
				{
					return node;
				}
			}

			return null;
		}

		/// <summary>
		/// Distance from a point to the drawn curve, by flattening it into segments. Sixteen is plenty:
		/// the curve is smooth and the tolerance is eight pixels, so a finer walk cannot change the answer.
		/// </summary>
		private float DistanceToWire(Num.Vector2 from, Num.Vector2 to, Num.Vector2 point)
		{
			var (c1, c2) = BezierControls(from, to);

			var best = float.MaxValue;
			var previous = from;

			for (var step = 1; step <= 16; step++)
			{
				var t = step / 16f;
				var current = CubicAt(from, c1, c2, to, t);
				best = Math.Min(best, DistanceToSegment(previous, current, point));
				previous = current;
			}

			return best;
		}

		private static Num.Vector2 CubicAt(Num.Vector2 p0, Num.Vector2 p1, Num.Vector2 p2, Num.Vector2 p3, float t)
		{
			var u = 1f - t;
			return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
		}

		private static float DistanceToSegment(Num.Vector2 a, Num.Vector2 b, Num.Vector2 point)
		{
			var ab = b - a;
			var lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
			if (lengthSquared <= 0.0001f)
				return Num.Vector2.Distance(a, point);

			var t = Math.Clamp(Num.Vector2.Dot(point - a, ab) / lengthSquared, 0f, 1f);
			return Num.Vector2.Distance(a + ab * t, point);
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

		/// <summary>
		/// The curve's control points. Shared with hit-testing on purpose: a wire you cannot click where you
		/// see it is worse than one you cannot click at all.
		/// </summary>
		private (Num.Vector2 First, Num.Vector2 Second) BezierControls(Num.Vector2 from, Num.Vector2 to)
		{
			var offset = Math.Max(30f, Math.Abs(to.X - from.X) * 0.5f) * _zoom;
			return (from + new Num.Vector2(offset, 0f), to - new Num.Vector2(offset, 0f));
		}

		private void Bezier(ImDrawListPtr draw, Num.Vector2 from, Num.Vector2 to, uint colour, float thickness = 2f)
		{
			var (first, second) = BezierControls(from, to);
			draw.AddBezierCubic(from, first, second, to, colour, thickness * _zoom);
		}

		/// <summary>Where a wire lands on a node: the single input, halfway down the header.</summary>
		private Num.Vector2 InputPortScreen(DialogueNode node)
		{
			var min = ToScreen(node.EditorX, node.EditorY);
			return new Num.Vector2(min.X, min.Y + HeaderHeight * 0.5f * _zoom);
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
			var target = NodeAt(graph, world);

			if (target != null && !string.Equals(target.Id, fromId, StringComparison.Ordinal))
			{
				window.PushUndo();
				Connect(source, port, target.Id);
				window.MarkDirty();
				return;
			}

			// Released over empty space: offer to build what it should connect to. Dragging a wire into
			// nothing is how you say "and then something new happens here", and answering it with silence -
			// which is what clearing the wire looked like - loses the gesture and the connection at once.
			// Removing a connection now has its own gestures: select the wire and delete it, or drag it off
			// the input port and let go.
			_dropFromNode = fromId;
			_dropFromPort = port;
			_dropWorld = world;
			ImGui.OpenPopup("wire-drop-menu");
		}

		/// <summary>
		/// The node-type menu a wire dropped on empty space opens. Choosing a type builds it where the wire
		/// was released and connects it in one step.
		/// </summary>
		private void DrawWireDropMenu(DialogueGraphWindow window, ref string selectedId)
		{
			if (!ImGui.BeginPopup("wire-drop-menu"))
			{
				// Dismissed by clicking away: the wire stays as it was, which for a fresh drag means no
				// connection and for one dragged off an input means it is gone - both what was asked for.
				_dropFromNode = null;
				_dropFromPort = -1;
				return;
			}

			var source = window.Graph?.FindNode(_dropFromNode);
			if (source == null)
			{
				ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
				return;
			}

			ImGui.TextDisabled($"Connect {PortTooltip(source, _dropFromPort)} to a new");
			ImGui.Separator();

			foreach (var (label, factory) in DialogueGraphWindow.NodeFactories)
			{
				if (!ImGui.MenuItem(label))
					continue;

				// Centred vertically on the drop point so the new node's input lands under the cursor,
				// rather than its top-left corner.
				var added = window.AddNode(factory(), _dropWorld - new Num.Vector2(0f, HeaderHeight * 0.5f));
				if (added != null)
				{
					Connect(source, _dropFromPort, added.Id);
					window.MarkDirty();
					SelectOnly(added.Id);
					selectedId = added.Id;
				}

				_dropFromNode = null;
				_dropFromPort = -1;
			}

			ImGui.EndPopup();
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

			if (ImGui.MenuItem("Cut", "Ctrl+X"))
				CutSelection(window, ref selectedId);

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
				// A selected wire wins: selecting one clears the node selection, so this is never ambiguous,
				// and deleting the nodes at both ends is not what pressing Delete on a wire should mean.
				if (_selectedWireNode != null)
					DeleteSelectedWire(window);
				else if (_selection.Count > 0)
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

			if (command && ImGui.IsKeyPressed(ImGuiKey.X))
				CutSelection(window, ref selectedId);

			if (command && ImGui.IsKeyPressed(ImGuiKey.V))
				PasteFromClipboard(window, ref selectedId, ViewCentreInWorld());

			if (command && ImGui.IsKeyPressed(ImGuiKey._0))
				ResetZoom();

			if (command && (ImGui.IsKeyPressed(ImGuiKey.Equal) || ImGui.IsKeyPressed(ImGuiKey.KeypadAdd)))
				ZoomBy(1.2f);

			if (command && (ImGui.IsKeyPressed(ImGuiKey.Minus) || ImGui.IsKeyPressed(ImGuiKey.KeypadSubtract)))
				ZoomBy(1f / 1.2f);

			if (!command && ImGui.IsKeyPressed(ImGuiKey.F))
			{
				if (io.KeyShift)
					FrameAll(graph);
				else
					FrameSelection(graph);
			}

			NudgeSelection(window, io);
		}

		/// <summary>
		/// Arrow keys move the selection: a pixel at a time for lining things up by eye, or a whole grid
		/// step with shift. Placing a node exactly is otherwise a mouse-only job, and a mouse cannot do it.
		/// </summary>
		private void NudgeSelection(DialogueGraphWindow window, ImGuiIOPtr io)
		{
			var anyHeld = ImGui.IsKeyDown(ImGuiKey.LeftArrow) || ImGui.IsKeyDown(ImGuiKey.RightArrow) ||
			              ImGui.IsKeyDown(ImGuiKey.UpArrow) || ImGui.IsKeyDown(ImGuiKey.DownArrow);
			if (!anyHeld)
				_nudging = false;

			if (_selection.Count == 0)
				return;

			var step = io.KeyShift ? GridStep : 1f;
			var delta = Num.Vector2.Zero;

			if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow)) delta.X -= step;
			if (ImGui.IsKeyPressed(ImGuiKey.RightArrow)) delta.X += step;
			if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) delta.Y -= step;
			if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) delta.Y += step;

			if (delta == Num.Vector2.Zero)
				return;

			// Only the first press of a held run opens an undo step, so holding an arrow down is one
			// movement to undo rather than sixty.
			if (!_nudging)
			{
				_nudging = true;
				window.PushUndo();
			}

			MoveSelection(window, null, delta);
			window.MarkDirty();
		}

		/// <summary>True while an arrow key is held, so a run of repeats stays one undo step.</summary>
		private bool _nudging;

		/// <summary>Menu-bar entry points, where there is no selectedId to write back through.</summary>
		public void DuplicateSelectionFromMenu(DialogueGraphWindow window)
		{
			string ignored = null;
			DuplicateSelection(window, ref ignored);
		}

		public void PasteFromMenu(DialogueGraphWindow window)
		{
			string ignored = null;
			PasteFromClipboard(window, ref ignored, ViewCentreInWorld());
		}

		public void CutSelectionFromMenu(DialogueGraphWindow window)
		{
			string ignored = null;
			CutSelection(window, ref ignored);
		}

		private void CutSelection(DialogueGraphWindow window, ref string selectedId)
		{
			if (_selection.Count == 0)
				return;

			window.CutToClipboard(_selection);
			_selection.Clear();
			selectedId = null;
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

		#region Minimap

		private const float MinimapWidth = 180f;
		private const float MinimapHeight = 120f;
		private const float MinimapMargin = 10f;

		public bool ShowMinimap = true;

		/// <summary>
		/// The whole graph in the corner, with the visible area marked on it. Past a couple of screens'
		/// worth of nodes, panning blind is the only way to find out where anything is.
		/// </summary>
		private void DrawMinimap(ImDrawListPtr draw, DialogueGraph graph)
		{
			if (!ShowMinimap || graph.Nodes.Count == 0)
				return;

			// Not worth stealing a corner of a canvas that is barely bigger than the map itself.
			if (_size.X < MinimapWidth * 2.5f || _size.Y < MinimapHeight * 2.5f)
				return;

			if (!GraphBounds(graph, out var worldMin, out var worldMax))
				return;

			var mapMax = _origin + _size - new Num.Vector2(MinimapMargin, MinimapMargin);
			var mapMin = mapMax - new Num.Vector2(MinimapWidth, MinimapHeight);

			draw.AddRectFilled(mapMin, mapMax, ImGui.GetColorU32(new Num.Vector4(0.07f, 0.07f, 0.09f, 0.85f)), 4f);
			draw.AddRect(mapMin, mapMax, ImGui.GetColorU32(new Num.Vector4(1f, 1f, 1f, 0.15f)), 4f);

			// One scale for both axes, so the map is a picture of the graph rather than a stretched one.
			var span = worldMax - worldMin;
			var inner = new Num.Vector2(MinimapWidth, MinimapHeight) - new Num.Vector2(12f, 12f);
			var scale = Math.Min(inner.X / Math.Max(span.X, 1f), inner.Y / Math.Max(span.Y, 1f));
			var offset = mapMin + new Num.Vector2(6f, 6f) + (inner - span * scale) * 0.5f;

			Num.Vector2 ToMap(float x, float y) => offset + new Num.Vector2((x - worldMin.X) * scale, (y - worldMin.Y) * scale);

			foreach (var node in graph.Nodes)
			{
				if (node?.Id == null)
					continue;

				var min = ToMap(node.EditorX, node.EditorY);
				var max = ToMap(node.EditorX + NodeWidth, node.EditorY + NodeHeight(node));

				// A node is a couple of pixels here; without a floor the smaller ones vanish entirely.
				max.X = Math.Max(max.X, min.X + 2f);
				max.Y = Math.Max(max.Y, min.Y + 2f);

				draw.AddRectFilled(min, max, _selection.Contains(node.Id)
					? ImGui.GetColorU32(new Num.Vector4(1f, 0.8f, 0.35f, 1f))
					: HeaderColour(node));
			}

			// The visible area, clipped to the map so it stays readable when zoomed right in.
			var viewMin = ToWorld(_origin);
			var viewMax = ToWorld(_origin + _size);
			var rectMin = Num.Vector2.Clamp(ToMap(viewMin.X, viewMin.Y), mapMin, mapMax);
			var rectMax = Num.Vector2.Clamp(ToMap(viewMax.X, viewMax.Y), mapMin, mapMax);
			draw.AddRect(rectMin, rectMax, ImGui.GetColorU32(new Num.Vector4(1f, 1f, 1f, 0.7f)), 2f);

			ImGui.SetCursorScreenPos(mapMin);
			ImGui.InvisibleButton("minimap", new Num.Vector2(MinimapWidth, MinimapHeight),
				ImGuiButtonFlags.MouseButtonLeft);

			// Held rather than clicked, so you can scrub around the graph in one gesture.
			if (!ImGui.IsItemActive() || scale <= 0f)
				return;

			var local = (ImGui.GetIO().MousePos - offset) / scale;
			CentreOn(new Num.Vector2(worldMin.X + local.X, worldMin.Y + local.Y));
		}

		private static bool GraphBounds(DialogueGraph graph, out Num.Vector2 min, out Num.Vector2 max)
		{
			float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;

			foreach (var node in graph.Nodes)
			{
				if (node == null)
					continue;

				minX = Math.Min(minX, node.EditorX);
				minY = Math.Min(minY, node.EditorY);
				maxX = Math.Max(maxX, node.EditorX + NodeWidth);
				maxY = Math.Max(maxY, node.EditorY + NodeHeight(node));
			}

			min = new Num.Vector2(minX, minY);
			max = new Num.Vector2(maxX, maxY);
			return minX <= maxX;
		}

		/// <summary>Puts a graph point in the middle of the view, leaving the zoom alone.</summary>
		private void CentreOn(Num.Vector2 world) =>
			_pan = new Num.Vector2(_size.X * 0.5f / _zoom - world.X, _size.Y * 0.5f / _zoom - world.Y);

		#endregion

		#region Find

		private const int MaxFindRows = 10;

		private bool _findOpen;
		private bool _findFocusNext;
		private string _findQuery = string.Empty;
		private readonly List<string> _findResults = new();

		public void ToggleFind()
		{
			_findOpen = !_findOpen;
			_findFocusNext = _findOpen;
		}

		/// <summary>
		/// Search across the text a node actually carries - speaker, keys, variables, id - not just its
		/// title. In a graph of any size the node you want is one you remember the words of, and the title
		/// is generated.
		/// </summary>
		private void DrawFindPanel(DialogueGraph graph, ref string selectedId)
		{
			if (!_findOpen)
				return;

			// Matched before the panel is opened, because its height depends on how many rows there are and
			// this build of ImGui.NET has no auto-resizing child.
			CollectMatches(graph);

			var rows = Math.Min(_findResults.Count, MaxFindRows);
			var lineHeight = ImGui.GetTextLineHeightWithSpacing();
			var height = ImGui.GetFrameHeightWithSpacing() + lineHeight * (rows + 1) + 12f;

			ImGui.SetCursorScreenPos(_origin + new Num.Vector2(MinimapMargin, MinimapMargin));

			if (ImGui.BeginChild("find", new Num.Vector2(280f, height), true, ImGuiWindowFlags.NoScrollbar))
			{
				if (_findFocusNext)
				{
					ImGui.SetKeyboardFocusHere();
					_findFocusNext = false;
				}

				ImGui.SetNextItemWidth(-1f);
				ImGui.InputTextWithHint("##find", "Find a node...", ref _findQuery, 128);

				if (_findResults.Count == 0)
				{
					ImGui.TextDisabled(string.IsNullOrWhiteSpace(_findQuery) ? "Type to search." : "No matches.");
				}
				else
				{
					ImGui.TextDisabled($"{_findResults.Count} match(es)");

					// Capped: the list is a way to reach a node, not a report. Refine the query instead.
					var shown = Math.Min(_findResults.Count, MaxFindRows);
					for (var i = 0; i < shown; i++)
					{
						var node = graph.FindNode(_findResults[i]);
						if (node == null)
							continue;

						ImGui.PushID(i);
						if (ImGui.Selectable(Truncate(node.DisplayName, 36)))
						{
							SelectOnly(node.Id);
							selectedId = node.Id;
							FrameNode(node);
						}

						ImGui.PopID();
					}
				}

				if (ImGui.IsKeyPressed(ImGuiKey.Escape))
					_findOpen = false;
			}

			ImGui.EndChild();
		}

		private void CollectMatches(DialogueGraph graph)
		{
			_findResults.Clear();
			if (string.IsNullOrWhiteSpace(_findQuery))
				return;

			var query = _findQuery.Trim();
			foreach (var node in graph.Nodes)
			{
				if (node?.Id != null && MatchesQuery(node, query))
					_findResults.Add(node.Id);
			}
		}

		private static bool MatchesQuery(DialogueNode node, string query)
		{
			if (Contains(node.Id, query) || Contains(node.DisplayName, query))
				return true;

			switch (node)
			{
				case LineNode line:
					return Contains(line.SpeakerId, query) || Contains(line.TextKey, query);
				case ChoiceNode choice:
					if (Contains(choice.PromptKey, query))
						return true;
					foreach (var option in choice.Options)
					{
						if (option != null && Contains(option.TextKey, query))
							return true;
					}

					return false;
				case ConditionNode condition:
					return Contains(condition.Condition?.Variable, query);
				case SetVariableNode set:
					return Contains(set.Assignment?.Variable, query);
				case EndNode end:
					return Contains(end.EndTag, query);
				case UnknownNode unknown:
					return Contains(unknown.UnknownTypeId, query);
				default:
					return false;
			}
		}

		private static bool Contains(string haystack, string needle) =>
			haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

		#endregion

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
				"wheel: zoom   middle/space/alt-drag: pan   right-click: menu   ctrl+z: undo   ctrl+f: find");
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
