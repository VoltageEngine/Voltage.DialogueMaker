using System;
using System.Collections.Generic;
using ImGuiNET;
using Voltage.Editor.Plugins;
using Num = System.Numerics;

namespace Voltage.Dialogue.Editor
{
	/// <summary>
	/// The plugin's manual: how a conversation goes from an asset to something running in a scene.
	///
	/// <para>It exists because the pieces are deliberately decoupled - a graph holds keys, a string table
	/// holds text, a runner plays one in a scene - and nothing in the editor makes that wiring visible.
	/// The step that catches everyone is that a string table is not attached to a graph at all; it is
	/// installed once at startup through <c>DialogueLocalisation.SetTable</c>.</para>
	/// </summary>
	public sealed class DialogueManualWindow : EditorPluginWindow
	{
		/// <summary>
		/// One instance, shared by the Plugins menu and the graph window's Help menu, so both open the
		/// same window rather than two that fight over the same ImGui id.
		/// </summary>
		public static DialogueManualWindow Instance { get; } = new();

		private static readonly Num.Vector4 Heading = new(0.45f, 0.75f, 1f, 1f);
		private static readonly Num.Vector4 Muted = new(0.62f, 0.62f, 0.66f, 1f);
		private static readonly Num.Vector4 Warn = new(1f, 0.78f, 0.35f, 1f);
		private static readonly Num.Vector4 Good = new(0.5f, 0.85f, 0.5f, 1f);

		private string _search = string.Empty;

		private DialogueManualWindow() => Title = "DialogueMaker Manual";

		public override void Draw()
		{
			if (!IsOpen)
				return;

			ImGui.SetNextWindowSize(new Num.Vector2(860, 720), ImGuiCond.FirstUseEver);

			var open = IsOpen;
			if (!ImGui.Begin(Title + "###DialogueManualWindow", ref open))
			{
				ImGui.End();
				IsOpen = open;
				return;
			}
			IsOpen = open;

			ImGui.TextColored(Heading, "DialogueMaker");
			ImGui.SameLine();
			ImGui.TextColored(Muted, "how a conversation gets from an asset into your game");

			ImGui.SetNextItemWidth(240);
			ImGui.InputTextWithHint("##manual-search", "Filter sections...", ref _search, 64);
			ImGui.SameLine();
			if (ImGui.SmallButton("Clear"))
				_search = string.Empty;

			ImGui.Separator();

			if (ImGui.BeginChild("manual-body", new Num.Vector2(0, 0), false))
			{
				DrawOverview();
				DrawStep1();
				DrawStep2();
				DrawStep3();
				DrawStep4();
				DrawStep5();
				DrawStep6();
				DrawStep7();
				DrawHeadless();
				DrawCanvasReference();
				DrawNodeReference();
				DrawTroubleshooting();
			}
			ImGui.EndChild();

			ImGui.End();
		}

		#region Sections

		private void DrawOverview()
		{
			if (!Section("The five pieces", defaultOpen: true))
				return;

			Body("DialogueMaker splits a conversation into parts that version separately, so rewriting a " +
			     "line never touches the graph and restructuring the graph never touches translation.");

			Bullet(".vdialogue", "the graph: nodes, wires, and the variables it declares. This is what the " +
			                    "Dialogue Graph window edits.");
			Bullet(".vasset  (Dialogue String Table)", "the text. Nodes store KEYS, never sentences. One " +
			                                          "table per locale.");
			Bullet("DialogueRunner", "the component that plays a graph on an entity in a scene.");
			Bullet("DialogueSession", "the walker underneath it, with no engine or scene dependency - the " +
			                          "same conversation runs headlessly in a test.");
			Bullet("TimelineDialogueTrack", "starts a conversation from a cutscene.");

			ImGui.Spacing();
			Note("The part nobody guesses: a string table is NOT referenced by the graph. Nothing connects " +
			     "them in the editor. You install one at startup with DialogueLocalisation.SetTable, and " +
			     "every key in every graph resolves against it. Step 3 covers this.");

			End();
		}

