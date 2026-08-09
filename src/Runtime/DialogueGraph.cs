using System;
using System.Collections.Generic;

namespace Voltage.Dialogue
{
	/// <summary>
	/// A branching conversation, stored as a <c>.vdialogue</c> asset.
	///
	/// <para>The graph is <b>shared</b>: every runner reading the same file gets the same instance, so it
	/// holds no playback state. A conversation's position, variables and history live on
	/// <see cref="DialogueRunner"/>.</para>
	/// </summary>
	public class DialogueGraph
	{
		/// <summary>Where playback begins. Empty falls back to the first node.</summary>
		public string EntryNodeId;

		public List<DialogueNode> Nodes = new();

		public List<DialogueVariableDef> Variables = new();

		/// <summary>
		/// Built on demand and dropped whenever the graph is edited. Not playback state — it is a pure
		/// function of <see cref="Nodes"/>, so sharing it across runners is safe.
		/// </summary>
		private Dictionary<string, DialogueNode> _byId;

		/// <summary>Call after any structural edit. The editor does this centrally, once per frame.</summary>
		public void InvalidateIndex() => _byId = null;

		public DialogueNode FindNode(string id)
		{
			if (string.IsNullOrEmpty(id))
				return null;

			var index = _byId;
			if (index == null)
			{
				index = new Dictionary<string, DialogueNode>(Nodes.Count, StringComparer.Ordinal);
				foreach (var node in Nodes)
				{
					if (node?.Id != null)
						index[node.Id] = node;
				}

				_byId = index;
			}

			return index.TryGetValue(id, out var found) ? found : null;
		}

		/// <summary>The node playback starts at, or null when the graph is empty.</summary>
		public DialogueNode EntryNode()
		{
			var entry = FindNode(EntryNodeId);
			if (entry != null)
				return entry;

			return Nodes.Count > 0 ? Nodes[0] : null;
		}

		public DialogueVariableDef FindVariable(string name)
		{
			if (string.IsNullOrEmpty(name))
				return null;

			foreach (var variable in Variables)
			{
				if (string.Equals(variable?.Name, name, StringComparison.Ordinal))
					return variable;
			}

			return null;
		}

		/// <summary>Generates an id not already used by a node in this graph.</summary>
		public string NewNodeId()
		{
			for (var attempt = 0; ; attempt++)
			{
				var id = Guid.NewGuid().ToString("N").Substring(0, 8);
				if (FindNode(id) == null)
					return id;

				if (attempt > 64)
					return Guid.NewGuid().ToString("N");
			}
		}

		public void AddNode(DialogueNode node)
		{
			if (node == null)
				return;

			if (string.IsNullOrEmpty(node.Id))
				node.Id = NewNodeId();

			Nodes.Add(node);
			InvalidateIndex();

			if (string.IsNullOrEmpty(EntryNodeId))
				EntryNodeId = node.Id;
		}

		/// <summary>
		/// Removes the node and clears every wire pointing at it, so a delete can never leave a dangling
		/// reference behind.
		/// </summary>
		public bool RemoveNode(string id)
		{
			var node = FindNode(id);
			if (node == null)
				return false;

			Nodes.Remove(node);
			InvalidateIndex();

			foreach (var other in Nodes)
				ClearReferencesTo(other, id);

			if (string.Equals(EntryNodeId, id, StringComparison.Ordinal))
				EntryNodeId = Nodes.Count > 0 ? Nodes[0].Id : null;

			return true;
		}

		private static void ClearReferencesTo(DialogueNode node, string removedId)
		{
			switch (node)
			{
				case LineNode line when line.NextId == removedId:
					line.NextId = null;
					break;
				case SetVariableNode set when set.NextId == removedId:
					set.NextId = null;
					break;
				case JumpNode jump when jump.TargetId == removedId:
					jump.TargetId = null;
					break;
				case ConditionNode condition:
					if (condition.TrueNextId == removedId)
						condition.TrueNextId = null;
					if (condition.FalseNextId == removedId)
						condition.FalseNextId = null;
					break;
				case ChoiceNode choice:
					foreach (var option in choice.Options)
					{
						if (option != null && option.NextId == removedId)
							option.NextId = null;
					}

					break;
			}
		}

