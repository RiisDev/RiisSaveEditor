using System.Text;

namespace RiisSaveEditor.Saves;

// A port of lz-string's compressToBase64 / decompressFromBase64 (pieroxy, MIT), which RPG Maker MV uses
// for its save files. The compressor is deterministic, so compressing the decompressed text of an
// untouched save gives back the same file.
public static class LzString
{
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=";

    // Characters, not bytes: the text is UTF-16 like the JavaScript string it came from.
    const long MaxChars = SafeIo.MaxExpandedBytes / 2;

    public static string CompressToBase64(string input)
    {
        string result = Compress(input, 6, i => Alphabet[i]);
        return (result.Length % 4) switch
        {
            1 => result + "===",
            2 => result + "==",
            3 => result + "=",
            _ => result,
        };
    }

    // lz-string 1.3.x, which RPG Maker MV ships: the same bitstream packed into 16-bit characters, then
    // standard base64 of their big-endian bytes. Only the padding at the end differs from the 1.4 form,
    // and DecompressFromBase64 reads both.
    public static string CompressToBase64Legacy(string input)
    {
        string packed = Compress(input, 16, i => (char)i);
        byte[] bytes = new byte[packed.Length * 2];
        for (int i = 0; i < packed.Length; i++)
        {
            bytes[2 * i] = (byte)(packed[i] >> 8);
            bytes[2 * i + 1] = (byte)packed[i];
        }

        return Convert.ToBase64String(bytes);
    }

    public static string DecompressFromBase64(string input)
    {
        int[] values = new int[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            int v = Alphabet.IndexOf(input[i]);
            values[i] = v >= 0 ? v : throw new SaveException("Not LZ-String base64 text.");
        }

        return Decompress(values, 32);
    }

    static string Compress(string input, int bitsPerChar, Func<int, char> charFor)
    {
        Dictionary<string, int> dictionary = [];
        HashSet<string> toCreate = [];
        string w = "";
        int enlargeIn = 2, dictSize = 3, numBits = 2;
        StringBuilder data = new();
        int dataVal = 0, dataPosition = 0;

        void Emit(int bit)
        {
            dataVal = (dataVal << 1) | bit;
            if (dataPosition == bitsPerChar - 1)
            {
                dataPosition = 0;
                data.Append(charFor(dataVal));
                dataVal = 0;
            }
            else
            {
                dataPosition++;
            }
        }

        void EmitValue(int value, int bits)
        {
            for (int i = 0; i < bits; i++)
            {
                Emit(value & 1);
                value >>= 1;
            }
        }

        void Grow()
        {
            if (--enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }
        }

        void OutputW()
        {
            if (toCreate.Remove(w))
            {
                if (w[0] < 256)
                {
                    EmitValue(0, numBits);
                    EmitValue(w[0], 8);
                }
                else
                {
                    EmitValue(1, numBits);
                    EmitValue(w[0], 16);
                }

                Grow();
            }
            else
            {
                EmitValue(dictionary[w], numBits);
            }

            Grow();
        }

        foreach (char ch in input)
        {
            string c = ch.ToString();
            if (dictionary.TryAdd(c, dictSize))
            {
                dictSize++;
                toCreate.Add(c);
            }

            string wc = w + c;
            if (dictionary.ContainsKey(wc))
            {
                w = wc;
                continue;
            }

            OutputW();
            dictionary[wc] = dictSize++;
            w = c;
        }

        if (w.Length > 0)
        {
            OutputW();
        }

        EmitValue(2, numBits);
        while (true)
        {
            dataVal <<= 1;
            if (dataPosition == bitsPerChar - 1)
            {
                data.Append(charFor(dataVal));
                break;
            }

            dataPosition++;
        }

        return data.ToString();
    }

    static string Decompress(int[] input, int resetValue)
    {
        List<string> dictionary = ["0", "1", "2"];
        int enlargeIn = 4, numBits = 3;
        int val = input.Length > 0 ? input[0] : 0, position = resetValue, index = 1;
        StringBuilder result = new();

        int Read(int count)
        {
            int bits = 0;
            for (int power = 1; power != 1 << count; power <<= 1)
            {
                int resb = val & position;
                position >>= 1;
                if (position == 0)
                {
                    position = resetValue;
                    val = index < input.Length ? input[index] : 0;
                    index++;
                }

                bits |= (resb > 0 ? 1 : 0) * power;
            }

            return bits;
        }

        string c = Read(2) switch
        {
            0 => ((char)Read(8)).ToString(),
            1 => ((char)Read(16)).ToString(),
            _ => "",
        };
        if (c.Length == 0)
        {
            return "";
        }

        dictionary.Add(c);
        string w = c;
        result.Append(c);
        while (true)
        {
            if (index > input.Length)
            {
                throw new SaveException("The compressed save ends early.");
            }

            int code = Read(numBits);
            switch (code)
            {
                case 0:
                case 1:
                    dictionary.Add(((char)Read(code == 0 ? 8 : 16)).ToString());
                    code = dictionary.Count - 1;
                    enlargeIn--;
                    break;
                case 2:
                    return result.ToString();
            }

            if (enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }

            string entry = code < dictionary.Count ? dictionary[code]
                : code == dictionary.Count ? w + w[0]
                : throw new SaveException("The compressed save is damaged.");

            if (result.Length + entry.Length > MaxChars)
            {
                throw new SaveException($"The file expands to more than {SafeIo.MaxExpandedBytes >> 20} MB when decompressed, so it was rejected.");
            }

            result.Append(entry);
            dictionary.Add(w + entry[0]);
            enlargeIn--;
            w = entry;
            if (enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }
        }
    }
}