		private void DrawStep1()
		{
			if (!Section("Step 1 - create the graph asset", defaultOpen: true))
				return;

			Body("In the Asset Browser, right-click the folder you want it in:");
			Ordered(1, "Create > Dialogue Graph");
			Ordered(2, "Name it. The file is written as <name>.vdialogue.");
			Ordered(3, "Double-click it to open the Dialogue Graph window.");

			ImGui.Spacing();
			Body("A new graph is not empty: it starts with a Line node wired to an End node, and the Line " +
			     "is already the entry node. That is a working conversation - it just has no text yet.");

			End();
		}

		private void DrawStep2()
		{
			if (!Section("Step 2 - build the graph"))
				return;

			Body("Right-click the canvas and pick Add Node, or use the Add Node menu in the window's menu bar.");

			ImGui.Spacing();
			Bullet("Wiring", "drag from an output port (the amber dot on a node's right edge) onto the body " +
			                 "of another node. Release over empty space to clear that connection.");
			Bullet("Entry node", "the node with the green dot is where the conversation starts. Right-click " +
			                     "a node > Make Entry Node to move it.");
			Bullet("Selection", "click to select, shift or ctrl click to add, drag a box on empty space to " +
			                    "select a group. Dragging any selected node moves the whole selection.");
			Bullet("Validation", "the side panel lists unreachable nodes, dangling wires and missing keys. " +
			                     "Click an entry to jump to the node it is about.");

			ImGui.Spacing();
			Note("The graph saves itself when an edit finishes - there is no explicit save step, though " +
			     "Ctrl+S forces one.");

			End();
		}

		private void DrawStep3()
		{
			if (!Section("Step 3 - the text, and the binding that is not obvious", defaultOpen: true))
				return;

			Body("A Line node has a TextKey, not a line of dialogue. A choice option has a TextKey too. " +
			     "Those keys mean nothing until a string table is installed.");

			ImGui.Spacing();
			ImGui.TextColored(Heading, "Create the table");
			Ordered(1, "Asset Browser > right-click a folder > Create > Data Asset > Dialogue String Table.");
			Ordered(2, "Double-click the new .vasset to open it in the Data Asset window.");
			Ordered(3, "Set Locale (\"en\", \"en-GB\", \"el\") and add an entry per key.");

			ImGui.Spacing();
			Body("Each entry is a Key and a Text. The Key is what you type into the node's TextKey field. " +
			     "A useful convention is scene.beat.speaker, for example ch1.gate.guard1.");

			ImGui.Spacing();
			ImGui.TextColored(Heading, "Install it - this is the step that is missing when text does not appear");
			Body("Nothing does this for you. Call it once during startup, before any conversation plays:");

			Code("install-table",
				"""
				using Voltage.Data;
				using Voltage.Dialogue;

				// Anywhere during startup - a scene's Initialize, or your game bootstrap.
				var table = DataAssets.LoadFromPath<DialogueStringTable>(
				    Path.Combine(Core.Content.RootDirectory, "Dialogue/Strings.en.vasset"));

				DialogueLocalisation.SetTable(table);
				""");

			Body("To switch language at runtime, call SetTable again with another locale's table. Keys are " +
			     "resolved at the moment a line is shown, so anything already on screen should be redrawn.");

			ImGui.Spacing();
			Note("A missing key renders as the key itself - you will see \"ch1.gate.guard1\" on screen " +
			     "rather than a blank line. That is deliberate: an empty string looks like a rendering bug " +
			     "and hides the cause. Subscribe to DialogueLocalisation.KeyMissing to log them.");

			End();
		}

		private void DrawStep4()
		{
			if (!Section("Step 4 - variables, conditions and branches"))
				return;

			Body("Declare variables in the graph window's Variables panel: a name and a typed default " +
			     "(bool, int, float, string). They are seeded from those defaults every time the " +
			     "conversation starts, unless the runner is told to persist them.");

			ImGui.Spacing();
			Bullet("Condition node", "branches on one variable. Two output ports: true on top, false below.");
			Bullet("Set Variable node", "assigns as the conversation passes through it.");
			Bullet("Gated choices", "tick Conditional on a choice option to hide it unless its condition " +
			                        "passes. Hidden options never shift the index your UI clicks - " +
			                        "AvailableOptions is already filtered.");

			ImGui.Spacing();
			Body("From code, before or during a conversation:");
			Code("variables",
				"""
				runner.Variables.SetBool("metGuard", true);
				runner.Variables.SetInt("gold", 40);

				if (runner.Variables.GetBool("metGuard"))
				    { /* ... */ }
				""");

			Note("Set PersistVariables on the runner when one variable store should span a whole campaign " +
			     "instead of resetting at every Play.");

			End();
		}

