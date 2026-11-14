using System.Text;

namespace RiisSaveEditor.Saves
{
	// Span-preserving JSON: values are read into nodes that remember where they sit in the original
// text, and saving splices only the edited values back in. Everything else (indent, key order,
// number spelling, line endings) stays exactly as the game wrote it.
	public sealed record JsonSite(int Start, int Length, string? Original);

	public static class JsonText
	{
		private const int MaxDepth = 200;
		private const int MaxNodes = 5_000_000;

		public static SaveNode Parse(string text, string rootName)
		{
			Reader reader = new(text);
			return reader.ParseDocument(rootName);
		}

		public static string Write(string original, SaveNode root)
		{
			StringBuilder sb = new();
			int pos = 0;

			Visit(root);
			return sb.Append(original, pos, original.Length - pos).ToString();

			void Visit(SaveNode n)
			{
				if (n.Meta is JsonSite site && n.Value != site.Original)
				{
					sb.Append(original, pos, site.Start - pos);
					if (n.Kind == NodeKind.String)
					{
						Quote(sb, n.Value ?? "");
					}
					else
					{
						sb.Append(n.Value);
					}

					pos = site.Start + site.Length;
				}

				foreach (SaveNode child in n.Items)
				{
					Visit(child);
				}
			}
		}

		private static void Quote(StringBuilder sb, string s)
		{
			sb.Append('"');
			foreach (char c in s)
			{
				switch (c)
				{
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					case '\b': sb.Append("\\b"); break;
					case '\f': sb.Append("\\f"); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4")); break;
					default: sb.Append(c); break;
				}
			}

			sb.Append('"');
		}

		private sealed class Reader(string s)
		{
			private int _i;
			private int _depth;
			private int _nodes;

			public SaveNode ParseDocument(string name)
			{
				SkipSpace();
				SaveNode root = ParseValue(name);
				SkipSpace();
				if (_i != s.Length)
				{
					throw Error("unexpected text after the value");
				}

				return root;
			}

			private SaveException Error(string what) => new($"Invalid JSON at position {_i}: {what}");

			private void SkipSpace()
			{
				while (_i < s.Length && s[_i] is ' ' or '\t' or '\r' or '\n')
				{
					_i++;
				}
			}

			private char Peek() => _i < s.Length ? s[_i] : throw Error("unexpected end of file");

			// Without these caps a small file of nested brackets overflows the stack, which ends the whole
			// process, and a large one allocates tens of millions of nodes.
			private SaveNode ParseValue(string name)
			{
				if (++_nodes > MaxNodes)
				{
					throw Error("too many values");
				}

				if (++_depth > MaxDepth)
				{
					throw Error("nested more than " + MaxDepth + " levels deep");
				}

				try
				{
					return ParseCore(name);
				}
				finally
				{
					_depth--;
				}
			}

			private SaveNode ParseCore(string name)
			{
				char c = Peek();
				switch (c)
				{
					case '{':
						return ParseContainer(name, NodeKind.Object, '}');
					case '[':
						return ParseContainer(name, NodeKind.Array, ']');
					case '"':
						int start = _i;
						string text = ParseString();
						return new SaveNode(name, NodeKind.String, text) { Meta = new JsonSite(start, _i - start, text) };
					case 't':
						return Literal(name, "true", NodeKind.Bool);
					case 'f':
						return Literal(name, "false", NodeKind.Bool);
					case 'n':
						return Literal(name, "null", NodeKind.Null);
					default:
						return ParseNumber(name);
				}
			}

			private SaveNode Literal(string name, string word, NodeKind kind)
			{
				if (string.CompareOrdinal(s, _i, word, 0, word.Length) != 0)
				{
					throw Error("unknown value");
				}

				int start = _i;
				_i += word.Length;
				return kind == NodeKind.Null
					? new SaveNode(name, kind)
					: new SaveNode(name, kind, word) { Meta = new JsonSite(start, word.Length, word) };
			}

			private SaveNode ParseNumber(string name)
			{
				int start = _i;
				while (_i < s.Length && s[_i] is >= '0' and <= '9' or '-' or '+' or '.' or 'e' or 'E')
				{
					_i++;
				}

				string raw = s[start.._i];
				if (raw.Length == 0 || !double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out _))
				{
					_i = start;
					throw Error("unknown value");
				}

				bool integral = raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0;
				return new SaveNode(name, integral ? NodeKind.Integer : NodeKind.Float, raw) { Meta = new JsonSite(start, raw.Length, raw) };
			}

			private SaveNode ParseContainer(string name, NodeKind kind, char close)
			{
				SaveNode node = new(name, kind);
				_i++;
				SkipSpace();
				if (Peek() == close)
				{
					_i++;
					return node;
				}

				while (true)
				{
					SkipSpace();
					string childName;
					if (kind == NodeKind.Object)
					{
						// Easy Save 3 writes integer dictionary keys without quotes ({161:26}). Names are never
						// rewritten, so the bare spelling survives a save.
						if (Peek() is '-' or >= '0' and <= '9')
						{
							int keyStart = _i++;
							while (_i < s.Length && s[_i] is >= '0' and <= '9')
							{
								_i++;
							}

							childName = s[keyStart.._i];
						}
						else if (Peek() == '"')
						{
							childName = ParseString();
						}
						else
						{
							throw Error("expected a property name");
						}

						SkipSpace();
						if (Peek() != ':')
						{
							throw Error("expected ':'");
						}

						_i++;
						SkipSpace();
					}
					else
					{
						childName = $"[{node.Children.Count}]";
					}

					node.Children.Add(ParseValue(childName));
					SkipSpace();
					char next = Peek();
					_i++;
					if (next == close)
					{
						return node;
					}

					if (next != ',')
					{
						_i--;
						throw Error($"expected ',' or '{close}'");
					}
				}
			}

			private string ParseString()
			{
				_i++;
				StringBuilder sb = new();
				while (true)
				{
					char c = Peek();
					_i++;
					if (c == '"')
					{
						return sb.ToString();
					}

					if (c != '\\')
					{
						sb.Append(c);
						continue;
					}

					char e = Peek();
					_i++;
					switch (e)
					{
						case 'n': sb.Append('\n'); break;
						case 't': sb.Append('\t'); break;
						case 'r': sb.Append('\r'); break;
						case 'b': sb.Append('\b'); break;
						case 'f': sb.Append('\f'); break;
						case '"' or '\\' or '/': sb.Append(e); break;
						case 'u':
							if (_i + 4 > s.Length || !ushort.TryParse(s.AsSpan(_i, 4), System.Globalization.NumberStyles.AllowHexSpecifier, null, out ushort code))
							{
								throw Error("bad \\u escape");
							}

							sb.Append((char)code);
							_i += 4;
							break;
						default:
							throw Error("bad escape");
					}
				}
			}
		}
	}
}
