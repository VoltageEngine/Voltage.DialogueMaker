using System;
using System.Collections.Generic;

namespace Voltage.Dialogue
{
	public enum DialogueState
	{
		/// <summary>Nothing started yet, or the session was reset.</summary>
		Idle,

		/// <summary>A line is on screen. <see cref="DialogueSession.Advance"/> moves on.</summary>
		Line,

		/// <summary>Waiting for <see cref="DialogueSession.Choose"/>.</summary>
		Choice,

		/// <summary>The conversation ended.</summary>
		Finished,
	}

	/// <summary>
	/// Walks a <see cref="DialogueGraph"/>, free of any engine or scene dependency so a conversation runs
	/// the same headlessly as in a scene. The graph is shared and read-only; all changing state is here.
	/// </summary>
	public sealed class DialogueSession
	{
		/// <summary>
		/// A jump cycle that passes through no line or choice would otherwise spin forever. Hitting this
		/// is always an authoring bug, so it ends the conversation and reports rather than hanging.
		/// </summary>
		private const int MaxStepsPerAdvance = 1024;

		private readonly List<DialogueChoiceOption> _available = new();

		public DialogueGraph Graph { get; private set; }
		public DialogueVariables Variables { get; }

		public DialogueState State { get; private set; } = DialogueState.Idle;

		/// <summary>Set while <see cref="State"/> is <see cref="DialogueState.Line"/>.</summary>
		public LineNode CurrentLine { get; private set; }

		/// <summary>Set while <see cref="State"/> is <see cref="DialogueState.Choice"/>.</summary>
		public ChoiceNode CurrentChoice { get; private set; }

		/// <summary>Options whose conditions pass. <see cref="Choose"/> indexes this, so a hidden option never shifts a click.</summary>
		public IReadOnlyList<DialogueChoiceOption> AvailableOptions => _available;

		/// <summary>The tag of the <see cref="EndNode"/> that finished the conversation, when it had one.</summary>
		public string EndTag { get; private set; }

		public event Action<LineNode> LineStarted;
		public event Action<ChoiceNode, IReadOnlyList<DialogueChoiceOption>> ChoicesPresented;
		public event Action<string> Finished;

		/// <summary>An event rather than a log call so this type stays engine-free; the component forwards it.</summary>
		public event Action<string> Warning;

		public DialogueSession(DialogueVariables variables = null) =>
			Variables = variables ?? new DialogueVariables();

		/// <summary>
		/// Begins a conversation, seeding variables from the graph's declarations. Pass
		/// <paramref name="resetVariables"/> false to carry state across graphs — a campaign-wide store.
		/// </summary>
		public void Start(DialogueGraph graph, string fromNodeId = null, bool resetVariables = true)
		{
			Graph = graph;
			CurrentLine = null;
			CurrentChoice = null;
			EndTag = null;
			_available.Clear();

			if (resetVariables)
				Variables.ResetTo(graph);

			if (graph == null)
			{
				Warn("Started with no graph.");
				Finish(null);
				return;
			}

			var start = string.IsNullOrEmpty(fromNodeId) ? graph.EntryNode() : graph.FindNode(fromNodeId);
			if (start == null)
			{
				Warn(string.IsNullOrEmpty(fromNodeId)
					? "Graph has no entry node."
					: $"Start node '{fromNodeId}' does not exist.");
				Finish(null);
				return;
			}

			State = DialogueState.Idle;
			EnterFrom(start);
		}

		/// <summary>
		/// Continues past the current line. Returns false when there was no line to advance — calling this
		/// while a choice is pending is a caller bug, not something to silently swallow.
		/// </summary>
		public bool Advance()
		{
			if (State != DialogueState.Line || CurrentLine == null)
				return false;

			var nextId = CurrentLine.NextId;
			if (string.IsNullOrEmpty(nextId))
			{
				// A line with nothing after it is a normal way to end a branch.
				Finish(null);
				return true;
			}

			return Continue(nextId);
		}

