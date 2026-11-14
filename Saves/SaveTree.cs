using System.Globalization;
using System.Numerics;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RiisSaveEditor.Saves
{
	public readonly record struct SaveEdit(SaveNode Node, string? Old);

	public sealed record SaveStats(int Nodes, int Integers, int Floats, int Bools, int Strings)
	{
		public int Values => Integers + Floats + Bools + Strings;
	}

	public enum ChangeKind { Changed, Increased, Decreased }

	public sealed record Change(string Old, ChangeKind Kind, double Delta);

	public static class SaveTree
	{
		// Deepest level below the root, measured without recursion so a hostile tree cannot overflow the stack.
		public static int MaxDepth(SaveNode root)
		{
			int deepest = 0;
			Stack<(SaveNode Node, int Level)> todo = [];
			todo.Push((root, 0));
			while (todo.TryPop(out (SaveNode Node, int Level) item))
			{
				deepest = Math.Max(deepest, item.Level);
				foreach (SaveNode child in item.Node.Items)
				{
					todo.Push((child, item.Level + 1));
				}
			}

			return deepest;
		}

		public static void LinkParents(SaveNode root)
		{
			Stack<SaveNode> todo = [];
			todo.Push(root);
			while (todo.TryPop(out SaveNode? node))
			{
				foreach (SaveNode child in node.Items)
				{
					child.Parent = node;
					todo.Push(child);
				}
			}
		}

		// Names from just below the root down to the node, joined for display.
		public static string Path(SaveNode node, int maxSegments = int.MaxValue, bool includeSelf = true)
		{
			List<string> parts = [];
			for (SaveNode? n = includeSelf ? node : node.Parent; n?.Parent is not null && parts.Count < maxSegments; n = n.Parent)
			{
				parts.Add(n.Name);
			}

			parts.Reverse();
			return string.Join(" / ", parts);
		}

		public static SaveNode TopLevel(SaveNode node)
		{
			while (node.Parent?.Parent is not null)
			{
				node = node.Parent;
			}

			return node;
		}

		public static List<SaveNode> Collect(SaveNode root, Func<SaveNode, bool> include)
		{
			List<SaveNode> result = [];
			Stack<SaveNode> stack = [];
			stack.Push(root);
			while (stack.TryPop(out SaveNode? node))
			{
				if (node != root && include(node))
				{
					result.Add(node);
				}

				IReadOnlyList<SaveNode> kids = node.Items;
				for (int i = kids.Count - 1; i >= 0; i--)
				{
					stack.Push(kids[i]);
				}
			}

			return result;
		}

		public static SaveStats Stats(SaveNode root)
		{
			int nodes = 0, ints = 0, floats = 0, bools = 0, strings = 0;
			Stack<SaveNode> stack = [];
			stack.Push(root);
			while (stack.TryPop(out SaveNode? node))
			{
				nodes++;
				switch (node.Kind)
				{
					case NodeKind.Integer: ints++; break;
					case NodeKind.Float: floats++; break;
					case NodeKind.Bool: bools++; break;
					case NodeKind.String: strings++; break;
				}

				foreach (SaveNode child in node.Items)
				{
					stack.Push(child);
				}
			}

			return new SaveStats(nodes, ints, floats, bools, strings);
		}

		// Aligns two trees by child name and records every value that differs, as seen from `current`.
		public static Dictionary<SaveNode, Change> Compare(SaveNode current, SaveNode earlier)
		{
			Dictionary<SaveNode, Change> changes = [];
			Stack<(SaveNode Now, SaveNode Then)> todo = [];
			todo.Push((current, earlier));
			while (todo.TryPop(out (SaveNode Now, SaveNode Then) pair))
			{
				(SaveNode now, SaveNode then) = pair;
				if (now.IsLeafValue && then.IsLeafValue)
				{
					if (now.Value != then.Value)
					{
						changes[now] = Describe(now, then);
					}

					continue;
				}

				if (now.Items.Count == 0 || then.Items.Count == 0)
				{
					continue;
				}

				// Names can repeat (dict keys that print the same), so repeats pair up in order.
				Dictionary<(string, int), SaveNode> earlierByName = new(then.Items.Count);
				Dictionary<string, int> seen = [];
				foreach (SaveNode c in then.Items)
				{
					int index = seen.GetValueOrDefault(c.Name);
					seen[c.Name] = index + 1;
					earlierByName[(c.Name, index)] = c;
				}

				seen.Clear();
				foreach (SaveNode c in now.Items)
				{
					int index = seen.GetValueOrDefault(c.Name);
					seen[c.Name] = index + 1;
					if (earlierByName.TryGetValue((c.Name, index), out SaveNode? match))
					{
						todo.Push((c, match));
					}
				}
			}

			return changes;
		}

		// A read-only JSON view of the tree for inspecting or diffing outside the editor. It is never loaded back.
		public static byte[] ToJson(SaveNode root)
		{
			using MemoryStream ms = new();
			using (Utf8JsonWriter writer = new(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 1024 }))
			{
				WriteJson(writer, root);
			}

			return ms.ToArray();
		}

		private static void WriteJson(Utf8JsonWriter w, SaveNode n)
		{
			switch (n.Kind)
			{
				case NodeKind.Object:
					w.WriteStartObject();
					foreach (SaveNode c in n.Items)
					{
						w.WritePropertyName(c.Name);
						WriteJson(w, c);
					}

					w.WriteEndObject();
					return;
				case NodeKind.Array:
					w.WriteStartArray();
					foreach (SaveNode c in n.Items)
					{
						WriteJson(w, c);
					}

					w.WriteEndArray();
					return;
				case NodeKind.Integer when BigInteger.TryParse(n.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger big):
					w.WriteRawValue(big.ToString(CultureInfo.InvariantCulture), skipInputValidation: true);
					return;
				case NodeKind.Float when double.TryParse(n.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d):
					w.WriteNumberValue(d);
					return;
				case NodeKind.Bool:
					w.WriteBooleanValue(n.Value == "true");
					return;
				case NodeKind.Null:
					w.WriteNullValue();
					return;
				default:
					w.WriteStringValue(n.Value ?? n.TypeLabel ?? "");
					return;
			}
		}

		private static Change Describe(SaveNode now, SaveNode then)
		{
			string old = then.Value ?? "";
			bool numeric = now.Kind is NodeKind.Integer or NodeKind.Float && then.Kind is NodeKind.Integer or NodeKind.Float;
			if (!numeric) return new Change(old, ChangeKind.Changed, 0);

			if (now.Kind == NodeKind.Integer && then.Kind == NodeKind.Integer
			                                 && BigInteger.TryParse(now.Value, CultureInfo.InvariantCulture, out BigInteger a)
			                                 && BigInteger.TryParse(old, CultureInfo.InvariantCulture, out BigInteger b))
			{
				double delta = (double)(a - b);
				return new Change(old, delta >= 0 ? ChangeKind.Increased : ChangeKind.Decreased, delta);
			}

			if (double.TryParse(now.Value, CultureInfo.InvariantCulture, out double x) && double.TryParse(old, CultureInfo.InvariantCulture, out double y))
			{
				return new Change(old, x >= y ? ChangeKind.Increased : ChangeKind.Decreased, x - y);
			}

			return new Change(old, ChangeKind.Changed, 0);
		}
	}
}
