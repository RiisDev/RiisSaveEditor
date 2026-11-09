namespace RiisSaveEditor.Saves;

// Rijndael with a 256-bit key and a 128 or 256-bit block. .NET's Aes only does 128-bit blocks, but some
// games use the 256-bit block variant (RijndaelManaged with BlockSize = 256), which is not AES.
internal sealed class RijndaelCipher
{
    const int KeyWords = 8;

    static readonly byte[] Sbox = new byte[256];
    static readonly byte[] InvSbox = new byte[256];

    readonly int _nb;
    readonly int _rounds;
    readonly int[] _shifts;
    readonly byte[][] _roundKeys;

    static RijndaelCipher()
    {
        byte p = 1;
        byte q = 1;
        do
        {
            p = (byte)(p ^ (p << 1) ^ ((p & 0x80) != 0 ? 0x1B : 0));
            q ^= (byte)(q << 1);
            q ^= (byte)(q << 2);
            q ^= (byte)(q << 4);
            if ((q & 0x80) != 0)
            {
                q ^= 0x09;
            }

            Sbox[p] = (byte)(q ^ Rotl(q, 1) ^ Rotl(q, 2) ^ Rotl(q, 3) ^ Rotl(q, 4) ^ 0x63);
        }
        while (p != 1);

        Sbox[0] = 0x63;
        for (int i = 0; i < 256; i++)
        {
            InvSbox[Sbox[i]] = (byte)i;
        }
    }

    public RijndaelCipher(byte[] key, int blockBytes)
    {
        if (key.Length != 32 || blockBytes is not (16 or 32))
        {
            throw new ArgumentException("Rijndael here needs a 32 byte key and a 16 or 32 byte block.");
        }

        _nb = blockBytes / 4;
        _rounds = Math.Max(_nb, KeyWords) + 6;
        _shifts = _nb == 4 ? [0, 1, 2, 3] : [0, 1, 3, 4];
        BlockBytes = blockBytes;
        _roundKeys = ExpandKey(key);
    }

    public int BlockBytes { get; }

    public byte[] DecryptCbc(byte[] data, byte[] iv)
    {
        if (data.Length == 0 || data.Length % BlockBytes != 0)
        {
            throw new SaveException("The encrypted data is not a whole number of blocks.");
        }

        byte[] plain = new byte[data.Length];
        byte[] previous = iv;
        byte[] block = new byte[BlockBytes];
        for (int offset = 0; offset < data.Length; offset += BlockBytes)
        {
            Array.Copy(data, offset, block, 0, BlockBytes);
            byte[] decrypted = DecryptBlock(block);
            for (int i = 0; i < BlockBytes; i++)
            {
                plain[offset + i] = (byte)(decrypted[i] ^ previous[i]);
            }

            previous = data[offset..(offset + BlockBytes)];
        }

        int pad = plain[^1];
        if (pad < 1 || pad > BlockBytes || pad > plain.Length || plain[^pad..].Any(b => b != pad))
        {
            throw new SaveException("The padding is invalid, so the password is wrong or the data is damaged.");
        }

        return plain[..^pad];
    }

    public byte[] EncryptCbc(byte[] plain, byte[] iv)
    {
        int pad = BlockBytes - plain.Length % BlockBytes;
        byte[] padded = [.. plain, .. Enumerable.Repeat((byte)pad, pad)];
        byte[] result = new byte[padded.Length];
        byte[] previous = iv;
        byte[] block = new byte[BlockBytes];
        for (int offset = 0; offset < padded.Length; offset += BlockBytes)
        {
            for (int i = 0; i < BlockBytes; i++)
            {
                block[i] = (byte)(padded[offset + i] ^ previous[i]);
            }

            previous = EncryptBlock(block);
            Array.Copy(previous, 0, result, offset, BlockBytes);
        }

        return result;
    }

    public byte[] EncryptBlock(byte[] input)
    {
        byte[] s = (byte[])input.Clone();
        AddRoundKey(s, 0);
        for (int round = 1; round < _rounds; round++)
        {
            SubBytes(s, Sbox);
            ShiftRows(s, inverse: false);
            MixColumns(s, inverse: false);
            AddRoundKey(s, round);
        }

        SubBytes(s, Sbox);
        ShiftRows(s, inverse: false);
        AddRoundKey(s, _rounds);
        return s;
    }

