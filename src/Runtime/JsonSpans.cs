using System.Collections.Generic;

namespace Voltage.Dialogue
{
	/// <summary>
	/// Locates the top-level elements of a named array as spans into the original JSON text.
	/// Needed because the decoder always resolves an <c>@type</c> hint into a typed object — pointing it at
	/// a dictionary yields an empty one — so raw text is the only way to keep an unreadable node intact.
	/// </summary>
	internal static class JsonSpans
	{
		/// <summary>
		/// Spans of each element of <paramref name="key"/>'s array in the root object, in document order.
		/// Empty when the key is absent or is not an array.
		/// </summary>
		public static List<(int Start, int Length)> ArrayElements(string json, string key)
		{
			var result = new List<(int, int)>();
			if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
				return result;

			var arrayStart = FindArrayStart(json, key);
			if (arrayStart < 0)
				return result;

			var i = arrayStart + 1;
			while (i < json.Length)
			{
				while (i < json.Length && (char.IsWhiteSpace(json[i]) || json[i] == ','))
					i++;

				if (i >= json.Length || json[i] == ']')
					break;

				var start = i;
				var depth = 0;
				var inString = false;
				var escaped = false;

				for (; i < json.Length; i++)
				{
					var c = json[i];

					if (inString)
					{
						if (escaped)
							escaped = false;
						else if (c == '\\')
							escaped = true;
						else if (c == '"')
							inString = false;

						continue;
					}

					if (c == '"')
					{
						inString = true;
						continue;
					}

					if (c == '{' || c == '[')
					{
						depth++;
					}
					else if (c == '}' || c == ']')
					{
						if (depth == 0)
							break;   // the array's own closing bracket

						depth--;
						if (depth == 0)
						{
							i++;     // include the closing brace of this element
							break;
						}
					}
					else if (c == ',' && depth == 0)
					{
						break;       // a scalar element ended
					}
				}

				result.Add((start, i - start));
			}

			return result;
		}

		/// <summary>The string value of <paramref name="key"/> in a JSON object, or null.</summary>
		public static string ReadStringProperty(string jsonObject, string key)
		{
			if (string.IsNullOrEmpty(jsonObject))
				return null;

			var needle = "\"" + key + "\"";
			var at = jsonObject.IndexOf(needle, System.StringComparison.Ordinal);
			if (at < 0)
				return null;

			var i = at + needle.Length;
			while (i < jsonObject.Length && char.IsWhiteSpace(jsonObject[i]))
				i++;

			if (i >= jsonObject.Length || jsonObject[i] != ':')
				return null;

			i++;
			while (i < jsonObject.Length && char.IsWhiteSpace(jsonObject[i]))
				i++;

			if (i >= jsonObject.Length || jsonObject[i] != '"')
				return null;

			i++;
			var value = new System.Text.StringBuilder();
			var escaped = false;
			for (; i < jsonObject.Length; i++)
			{
				var c = jsonObject[i];
				if (escaped)
				{
					value.Append(c);
					escaped = false;
				}
				else if (c == '\\')
				{
					escaped = true;
				}
				else if (c == '"')
				{
					break;
				}
				else
				{
					value.Append(c);
				}
			}

			return value.ToString();
		}

		/// <summary>
		/// Replaces the given element spans with new text. Applied back-to-front so earlier spans stay
		/// valid.
		/// </summary>
		public static string ReplaceSpans(string json, IReadOnlyList<(int Start, int Length, string Text)> replacements)
		{
			if (replacements == null || replacements.Count == 0)
				return json;

			var ordered = new List<(int Start, int Length, string Text)>(replacements);
			ordered.Sort((a, b) => b.Start.CompareTo(a.Start));

			var sb = new System.Text.StringBuilder(json);
			foreach (var (start, length, text) in ordered)
			{
				sb.Remove(start, length);
				sb.Insert(start, text);
			}

			return sb.ToString();
		}

		private static int FindArrayStart(string json, string key)
		{
			var needle = "\"" + key + "\"";
			var depth = 0;
			var inString = false;
			var escaped = false;

			for (var i = 0; i < json.Length; i++)
			{
				var c = json[i];

				if (inString)
				{
					if (escaped)
						escaped = false;
					else if (c == '\\')
						escaped = true;
					else if (c == '"')
						inString = false;

					continue;
				}

				if (c == '"')
				{
					// Only the root object's own key, so a nested "Nodes" cannot be mistaken for it.
					if (depth == 1 && string.CompareOrdinal(json, i, needle, 0, needle.Length) == 0)
					{
						var j = i + needle.Length;
						while (j < json.Length && char.IsWhiteSpace(json[j]))
							j++;

						if (j < json.Length && json[j] == ':')
						{
							j++;
							while (j < json.Length && char.IsWhiteSpace(json[j]))
								j++;

							if (j < json.Length && json[j] == '[')
								return j;
						}
					}

					inString = true;
					continue;
				}

				if (c == '{' || c == '[')
					depth++;
				else if (c == '}' || c == ']')
					depth--;
			}

			return -1;
		}
	}
}
