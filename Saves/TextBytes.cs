using System.Text;

namespace RiisSaveEditor.Saves
{
	// Strict UTF-8 and byte-order-mark handling shared by the text-based formats.
	public static class TextBytes
	{
		public static readonly UTF8Encoding Strict = new(false, true);
		public static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

		public static bool HasBom(ReadOnlySpan<byte> data) => data.StartsWith(Utf8Bom);

		// Throws a DecoderFallbackException (an ArgumentException) on invalid UTF-8.
		public static string DecodeUtf8(byte[] data, out bool bom)
		{
			bom = HasBom(data);
			int skip = bom ? Utf8Bom.Length : 0;
			return Strict.GetString(data, skip, data.Length - skip);
		}

		public static byte[] EncodeUtf8(string text, bool bom)
		{
			byte[] body = Encoding.UTF8.GetBytes(text);
			return bom ? [.. Utf8Bom, .. body] : body;
		}
	}
}
