﻿using System;
using System.Collections.Generic;
using Common.Logging;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace LaunchDarkly.Client.Redis
{
    internal sealed class RedisFeatureStore : IFeatureStore
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(RedisFeatureStore));
        private static readonly string InitKey = "$initialized$";
        private static readonly string CacheName = typeof(RedisFeatureStore).AssemblyQualifiedName;

        private readonly ConnectionMultiplexer _redis;
        private readonly TimeSpan _cacheExpiration;
        private readonly string _prefix;

        private readonly InMemoryCache _cache;
        private readonly InMemoryCache _initCache;

        // This event handler is used for unit testing only
        public event EventHandler WillUpdate;

        internal RedisFeatureStore(ConfigurationOptions redisConfig, string prefix, TimeSpan cacheExpiration)
        {
            Log.InfoFormat("Creating Redis feature store using Redis server(s) at [{0}]",
                String.Join(", ", redisConfig.EndPoints));
            _redis = ConnectionMultiplexer.Connect(redisConfig);

            _prefix = prefix;

            _cacheExpiration = cacheExpiration;
            if (_cacheExpiration.TotalMilliseconds > 0)
            {
                _cache = new InMemoryCache(typeof(RedisFeatureStore).AssemblyQualifiedName,
                    _cacheExpiration);
            }
            else
            {
                _cache = null;
            }

            _initCache = new InMemoryCache(typeof(RedisFeatureStore).AssemblyQualifiedName + "-init", null);
        }
        
        /// <summary>
        /// <see cref="IFeatureStore.Initialized"/>
        /// </summary>
        public bool Initialized()
        {
            return _initCache.GetOrAdd(InitKey, GetInitedState);
        }

        /// <summary>
        /// <see cref="IFeatureStore.Init"/>
        /// </summary>
        public void Init(IDictionary<IVersionedDataKind, IDictionary<string, IVersionedData>> items)
        {
            IDatabase db = _redis.GetDatabase();
            ITransaction txn = db.CreateTransaction();
            foreach (KeyValuePair<IVersionedDataKind, IDictionary<string, IVersionedData>> collection in items)
            {
                string key = ItemsKey(collection.Key);
                txn.KeyDeleteAsync(key);
                foreach (KeyValuePair<string, IVersionedData> item in collection.Value)
                {
                    txn.HashSetAsync(key, item.Key, JsonConvert.SerializeObject(item.Value));
                    // Note, these methods are async because this Redis client treats all actions
                    // in a transaction as async - they are only sent to Redis when we execute the
                    // transaction. We don't need to await them.
                }
            }
            txn.StringSetAsync(_prefix, "");
            txn.Execute();
            _initCache.Set(InitKey, true);
            if (_cache != null)
            {
                foreach (KeyValuePair<IVersionedDataKind, IDictionary<string, IVersionedData>> collection in items)
                {
                    foreach (KeyValuePair<string, IVersionedData> item in collection.Value)
                    {
                        _cache.Set(CacheKey(collection.Key, item.Key), item.Value);
                    }
                }
            }
        }

        /// <summary>
        /// <see cref="IFeatureStore.Get"/>
        /// </summary>
        public T Get<T>(VersionedDataKind<T> kind, String key) where T : class, IVersionedData
        {
            T item;
            if (_cache != null)
            {
                item = _cache.GetOrAdd(CacheKey(kind, key), () =>
                {
                    T result;
                    TryGetFromRedis(_redis.GetDatabase(), kind, key, out result);
                    return result;
                });
            }
            else
            {
                TryGetFromRedis(_redis.GetDatabase(), kind, key, out item);
            }
            if (item != null && item.Deleted)
            {
                return null;
            }
            return item;
        }

        /// <summary>
        /// <see cref="IFeatureStore.All"/>
        /// </summary>
        public IDictionary<string, T> All<T>(VersionedDataKind<T> kind) where T : class, IVersionedData
        {
            IDatabase db = _redis.GetDatabase();
            HashEntry[] allEntries = db.HashGetAll(ItemsKey(kind));
            Dictionary<string, T> result = new Dictionary<string, T>();
            foreach (HashEntry entry in allEntries)
            {
                T item = JsonConvert.DeserializeObject<T>(entry.Value);
                if (!item.Deleted)
                {
                    result[item.Key] = item;
                }
            }
            return result;
        }

        /// <summary>
        /// <see cref="IFeatureStore.Upsert"/>
        /// </summary>
        public void Upsert<T>(VersionedDataKind<T> kind, T item) where T : IVersionedData
        {
            UpdateItemWithVersioning(kind, item);
        }

        /// <summary>
        /// <see cref="IFeatureStore.Delete"/>
        /// </summary>
        public void Delete<T>(VersionedDataKind<T> kind, string key, int version) where T : IVersionedData
        {
            T deletedItem = kind.MakeDeletedItem(key, version);
            UpdateItemWithVersioning(kind, deletedItem);
        }
        
        private void UpdateItemWithVersioning<T>(VersionedDataKind<T> kind, T newItem) where T : IVersionedData
        {
            IDatabase db = _redis.GetDatabase();
            string baseKey = ItemsKey(kind);
            while (true)
            {
                string oldJson = db.HashGet(baseKey, newItem.Key);
                T oldItem = (oldJson == null) ? default(T) : JsonConvert.DeserializeObject<T>(oldJson);
                int oldVersion = (oldJson == null) ? -1 : oldItem.Version;
                if (oldVersion >= newItem.Version)
                {
                    Log.DebugFormat("Attempted to {0} key: {1} version: {2} with a version that is" +
                        " the same or older: {3} in \"{4}\"",
                        newItem.Deleted ? "delete" : "update",
                        newItem.Key, oldVersion, newItem.Version, kind.GetNamespace());
                    if (_cache != null)
                    {
                        _cache.Set(CacheKey(kind, newItem.Key), oldItem);
                    }
                    return;
                }

                // This hook is used only in unit tests
                WillUpdate?.Invoke(null, null);

                // Note that transactions work a bit differently in StackExchange.Redis than in other
                // Redis clients. The same Redis connection is shared across all threads, so it can't
                // set a WATCH at the moment we start the transaction. Instead, it saves up all of
                // the actions we send during the transaction, and replays them all within a MULTI
                // when the transaction. AddCondition() is this client's way of doing a WATCH, and it
                // can only refer to the whole value, not to a JSON property of the value; that's why
                // we kept track of the whole value in "oldJson".
                ITransaction txn = db.CreateTransaction();
                txn.AddCondition(oldJson == null ? Condition.HashNotExists(baseKey, newItem.Key) :
                    Condition.HashEqual(baseKey, newItem.Key, oldJson));

                txn.HashSetAsync(baseKey, newItem.Key, JsonConvert.SerializeObject(newItem));

                bool success = txn.Execute();
                if (!success)
                {
                    // The watch was triggered, we should retry
                    Log.Debug("Concurrent modification detected, retrying");
                    continue;
                }
                
                if (_cache != null)
                {
                    _cache.Set(CacheKey(kind, newItem.Key), newItem);
                }
                return;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                _redis.Dispose();
            }
        }
        
        private bool TryGetFromRedis<T>(IDatabase db, VersionedDataKind<T> kind, string key, out T result) where T : IVersionedData
        {
            string json = db.HashGet(ItemsKey(kind), key);
            if (json == null)
            {
                Log.DebugFormat("[get] Key: {0} not found in \"{1}\"", key, kind.GetNamespace());
                result = default(T);
                return false;
            }
            result = JsonConvert.DeserializeObject<T>(json);
            return true;
        }

        private string ItemsKey(IVersionedDataKind kind)
        {
            return _prefix + ":" + kind.GetNamespace();
        }

        private string CacheKey(IVersionedDataKind kind, string key)
        {
            return kind.GetNamespace() + ":" + key;
        }

        private bool GetInitedState()
        {
            IDatabase db = _redis.GetDatabase();
            return db.KeyExists(_prefix);
        }
    }
}