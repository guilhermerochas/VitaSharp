#nullable disable
using System;

namespace VitaSharp.Core.Hle.Audio.Atrac9
{
    public static class Arrays
    {
        public static T[] Generate<T>(int count, Func<int, T> elementGenerator)
        {
            var table = new T[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = elementGenerator(i);
            }
            return table;
        }
    }
}
