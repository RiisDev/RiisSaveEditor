using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace RiisSaveEditor.Saves
{
	// MemoLength is the memo-put opcode right after the value, Shared means a memo get refers back to it.
	public sealed record PatchSite(int Start, int Length, string? Original, byte Op, int MemoLength = 0, bool Shared = false);

// A protocol 4+ frame: the declared length of the 8 byte field at FieldOffset covers [Start, Start + Length).
	public sealed record PickleFrame(int FieldOffset, int Start, long Length);

	public sealed record PickleDocument(byte[] Data, SaveNode Root, List<SaveNode> Sites, List<PickleFrame> Frames);

// Reads a Python pickle into a display tree without executing anything. Writing never rebuilds the
// stream: edited primitives are re-encoded in place and every other byte is copied, so memo indexes
// and object identity stay valid. Frame lengths are corrected when an edit changes a size.
	public static class Pickle
	{
		public static PickleDocument Parse(byte[] d)
		{
			List<SaveNode> stack = [];
			List<SaveNode> sites = [];
			List<PickleFrame> frames = [];
			List<int> marks = [];
			Dictionary<int, SaveNode> memo = [];
			int p = 0;

			byte U8() => d[p++];
			int I32() { int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4; return v; }
			uint U32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)); p += 4; return v; }
			int U64AsInt() { ulong v = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p)); p += 8; return checked((int)v); }
			string Line() { int e = Array.IndexOf(d, (byte)'\n', p); string s = Encoding.UTF8.GetString(d, p, e - p); p = e + 1; return s; }
			List<SaveNode> PopToMark() { int m = marks[^1]; marks.RemoveAt(marks.Count - 1); List<SaveNode> items = stack.GetRange(m, stack.Count - m); stack.RemoveRange(m, stack.Count - m); return items; }
			List<SaveNode> PopN(int n) { List<SaveNode> items = stack.GetRange(stack.Count - n, n); stack.RemoveRange(stack.Count - n, n); return items; }
			SaveNode Pop() { SaveNode n = stack[^1]; stack.RemoveAt(stack.Count - 1); return n; }

			SaveNode Site(int start, NodeKind kind, string value)
			{
				SaveNode n = new("", kind, value)
				{
					Meta = new PatchSite(start, p - start, value, d[start])
				};
				sites.Add(n);
				return n;
			}

			SaveNode Bytes(int len)
			{
				SaveNode n = new("", NodeKind.Binary, Convert.ToHexString(d, p, len)) { ReadOnly = true };
				p += len;
				return n;
			}

			SaveNode Text(int start, int len)
			{
				ReadOnlySpan<byte> raw = d.AsSpan(p, len);
				string s = Encoding.UTF8.GetString(raw);
				bool lossless = Encoding.UTF8.GetByteCount(s) == len && Encoding.UTF8.GetBytes(s).AsSpan().SequenceEqual(raw);
				p += len;
				return lossless
					? Site(start, NodeKind.String, s)
					: new SaveNode("", NodeKind.String, s) { ReadOnly = true };
			}

			static SaveNode MakeTuple(List<SaveNode> items)
			{
				SaveNode t = new("", NodeKind.Array) { TypeLabel = "tuple" };
				AddIndexed(t, items);
				return t;
			}

			static SaveNode Instance(string? cls, List<SaveNode> args)
			{
				SaveNode obj = new("", NodeKind.Object) { TypeLabel = cls };
				obj.Children.AddRange(args);
				return obj;
			}

			while (true)
			{
				if (p >= d.Length)
				{
					throw new SaveException("Pickle ended without a STOP opcode");
				}

				int start = p;
				byte op = d[p++];
				switch (op)
				{
					case 0x80: p++; break;
					case 0x95:
						frames.Add(new PickleFrame(p, p + 8, BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(p))));
						p += 8;
						break;
					case (byte)'.': return new PickleDocument(d, stack[^1], sites, frames);
					case (byte)'(': marks.Add(stack.Count); break;
					case (byte)'}': stack.Add(new SaveNode("", NodeKind.Object) { TypeLabel = "dict" }); break;
					case (byte)']': stack.Add(new SaveNode("", NodeKind.Array) { TypeLabel = "list" }); break;
					case 0x8f: stack.Add(new SaveNode("", NodeKind.Array) { TypeLabel = "set" }); break;
					case (byte)')': stack.Add(MakeTuple([])); break;
					case 0x85: stack.Add(MakeTuple(PopN(1))); break;
					case 0x86: stack.Add(MakeTuple(PopN(2))); break;
					case 0x87: stack.Add(MakeTuple(PopN(3))); break;
					case (byte)'t': stack.Add(MakeTuple(PopToMark())); break;
					case (byte)'l':
						SaveNode list = new("", NodeKind.Array) { TypeLabel = "list" };
						AddIndexed(list, PopToMark());
						stack.Add(list);
						break;
					case 0x91:
						SaveNode frozen = new("", NodeKind.Array) { TypeLabel = "frozenset" };
						AddIndexed(frozen, PopToMark());
						stack.Add(frozen);
						break;
					case (byte)'d':
						SaveNode dict = new("", NodeKind.Object) { TypeLabel = "dict" };
						AddPairs(dict, PopToMark());
						stack.Add(dict);
						break;
					case (byte)'a': AddIndexed(stack[^2], [Pop()]); break;
					case (byte)'e':
					case 0x90:
						AddIndexed(stack[marks[^1] - 1], PopToMark());
						break;
					case (byte)'s': AddPairs(stack[^3], PopN(2)); break;
					case (byte)'u': AddPairs(stack[marks[^1] - 1], PopToMark()); break;
					case (byte)'q': memo[U8()] = MarkMemo(stack[^1], start, p); break;
					case (byte)'r': memo[I32()] = MarkMemo(stack[^1], start, p); break;
					case 0x94: memo[memo.Count] = MarkMemo(stack[^1], start, p); break;
					case (byte)'h': stack.Add(Resolve(memo, U8())); break;
					case (byte)'j': stack.Add(Resolve(memo, I32())); break;
					case (byte)'K': { int v = U8(); stack.Add(Site(start, NodeKind.Integer, v.ToString(CultureInfo.InvariantCulture))); break; }
					case (byte)'M': { int v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p)); p += 2; stack.Add(Site(start, NodeKind.Integer, v.ToString(CultureInfo.InvariantCulture))); break; }
					case (byte)'J': { int v = I32(); stack.Add(Site(start, NodeKind.Integer, v.ToString(CultureInfo.InvariantCulture))); break; }
					case 0x8a:
					case 0x8b:
					{
						int len = op == 0x8a ? U8() : I32();
						BigInteger v = new(d.AsSpan(p, len), isUnsigned: false, isBigEndian: false);
						p += len;
						stack.Add(Site(start, NodeKind.Integer, v.ToString(CultureInfo.InvariantCulture)));
						break;
					}
					case (byte)'G':
					{
						double v = BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(p));
						p += 8;
						stack.Add(Site(start, NodeKind.Float, v.ToString("R", CultureInfo.InvariantCulture)));
						break;
					}
					case (byte)'X': { int len = (int)U32(); stack.Add(Text(start, len)); break; }
					case 0x8c: { int len = U8(); stack.Add(Text(start, len)); break; }
					case 0x8d: { int len = U64AsInt(); stack.Add(Text(start, len)); break; }
					case (byte)'T': { int len = I32(); stack.Add(Text(start, len)); break; }
					case (byte)'U': { int len = U8(); stack.Add(Text(start, len)); break; }
					case (byte)'B': { int len = (int)U32(); stack.Add(Bytes(len)); break; }
					case (byte)'C': { int len = U8(); stack.Add(Bytes(len)); break; }
					case 0x8e:
					case 0x96:
					{
						int len = U64AsInt();
						stack.Add(Bytes(len));
						break;
					}
					case (byte)'N': stack.Add(new SaveNode("", NodeKind.Null) { ReadOnly = true }); break;
					case 0x88: stack.Add(Site(start, NodeKind.Bool, "true")); break;
					case 0x89: stack.Add(Site(start, NodeKind.Bool, "false")); break;
					case (byte)'c':
					{
						string module = Line();
						stack.Add(Global(module, Line()));
						break;
					}
					case 0x93:
					{
						string name = Pop().Value ?? "";
						stack.Add(Global(Pop().Value ?? "", name));
						break;
					}
					case (byte)'R':
					case 0x81:
					{
						SaveNode args = Pop();
						SaveNode cls = Pop();
						stack.Add(Instance(cls.Value, args.Kind == NodeKind.Array ? args.Children : []));
						break;
					}
					case 0x92:
					{
						Pop();
						SaveNode args = Pop();
						SaveNode cls = Pop();
						stack.Add(Instance(cls.Value, args.Kind == NodeKind.Array ? args.Children : []));
						break;
					}
					case (byte)'o':
					{
						List<SaveNode> items = PopToMark();
						stack.Add(Instance(items[0].Value, items.GetRange(1, items.Count - 1)));
						break;
					}
					case (byte)'i':
					{
						string module = Line();
						string name = Line();
						stack.Add(Instance($"{module}.{name}", PopToMark()));
						break;
					}
					case (byte)'b':
					{
						SaveNode state = Pop();
						SaveNode obj = stack[^1];
						if (state is { Kind: NodeKind.Object, TypeLabel: "dict" })
						{
							obj.Children.AddRange(state.Children);
						}
						else
						{
							state.Name = "(state)";
							obj.Children.Add(state);
						}

						break;
					}
					case (byte)'0': Pop(); break;
					case (byte)'1': PopToMark(); break;
					case (byte)'2': stack.Add(stack[^1]); break;
					default: throw new SaveException($"Unsupported pickle opcode 0x{op:X2} at offset {start}");
				}
			}
		}

		public static byte[] Write(PickleDocument doc)
		{
			List<SaveNode> edited = [.. doc.Sites.Where(n => n.Value != ((PatchSite)n.Meta!).Original)];
			edited.Sort((a, b) => ((PatchSite)a.Meta!).Start.CompareTo(((PatchSite)b.Meta!).Start));

			List<(int Start, int Length, byte[] Bytes)> patches = [];
			long[] frameDelta = new long[doc.Frames.Count];
			int frame = 0;
			foreach (SaveNode n in edited)
			{
				PatchSite site = (PatchSite)n.Meta!;
				byte[] encoded = Encode(n, site);
				int span = site.Length;

				// A shared value keeps its bytes and memo-put so every memo get still sees the original; it is
				// then popped and the new value pushed in its place, which changes only this one use.
				if (site.Shared && site.MemoLength > 0)
				{
					span += site.MemoLength;
					encoded = [.. doc.Data.AsSpan(site.Start, span), (byte)'0', .. encoded];
				}

				patches.Add((site.Start, span, encoded));

				while (frame < doc.Frames.Count && site.Start >= doc.Frames[frame].Start + doc.Frames[frame].Length)
				{
					frame++;
				}

				if (frame < doc.Frames.Count && site.Start >= doc.Frames[frame].Start)
				{
					frameDelta[frame] += encoded.Length - span;
				}
			}

			for (int i = 0; i < frameDelta.Length; i++)
			{
				if (frameDelta[i] == 0) continue;

				byte[] length = new byte[8];
				BinaryPrimitives.WriteInt64LittleEndian(length, doc.Frames[i].Length + frameDelta[i]);
				patches.Add((doc.Frames[i].FieldOffset, 8, length));
			}

			patches.Sort((a, b) => a.Start.CompareTo(b.Start));

			using MemoryStream ms = new(doc.Data.Length + 64);
			int pos = 0;
			foreach ((int start, int length, byte[] bytes) in patches)
			{
				ms.Write(doc.Data, pos, start - pos);
				ms.Write(bytes);
				pos = start + length;
			}

			ms.Write(doc.Data, pos, doc.Data.Length - pos);
			return ms.ToArray();
		}

		private static byte[] Encode(SaveNode n, PatchSite site)
		{
			string text = n.Value ?? "";
			switch (n.Kind)
			{
				case NodeKind.Integer:
					BigInteger v = BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
					if (v >= 0 && v < 256)
					{
						return [(byte)'K', (byte)v];
					}

					if (v >= 0 && v < 65536)
					{
						return [(byte)'M', (byte)(v & 0xFF), (byte)((int)v >> 8)];
					}

					if (v >= int.MinValue && v <= int.MaxValue)
					{
						byte[] four = new byte[5];
						four[0] = (byte)'J';
						BinaryPrimitives.WriteInt32LittleEndian(four.AsSpan(1), (int)v);
						return four;
					}

					byte[] big = v.ToByteArray();
					return [0x8a, (byte)big.Length, .. big];
				case NodeKind.Float:
					byte[] f = new byte[9];
					f[0] = (byte)'G';
					BinaryPrimitives.WriteDoubleBigEndian(f.AsSpan(1), double.Parse(text, CultureInfo.InvariantCulture));
					return f;
				case NodeKind.Bool:
					return [text == "true" ? (byte)0x88 : (byte)0x89];
				default:
					byte[] utf8 = Encoding.UTF8.GetBytes(text);

					// Python 2 byte strings stay byte strings; everything else becomes a unicode string.
					if (site.Op is (byte)'T' or (byte)'U')
					{
						if (utf8.Length < 256)
						{
							return [(byte)'U', (byte)utf8.Length, .. utf8];
						}

						byte[] longStr = new byte[5 + utf8.Length];
						longStr[0] = (byte)'T';
						BinaryPrimitives.WriteInt32LittleEndian(longStr.AsSpan(1), utf8.Length);
						utf8.CopyTo(longStr, 5);
						return longStr;
					}

					byte[] s = new byte[5 + utf8.Length];
					s[0] = (byte)'X';
					BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(1), (uint)utf8.Length);
					utf8.CopyTo(s, 5);
					return s;
			}
		}

		private static SaveNode Global(string module, string name) =>
			new("", NodeKind.Reference, $"{module}.{name}") { TypeLabel = "global", ReadOnly = true };

		// Remembers where a value's memo-put opcode sits, so an edit can leave the memo slot holding the original.
		private static SaveNode MarkMemo(SaveNode n, int opStart, int opEnd)
		{
			if (n.Meta is PatchSite site && site.Start + site.Length == opStart)
			{
				n.Meta = site with { MemoLength = opEnd - opStart };
			}

			return n;
		}

		private static SaveNode Resolve(Dictionary<int, SaveNode> memo, int index)
		{
			SaveNode n = memo[index];
			if (n.Kind is NodeKind.Object or NodeKind.Array or NodeKind.Reference)
			{
				return new SaveNode("", NodeKind.Reference, "-> " + (n.TypeLabel ?? n.Kind.ToString())) { TypeLabel = "ref", ReadOnly = true };
			}

			// Strings and numbers are immutable in Python, so a memo get is just another use of the original
			// value: it is shown as that value, and editing the original must not change it (see Write).
			if (n.Meta is PatchSite site)
			{
				n.Meta = site with { Shared = true };
			}

			return new SaveNode("", n.Kind, n.Value) { ReadOnly = true };
		}

		private static void AddIndexed(SaveNode target, List<SaveNode> items)
		{
			foreach (SaveNode item in items)
			{
				item.Name = $"[{target.Children.Count}]";
				target.Children.Add(item);
			}
		}

		private static void AddPairs(SaveNode dict, List<SaveNode> flat)
		{
			for (int i = 0; i + 1 < flat.Count; i += 2)
			{
				flat[i + 1].Name = KeyText(flat[i]);
				dict.Children.Add(flat[i + 1]);
			}
		}

		private static string KeyText(SaveNode k) => k.Kind switch
		{
			NodeKind.Null => "None",
			NodeKind.Array => "(" + string.Join(", ", k.Children.Select(KeyText)) + ")",
			NodeKind.Object => k.TypeLabel ?? "object",
			_ => k.Value ?? "",
		};
	}
}
