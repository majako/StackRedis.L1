using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Caching.Memory;
using System.Threading;
using StackExchange.Redis;

namespace StackRedis.L1.MemoryCache
{
    internal sealed class ObjMemCache : IDisposable
    {
        private struct Entry<T>
        {
            public T Value;
            public DateTimeOffset? AbsoluteExpiration;

            public Entry(T value, DateTimeOffset? absoluteExpiration = null)
            {
                Value = value;
                AbsoluteExpiration = absoluteExpiration;
            }
        }

        //object storage
        private IMemoryCache _cache;

        internal ObjMemCache()
        {
            Flush();
        }

        public bool ContainsKey(string key)
        {
            return _cache.TryGetValue(key, out _);
        }

        public void Update<T>(string key, T o)
        {
            if (!ContainsKey(key)) return;
            var expiry = GetExpiry(key);
            var expiryTimespan = expiry.HasValue ? expiry.Value : null;
            Add(key, o, expiryTimespan, When.Always);
        }

        public void Add<T>(string key, T value, TimeSpan? expiry, When when)
        {
            if (_cache == null) return;

            if (when == When.Exists && !ContainsKey(key))
                return;
            if (when == When.NotExists && ContainsKey(key))
                return;

            DateTimeOffset? absoluteExpiration = expiry.HasValue ? DateTime.UtcNow.Add(expiry.Value) : null;
            _cache.Set(key, new Entry<T>(value, absoluteExpiration), new MemoryCacheEntryOptions { AbsoluteExpiration = absoluteExpiration});
        }

        public void Add(IEnumerable<KeyValuePair<RedisKey, RedisValue>> items, TimeSpan? expiry, When when)
        {
            foreach (var item in items)
                Add(item.Key, item.Value, expiry, when);
        }

        public ValOrRefNullable<T> Get<T>(string key)
        {
            return _cache.TryGetValue(key, out Entry<T> result)
                ? new ValOrRefNullable<T>(result.Value)
                : new ValOrRefNullable<T>();
        }

        public ValOrRefNullable<TimeSpan?> GetExpiry(string key)
        {
            return _cache.TryGetValue(key, out Entry<object> entry)
                ? new ValOrRefNullable<TimeSpan?>(entry.AbsoluteExpiration?.Subtract(DateTime.UtcNow))
                : new ValOrRefNullable<TimeSpan?>();
        }

        public bool Expire(string key, DateTimeOffset? expiry)
        {
            TimeSpan? diff = null;

            if (expiry.HasValue && expiry != default(DateTimeOffset))
                diff = expiry.Value.Subtract(DateTime.UtcNow);

            return Expire(key, diff);
        }

        public bool Expire(string key, TimeSpan? expiry = null)
        {
            var o = Get<object>(key);
            if (o.HasValue)
            {
                if (expiry == null || expiry.Value.TotalMilliseconds > 0)
                    Add(key, o.Value, expiry, When.Always);
                else
                    Remove(new[] { key });

                return true;
            }
            else
            {
                return false;
            }
        }

        public bool RenameKey(string keyFrom, string keyTo)
        {
            if (keyFrom != keyTo && _cache.TryGetValue(keyFrom, out Entry<object> entry))
            {
                Add(keyTo, entry.Value, entry.AbsoluteExpiration?.Subtract(DateTime.UtcNow), When.Always);
                Remove(keyFrom);
                return true;
            }
            return false;
        }

        public long Remove(params string[] keys)
        {
            long count = 0;
            foreach (var key in keys)
            {
                if (!string.IsNullOrEmpty(key) && ContainsKey(key))
                {
                    _cache.Remove(key);
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Clears the cache.
        /// </summary>
        public void Flush()
        {
            _cache?.Dispose();
            _cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new MemoryCacheOptions());
        }

        public void Dispose()
        {
            _cache?.Dispose();
        }
    }
}
