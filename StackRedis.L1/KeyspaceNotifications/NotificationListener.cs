﻿using System;
using System.Linq;
using System.Threading.Tasks;
using StackExchange.Redis;
using StackRedis.L1.Notifications;

namespace StackRedis.L1.KeyspaceNotifications
{
    internal class NotificationListener : IDisposable
    {
        private static readonly string _keyspace = "__keyspace@{0}__:";
        private static readonly string _keyspaceDetail = "__keyspace_detailed@{0}__:";

        private DatabaseInstanceData _database;
        private readonly ISubscriber _subscriber;
        private readonly int _databaseId;

        internal bool Paused { get; set; }

        internal NotificationListener(IDatabase redisDb)
        {
            var connection = redisDb.Multiplexer;
            _subscriber = connection.GetSubscriber();

            _databaseId = redisDb.Database;

            //Listen for standard redis keyspace events
            _subscriber.Subscribe(string.Format(_keyspace, _databaseId) + "*", (channel, value) =>
            {
                if (Paused) return;

                var key = ((string)channel).Replace(string.Format(_keyspace, _databaseId), "");
                if (_database != null)
                {
                    HandleKeyspaceEvent(_database, key, value);
                }
            });

            //Listen for advanced keyspace events
            _subscriber.Subscribe(string.Format(_keyspaceDetail, _databaseId) + "*", (channel, value) =>
            {
                if (Paused) return;

                var machine = ((string)value).Split(':').First();

                //Only listen to events caused by other redis clients
                if (machine == ProcessId.GetCurrent()) return;

                var key = ((string)channel).Replace(string.Format(_keyspaceDetail, _databaseId), "");

                var eventType = ((string)value)[(machine.Length + 1)..];
                if (_database != null)
                {
                    HandleKeyspaceDetailEvent(_database, redisDb, key, machine, eventType);
                }
            });
        }

        public void Dispose()
        {
            _subscriber.Unsubscribe(string.Format(_keyspace, _databaseId) + "*");
            _subscriber.Unsubscribe(string.Format(_keyspaceDetail, _databaseId) + "*");
        }

        internal void HandleKeyspaceEvents(DatabaseInstanceData dbData)
        {
            _database = dbData;
        }

        private static void HandleKeyspaceDetailEvent(DatabaseInstanceData dbData, IDatabase redisDb, string key, string machine, string eventType)
        {
            // System.Diagnostics.Debug.WriteLine("Keyspace detail event. Key=" + key + ", Machine=" + machine + ", Event=" + eventType);

            var eventName = eventType.Split(':').First();
            var eventArg = "";
            if (eventName.Length < eventType.Length)
            {
                eventArg = eventType[(eventName.Length + 1)..];
            }

            if (eventName == "hset" || eventName == "hdel" ||
                eventName == "hincr" || eventName == "hincrbyfloat" ||
                eventName == "hdecr" || eventName == "hdecrbyfloat")
            {
                //eventArg is the hash entry name. Since it has changed, remove it.
                dbData.MemoryHashes.Delete(key, new[] { (RedisValue)eventArg });
            }
            else if (eventName == "srem")
            {
                //Removing an item from a set.
                dbData.MemorySets.RemoveByHashCode(key, new[] { eventArg });
            }
            else if (eventName == "zadd")
            {
                //An item is added to a sorted set. We should remove it from its current location if it's already there.
                if (int.TryParse(eventArg, out var hashCode))
                {
                    dbData.MemorySortedSets.RemoveByHashCode(key, hashCode);
                }
            }
            else if (eventName == "zrem" || eventName == "zincr" || eventName == "zdecr")
            {
                //An item is removed from a sorted set.
                if (int.TryParse(eventArg, out var hashCode))
                {
                    dbData.MemorySortedSets.RemoveByHashCode(key, hashCode);
                }
            }
            else if (eventName == "zremrangebyscore")
            {
                if (!string.IsNullOrEmpty(eventArg))
                {
                    var scores = eventArg.Split('-');
                    if (scores.Length == 3)
                    {
                        if (double.TryParse(scores[0], out var start) && double.TryParse(scores[1], out var stop) && int.TryParse(scores[2], out var exclude))
                        {
                            dbData.MemorySortedSets.DeleteByScore(key, start, stop, (Exclude)exclude);
                        }
                    }
                }
            }
            else if (eventName == "del")
            {
                //A key was removed
                dbData.MemoryCache.Remove(key);
            }
            else if (eventName == "getdel")
            {
                //A key was removed
                dbData.MemoryCache.Remove(key);
            }
            else if (eventName == "expire")
            {
                //The TTL has changed - clear it in memory
                dbData.MemoryCache.Expire(key);
            }
            else if (eventName == "rename_key")
            {
                //the arg contains the new key
                if (!string.IsNullOrEmpty(eventArg))
                {
                    dbData.MemoryCache.RenameKey(key, eventArg);
                }
            }
            else if (eventName == "set" /* Setting a string */)
            {
                //A key has been set by another client. If it exists in memory, it is probably now outdated.
                _ = UpdateAsync(dbData, redisDb, key);
            }
            else if (eventName == "setbit" || eventName == "setrange" ||
                    eventName == "incrby" || eventName == "incrbyfloat" ||
                    eventName == "decrby" || eventName == "decrbyfloat" ||
                    eventName == "append")
            {
                //Many string operations are not performed in-memory, so the key needs to be invalidated and we go back to redis for the result.
                dbData.MemoryCache.Remove(key);
            }
            else if (eventName == "zremrangebyrank" || eventName == "zremrangebylex")
            {
                //Many sorted set operations are not performed in-memory, so the key needs to be invalidated and we go back to redis for the result.
                dbData.MemoryCache.Remove(key);
            }
        }

        /// <summary>
        /// Update the memory cache with the new value if found, otherwise remove the key
        /// </summary>
        private static async Task UpdateAsync(DatabaseInstanceData dbData, IDatabase redisDb, string key)
        {
            try
            {
                var value = await redisDb.StringGetWithExpiryAsync(key);
                if (value.Value.HasValue)
                    dbData.MemoryCache.Add(key, value.Value, value.Expiry, When.Always);
                else
                    dbData.MemoryCache.Remove(key);
            }
            catch
            {
                dbData.MemoryCache.Remove(key);
            }
        }

        /// <summary>
        /// Reads the key/value and updates the database with the relevant value
        /// </summary>
        private static void HandleKeyspaceEvent(DatabaseInstanceData dbData, string key, string value)
        {
            // System.Diagnostics.Debug.WriteLine("Keyspace event. Key=" + key + ", Value=" + value);
            if (value == "expired")
            {
                //A key has expired. Sometimes the expiry is performed in-memory, so the key may have already been removed.
                //It's also possible that the expiry is performed in redis and not in memory, so we listen for this event.
                dbData.MemoryCache.Remove(key);
                // System.Diagnostics.Debug.WriteLine("Key expired and removed:" + key);
            }
        }
    }
}
