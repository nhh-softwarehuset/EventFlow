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
using System.Reflection;
using EventFlow.Aggregates;
using EventFlow.Core;
using EventFlow.EventStores;
using Microsoft.Extensions.DependencyInjection;

namespace EventFlow.Extensions
{
    public static class EventFlowOptionsEventUpgradersExtensions
    {
        public static IEventFlowOptions AddEventUpgrader<TAggregate, TIdentity, TEventUpgrader>(
            this IEventFlowOptions eventFlowOptions)
            where TAggregate : IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
            where TEventUpgrader : class, IEventUpgrader<TAggregate, TIdentity>
        {
            eventFlowOptions.ServiceCollection
                .AddTransient<IEventUpgrader<TAggregate, TIdentity>, TEventUpgrader>();
            return eventFlowOptions;
        }

        public static IEventFlowOptions AddEventUpgrader<TAggregate, TIdentity>(
            this IEventFlowOptions eventFlowOptions,
            Func<IServiceProvider, IEventUpgrader<TAggregate, TIdentity>> factory)
            where TAggregate : IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
        {
            eventFlowOptions.ServiceCollection.AddTransient(factory);
            return eventFlowOptions;
        }

        public static IEventFlowOptions AddEventUpgraders(
            this IEventFlowOptions eventFlowOptions,
            Assembly fromAssembly,
            Predicate<Type> predicate = null)
        {
            predicate = predicate ?? (t => true);
            var eventUpgraderTypes = fromAssembly
                .GetTypes()
                .Where(t => t.GetTypeInfo().GetInterfaces().Any(IsEventUpgraderInterface))
                .Where(t => !t.HasConstructorParameterOfType(IsEventUpgraderInterface))
                .Where(t => predicate(t));
            return eventFlowOptions
                .AddEventUpgraders(eventUpgraderTypes);
        }

        public static IEventFlowOptions AddEventUpgraders(
            this IEventFlowOptions eventFlowOptions,
            params Type[] eventUpgraderTypes)
        {
            return eventFlowOptions
                .AddEventUpgraders((IEnumerable<Type>)eventUpgraderTypes);
        }

        public static IEventFlowOptions AddEventUpgraders(
            this IEventFlowOptions eventFlowOptions,
            IEnumerable<Type> eventUpgraderTypes)
        {
            foreach (var eventUpgraderType in eventUpgraderTypes)
            {
                var t = eventUpgraderType;
                if (t.GetTypeInfo().IsAbstract) continue;
                var eventUpgraderForAggregateType = t
                    .GetTypeInfo()
                    .GetInterfaces()
                    .SingleOrDefault(IsEventUpgraderInterface);
                if (eventUpgraderForAggregateType == null)
                {
                    throw new ArgumentException($"Type '{eventUpgraderType.Name}' does not have the '{typeof(IEventUpgrader<,>).PrettyPrint()}' interface");
                }

                eventFlowOptions.ServiceCollection.AddTransient(eventUpgraderForAggregateType, t);
            }

            return eventFlowOptions;
        }

        private static bool IsEventUpgraderInterface(Type type)
        {
            return type.GetTypeInfo().IsGenericType && type.GetGenericTypeDefinition() == typeof(IEventUpgrader<,>);
        }
    }
}
