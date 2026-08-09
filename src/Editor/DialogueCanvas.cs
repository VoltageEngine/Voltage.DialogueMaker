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

		private Num.Vector2 _pan;
		private float _zoom = 1f;

		private string _dragNodeId;
		private Num.Vector2 _dragGrabOffset;

		private string _wireFromNode;
		private int _wireFromPort = -1;

		private Num.Vector2 _origin;
		private Num.Vector2 _size;

		public void ResetZoom() => _zoom = 1f;

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

			_pan = new Num.Vector2(_size.X * 0.5f - node.EditorX * _zoom, _size.Y * 0.5f - node.EditorY * _zoom) / _zoom;
		}

		/// <summary>Centre of the visible area, in graph coordinates — where a new node should land.</summary>
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

			var draw = ImGui.GetWindowDrawList();
			draw.PushClipRect(_origin, _origin + _size, true);
			draw.AddRectFilled(_origin, _origin + _size, ImGui.GetColorU32(new Num.Vector4(0.11f, 0.11f, 0.13f, 1f)));
			DrawGrid(draw);

			// Background first so nodes, drawn after, win the hover test.
			ImGui.SetCursorScreenPos(_origin);
			ImGui.InvisibleButton("canvas", _size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight |
			                                       ImGuiButtonFlags.MouseButtonMiddle);
			var backgroundHovered = ImGui.IsItemHovered();

			HandleZoom(backgroundHovered);
			HandlePan(backgroundHovered);

			DrawWires(draw, graph);
			DrawNodes(draw, window, graph, ref selectedId);
			DrawPendingWire(draw, graph);

			if (backgroundHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && _wireFromNode == null)
				selectedId = null;

			if (backgroundHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
				ImGui.OpenPopup("canvas-add");

			DrawAddPopup(window);

			if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				_dragNodeId = null;
				if (_wireFromNode != null)
					FinishWire(window, graph);
			}

			draw.PopClipRect();
		}

		private void DrawGrid(ImDrawListPtr draw)
		{
			var step = 48f * _zoom;
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

		private void HandleZoom(bool hovered)
		{
			var wheel = ImGui.GetIO().MouseWheel;
			if (!hovered || Math.Abs(wheel) < 0.001f)
				return;

			// Zoom about the cursor, so the point under the mouse stays put.
			var before = ToWorld(ImGui.GetIO().MousePos);
			_zoom = Math.Clamp(_zoom * (1f + wheel * 0.1f), 0.25f, 2.5f);
			var after = ToWorld(ImGui.GetIO().MousePos);
			_pan += after - before;
		}

		private void HandlePan(bool hovered)
		{
			var io = ImGui.GetIO();
			var panning = ImGui.IsMouseDragging(ImGuiMouseButton.Middle) ||
			              (hovered && _dragNodeId == null && _wireFromNode == null &&
			               ImGui.IsMouseDragging(ImGuiMouseButton.Left));

			if (panning)
				_pan += io.MouseDelta / _zoom;
		}

		private void DrawNodes(ImDrawListPtr draw, DialogueGraphWindow window, DialogueGraph graph, ref string selectedId)
		{
			for (var i = 0; i < graph.Nodes.Count; i++)
			{
				var node = graph.Nodes[i];
				if (node == null)
					continue;

				var height = NodeHeight(node);
				var min = ToScreen(node.EditorX, node.EditorY);
				var max = min + new Num.Vector2(NodeWidth * _zoom, height * _zoom);

				var isSelected = string.Equals(selectedId, node.Id, StringComparison.Ordinal);
				var isEntry = string.Equals(graph.EntryNodeId, node.Id, StringComparison.Ordinal);

				draw.AddRectFilled(min, max, ImGui.GetColorU32(new Num.Vector4(0.17f, 0.18f, 0.21f, 0.97f)), 5f);
				draw.AddRectFilled(min, new Num.Vector2(max.X, min.Y + HeaderHeight * _zoom), HeaderColour(node), 5f);
				draw.AddRect(min, max,
					isSelected ? ImGui.GetColorU32(new Num.Vector4(1f, 0.8f, 0.35f, 1f))
					           : ImGui.GetColorU32(new Num.Vector4(0f, 0f, 0f, 0.6f)),
					5f, ImDrawFlags.None, isSelected ? 2f : 1f);

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

				DrawOutputPorts(draw, node, min, height);
				HandleNodeInteraction(window, node, min, max, ref selectedId);
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
					Line("→");
					break;
				case UnknownNode unknown:
					Line(unknown.UnknownTypeId ?? "unknown");
					break;
			}
		}

		private void DrawOutputPorts(ImDrawListPtr draw, DialogueNode node, Num.Vector2 min, float height)
		{
			var count = OutputCount(node);
			for (var i = 0; i < count; i++)
			{
				var pos = OutputPortScreen(node, min, height, i);
				draw.AddCircleFilled(pos, PortRadius * _zoom, ImGui.GetColorU32(new Num.Vector4(0.95f, 0.75f, 0.35f, 1f)));

				var id = $"port-{node.Id}-{i}";
				ImGui.SetCursorScreenPos(pos - new Num.Vector2(PortRadius * 2f * _zoom, PortRadius * 2f * _zoom));
				ImGui.InvisibleButton(id, new Num.Vector2(PortRadius * 4f * _zoom, PortRadius * 4f * _zoom));

				if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left) && _wireFromNode == null)
				{
					_wireFromNode = node.Id;
					_wireFromPort = i;
				}
			}
		}

		private void HandleNodeInteraction(DialogueGraphWindow window, DialogueNode node, Num.Vector2 min,
			Num.Vector2 max, ref string selectedId)
		{
			ImGui.SetCursorScreenPos(min);
			ImGui.InvisibleButton($"node-{node.Id}", max - min,
				ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);

			if (ImGui.IsItemClicked(ImGuiMouseButton.Left) || ImGui.IsItemClicked(ImGuiMouseButton.Right))
				selectedId = node.Id;

			if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left) && _wireFromNode == null)
			{
				if (_dragNodeId != node.Id)
				{
					_dragNodeId = node.Id;
					_dragGrabOffset = ToWorld(ImGui.GetIO().MousePos) - new Num.Vector2(node.EditorX, node.EditorY);
				}

				var target = ToWorld(ImGui.GetIO().MousePos) - _dragGrabOffset;
				node.EditorX = target.X;
				node.EditorY = target.Y;
				window.MarkDirty();
			}

			if (ImGui.BeginPopupContextItem($"ctx-{node.Id}"))
			{
				if (ImGui.MenuItem("Delete"))
					window.DeleteNode(node.Id);
				ImGui.EndPopup();
			}
		}

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

		private void DrawAddPopup(DialogueGraphWindow window)
		{
			if (!ImGui.BeginPopup("canvas-add"))
				return;

			var world = ToWorld(ImGui.GetIO().MousePos);
			foreach (var (label, factory) in DialogueGraphWindow.NodeFactories)
			{
				if (ImGui.MenuItem(label))
					window.AddNode(factory(), world);
			}

			ImGui.EndPopup();
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

		private static string Truncate(string text, int max) =>
			string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text.Substring(0, max - 1) + "…";
	}
}