		private void DrawStep5()
		{
			if (!Section("Step 5 - play it in a scene", defaultOpen: true))
				return;

			Ordered(1, "Select an entity and add the Dialogue Runner component.");
			Ordered(2, "Assign its Graph field by dragging the .vdialogue in from the Asset Browser.");
			Ordered(3, "Tick Play On Start, or call Play() yourself.");

			ImGui.Spacing();
			Body("From code:");
			Code("play",
				"""
				var runner = entity.AddComponent(new DialogueRunner());
				runner.Graph = graphReference;   // AssetReference, usually assigned in the inspector
				runner.Play();                   // or runner.Play("some_node_id") to start mid-graph
				""");

			Body("The runner does not draw anything. It walks the graph and raises events; what a line " +
			     "looks like on screen is entirely yours.");

			End();
		}

		private void DrawStep6()
		{
			if (!Section("Step 6 - drive your UI from the runner", defaultOpen: true))
				return;

			Body("A complete component that listens to a runner and turns it into something on screen. " +
			     "This is the whole integration surface - there is nothing else to implement.");

			Code("ui",
				"""
				using System.Collections.Generic;
				using Voltage;
				using Voltage.Dialogue;

				public class DialogueUi : Component, IUpdatable
				{
				    private DialogueRunner _runner;

				    public override void OnAddedToEntity()
				    {
				        _runner = this.GetComponent<DialogueRunner>();

				        _runner.LineStarted      += OnLine;
				        _runner.ChoicesPresented += OnChoices;
				        _runner.Finished         += OnFinished;
				    }

				    public override void OnRemovedFromEntity()
				    {
				        // Always unsubscribe: the runner outlives this component when the entity is reused.
				        _runner.LineStarted      -= OnLine;
				        _runner.ChoicesPresented -= OnChoices;
				        _runner.Finished         -= OnFinished;
				    }

				    private void OnLine(LineNode line)
				    {
				        // TextOf resolves the key against the installed string table.
				        var speaker = line.SpeakerId;
				        var text    = DialogueLocalisation.TextOf(line);
				        ShowLine(speaker, text);
				    }

				    private void OnChoices(ChoiceNode node, IReadOnlyList<DialogueChoiceOption> options)
				    {
				        // Already filtered: options whose condition failed are not in this list.
				        for (var i = 0; i < options.Count; i++)
				            ShowOption(i, DialogueLocalisation.TextOf(options[i]));
				    }

				    private void OnFinished(string endTag)
				    {
				        // endTag is the tag on the End node that finished it, when it had one -
				        // handy for "did this conversation end in a fight or a handshake".
				        HideEverything();
				    }

				    public void Update()
				    {
				        if (_runner.State == DialogueState.Line && Input.IsKeyPressed(Keys.Space))
				            _runner.Advance();

				        if (_runner.State == DialogueState.Choice && Input.IsKeyPressed(Keys.D1))
				            _runner.Choose(0);   // indexes AvailableOptions, not the authored list
				    }
				}
				""");

			ImGui.Spacing();
			Bullet("Advance()", "moves past the current line. Returns false if no line was showing.");
			Bullet("Choose(i)", "picks AvailableOptions[i]. Using the authored index would select the wrong " +
			                    "option whenever one is hidden by a condition.");
			Bullet("Cancel()", "ends the conversation early, from any state.");
			Bullet("State", "Idle, Line, Choice or Finished - poll it, or drive purely off the events.");

			End();
		}

		private void DrawStep7()
		{
			if (!Section("Step 7 - starting a conversation from a cutscene"))
				return;

			Body("Add a Dialogue track to a .vtimeline and give it a clip:");
			Bullet("Time", "when the playhead crosses it, the conversation starts.");
			Bullet("Graph", "the .vdialogue to play.");
			Bullet("Role", "the timeline role whose entity carries the DialogueRunner.");
			Bullet("Start Node Id", "optional; empty starts at the graph's own entry node.");

			ImGui.Spacing();
			Note("The track fires once as the playhead crosses a clip and stays silent while seeking. A " +
			     "conversation waits for the player, so it is not a function of time and cannot be " +
			     "scrubbed. Interrupting the cutscene cancels every conversation it started.");

			Body("A role with no DialogueRunner is reported as a warning and the clip is skipped - so if " +
			     "nothing happens, check that the role's entity actually has the component.");

			End();
		}