    public byte[] DecryptBlock(byte[] input)
    {
        byte[] s = (byte[])input.Clone();
        AddRoundKey(s, _rounds);
        for (int round = _rounds - 1; round >= 1; round--)
        {
            ShiftRows(s, inverse: true);
            SubBytes(s, InvSbox);
            AddRoundKey(s, round);
            MixColumns(s, inverse: true);
        }

        ShiftRows(s, inverse: true);
        SubBytes(s, InvSbox);
        AddRoundKey(s, 0);
        return s;
    }

    byte[][] ExpandKey(byte[] key)
    {
        int words = _nb * (_rounds + 1);
        byte[][] w = new byte[words][];
        for (int i = 0; i < KeyWords; i++)
        {
            w[i] = key[(4 * i)..(4 * i + 4)];
        }

        byte rcon = 1;
        for (int i = KeyWords; i < words; i++)
        {
            byte[] temp = (byte[])w[i - 1].Clone();
            if (i % KeyWords == 0)
            {
                temp = [Sbox[temp[1]], Sbox[temp[2]], Sbox[temp[3]], Sbox[temp[0]]];
                temp[0] ^= rcon;
                rcon = Xtime(rcon);
            }
            else if (i % KeyWords == 4)
            {
                temp = [Sbox[temp[0]], Sbox[temp[1]], Sbox[temp[2]], Sbox[temp[3]]];
            }

            w[i] = new byte[4];
            for (int b = 0; b < 4; b++)
            {
                w[i][b] = (byte)(w[i - KeyWords][b] ^ temp[b]);
            }
        }

        byte[][] roundKeys = new byte[_rounds + 1][];
        for (int r = 0; r <= _rounds; r++)
        {
            roundKeys[r] = [.. Enumerable.Range(0, _nb).SelectMany(c => w[r * _nb + c])];
        }

        return roundKeys;
    }

    void AddRoundKey(byte[] s, int round)
    {
        byte[] key = _roundKeys[round];
        for (int i = 0; i < s.Length; i++)
        {
            s[i] ^= key[i];
        }
    }

    static void SubBytes(byte[] s, byte[] box)
    {
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = box[s[i]];
        }
    }

    // State bytes are column-major: byte i sits in row i % 4 and column i / 4.
    void ShiftRows(byte[] s, bool inverse)
    {
        byte[] copy = (byte[])s.Clone();
        for (int row = 1; row < 4; row++)
        {
            for (int col = 0; col < _nb; col++)
            {
                // Forward moves each row left by its offset, so the byte now at col came from col + offset.
                int source = inverse ? (col - _shifts[row] + _nb) % _nb : (col + _shifts[row]) % _nb;
                s[4 * col + row] = copy[4 * source + row];
            }
        }
    }

    void MixColumns(byte[] s, bool inverse)
    {
        for (int col = 0; col < _nb; col++)
        {
            int i = 4 * col;
            byte a0 = s[i], a1 = s[i + 1], a2 = s[i + 2], a3 = s[i + 3];
            if (!inverse)
            {
                s[i] = (byte)(Mul(a0, 2) ^ Mul(a1, 3) ^ a2 ^ a3);
                s[i + 1] = (byte)(a0 ^ Mul(a1, 2) ^ Mul(a2, 3) ^ a3);
                s[i + 2] = (byte)(a0 ^ a1 ^ Mul(a2, 2) ^ Mul(a3, 3));
                s[i + 3] = (byte)(Mul(a0, 3) ^ a1 ^ a2 ^ Mul(a3, 2));
            }
            else
            {
                s[i] = (byte)(Mul(a0, 14) ^ Mul(a1, 11) ^ Mul(a2, 13) ^ Mul(a3, 9));
                s[i + 1] = (byte)(Mul(a0, 9) ^ Mul(a1, 14) ^ Mul(a2, 11) ^ Mul(a3, 13));
                s[i + 2] = (byte)(Mul(a0, 13) ^ Mul(a1, 9) ^ Mul(a2, 14) ^ Mul(a3, 11));
                s[i + 3] = (byte)(Mul(a0, 11) ^ Mul(a1, 13) ^ Mul(a2, 9) ^ Mul(a3, 14));
            }
        }
    }

    static byte Xtime(byte x) => (byte)((x << 1) ^ ((x & 0x80) != 0 ? 0x1B : 0));

    static byte Mul(byte x, int by)
    {
        byte result = 0;
        for (; by > 0; by >>= 1)
        {
            if ((by & 1) != 0)
            {
                result ^= x;
            }

            x = Xtime(x);
        }

        return result;
    }

    static int Rotl(int x, int n) => ((x << n) | (x >> (8 - n))) & 0xFF;
}
