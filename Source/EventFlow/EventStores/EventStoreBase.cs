// The MIT License (MIT)
// 
// Copyright (c) 2015-2024 Rasmus Mikkelsen
// https://github.com/eventflow/EventFlow
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
// the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
// FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
// IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
// CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventFlow.Aggregates;
using EventFlow.Core;
using EventFlow.Extensions;
using EventFlow.Snapshots;
using Microsoft.Extensions.Logging;

namespace EventFlow.EventStores
{
    public class EventStoreBase : IEventStore
    {
        private readonly IAggregateFactory _aggregateFactory;
        private readonly IEventJsonSerializer _eventJsonSerializer;
        private readonly ILogger<EventStoreBase> _logger;
        private readonly IEventPersistence _eventPersistence;
        private readonly ISnapshotStore _snapshotStore;
        private readonly IEventUpgradeManager _eventUpgradeManager;
        private readonly IReadOnlyCollection<IMetadataProvider> _metadataProviders;

        public EventStoreBase(
            ILogger<EventStoreBase> logger,
            IAggregateFactory aggregateFactory,
            IEventJsonSerializer eventJsonSerializer,
            IEventUpgradeManager eventUpgradeManager,
            IEnumerable<IMetadataProvider> metadataProviders,
            IEventPersistence eventPersistence,
            ISnapshotStore snapshotStore)
        {
            _logger = logger;
            _eventPersistence = eventPersistence;
            _snapshotStore = snapshotStore;
            _aggregateFactory = aggregateFactory;
            _eventJsonSerializer = eventJsonSerializer;
            _eventUpgradeManager = eventUpgradeManager;
            _metadataProviders = metadataProviders.ToList();
        }

        public virtual async Task<IReadOnlyCollection<IDomainEvent<TAggregate, TIdentity>>> StoreAsync<TAggregate, TIdentity>(
            TIdentity id,
            IReadOnlyCollection<IUncommittedEvent> uncommittedDomainEvents,
            ISourceId sourceId,
            CancellationToken cancellationToken)
            where TAggregate : IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (sourceId.IsNone()) throw new ArgumentNullException(nameof(sourceId));

            if (uncommittedDomainEvents == null || !uncommittedDomainEvents.Any())
            {
                return new IDomainEvent<TAggregate, TIdentity>[] {};
            }

            var aggregateType = typeof(TAggregate);
            _logger.LogTrace(
                "Storing {UncommittedDomainEventsCount} events for aggregate {AggregateType} with ID {Id}",
                uncommittedDomainEvents.Count,
                aggregateType.PrettyPrint(),
                id);

            var batchId = Guid.NewGuid().ToString();
            var storeMetadata = new[]
                {
                    new KeyValuePair<string, string>(MetadataKeys.BatchId, batchId),
                    new KeyValuePair<string, string>(MetadataKeys.SourceId, sourceId.Value)
                };

            var serializedEvents = uncommittedDomainEvents
                .Select(e =>
                    {
                        var md = _metadataProviders
                            .SelectMany(p => p.ProvideMetadata<TAggregate, TIdentity>(id, e.AggregateEvent, e.Metadata))
                            .Concat(e.Metadata)
                            .Concat(storeMetadata);
                        return _eventJsonSerializer.Serialize(e.AggregateEvent, md);
                    })
                .ToList();

            var committedDomainEvents = await _eventPersistence.CommitEventsAsync(
                id,
                serializedEvents,
                cancellationToken)
                .ConfigureAwait(false);

            var domainEvents = committedDomainEvents
                .Select(e => _eventJsonSerializer.Deserialize<TAggregate, TIdentity>(id, e))
                .ToList();

            return domainEvents;
        }

