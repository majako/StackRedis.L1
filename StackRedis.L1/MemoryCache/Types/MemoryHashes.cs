using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StackRedis.L1.MemoryCache.Types
{
    internal class MemoryHashes
    {
        private ObjMemCache _objMemCache;
        internal MemoryHashes(ObjMemCache objMemCache)
        {
            _objMemCache = objMemCache;
        }

        /// <summary>
        /// Returns all items of the hash, storing them in memory.
        /// TODO: cache a flag that indicates that all have been retrieved. That is difficult because there are lots of places to remove that flag (hashset, hashdelete, etc...)
        /// </summary>
        internal HashEntry[] GetAll(string hashKey, Func<HashEntry[]> retrieval)
        {
            //At the moment, we always go to redis, but store the contents in the cache for later retrieval by HashGet.
            var hashEntries = retrieval();

            //Empty existing keys
            GetHash(hashKey)?.Clear();

            //Save all the new values
            Set(hashKey, hashEntries);

            return hashEntries;
        }

        internal async Task<HashEntry[]> GetAllAsync(string hashKey, Func<Task<HashEntry[]>> retrieval)
        {
            //At the moment, we always go to redis, but store the contents in the cache for later retrieval by HashGet.
            var hashEntries = await retrieval().ConfigureAwait(false);

            //Empty existing keys
            var hash = GetHash(hashKey);
            hash?.Clear();

            //Save all the new values
            Set(hashKey, hashEntries);

            return hashEntries;
        }

        internal RedisValue Get(string hashKey, RedisValue key, Func<RedisValue, RedisValue> retrieval)
        {
            return GetMulti(hashKey, new[] { key }, keys =>
            {
                var result = retrieval(key);
                return new[] { result };
            }).Single();
        }

        internal async Task<RedisValue> GetAsync(string hashKey, RedisValue key, Func<RedisValue, Task<RedisValue>> retrieval)
        {
            return (await GetMultiAsync(hashKey, new[] { key }, async keys =>
            {
                var result = await retrieval(key).ConfigureAwait(false);
                return new[] { result };
            })).Single();
        }

        internal RedisValue[] GetMulti(string hashKey, RedisValue[] keys, Func<RedisValue[], RedisValue[]> retrieval)
        {
            if (!keys.Any()) return new RedisValue[0];

            //Get the in-memory hash
            var hash = GetHash(hashKey) ?? SetHash(hashKey);

            var result = new RedisValue[keys.Length];
            var nonCachedIndices = new List<int>();

            for (var i = 0; i < keys.Length; i++)
            {
                if (hash.ContainsKey(keys[i]))
                    result[i] = hash[keys[i]];
                else
                    nonCachedIndices.Add(i);
            }

            //Get all non cached indices from redis and place them in their correct positions for the result array
            if (!nonCachedIndices.Any()) return result;

            var nonCachedKeys = keys.Where((key, index) => nonCachedIndices.Contains(index)).ToArray();
            var redisResults = retrieval(nonCachedKeys);
            if (redisResults == null) return result;

            var j = 0;
            foreach (var redisResult in redisResults)
            {
                var originalIndex = nonCachedIndices[j++];
                result[originalIndex] = redisResult;

                //Cache this key for next time
                hash.TryAdd(keys[originalIndex], redisResult);
            }

            return result;
        }

        internal async Task<RedisValue[]> GetMultiAsync(string hashKey, RedisValue[] keys, Func<RedisValue[], Task<RedisValue[]>> retrieval)
        {
            if (!keys.Any()) return new RedisValue[0];

            //Get the in-memory hash
            var hash = GetHash(hashKey) ?? SetHash(hashKey);

            var result = new RedisValue[keys.Length];
            var nonCachedIndices = new List<int>();

            for (var i = 0; i < keys.Length; i++)
            {
                if (hash.TryGetValue(keys[i], out var value))
                    result[i] = value;
                else
                    nonCachedIndices.Add(i);
            }

            //Get all non cached indices from redis and place them in their correct positions for the result array
            if (!nonCachedIndices.Any()) return result;

            var nonCachedKeys = keys.Where((key, index) => nonCachedIndices.Contains(index)).ToArray();
            var redisResults = await retrieval(nonCachedKeys).ConfigureAwait(false);
            if (redisResults == null) return result;

            var j = 0;
            foreach (var redisResult in redisResults)
            {
                var originalIndex = nonCachedIndices[j++];
                result[originalIndex] = redisResult;

                //Cache this key for next time
                hash[keys[originalIndex]] = redisResult;  // MAJAKO_CHANGE use indexing instead of Add
            }

            return result;
        }

        internal bool Contains(string hashKey, string key)
        {
            return GetHash(hashKey)?.ContainsKey(key) == true;
        }

        internal RedisValue Get(string hashKey, string key)
        {
            return GetHash(hashKey)?.TryGetValue(key, out var value) == true
                ? value
                : new RedisValue();
        }
        
        internal long Set(string hashKey, HashEntry[] hashEntries, When when = When.Always)
        {
            long result = 0;
            var hash = GetHash(hashKey);
            if (hash == null)
                hash = SetHash(hashKey);
            
            foreach (var entry in hashEntries)
            {
                switch (when)
                {
                    case When.Always:
                        hash[entry.Name] = entry.Value;
                        result++;
                        break;
                    case When.Exists:
                        if (hash.ContainsKey(entry.Name))
                        {
                            hash[entry.Name] = entry.Value;
                            result++;
                        }
                        break;
                    case When.NotExists:
                        hash.TryAdd(entry.Name, entry.Value);
                        result++;
                        break;
                    default:
                        break;
                }
            }

            return result;
        }

        internal long Delete(string hashKey, RedisValue[] keys)
        {
            long result = 0;
            var hash = GetHash(hashKey);
            if (hash != null)
            {
                foreach (var key in keys)
                {
                    if (hash.TryRemove(key, out _))
                        result++;
                }
            }
            return result;
        }

        private ConcurrentDictionary<string,RedisValue> SetHash(string hashKey)
        {
            var hash = new ConcurrentDictionary<string, RedisValue>();
            _objMemCache.Add(hashKey, hash, null, When.Always);
            return hash;
        }

        private ConcurrentDictionary<string,RedisValue> GetHash(string hashKey)
        {
            var result = _objMemCache.Get<ConcurrentDictionary<string, RedisValue>>(hashKey);
            if(result.HasValue)
            {
                return result.Value;
            }
            else
            {
                return null;
            }
        }
    }
}
