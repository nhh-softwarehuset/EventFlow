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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using EventFlow.Configuration;
using EventFlow.Extensions;
using Microsoft.Extensions.Logging;

namespace EventFlow.Sagas
{
    public class SagaDefinitionService : ISagaDefinitionService
    {
        private static readonly IReadOnlyCollection<SagaDetails> Empty = new SagaDetails[] { };
        private readonly ILogger<SagaDefinitionService> _logger;
        private readonly ConcurrentDictionary<Type, SagaDetails> _sagaDetails = new ConcurrentDictionary<Type, SagaDetails>();
        private readonly ConcurrentDictionary<Type, List<SagaDetails>> _sagaDetailsByAggregateEvent = new ConcurrentDictionary<Type, List<SagaDetails>>();

        public SagaDefinitionService(
            ILogger<SagaDefinitionService> logger,
            ILoadedVersionedTypes loadedVersionedTypes)
        {
            _logger = logger;
            LoadSagas(loadedVersionedTypes.Sagas);
        }

        public void LoadSagas(params Type[] sagaTypes)
        {
            LoadSagas((IEnumerable<Type>)sagaTypes);
        }

        public void LoadSagas(IEnumerable<Type> sagaTypes)
        {
            foreach (var sagaType in sagaTypes)
            {
                if (_sagaDetails.ContainsKey(sagaType))
                {
                    _logger.LogWarning(
                        "Saga type {SagaType} has already been added, skipping it this time",
                        sagaType.PrettyPrint());
                    continue;
                }

                var sagaDetails = SagaDetails.From(sagaType);
                _sagaDetails[sagaType] = sagaDetails;

                foreach (var aggregateEventType in sagaDetails.AggregateEventTypes)
                {
                    var sagaDetailsList = _sagaDetailsByAggregateEvent.GetOrAdd(
                        aggregateEventType,
                        new List<SagaDetails>());

                    sagaDetailsList.Add(sagaDetails);
                }
            }
        }

        public IReadOnlyCollection<SagaDetails> GetSagaDetails(Type aggregateEventType)
        {
            return _sagaDetailsByAggregateEvent.TryGetValue(aggregateEventType, out var sagaDetails)
                ? sagaDetails
                : Empty;
        }
    }
}
