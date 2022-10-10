using System;
using System.Threading.Tasks;

namespace StackRedis.L1.MemoryCache
{
    internal struct ValOrRefNullable<T>
    {
        public readonly bool HasValue;
        public readonly T Value;

        public ValOrRefNullable()
        {
            HasValue = false;
            Value = default;
        }

        public ValOrRefNullable(T value)
        {
            HasValue = true;
            Value = value;
        }
    }
}