		/// <summary>Picks an option by its index into <see cref="AvailableOptions"/>.</summary>
		public bool Choose(int index)
		{
			if (State != DialogueState.Choice)
				return false;

			if (index < 0 || index >= _available.Count)
			{
				Warn($"Choice index {index} is out of range (0..{_available.Count - 1}).");
				return false;
			}

			var chosen = _available[index];
			if (string.IsNullOrEmpty(chosen.NextId))
			{
				Finish(null);
				return true;
			}

			return Continue(chosen.NextId);
		}

		/// <summary>Ends the conversation early. Safe to call from any state.</summary>
		public void Cancel()
		{
			if (State != DialogueState.Finished)
				Finish(null);
		}

		private bool Continue(string nodeId)
		{
			var next = Graph?.FindNode(nodeId);
			if (next == null)
			{
				Warn($"Wire points at '{nodeId}', which does not exist. Ending the conversation.");
				Finish(null);
				return false;
			}

			EnterFrom(next);
			return true;
		}

		/// <summary>Runs every node that does not need the player, until one does or the conversation ends.</summary>
		private void EnterFrom(DialogueNode node)
		{
			var current = node;

			for (var step = 0; step < MaxStepsPerAdvance; step++)
			{
				switch (current)
				{
					case LineNode line:
						CurrentLine = line;
						CurrentChoice = null;
						State = DialogueState.Line;
						LineStarted?.Invoke(line);
						return;

					case ChoiceNode choice:
						PresentChoice(choice);
						return;

					case EndNode end:
						Finish(end.EndTag);
						return;

					case SetVariableNode set:
						Variables.Apply(set.Assignment);
						current = Step(set.NextId);
						break;

					case ConditionNode condition:
						var branch = Variables.Evaluate(condition.Condition)
							? condition.TrueNextId
							: condition.FalseNextId;
						current = Step(branch);
						break;

					case JumpNode jump:
						current = Step(jump.TargetId);
						break;

					case UnknownNode unknown:
						Warn($"Reached a node of unregistered type '{unknown.UnknownTypeId}' — the plugin that " +
						     "declares it is not loaded, so the conversation cannot continue past it.");
						Finish(null);
						return;

					case null:
						Finish(null);
						return;

					default:
						Warn($"Unhandled node type '{current.GetType().Name}'. Ending the conversation.");
						Finish(null);
						return;
				}

				if (current == null)
					return;
			}

			Warn($"Gave up after {MaxStepsPerAdvance} steps without reaching a line or a choice — the graph " +
			     "very likely contains a jump cycle.");
			Finish(null);
		}

		/// <summary>Null once the conversation has ended, which is the loop's signal to stop.</summary>
		private DialogueNode Step(string nodeId)
		{
			if (string.IsNullOrEmpty(nodeId))
			{
				Finish(null);
				return null;
			}

			var next = Graph?.FindNode(nodeId);
			if (next != null)
				return next;

			Warn($"Wire points at '{nodeId}', which does not exist. Ending the conversation.");
			Finish(null);
			return null;
		}

		private void PresentChoice(ChoiceNode choice)
		{
			_available.Clear();

			foreach (var option in choice.Options)
			{
				if (option == null)
					continue;

				if (option.Condition == null || Variables.Evaluate(option.Condition))
					_available.Add(option);
			}

			if (_available.Count == 0)
			{
				// Every option gated off is a dead end the player cannot escape, so it is worth saying
				// loudly rather than appearing as a conversation that silently stops.
				Warn($"Choice node '{choice.Id}' has no available options — every option's condition is " +
				     "false. Ending the conversation.");
				Finish(null);
				return;
			}

			CurrentChoice = choice;
			CurrentLine = null;
			State = DialogueState.Choice;
			ChoicesPresented?.Invoke(choice, _available);
		}

		private void Finish(string endTag)
		{
			CurrentLine = null;
			CurrentChoice = null;
			_available.Clear();
			EndTag = endTag;
			State = DialogueState.Finished;
			Finished?.Invoke(endTag);
		}

		private void Warn(string message) => Warning?.Invoke(message);
	}
}
