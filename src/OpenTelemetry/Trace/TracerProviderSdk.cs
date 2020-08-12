﻿// <copyright file="TracerProviderSdk.cs" company="OpenTelemetry Authors">
// Copyright The OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using OpenTelemetry.Resources;

namespace OpenTelemetry.Trace
{
    internal class TracerProviderSdk : TracerProvider
    {
        private readonly List<object> instrumentations;
        private readonly ActivityListener listener;
        private readonly Resource resource;
        private readonly Sampler sampler;
        private ActivityProcessor processor;

        static TracerProviderSdk()
        {
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;
            Activity.ForceDefaultIdFormat = true;
        }

        internal TracerProviderSdk(
            IEnumerable<string> sources,
            List<ActivityProcessor> processors,
            IEnumerable<TracerProviderBuilder.InstrumentationFactory> instrumentationFactories = null,
            Sampler sampler = null,
            Resource resource = null)
        {
            foreach (var processor in processors)
            {
                this.AddProcessor(processor);
            }

            this.sampler = sampler;

            this.resource = resource;

            if (instrumentationFactories != null)
            {
                // TODO: check if individual element is null
                this.instrumentations = new List<object>();
                var adapter = new ActivitySourceAdapter(sampler, this.processor, resource);
                foreach (var instrumentationFactory in instrumentationFactories)
                {
                    this.instrumentations.Add(instrumentationFactory.Factory(adapter));
                }
            }

            var listener = new ActivityListener
            {
                // Callback when Activity is started.
                ActivityStarted = (activity) =>
                {
                    if (activity.IsAllDataRequested)
                    {
                        activity.SetResource(this.resource);
                        this.processor?.OnStart(activity);
                    }
                },

                // Callback when Activity is stopped.
                ActivityStopped = (activity) =>
                {
                    if (activity.IsAllDataRequested)
                    {
                        this.processor?.OnEnd(activity);
                    }
                },

                // Setting this to true means TraceId will be always
                // available in sampling callbacks and will be the actual
                // traceid used, if activity ends up getting created.
                AutoGenerateRootContextTraceId = true,

                // This delegate informs ActivitySource about sampling decision when the parent context is an ActivityContext.
                GetRequestedDataUsingContext = (ref ActivityCreationOptions<ActivityContext> options) => ComputeActivityDataRequest(options, sampler),
            };

            if (sources != null & sources.Any())
            {
                // Sources can be null. This happens when user
                // is only interested in InstrumentationLibraries
                // which do not depend on ActivitySources.

                var wildcardMode = false;

                // Validation of source name is already done in builder.
                foreach (var name in sources)
                {
                    if (name.Contains('*'))
                    {
                        wildcardMode = true;
                    }
                }

                if (wildcardMode)
                {
                    var pattern = "^(" + string.Join("|", from name in sources select '(' + Regex.Escape(name).Replace("\\*", ".*") + ')') + ")$";
                    var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

                    // Function which takes ActivitySource and returns true/false to indicate if it should be subscribed to
                    // or not.
                    listener.ShouldListenTo = (activitySource) => regex.IsMatch(activitySource.Name);
                }
                else
                {
                    var activitySources = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

                    foreach (var name in sources)
                    {
                        activitySources[name] = true;
                    }

                    // Function which takes ActivitySource and returns true/false to indicate if it should be subscribed to
                    // or not.
                    listener.ShouldListenTo = (activitySource) => activitySources.ContainsKey(activitySource.Name);
                }
            }

            ActivitySource.AddActivityListener(listener);
            this.listener = listener;
        }

        internal TracerProviderSdk AddProcessor(ActivityProcessor processor)
        {
            if (processor == null)
            {
                throw new ArgumentNullException(nameof(processor));
            }

            if (this.processor == null)
            {
                this.processor = processor;
            }
            else if (this.processor is CompositeActivityProcessor compositeProcessor)
            {
                compositeProcessor.AddProcessor(processor);
            }
            else
            {
                this.processor = new CompositeActivityProcessor(new[]
                {
                    this.processor,
                    processor,
                });
            }

            return this;
        }

        protected override void Dispose(bool disposing)
        {
            if (this.instrumentations != null)
            {
                foreach (var item in this.instrumentations)
                {
                    (item as IDisposable)?.Dispose();
                }

                this.instrumentations.Clear();
            }

            (this.sampler as IDisposable)?.Dispose();
            this.processor?.Dispose();

            // Shutdown the listener last so that anything created while instrumentation cleans up will still be processed.
            // Redis instrumentation, for example, flushes during dispose which creates Activity objects for any profiling
            // sessions that were open.
            this.listener?.Dispose();

            base.Dispose(disposing);
        }

        private static ActivityDataRequest ComputeActivityDataRequest(
            in ActivityCreationOptions<ActivityContext> options,
            Sampler sampler)
        {
            var isRootSpan = /*TODO: Put back once AutoGenerateRootContextTraceId is removed.
                              options.Parent.TraceId == default ||*/ options.Parent.SpanId == default;

            if (sampler != null)
            {
                // As we set ActivityListener.AutoGenerateRootContextTraceId = true,
                // Parent.TraceId will always be the TraceId of the to-be-created Activity,
                // if it get created.
                ActivityTraceId traceId = options.Parent.TraceId;

                var samplingParameters = new SamplingParameters(
                    options.Parent,
                    traceId,
                    options.Name,
                    options.Kind,
                    options.Tags,
                    options.Links);

                var shouldSample = sampler.ShouldSample(samplingParameters);

                var activityDataRequest = shouldSample.Decision switch
                {
                    SamplingDecision.RecordAndSampled => ActivityDataRequest.AllDataAndRecorded,
                    SamplingDecision.Record => ActivityDataRequest.AllData,
                    _ => ActivityDataRequest.PropagationData
                };

                if (activityDataRequest != ActivityDataRequest.PropagationData)
                {
                    return activityDataRequest;
                }
            }

            // If it is the root span select PropagationData so the trace ID is preserved
            // even if no activity of the trace is recorded (sampled per OpenTelemetry parlance).
            return isRootSpan
                ? ActivityDataRequest.PropagationData
                : ActivityDataRequest.None;
        }
    }
}