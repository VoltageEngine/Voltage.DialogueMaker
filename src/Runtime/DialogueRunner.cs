using System;
using System.Collections.Generic;
using System.IO;
using Voltage.Serialization;

namespace Voltage.Dialogue
{
	/// <summary>
	/// Plays a <c>.vdialogue</c> graph in a scene. The logic lives in <see cref="DialogueSession"/>; this
	/// resolves the asset, forwards warnings to the log, and re-exposes its events.
	/// </summary>
	[ComponentId("dialogue_runner")]
	public partial class DialogueRunner : Component
	{
		/// <summary>The conversation to play.</summary>
		[AssetType(typeof(DialogueGraph))]
		public AssetReference Graph;

		/// <summary>Begin as soon as the entity is added to a scene.</summary>
		public bool PlayOnStart;

		/// <summary>
		/// Keep variable state between <see cref="Play"/> calls instead of reseeding from the graph's
		/// declarations. Use it when one store spans a whole campaign.
		/// </summary>
		public bool PersistVariables;

		private readonly HashSet<string> _warned = new();
		private DialogueGraph _graph;
		private DialogueSession _session;

		/// <summary>Created on first use, so variables can be seeded before the first <see cref="Play"/>.</summary>
		public DialogueSession Session
		{
			get
			{
				if (_session == null)
				{
					_session = new DialogueSession();
					_session.Warning += OnSessionWarning;
					_session.LineStarted += line => LineStarted?.Invoke(line);
					_session.ChoicesPresented += (choice, options) => ChoicesPresented?.Invoke(choice, options);
					_session.Finished += tag => Finished?.Invoke(tag);
				}

				return _session;
			}
		}

		public DialogueVariables Variables => Session.Variables;

		public DialogueState State => Session.State;

		public LineNode CurrentLine => Session.CurrentLine;

		public ChoiceNode CurrentChoice => Session.CurrentChoice;

		public IReadOnlyList<DialogueChoiceOption> AvailableOptions => Session.AvailableOptions;

		public event Action<LineNode> LineStarted;
		public event Action<ChoiceNode, IReadOnlyList<DialogueChoiceOption>> ChoicesPresented;
		public event Action<string> Finished;

		public override void OnAddedToEntity()
		{
			if (PlayOnStart)
				Play();
		}

		/// <summary>Starts the assigned graph. Does nothing when the asset cannot be resolved.</summary>
		public bool Play(string fromNodeId = null)
		{
			EnsureGraph();
			if (_graph == null)
				return false;

			Session.Start(_graph, fromNodeId, resetVariables: !PersistVariables);
			return true;
		}

		/// <summary>Plays a graph handed over directly, bypassing <see cref="Graph"/>.</summary>
		public bool Play(DialogueGraph graph, string fromNodeId = null)
		{
			if (graph == null)
				return false;

			_graph = graph;
			Session.Start(graph, fromNodeId, resetVariables: !PersistVariables);
			return true;
		}

		public bool Advance() => Session.Advance();

		public bool Choose(int index) => Session.Choose(index);

		public void Cancel() => Session.Cancel();

		/// <summary>Drops the cached asset so the next <see cref="Play"/> re-reads it from disk.</summary>
		public void ReloadGraph()
		{
			_graph = null;
			_warned.Clear();
		}

		private void EnsureGraph()
		{
			_graph ??= TryLoadGraph();
		}

		private DialogueGraph TryLoadGraph()
		{
			if (!Graph.IsValid)
				return null;   // nothing assigned yet — not an error

			var path = Graph.ResolvePath();
			if (string.IsNullOrEmpty(path))
			{
				WarnOnce($"dialogue-unresolved:{Graph.AssetGuid}",
					$"[DialogueRunner] '{Entity?.Name}' references dialogue {Graph} but it could not be " +
					"resolved. The file may have been deleted, or the asset manifest may be stale — reopen " +
					"the project in the editor to regenerate it.");
				return null;
			}

			if (!File.Exists(path))
			{
				WarnOnce($"dialogue-missing:{path}",
					$"[DialogueRunner] '{Entity?.Name}' references a dialogue graph that is not on disk: {path}");
				return null;
			}

			try
			{
				return DialogueGraphIO.Load(path);
			}
			catch (Exception ex)
			{
				// A malformed graph must not take the scene down with it; the conversation simply does not
				// start, and the reason is in the log.
				WarnOnce($"dialogue-unreadable:{path}",
					$"[DialogueRunner] Could not read '{path}': {ex.Message}");
				return null;
			}
		}

		private void OnSessionWarning(string message) =>
			WarnOnce($"session:{message}", $"[DialogueRunner] '{Entity?.Name}': {message}");

		private void WarnOnce(string key, string message)
		{
			if (!_warned.Add(key))
				return;

			Debug.Warn(message);
		}
	}
}
