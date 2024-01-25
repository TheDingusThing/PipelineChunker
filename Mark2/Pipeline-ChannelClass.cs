﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using static Mark2.Pipeline;

namespace Mark2 {
    public partial class Pipeline {
        private abstract class ChannelAbstract {
            protected ChannelAbstract(Pipeline pipeline) { Pipeline = pipeline; }
            public Pipeline Pipeline { get; private set; }
        }
        private class ChannelClass<ConduitT> : ChannelAbstract, IChanel<ConduitT> where ConduitT : IConduit<ConduitT>, new() {
            // anonymous (or static) methods that do not capture values
            private static readonly Dictionary<MethodInfo, bool> verifiedNonCapturingMethods = new Dictionary<MethodInfo, bool>();
            private readonly Pipeline _pipeline;
            private int total = 0;
            private readonly ConduitWrapper[] chunk;
            private readonly Type conduitTYpe = typeof(ConduitT);
            private int channelingId;


            public string Name { get; private set; }

            public ChannelClass(Pipeline pipeline) : base(pipeline) {
                _pipeline = pipeline;
                chunk = new ConduitWrapper[_pipeline._maxChunkSize];
            }

            public ConduitT Chunk<StaticT, InT, OutT>(
                Func<IChanel<ConduitT>, StaticT> ChunkInitializer,
                Func<IChanel<ConduitT>, StaticT, InT> ConduitInitializer,
                Func<IChanel<ConduitT>, StaticT, IEnumerable<KeyValuePair<ConduitT, InT>>, IEnumerable<KeyValuePair<ConduitT, OutT>>> ChunkTransform,
                Action<IChanel<ConduitT>, StaticT, KeyValuePair<ConduitT, OutT>> ConduitOperation,
                string Name = null
            ) {
                //Check if ChunkInitializer and ChunkTransform is static (or if they are not capturing variables)
                //  because these methods will only be invoked on a per-chunk basis instead of for each conduit.
                if (!IsMethodNoCapturing(ChunkInitializer.Method)) {
                    throw new MethodIsCapturingException<ConduitT>("for the ChunkInitializer parameter", ChunkInitializer.Method);
                }
                if (!IsMethodNoCapturing(ChunkTransform.Method)) {
                    throw new MethodIsCapturingException<ConduitT>("for the ChunkTransform parameter", ChunkTransform.Method);
                }

                this.Name = Name ?? typeof(ConduitT).FullName;

                return chunk[channelingId].enumerator.Current;
                //throw new NotImplementedException();
            }

            bool IsMethodNoCapturing(MethodInfo info) {
                if (!verifiedNonCapturingMethods.TryGetValue(info, out bool value)) {
                    return verifiedNonCapturingMethods[info] = info.IsStatic
                                                               || info.DeclaringType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length == 0;
                }
                return verifiedNonCapturingMethods[info];
            }

            public void AddConduit(Action<ConduitT> channelInitializer, Action<ConduitT> channelFinalizer) {
                if (total >= Pipeline._maxChunkSize)
                    Flush();

                chunk[total] = new ConduitWrapper();

                ConduitT conduit = new ConduitT();
                conduit.Initialize(total, this, out var setException);
                if(conduit.Id != total) throw new ConduitInitializationException($"{conduitTYpe.FullName} must assign the id provided in {nameof(IConduit<ConduitT>.Initialize)}");
                if(conduit.Channel != this) throw new ConduitInitializationException($"{conduitTYpe.FullName} must assign the id channel in {nameof(IConduit<ConduitT>.Initialize)}");


                channelInitializer?.Invoke(conduit);
                chunk[total].enumerator = conduit.GetEnumerator();
                chunk[total].enumerator.MoveNext();
                if (chunk[total].enumerator.Current == null) {
                    throw new ConduitIterationException($"{conduitTYpe.FullName} must yield this on the first iteration");
                }
                chunk[total].channelFinalizer = channelFinalizer;
                chunk[total].setException = setException;
                total++;
            }

            public void Flush() {
                //Iterate Enumerator One By One (to try and keep them in sync. Note: they may move out of sync though)
                bool complete;
                do {
                    complete = true;
                    for (channelingId = 0; channelingId < total; channelingId++) {
                        try {
                            complete &= !chunk[channelingId].enumerator.MoveNext();
                        } catch (Exception ex) {
                            chunk[channelingId].setException(ex);
                            if (chunk[channelingId].enumerator.Current.Exception != ex) new ConduitInitializationException($"{conduitTYpe.FullName} must assign the exception in the SetException action set in {nameof(IConduit<ConduitT>.Initialize)}");
                        }
                    }
                } while (!complete);

                Reset();
            }

            private void Reset() {
                total = 0;
            }

            private struct ConduitWrapper {
                public IEnumerator<ConduitT> enumerator;
                public Action<ConduitT> channelFinalizer;
                public Action<Exception> setException;
            }

        }
    }
}
