namespace StableRemake;

// Minimal LZMA decoder for the "LZMA alone" format stable uses inside .osr replays
// (5-byte properties, 8-byte uncompressed size, range-coded stream). Written from the public-domain
// LZMA specification (LzmaSpec); decode-only, whole output kept in memory.
static class Lzma
{
    public static byte[] Decode(byte[] input)
    {
        if (input.Length < 13) throw new InvalidDataException("lzma: truncated header");
        int d = input[0];
        if (d >= 9 * 5 * 5) throw new InvalidDataException("lzma: bad properties");
        int lc = d % 9; d /= 9;
        int lp = d % 5, pb = d / 5;
        long size = BitConverter.ToInt64(input, 5);
        return new Decoder(input, 13, lc, lp, pb).Run(size);
    }

    // Encoder for writing .osr files: every byte coded as a literal (no matches), which is a valid LZMA stream
    // any decoder reads; larger than real LZMA output but simple. lc=3 lp=0 pb=2, sizes in the header.
    public static byte[] Encode(byte[] data)
    {
        var o = new List<byte>(data.Length + 32) { 0x5D };
        o.AddRange(BitConverter.GetBytes(1 << 20)); // dictionary size
        o.AddRange(BitConverter.GetBytes((long)data.Length));
        ulong low = 0;
        uint range = 0xFFFFFFFF;
        byte cache = 0;
        long cacheSize = 1;
        var isMatch = new ushort[4];
        var literal = new ushort[0x300 << 3];
        Array.Fill(isMatch, (ushort)1024);
        Array.Fill(literal, (ushort)1024);
        void ShiftLow()
        {
            if ((uint)low < 0xFF000000u || (low >> 32) != 0)
            {
                byte carry = (byte)(low >> 32), temp = cache;
                do { o.Add((byte)(temp + carry)); temp = 0xFF; } while (--cacheSize != 0);
                cache = (byte)((uint)low >> 24);
            }
            cacheSize++;
            low = (low & 0x00FFFFFF) << 8;
        }
        void Bit(ushort[] probs, int i, int bit)
        {
            uint bound = (range >> 11) * probs[i];
            if (bit == 0) { range = bound; probs[i] += (ushort)((2048 - probs[i]) >> 5); }
            else { low += bound; range -= bound; probs[i] -= (ushort)(probs[i] >> 5); }
            while (range < 1u << 24) { range <<= 8; ShiftLow(); }
        }
        byte prev = 0;
        for (int p = 0; p < data.Length; p++)
        {
            Bit(isMatch, p & 3, 0); // state stays 0: only literals
            int b = data[p], baseIdx = 0x300 * (prev >> 5), symbol = 1;
            for (int k = 7; k >= 0; k--)
            {
                int bit = (b >> k) & 1;
                Bit(literal, baseIdx + symbol, bit);
                symbol = (symbol << 1) | bit;
            }
            prev = (byte)b;
        }
        for (int i = 0; i < 5; i++) ShiftLow();
        return o.ToArray();
    }

    sealed class Decoder
    {
        readonly byte[] src;
        int pos;
        uint range = 0xFFFFFFFF, code;
        readonly int lc, lp, pb;
        readonly List<byte> output = [];

        readonly ushort[] literal;
        readonly ushort[] isMatch = Probs(12 << 4), isRep = Probs(12), isRepG0 = Probs(12), isRepG1 = Probs(12),
            isRepG2 = Probs(12), isRep0Long = Probs(12 << 4);
        readonly ushort[][] posSlot = [Probs(1 << 6), Probs(1 << 6), Probs(1 << 6), Probs(1 << 6)];
        readonly ushort[] posDecoders = Probs(1 + 128 - 14), align = Probs(1 << 4);
        readonly LenDecoder len = new(), repLen = new();

        static ushort[] Probs(int n) => Enumerable.Repeat((ushort)1024, n).ToArray();

        public Decoder(byte[] src, int start, int lc, int lp, int pb)
        {
            this.src = src; pos = start; this.lc = lc; this.lp = lp; this.pb = pb;
            literal = Probs(0x300 << (lc + lp));
            if (Next() != 0) throw new InvalidDataException("lzma: bad stream start");
            for (int i = 0; i < 4; i++) code = (code << 8) | Next();
        }

        byte Next() => pos < src.Length ? src[pos++] : throw new InvalidDataException("lzma: unexpected end of input");

        void Normalize()
        {
            if (range < 1u << 24) { range <<= 8; code = (code << 8) | Next(); }
        }

        int Bit(ushort[] probs, int i)
        {
            uint bound = (range >> 11) * probs[i];
            int bit;
            if (code < bound) { probs[i] += (ushort)((2048 - probs[i]) >> 5); range = bound; bit = 0; }
            else { probs[i] -= (ushort)(probs[i] >> 5); code -= bound; range -= bound; bit = 1; }
            Normalize();
            return bit;
        }

