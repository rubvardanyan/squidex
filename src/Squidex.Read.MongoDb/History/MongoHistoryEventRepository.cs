﻿// ==========================================================================
//  MongoHistoryEventRepository.cs
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex Group
//  All rights reserved.
// ==========================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using Squidex.Infrastructure.CQRS;
using Squidex.Infrastructure.CQRS.Events;
using Squidex.Infrastructure.CQRS.Replay;
using Squidex.Infrastructure.MongoDb;
using Squidex.Read.History;
using Squidex.Read.History.Repositories;
using Squidex.Read.MongoDb.Utils;

namespace Squidex.Read.MongoDb.History
{
    public class MongoHistoryEventRepository : MongoRepositoryBase<MongoHistoryEventEntity>, IHistoryEventRepository, ICatchEventConsumer, IReplayableStore
    {
        private readonly List<IHistoryEventsCreator> creators;
        private readonly Dictionary<string, string> texts = new Dictionary<string, string>();
        private int sessionEventCount;

        public MongoHistoryEventRepository(IMongoDatabase database, IEnumerable<IHistoryEventsCreator> creators) 
            : base(database)
        {
            this.creators = creators.ToList();

            foreach (var creator in this.creators)
            {
                foreach (var text in creator.Texts)
                {
                    texts[text.Key] = text.Value;
                }
            }
        }

        protected override string CollectionName()
        {
            return "Projections_History";
        }

        protected override Task SetupCollectionAsync(IMongoCollection<MongoHistoryEventEntity> collection)
        {
            return Task.WhenAll(
                collection.Indexes.CreateOneAsync(
                    IndexKeys
                        .Ascending(x => x.AppId)
                        .Ascending(x => x.Channel)
                        .Descending(x => x.Created)
                        .Descending(x => x.SessionEventIndex)),
                collection.Indexes.CreateOneAsync(IndexKeys.Ascending(x => x.Created), new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(365) }));
        }

        public Task ClearAsync()
        {
            return TryDropCollectionAsync();
        }

        public async Task<List<IHistoryEventEntity>> QueryEventsByChannel(Guid appId, string channelPrefix, int count)
        {
            var entities =
                await Collection.Find(x => x.AppId == appId && x.Channel == channelPrefix)
                    .SortByDescending(x => x.Created).ThenByDescending(x => x.SessionEventIndex).Limit(count).ToListAsync();

            return entities.Select(x => (IHistoryEventEntity)new ParsedHistoryEvent(x, texts)).ToList();
        }

        public async Task On(Envelope<IEvent> @event)
        {
            foreach (var creator in creators)
            {
                var message = await creator.CreateEventAsync(@event);

                if (message != null)
                {
                    await Collection.CreateAsync(@event.Headers, x =>
                    {
                        x.SessionEventIndex = Interlocked.Increment(ref sessionEventCount);

                        x.Channel = message.Channel;
                        x.Message = message.Message;

                        x.Parameters = message.Parameters.ToDictionary(p => p.Key, p => p.Value);
                    }, false);
                }
            }
        }
    }
}
