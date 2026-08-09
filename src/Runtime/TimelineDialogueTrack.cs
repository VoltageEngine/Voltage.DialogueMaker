using System.Collections.Generic;
using Voltage.Cinematics;
using Voltage.Serialization;

namespace Voltage.Dialogue
{
	/// <summary>One conversation started at a point on the timeline.</summary>
	public class TimelineDialogueClip
	{
		public float Time;

		[AssetType(typeof(DialogueGraph))]
		public AssetReference Graph;

		/// <summary>Timeline role whose entity carries the <see cref="DialogueRunner"/>.</summary>
		public string Role;

		/// <summary>Optional entry override; empty starts at the graph's own entry node.</summary>
		public string StartNodeId;

		/// <summary>Designer-facing label for the lane.</summary>
		public string Name;
	}

	/// <summary>
	/// Starts conversations from a cutscene.
	///
	/// <para>Imperative, not evaluable: a conversation waits for the player, so it is not a function of
	/// time and cannot be scrubbed. It fires once as the playhead crosses the clip and stays silent while
	/// seeking, exactly like the audio track.</para>
	/// </summary>
	[TrackTypeId("dialogue")]
	public class TimelineDialogueTrack : TimelineParameterTrack
	{
		public List<TimelineDialogueClip> Clips = new();

		/// <summary>Runners this track started, so an interrupted cutscene can stop them.</summary>
		private readonly List<DialogueRunner> _started = new();

		public override void Evaluate(float time, ITimelineContext context)
		{
		}

		public override float ContentEndTime()
		{
			var end = 0f;
			foreach (var clip in Clips)
			{
				if (clip != null && clip.Time > end)
					end = clip.Time;
			}

			return end;
		}

		public override void Validate(ITimelineContext context, List<string> problems)
		{
			foreach (var clip in Clips)
			{
				if (clip == null)
					continue;

				var label = clip.Name ?? $"the clip at {clip.Time:0.00}s";

				if (string.IsNullOrEmpty(clip.Role))
					problems.Add($"dialogue track: {label} has no role, so there is nothing to play it on.");
				else if (context?.ResolveComponent(clip.Role, DialogueRunnerComponentId) == null)
					problems.Add($"dialogue track: {label} targets role '{clip.Role}', which has no DialogueRunner.");

				if (!clip.Graph.IsValid)
					problems.Add($"dialogue track: {label} has no dialogue assigned.");
				else if (clip.Graph.ResolvePath() == null)
					problems.Add($"dialogue track: {label} references a dialogue that cannot be resolved.");
			}
		}

		public override void OnCrossForward(float previous, float next, ITimelineContext context)
		{
			if (Clips == null || context == null)
				return;

			foreach (var clip in Clips)
			{
				if (clip == null || clip.Time <= previous || clip.Time > next)
					continue;

				if (context.ResolveComponent(clip.Role, DialogueRunnerComponentId) is not DialogueRunner runner)
				{
					Debug.Warn($"[TimelineDialogueTrack] Role '{clip.Role}' has no DialogueRunner; " +
					           $"'{clip.Name ?? clip.Graph.ToString()}' was skipped.");
					continue;
				}

				runner.Graph = clip.Graph;
				runner.ReloadGraph();

				if (runner.Play(string.IsNullOrEmpty(clip.StartNodeId) ? null : clip.StartNodeId))
					_started.Add(runner);
			}
		}

		/// <summary>
		/// A cutscene skipped mid-line must not leave a conversation running with no one driving it.
		/// </summary>
		public override void OnPlaybackInterrupted(ITimelineContext context)
		{
			foreach (var runner in _started)
				runner?.Cancel();

			_started.Clear();
		}

		/// <summary>Matches the [ComponentId] on <see cref="DialogueRunner"/>.</summary>
		private const string DialogueRunnerComponentId = "dialogue_runner";
	}
}