        uint DirectBits(int n)
        {
            uint res = 0;
            do
            {
                range >>= 1;
                code -= range;
                uint t = 0u - (code >> 31);
                code += range & t;
                Normalize();
                res = (res << 1) + (t + 1);
            } while (--n > 0);
            return res;
        }

        int Tree(ushort[] probs, int bits, int offset = 0)
        {
            int m = 1;
            for (int i = 0; i < bits; i++) m = (m << 1) + Bit(probs, offset + m);
            return m - (1 << bits);
        }

        int ReverseTree(ushort[] probs, int bits, int offset = 0)
        {
            int m = 1, symbol = 0;
            for (int i = 0; i < bits; i++)
            {
                int bit = Bit(probs, offset + m);
                m = (m << 1) + bit;
                symbol |= bit << i;
            }
            return symbol;
        }

        sealed class LenDecoder
        {
            readonly ushort[] choice = Probs(2), high = Probs(1 << 8);
            readonly ushort[][] low = Enumerable.Range(0, 16).Select(_ => Probs(1 << 3)).ToArray();
            readonly ushort[][] mid = Enumerable.Range(0, 16).Select(_ => Probs(1 << 3)).ToArray();

            public int Decode(Decoder d, int posState)
            {
                if (d.Bit(choice, 0) == 0) return d.Tree(low[posState], 3);
                if (d.Bit(choice, 1) == 0) return 8 + d.Tree(mid[posState], 3);
                return 16 + d.Tree(high, 8);
            }
        }

        byte Back(uint dist) => output[output.Count - (int)dist];

        void Literal(int state, uint rep0)
        {
            int prev = output.Count > 0 ? output[^1] : 0;
            int litState = ((output.Count & ((1 << lp) - 1)) << lc) + (prev >> (8 - lc));
            int baseIndex = 0x300 * litState;
            int symbol = 1;
            if (state >= 7)
            {
                int matchByte = Back(rep0 + 1);
                do
                {
                    int matchBit = (matchByte >> 7) & 1;
                    matchByte <<= 1;
                    int bit = Bit(literal, baseIndex + ((1 + matchBit) << 8) + symbol);
                    symbol = (symbol << 1) | bit;
                    if (matchBit != bit) break;
                } while (symbol < 0x100);
            }
            while (symbol < 0x100) symbol = (symbol << 1) | Bit(literal, baseIndex + symbol);
            output.Add((byte)(symbol - 0x100));
        }

        uint Distance(int length)
        {
            int lenState = Math.Min(length, 3);
            int slot = Tree(posSlot[lenState], 6);
            if (slot < 4) return (uint)slot;
            int direct = (slot >> 1) - 1;
            uint dist = (uint)((2 | (slot & 1)) << direct);
            if (slot < 14) dist += (uint)ReverseTree(posDecoders, direct, (int)dist - slot);
            else
            {
                dist += DirectBits(direct - 4) << 4;
                dist += (uint)ReverseTree(align, 4);
            }
            return dist;
        }

        public byte[] Run(long size)
        {
            bool sized = size >= 0;
            int state = 0;
            uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
            while (!sized || output.Count < size)
            {
                int posState = output.Count & ((1 << pb) - 1);
                if (Bit(isMatch, (state << 4) + posState) == 0)
                {
                    Literal(state, rep0);
                    state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                    continue;
                }
                int length;
                if (Bit(isRep, state) != 0)
                {
                    if (output.Count == 0) throw new InvalidDataException("lzma: rep at start");
                    if (Bit(isRepG0, state) == 0)
                    {
                        if (Bit(isRep0Long, (state << 4) + posState) == 0)
                        {
                            state = state < 7 ? 9 : 11;
                            output.Add(Back(rep0 + 1));
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (Bit(isRepG1, state) == 0) dist = rep1;
                        else
                        {
                            if (Bit(isRepG2, state) == 0) dist = rep2;
                            else { dist = rep3; rep3 = rep2; }
                            rep2 = rep1;
                        }
                        rep1 = rep0;
                        rep0 = dist;
                    }
                    length = repLen.Decode(this, posState);
                    state = state < 7 ? 8 : 11;
                }
                else
                {
                    rep3 = rep2; rep2 = rep1; rep1 = rep0;
                    length = len.Decode(this, posState);
                    state = state < 7 ? 7 : 10;
                    rep0 = Distance(length);
                    if (rep0 == 0xFFFFFFFF) break; // end marker
                    if (rep0 >= output.Count) throw new InvalidDataException("lzma: distance out of range");
                }
                length += 2;
                for (int i = 0; i < length && (!sized || output.Count < size); i++) output.Add(Back(rep0 + 1));
            }
            return output.ToArray();
        }
    }
}