		private void DrawHeadless()
		{
			if (!Section("Running a conversation without a scene"))
				return;

			Body("DialogueSession has no engine dependency, so a conversation can be walked in a test:");

			Code("headless",
				"""
				var graph   = DialogueGraphIO.Load("Content/Dialogue/Intro.vdialogue");
				var session = new DialogueSession();

				session.LineStarted += line => Console.WriteLine(line.TextKey);
				session.Start(graph);

				while (session.State == DialogueState.Line)
				    session.Advance();
				""");

			Note("Warnings surface as the session's Warning event rather than going to the log, which is " +
			     "what keeps this type engine-free. The DialogueRunner component forwards them for you.");

			End();
		}

		private void DrawCanvasReference()
		{
			if (!Section("Canvas controls"))
				return;

			Table("canvas-controls", new (string, string)[]
			{
				("Click", "select a node, or a wire"),
				("Shift / Ctrl + click", "add to or remove from the selection"),
				("Drag a node", "move it, and everything else selected with it"),
				("Arrow keys  /  Shift + arrows", "nudge the selection a pixel / a grid step"),
				("Drag on empty space", "box-select"),
				("Middle-drag, Space + drag, or Alt + drag", "pan"),
				("Mouse wheel  /  Ctrl +  /  Ctrl -", "zoom about the cursor / zoom in / zoom out"),
				("Drag an output port onto a node", "connect"),
				("Drag an output port to empty space", "pick the new node to connect to"),
				("Drag off an input port", "take that connection somewhere else"),
				("Del on a selected wire", "remove just that connection"),
				("Right-click empty space", "add a node, paste, frame, snap"),
				("Right-click a node", "make entry, duplicate, copy, cut, disconnect, delete"),
				("Right-click a wire", "delete that connection"),
				("Del / Backspace", "delete the selection"),
				("Ctrl+Z  /  Ctrl+Shift+Z", "undo / redo"),
				("Ctrl+A / Ctrl+D / Ctrl+X / Ctrl+C / Ctrl+V", "select all / duplicate / cut / copy / paste"),
				("Ctrl+F", "find a node by speaker, key, variable or id"),
				("Enter  /  Shift+Enter in the find box", "jump to the next match / the previous one"),
				("F  /  Shift+F", "frame the selection / frame everything"),
				("Ctrl+0", "reset zoom"),
				("Ctrl+S", "save now (it also autosaves after each edit)"),
			});

			Note("Copy and paste go through the system clipboard, so nodes can be moved from one graph " +
			     "into another. Wires inside the copied set follow the copies; wires leaving it keep " +
			     "pointing at the originals.");

			End();
		}

		private void DrawNodeReference()
		{
			if (!Section("Node types"))
				return;

			Table("node-types", new (string, string)[]
			{
				("Line", "one spoken line: SpeakerId, TextKey, optional Voice asset. One output."),
				("Choice", "a prompt and a list of options, each with its own TextKey, optional condition, " +
				           "and its own output."),
				("Condition", "branches on a variable. Two outputs: true, then false."),
				("Set Variable", "assigns a variable as the conversation passes through. One output."),
				("Jump", "continues at another node. Use it to merge branches back together."),
				("End", "finishes the conversation. Its optional tag is what Finished reports."),
				("Unknown", "a node whose type is not registered - usually a plugin that is not loaded. " +
				            "Its data is preserved exactly and written back untouched, so opening and " +
				            "saving a graph never destroys it."),
			});

			End();
		}