        public async Task<AllEventsPage> LoadAllEventsAsync(
            GlobalPosition globalPosition,
            int pageSize,
            IEventUpgradeContext eventUpgradeContext,
            CancellationToken cancellationToken)
        {
            if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));

            var allCommittedEventsPage = await _eventPersistence.LoadAllCommittedEvents(
                globalPosition,
                pageSize,
                cancellationToken)
                .ConfigureAwait(false);
            var domainEvents = (IReadOnlyCollection<IDomainEvent>) allCommittedEventsPage.CommittedDomainEvents
                .Select(e => _eventJsonSerializer.Deserialize(e))
                .ToList();

            // TODO: Pass a real IAsyncEnumerable instead
            domainEvents = await _eventUpgradeManager.UpgradeAsync(
                domainEvents.ToAsyncEnumerable(),
                eventUpgradeContext,
                cancellationToken).ToArrayAsync(cancellationToken);

            return new AllEventsPage(allCommittedEventsPage.NextGlobalPosition, domainEvents);
        }

        public Task<IReadOnlyCollection<IDomainEvent<TAggregate, TIdentity>>> LoadEventsAsync<TAggregate, TIdentity>(
            TIdentity id,
            CancellationToken cancellationToken)
            where TAggregate : IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
        {
            return LoadEventsAsync<TAggregate, TIdentity>(
                id,
                1,
                cancellationToken);
        }

        public async Task<IReadOnlyCollection<IDomainEvent<TAggregate, TIdentity>>> LoadEventsAsync<TAggregate, TIdentity>(
            TIdentity id,
            int fromEventSequenceNumber,
            int toEventSequenceNumber,
            CancellationToken cancellationToken) where TAggregate : IAggregateRoot<TIdentity> where TIdentity : IIdentity
        {
            if (fromEventSequenceNumber < 1) throw new ArgumentOutOfRangeException(nameof(fromEventSequenceNumber), "Event sequence numbers start at 1");
            if (toEventSequenceNumber <= fromEventSequenceNumber) throw new ArgumentOutOfRangeException(nameof(toEventSequenceNumber), "Event sequence numbers end at start");
            
            var committedDomainEvents = await _eventPersistence.LoadCommittedEventsAsync(
                    id,
                    fromEventSequenceNumber,
                    toEventSequenceNumber,
                    cancellationToken)
                .ConfigureAwait(false);
            
            return await MapToDomainEvents<TAggregate, TIdentity>(id, cancellationToken, committedDomainEvents);
        }

        public virtual async Task<IReadOnlyCollection<IDomainEvent<TAggregate, TIdentity>>> LoadEventsAsync<TAggregate, TIdentity>(
            TIdentity id,
            int fromEventSequenceNumber,
            CancellationToken cancellationToken)
            where TAggregate : IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
        {
            if (fromEventSequenceNumber < 1) throw new ArgumentOutOfRangeException(nameof(fromEventSequenceNumber), "Event sequence numbers start at 1");

            var committedDomainEvents = await _eventPersistence.LoadCommittedEventsAsync(
                    id,
                    fromEventSequenceNumber,
                    cancellationToken)
                .ConfigureAwait(false);
            
            return await MapToDomainEvents<TAggregate, TIdentity>(id, cancellationToken, committedDomainEvents);
        }
        
        private async Task<IReadOnlyCollection<IDomainEvent<TAggregate, TIdentity>>> MapToDomainEvents<TAggregate, TIdentity>(TIdentity id, CancellationToken cancellationToken,
            IReadOnlyCollection<ICommittedDomainEvent> committedDomainEvents) where TAggregate : IAggregateRoot<TIdentity> where TIdentity : IIdentity
        {
            var domainEvents = (IReadOnlyCollection<IDomainEvent<TAggregate, TIdentity>>)committedDomainEvents
                .Select(e => _eventJsonSerializer.Deserialize<TAggregate, TIdentity>(id, e))
                .ToList();

            if (!domainEvents.Any())
            {
                return domainEvents;
            }

            // TODO: Pass a real IAsyncEnumerable instead
            domainEvents = await _eventUpgradeManager.UpgradeAsync(
                domainEvents.ToAsyncEnumerable(),
                cancellationToken).ToArrayAsync(cancellationToken);

            return domainEvents;
        }

        public virtual async Task<TAggregate> LoadAggregateAsync<TAggregate, TIdentity>(
            TIdentity id,
            CancellationToken cancellationToken)
            where TAggregate : IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
        {
            var aggregate = await _aggregateFactory.CreateNewAggregateAsync<TAggregate, TIdentity>(id).ConfigureAwait(false);
            await aggregate.LoadAsync(this, _snapshotStore, cancellationToken).ConfigureAwait(false);
            return aggregate;
        }

        public Task DeleteAggregateAsync<TAggregate, TIdentity>(
            TIdentity id,
            CancellationToken cancellationToken)
            where TAggregate : IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
        {
            return _eventPersistence.DeleteEventsAsync(
                id,
                cancellationToken);
        }
    }
}