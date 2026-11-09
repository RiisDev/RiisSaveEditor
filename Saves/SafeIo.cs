namespace RiisSaveEditor.Saves;

// Uploaded saves are untrusted, so anything that expands data (zip, gzip, zlib) goes through
// here. A few hundred KB can otherwise inflate to many GB and take the server down.
public static class SafeIo
{
    // Same as the upload limit: a save that expands past this is not a realistic save.
    public const long MaxExpandedBytes = 128L * 1024 * 1024;

    public static byte[] ReadAll(Stream source, long limit = MaxExpandedBytes)
    {
        using MemoryStream result = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (result.Length + read > limit)
            {
                throw new SaveException($"The file expands to more than {limit >> 20} MB when decompressed, so it was rejected.");
            }

            result.Write(buffer, 0, read);
        }

        return result.ToArray();
    }
}