		private void DrawTroubleshooting()
		{
			if (!Section("When it does not work"))
				return;

			Problem("The keys show on screen instead of the text",
				"No string table is installed, or the key is not in it. Call DialogueLocalisation.SetTable " +
				"during startup, and subscribe to DialogueLocalisation.KeyMissing to find out which keys " +
				"are absent.");

			Problem("Nothing happens when the runner plays",
				"The Graph field may be unassigned or unresolvable - the runner logs a warning naming the " +
				"asset. Check the graph has an entry node, and that something is calling Advance: the " +
				"runner stops at the first line and waits for you.");

			Problem("Choose(index) picks the wrong option",
				"Index into AvailableOptions, not the authored option list. A condition that hides an " +
				"option shifts every index after it.");

			Problem("A node shows as Unknown",
				"Its type is not registered, so the plugin that defines it is probably not loaded. Its " +
				"content is preserved untouched, so fix the plugin and reopen - nothing was lost.");

			Problem("Variables reset between conversations",
				"That is the default: they are reseeded from the graph's declarations at every Play. Tick " +
				"Persist Variables on the runner to carry them.");

			Problem("A cutscene's dialogue clip is skipped",
				"The clip's Role resolved to an entity with no DialogueRunner. The timeline logs a warning " +
				"naming the role.");

			End();
		}

		#endregion

		#region Layout helpers

		/// <summary>A collapsing section that honours the filter box.</summary>
		private bool Section(string title, bool defaultOpen = false)
		{
			if (!string.IsNullOrWhiteSpace(_search) &&
			    title.IndexOf(_search.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
			{
				return false;
			}

			ImGui.PushID(title);
			var open = ImGui.CollapsingHeader(title, defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
			if (open)
				ImGui.Indent();
			else
				ImGui.PopID();

			return open;
		}

		private static void End()
		{
			ImGui.Unindent();
			ImGui.Spacing();
			ImGui.PopID();
		}

		private static void Body(string text)
		{
			ImGui.TextWrapped(text);
			ImGui.Spacing();
		}

		private static void Bullet(string term, string text)
		{
			ImGui.Bullet();
			ImGui.SameLine();
			ImGui.TextColored(Heading, term);
			ImGui.SameLine();
			ImGui.TextWrapped(text);
		}

		private static void Ordered(int index, string text)
		{
			ImGui.TextColored(Heading, index + ".");
			ImGui.SameLine();
			ImGui.TextWrapped(text);
		}

		private static void Note(string text)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, Warn);
			ImGui.TextWrapped("Note: " + text);
			ImGui.PopStyleColor();
			ImGui.Spacing();
		}

		private static void Problem(string symptom, string cause)
		{
			ImGui.TextColored(Warn, symptom);
			ImGui.Indent();
			ImGui.TextWrapped(cause);
			ImGui.Unindent();
			ImGui.Spacing();
		}

		/// <summary>A read-only code block with a copy button, so an example can be lifted straight out.</summary>
		private static void Code(string id, string code)
		{
			ImGui.PushID(id);

			var lines = 1;
			foreach (var c in code)
			{
				if (c == '\n')
					lines++;
			}

			var height = Math.Min(lines + 1, 34) * ImGui.GetTextLineHeightWithSpacing();

			ImGui.PushStyleColor(ImGuiCol.FrameBg, new Num.Vector4(0.09f, 0.09f, 0.11f, 1f));
			ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(0.82f, 0.86f, 0.78f, 1f));
			var buffer = code;
			ImGui.InputTextMultiline("##code", ref buffer, (uint)code.Length + 1,
				new Num.Vector2(-1, height), ImGuiInputTextFlags.ReadOnly);
			ImGui.PopStyleColor(2);

			if (ImGui.SmallButton("Copy"))
				ImGui.SetClipboardText(code);

			ImGui.PopID();
			ImGui.Spacing();
		}

		private static void Table(string id, IReadOnlyList<(string Left, string Right)> rows)
		{
			if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
			                             ImGuiTableFlags.SizingStretchProp))
			{
				return;
			}

			ImGui.TableSetupColumn("a", ImGuiTableColumnFlags.WidthStretch, 1f);
			ImGui.TableSetupColumn("b", ImGuiTableColumnFlags.WidthStretch, 2.2f);

			foreach (var (left, right) in rows)
			{
				ImGui.TableNextRow();
				ImGui.TableNextColumn();
				ImGui.TextColored(Good, left);
				ImGui.TableNextColumn();
				ImGui.TextWrapped(right);
			}

			ImGui.EndTable();
			ImGui.Spacing();
		}

		#endregion
	}
}