		/// <summary>
		/// Authoring mistakes that would only surface mid-conversation otherwise: dangling wires, ids that
		/// collide, variables nothing declares, and nodes no path reaches.
		/// </summary>
		public DialogueValidationReport Validate()
		{
			var report = new DialogueValidationReport();
			var seenIds = new HashSet<string>(StringComparer.Ordinal);
			var outputs = new List<string>();

			foreach (var node in Nodes)
			{
				if (node == null)
				{
					report.Add(new DialogueValidationIssue { Message = "Graph contains a null node." });
					continue;
				}

				if (string.IsNullOrEmpty(node.Id))
				{
					report.Add(new DialogueValidationIssue { Message = $"A {node.DisplayName} node has no id." });
					continue;
				}

				if (!seenIds.Add(node.Id))
				{
					report.Add(new DialogueValidationIssue
					{
						NodeId = node.Id,
						Message = "Duplicate node id — only the first is reachable by wires.",
					});
				}

				if (node is UnknownNode unknown)
				{
					report.Add(new DialogueValidationIssue
					{
						NodeId = node.Id,
						Message = $"Node type '{unknown.UnknownTypeId}' is not registered — the plugin that " +
						          "declares it is probably not installed. Its content is preserved as-is and " +
						          "will be written back unchanged, but nothing after it is reachable.",
					});
				}

				outputs.Clear();
				node.CollectOutputs(outputs);
				foreach (var target in outputs)
				{
					if (FindNode(target) == null)
					{
						report.Add(new DialogueValidationIssue
						{
							NodeId = node.Id,
							Message = $"Points at '{target}', which no longer exists.",
						});
					}
				}

				ValidateVariableUse(node, report);
			}

			if (Nodes.Count > 0 && EntryNode() == null)
			{
				report.Add(new DialogueValidationIssue
				{
					Message = $"Entry node '{EntryNodeId}' does not exist.",
				});
			}

			ReportUnreachable(report);
			return report;
		}

		private void ValidateVariableUse(DialogueNode node, DialogueValidationReport report)
		{
			switch (node)
			{
				case ConditionNode condition:
					CheckCondition(node.Id, condition.Condition, report);
					break;
				case ChoiceNode choice:
					foreach (var option in choice.Options)
					{
						if (option?.Condition != null)
							CheckCondition(node.Id, option.Condition, report);
					}

					break;
				case SetVariableNode set:
					var assignment = set.Assignment;
					if (assignment == null || string.IsNullOrEmpty(assignment.Variable))
					{
						report.Add(new DialogueValidationIssue { NodeId = node.Id, Message = "Assignment has no variable." });
					}
					else if (FindVariable(assignment.Variable) == null)
					{
						report.Add(new DialogueValidationIssue
						{
							NodeId = node.Id,
							Message = $"Assigns undeclared variable '{assignment.Variable}'.",
						});
					}
					else if (assignment.NeedsValue && assignment.Value == null)
					{
						report.Add(new DialogueValidationIssue { NodeId = node.Id, Message = "Assignment has no value." });
					}

					break;
			}
		}

		private void CheckCondition(string nodeId, DialogueCondition condition, DialogueValidationReport report)
		{
			if (condition == null || string.IsNullOrEmpty(condition.Variable))
			{
				report.Add(new DialogueValidationIssue { NodeId = nodeId, Message = "Condition has no variable." });
				return;
			}

			if (FindVariable(condition.Variable) == null)
			{
				report.Add(new DialogueValidationIssue
				{
					NodeId = nodeId,
					Message = $"Tests undeclared variable '{condition.Variable}'.",
				});
			}

			if (condition.NeedsValue && condition.Value == null)
				report.Add(new DialogueValidationIssue { NodeId = nodeId, Message = "Condition has no value to compare against." });
		}

		private void ReportUnreachable(DialogueValidationReport report)
		{
			var entry = EntryNode();
			if (entry == null)
				return;

			var reached = new HashSet<string>(StringComparer.Ordinal);
			var pending = new Stack<DialogueNode>();
			var outputs = new List<string>();

			pending.Push(entry);
			reached.Add(entry.Id);

			while (pending.Count > 0)
			{
				outputs.Clear();
				pending.Pop().CollectOutputs(outputs);

				foreach (var target in outputs)
				{
					if (!reached.Add(target))
						continue;

					var next = FindNode(target);
					if (next != null)
						pending.Push(next);
				}
			}

			foreach (var node in Nodes)
			{
				if (node?.Id != null && !reached.Contains(node.Id))
				{
					report.Add(new DialogueValidationIssue
					{
						NodeId = node.Id,
						Message = $"{node.DisplayName} is unreachable from the entry node.",
					});
				}
			}
		}
	}
}
