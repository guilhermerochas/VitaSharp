#nullable disable
using System;
using System.Collections.Generic;

namespace VitaSharp.Core.Hle.Audio.Atrac9
{
    public unsafe class Mdct
    {
        public int MdctBits { get; }
        public int MdctSize { get; }
        public double Scale { get; }

        private static readonly object TableLock = new object();
        private static int _tableBits = -1;
        private static readonly List<double[]> SinTables = new List<double[]>();
        private static readonly List<double[]> CosTables = new List<double[]>();
        private static readonly List<int[]> ShuffleTables = new List<int[]>();

        private readonly double[] _mdctPrevious;
        private readonly double[] _imdctPrevious;
        private readonly double[] _imdctWindow;

        private readonly double[] _scratchMdct;
        private readonly double[] _scratchDct;
        // this size's tables, fetched once (List indexers per stage were calls under Mono's AOT)
        private readonly int[] _shuffle;
        private readonly double[][] _sin;
        private readonly double[][] _cos;

        public Mdct(int mdctBits, double[] window, double scale = 1)
        {
            SetTables(mdctBits);

            MdctBits = mdctBits;
            MdctSize = 1 << mdctBits;
            Scale = scale;

            if (window.Length < MdctSize)
            {
                throw new ArgumentException("Window must be as long as the MDCT size.", nameof(window));
            }

            _mdctPrevious = new double[MdctSize];
            _imdctPrevious = new double[MdctSize];
            _scratchMdct = new double[MdctSize];
            _scratchDct = new double[MdctSize];
            _imdctWindow = window;
            lock (TableLock)
            {
                _shuffle = ShuffleTables[mdctBits];
                _sin = SinTables.GetRange(0, mdctBits + 1).ToArray();
                _cos = CosTables.GetRange(0, mdctBits + 1).ToArray();
            }
        }

        private static void SetTables(int maxBits)
        {
            lock (TableLock)
            {
                if (maxBits > _tableBits)
                {
                    for (int i = _tableBits + 1; i <= maxBits; i++)
                    {
                        GenerateTrigTables(i, out double[] sin, out double[] cos);
                        SinTables.Add(sin);
                        CosTables.Add(cos);
                        ShuffleTables.Add(GenerateShuffleTable(i));
                    }
                    _tableBits = maxBits;
                }
            }
        }

        public void RunMdct(double[] input, double[] output)
        {
            if (input.Length < MdctSize)
            {
                throw new ArgumentException("Input must be as long as the MDCT size.", nameof(input));
            }

            if (output.Length < MdctSize)
            {
                throw new ArgumentException("Output must be as long as the MDCT size.", nameof(output));
            }

            int size = MdctSize;
            int half = size / 2;
            double[] dctIn = _scratchMdct;

            for (int i = 0; i < half; i++)
            {
                double a = _imdctWindow[half - i - 1] * -input[half + i];
                double b = _imdctWindow[half + i] * input[half - i - 1];
                double c = _imdctWindow[i] * _mdctPrevious[i];
                double d = _imdctWindow[size - i - 1] * _mdctPrevious[size - i - 1];

                dctIn[i] = a - b;
                dctIn[half + i] = c - d;
            }

            Dct4(dctIn, output);
            Array.Copy(input, _mdctPrevious, input.Length);
        }

        public void RunImdct(double[] input, double[] output)
        {
            if (input.Length < MdctSize)
            {
                throw new ArgumentException("Input must be as long as the MDCT size.", nameof(input));
            }

            if (output.Length < MdctSize)
            {
                throw new ArgumentException("Output must be as long as the MDCT size.", nameof(output));
            }

            int size = MdctSize;
            int half = size / 2;
            double[] dctOut = _scratchMdct;

            Dct4(input, dctOut);

            // pointers: no bounds check per element (Mono's AOT keeps them all); lengths checked above
            fixed (double* o = output, d = dctOut, w = _imdctWindow, prev = _imdctPrevious)
            {
                for (int i = 0; i < half; i++)
                {
                    o[i] = w[i] * d[i + half] + prev[i];
                    o[i + half] = w[i + half] * -d[size - 1 - i] - prev[i + half];
                    prev[i] = w[size - 1 - i] * -d[half - i - 1];
                    prev[i + half] = w[half - i - 1] * d[i];
                }
            }
        }

