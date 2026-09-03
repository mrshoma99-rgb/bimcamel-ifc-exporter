using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace BIMCamel.Ifc
{
    /// <summary>
    /// Runs the extractor and the IFC writer on two threads instead of one (v5 E7; the design is
    /// v3 Part A4, which was specified and never built).
    ///
    /// WHY THIS SHAPE. The Navisworks read API is STA — geometry can only be read on the
    /// Navisworks UI thread — so the *read* cannot be parallelised and the producer must stay put.
    /// Everything downstream of the read is ordinary managed work on plain data objects
    /// (<c>ElementMesh</c> / <c>InstancedElement</c> hold no Navisworks types at all), so the
    /// writer can move off that thread and run in the read's shadow. On the prova model the read
    /// is 82–92% of wall clock, so this does not make the export several times faster: it collects
    /// the remaining 8–18% and stops the UI thread from doing serialisation between COM calls.
    ///
    /// THE QUEUE IS BOUNDED, deliberately and tightly. An unbounded queue would let the producer
    /// run ahead and hold the whole model in memory — the exact failure the streaming writer was
    /// built to end. At this capacity the producer blocks whenever the writer falls behind, which
    /// is the correct behaviour: peak memory stays a function of the queue, not of the model.
    ///
    /// FAILURE. A writer that throws on a background thread must not leave a half-written file and
    /// a caller that thinks the export succeeded, so:
    ///   • the consumer's exception is captured and rethrown to the caller after the join;
    ///   • the producer notices it and stops reading rather than feeding a dead writer;
    ///   • the consumer drains the queue on its way out, so a producer blocked on a full queue is
    ///     always released — no deadlock;
    ///   • CompleteAdding and Join both run in a finally, so a producer exception (a Stop, an
    ///     OutOfMemory) still closes the writer down in an orderly way.
    ///
    /// TIMING COUNTERS. <see cref="Geometry.ExportTiming"/> documents which counters belong to
    /// which thread; the Join here is the memory barrier that makes them safe to read afterwards.
    /// </summary>
    public static class ExportPipeline
    {
        /// <summary>Elements in flight between the reader and the writer. Small on purpose — see
        /// the note above about bounded memory.</summary>
        public const int Capacity = 64;

        /// <summary>Mutable across threads, so the flag lives in a field rather than a local.</summary>
        private sealed class Fault { public volatile Exception? Error; }

        /// <summary>
        /// Pulls <paramref name="source"/> on the CALLING thread (which must be the Navisworks UI
        /// thread) and hands each item to <paramref name="consume"/> running on a background
        /// thread. Returns once the writer has finished and been joined.
        /// </summary>
        public static void Run<T>(IEnumerable<T> source, Action<IEnumerable<T>> consume)
        {
            using (var queue = new BlockingCollection<T>(Capacity))
            {
                var fault = new Fault();
                var worker = new Thread(() =>
                {
                    try
                    {
                        consume(queue.GetConsumingEnumerable());
                    }
                    catch (Exception ex)
                    {
                        fault.Error = ex;
                    }
                    finally
                    {
                        // Release a producer that is blocked on a full queue after we have stopped
                        // taking from it. Harmless in the normal case: the enumerable is already
                        // exhausted, so this returns as soon as CompleteAdding lands.
                        try { foreach (var _ in queue.GetConsumingEnumerable()) { } }
                        catch { /* the collection is being disposed — nothing left to release */ }
                    }
                })
                {
                    IsBackground = true,
                    Name = "BIMCamel IFC writer"
                };
                worker.Start();

                try
                {
                    foreach (var item in source)
                    {
                        if (fault.Error != null) break;   // the writer is gone; stop reading
                        queue.Add(item);
                    }
                }
                finally
                {
                    queue.CompleteAdding();
                    worker.Join();
                }

                // Rethrow AFTER the join, on the caller's thread, so the pane reports it exactly
                // as it always reported a writer failure.
                var err = fault.Error;
                if (err != null)
                    throw new InvalidOperationException("The IFC writer failed: " + err.Message, err);
            }
        }
    }
}
