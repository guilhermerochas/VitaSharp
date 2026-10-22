#nullable disable
using System;
using System.IO;
using System.IO.Compression;

namespace VitaSharp.Core.Hle.Audio.Atrac9
{
    public static class ArrayUnpacker
    {
        public static Array[] UnpackArrays(byte[] packedArrays)
        {
            packedArrays = TryDecompress(packedArrays);

            using (var stream = new MemoryStream(packedArrays))
            using (var reader = new BinaryReader(stream))
            {
                int compressed = reader.ReadByte();
                int version = reader.ReadByte();
                if (compressed != 0 || version != 0) throw new InvalidDataException();

                int count = reader.ReadUInt16();
                var arrays = new Array[count];

                for (int i = 0; i < count; i++)
                {
                    byte id = reader.ReadByte();
                    byte type = reader.ReadByte();
                    Type outType = TypeLookup[Helpers.GetHighNibble(type)];
                    byte rank = Helpers.GetLowNibble(type);
                    arrays[id] = UnpackArray(reader, outType, rank);
                }

                return arrays;
            }
        }

        private static Array UnpackArray(BinaryReader reader, Type outType, int rank)
        {
            byte modeType = reader.ReadByte();
            if (modeType == byte.MaxValue) return null;

            byte mode = Helpers.GetHighNibble(modeType);
            Type storedType = TypeLookup[Helpers.GetLowNibble(modeType)];
            Type elementType = JaggedType(outType, rank - 1);

            switch (mode)
            {
                case 0:
                    {
                        int length = reader.ReadUInt16();

                        if (rank == 1)
                        {
                            return ReadArray(reader, storedType, elementType, length);
                        }

                        Array array = NewArray(elementType, length);

                        for (int i = 0; i < length; i++)
                        {
                            array.SetValue(UnpackArray(reader, outType, rank - 1), i);
                        }

                        return array;
                    }
                case 1:
                    {
                        var dimensions = new int[rank];

                        for (int d = 0; d < dimensions.Length; d++)
                        {
                            dimensions[d] = reader.ReadUInt16();
                        }

                        return UnpackInternal(elementType, storedType, reader, 0, dimensions);
                    }
                case 2:
                    {
                        int length = reader.ReadUInt16();
                        var lengths = new int[length];

                        for (int i = 0; i < length; i++)
                        {
                            lengths[i] = reader.ReadUInt16();
                        }

                        Array array = NewArray(elementType, length);

                        for (int i = 0; i < length; i++)
                        {
                            array.SetValue(ReadArray(reader, storedType, outType, lengths[i]), i);
                        }

                        return array;
                    }

                default:
                    throw new InvalidDataException();
            }
        }

        private static Array ReadArray(BinaryReader reader, Type storedType, Type outType, int length)
        {
            if (length == ushort.MaxValue) return null;

            int lengthBytes = length * SizeOf(storedType);
            Array array = NewArray(storedType, length);
            byte[] bytes = reader.ReadBytes(lengthBytes);
            Buffer.BlockCopy(bytes, 0, array, 0, lengthBytes);

            return storedType == outType ? array : CastArray(array, outType);
        }

        private static Array CastArray(Array inArray, Type outType)
        {
            Array outArray = NewArray(outType, inArray.Length);

            for (int i = 0; i < inArray.Length; i++)
            {
                object inValue = inArray.GetValue(i);
                object outValue = Convert.ChangeType(inValue, outType);
                outArray.SetValue(outValue, i);
            }
            return outArray;
        }

        private static byte[] TryDecompress(byte[] data)
        {
            bool compressed = data[0] == 1;
            if (compressed)
            {
                int decompressedLength = BitConverter.ToInt32(data, 1);
                data = Inflate(data, 5, decompressedLength);
            }
            return data;
        }

        private static byte[] Inflate(byte[] compressed, int startIndex, int length)
        {
            var inflatedBytes = new byte[length];
            using (var stream = new MemoryStream(compressed))
            {
                stream.Position = startIndex;
                using (var deflate = new DeflateStream(stream, CompressionMode.Decompress))
                {
                    deflate.ReadExactly(inflatedBytes, 0, length); // VitaSharp: Read may return partial data on .NET 6+
                }
            }

            return inflatedBytes;
        }

        private static Array UnpackInternal(Type outType, Type storedType, BinaryReader reader, int depth, int[] dimensions)
        {
            if (depth >= dimensions.Length) return null;
            if (depth == dimensions.Length - 1)
            {
                return ReadArray(reader, storedType, outType, dimensions[depth]);
            }

            Array array = NewArray(outType, dimensions[depth]);

            for (int i = 0; i < dimensions[depth]; i++)
            {
                array.SetValue(UnpackInternal(outType.GetElementType(), storedType, reader, depth + 1, dimensions), i);
            }

            return array;
        }

        private static readonly Type[] TypeLookup =
        {
            typeof(byte),
            typeof(sbyte),
            typeof(char),
            typeof(short),
            typeof(ushort),
            typeof(int),
            typeof(uint),
            typeof(long),
            typeof(ulong),
            typeof(float),
            typeof(double)
        };

        // VitaSharp: Native AOT. The tables only hold (jagged arrays of) the primitive types above, so the array types
        // are enumerated statically instead of Array.CreateInstance / MakeArrayType / Marshal.SizeOf on runtime types.
        private static readonly System.Collections.Generic.Dictionary<(Type, int), Type> Jagged = new(); // before Factories: filled by BuildFactories
        private static readonly System.Collections.Generic.Dictionary<Type, Func<int, Array>> Factories = BuildFactories();

        private static void Add<T>(System.Collections.Generic.Dictionary<Type, Func<int, Array>> f)
        {
            f[typeof(T)] = n => new T[n];
            f[typeof(T[])] = n => new T[n][];
            f[typeof(T[][])] = n => new T[n][][];
            f[typeof(T[][][])] = n => new T[n][][][];
            Jagged[(typeof(T), 0)] = typeof(T);
            Jagged[(typeof(T), 1)] = typeof(T[]);
            Jagged[(typeof(T), 2)] = typeof(T[][]);
            Jagged[(typeof(T), 3)] = typeof(T[][][]);
        }

        private static System.Collections.Generic.Dictionary<Type, Func<int, Array>> BuildFactories()
        {
            var f = new System.Collections.Generic.Dictionary<Type, Func<int, Array>>();
            Add<byte>(f); Add<sbyte>(f); Add<char>(f); Add<short>(f); Add<ushort>(f); Add<int>(f);
            Add<uint>(f); Add<long>(f); Add<ulong>(f); Add<float>(f); Add<double>(f);
            return f;
        }

        private static Array NewArray(Type elementType, int length) =>
            Factories.TryGetValue(elementType, out var make) ? make(length) : throw new InvalidDataException($"unsupported table type {elementType}");

        private static Type JaggedType(Type elementType, int rank) =>
            Jagged.TryGetValue((elementType, rank), out var t) ? t : throw new InvalidDataException($"unsupported table rank {rank}");

        /// <summary>Marshal.SizeOf of the stored primitive (char marshals as one byte).</summary>
        private static int SizeOf(Type t) =>
            t == typeof(byte) || t == typeof(sbyte) || t == typeof(char) ? 1
            : t == typeof(short) || t == typeof(ushort) ? 2
            : t == typeof(long) || t == typeof(ulong) || t == typeof(double) ? 8
            : 4;
    }
}