        /// <summary>
        /// Does a Type-4 DCT.
        /// </summary>
        /// <param name="input">The input array containing the time or frequency-domain samples</param>
        /// <param name="output">The output array that will contain the transformed time or frequency-domain samples</param>
        private void Dct4(double[] input, double[] output)
        {
            int size = MdctSize;
            if (input.Length < size || output.Length < size) throw new ArgumentException("Buffers must be as long as the MDCT size.");
            int lastIndex = size - 1;
            int halfSize = size / 2;
            double scale = Scale;
            // the same arithmetic as before through pointers: no bounds check per element (Mono's AOT keeps them all)
            fixed (double* inp = input, outp = output, t = _scratchDct, sin0 = _sin[MdctBits], cos0 = _cos[MdctBits])
            fixed (int* shuffle = _shuffle)
            {
                for (int i = 0; i < halfSize; i++)
                {
                    int i2 = i * 2;
                    double a = inp[i2];
                    double b = inp[lastIndex - i2];
                    double sin = sin0[i];
                    double cos = cos0[i];
                    t[i2] = a * cos + b * sin;
                    t[i2 + 1] = a * sin - b * cos;
                }
                int stageCount = MdctBits - 1;

                for (int stage = 0; stage < stageCount; stage++)
                {
                    int blockCount = 1 << stage;
                    int blockSizeBits = stageCount - stage;
                    int blockHalfSizeBits = blockSizeBits - 1;
                    int blockSize = 1 << blockSizeBits;
                    int blockHalfSize = 1 << blockHalfSizeBits;
                    fixed (double* sinT = _sin[blockHalfSizeBits], cosT = _cos[blockHalfSizeBits])
                    {
                        for (int block = 0; block < blockCount; block++)
                        {
                            double* front = t + block * blockSize * 2;
                            double* back = front + blockSize;
                            for (int i = 0; i < blockHalfSize; i++)
                            {
                                int p = i * 2;
                                double f0 = front[p], f1 = front[p + 1], b0 = back[p], b1 = back[p + 1];
                                double a = f0 - b0;
                                double b = f1 - b1;
                                double sin = sinT[i];
                                double cos = cosT[i];
                                front[p] = f0 + b0;
                                front[p + 1] = f1 + b1;
                                back[p] = a * cos + b * sin;
                                back[p + 1] = a * sin - b * cos;
                            }
                        }
                    }
                }

                for (int i = 0; i < size; i++)
                {
                    outp[i] = t[shuffle[i]] * scale;
                }
            }
        }

        /// <summary>The original managed Dct4 (bounds-checked arrays), kept for the equivalence test.</summary>
        internal void Dct4Reference(double[] input, double[] output)
        {
            int[] shuffleTable = ShuffleTables[MdctBits];
            double[] sinTable = SinTables[MdctBits];
            double[] cosTable = CosTables[MdctBits];
            double[] dctTemp = new double[MdctSize];

            int size = MdctSize;
            int lastIndex = size - 1;
            int halfSize = size / 2;

            for (int i = 0; i < halfSize; i++)
            {
                int i2 = i * 2;
                double a = input[i2];
                double b = input[lastIndex - i2];
                double sin = sinTable[i];
                double cos = cosTable[i];
                dctTemp[i2] = a * cos + b * sin;
                dctTemp[i2 + 1] = a * sin - b * cos;
            }
            int stageCount = MdctBits - 1;

            for (int stage = 0; stage < stageCount; stage++)
            {
                int blockCount = 1 << stage;
                int blockSizeBits = stageCount - stage;
                int blockHalfSizeBits = blockSizeBits - 1;
                int blockSize = 1 << blockSizeBits;
                int blockHalfSize = 1 << blockHalfSizeBits;
                sinTable = SinTables[blockHalfSizeBits];
                cosTable = CosTables[blockHalfSizeBits];

                for (int block = 0; block < blockCount; block++)
                {
                    for (int i = 0; i < blockHalfSize; i++)
                    {
                        int frontPos = (block * blockSize + i) * 2;
                        int backPos = frontPos + blockSize;
                        double a = dctTemp[frontPos] - dctTemp[backPos];
                        double b = dctTemp[frontPos + 1] - dctTemp[backPos + 1];
                        double sin = sinTable[i];
                        double cos = cosTable[i];
                        dctTemp[frontPos] += dctTemp[backPos];
                        dctTemp[frontPos + 1] += dctTemp[backPos + 1];
                        dctTemp[backPos] = a * cos + b * sin;
                        dctTemp[backPos + 1] = a * sin - b * cos;
                    }
                }
            }

            for (int i = 0; i < MdctSize; i++)
            {
                output[i] = dctTemp[shuffleTable[i]] * Scale;
            }
        }

        /// <summary>Test hook: the optimized Dct4.</summary>
        internal void Dct4ForTest(double[] input, double[] output) => Dct4(input, output);

        internal static void GenerateTrigTables(int sizeBits, out double[] sin, out double[] cos)
        {
            int size = 1 << sizeBits;
            sin = new double[size];
            cos = new double[size];

            for (int i = 0; i < size; i++)
            {
                double value = Math.PI * (4 * i + 1) / (4 * size);
                sin[i] = Math.Sin(value);
                cos[i] = Math.Cos(value);
            }
        }

        internal static int[] GenerateShuffleTable(int sizeBits)
        {
            int size = 1 << sizeBits;
            var table = new int[size];

            for (int i = 0; i < size; i++)
            {
                table[i] = Bit.BitReverse32(i ^ (i / 2), sizeBits);
            }

            return table;
        }

        // ReSharper disable once UnusedMember.Local
        /// <summary>
        /// Does a Type-4 DCT. Intended for reference.
        /// </summary>
        /// <param name="input">The input array containing the time or frequency-domain samples</param>
        /// <param name="output">The output array that will contain the transformed time or frequency-domain samples</param>
        private void Dct4Slow(double[] input, double[] output)
        {
            for (int k = 0; k < MdctSize; k++)
            {
                double sample = 0;
                for (int n = 0; n < MdctSize; n++)
                {
                    double angle = Math.PI / MdctSize * (k + 0.5) * (n + 0.5);
                    sample += Math.Cos(angle) * input[n];
                }
                output[k] = sample * Scale;
            }
        }
    }
}
