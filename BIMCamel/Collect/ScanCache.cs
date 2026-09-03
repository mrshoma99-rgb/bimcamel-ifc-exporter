using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Navisworks.Api;

namespace BIMCamel.Collect
{
    /// <summary>
    /// What the scan phase computed, kept so the next button does not compute it again (v5 S2).
    ///
    /// The pane used to build a FRESH <c>Dictionary&lt;ModelItem,string&gt;</c> in each of
    /// <c>PreviewMapping</c>, <c>RunExport</c> and <c>RunBatchExport</c>, and to re-resolve every
    /// mapping set in each of them too. A user who runs Smart setup, reads the preview and then
    /// exports therefore paid for the same item keys three times and for the same model-wide
    /// <c>Search.FindAll</c> per set twice — on a 674k-element model that is the dominant cost of
    /// the whole pane.
    ///
    /// THE TWO HALVES HAVE DIFFERENT SAFETY, and are cached differently because of it.
    ///
    /// • <b>Item keys</b> are a pure memo of a pure function: <see cref="ItemCollector.ItemKey"/>
    ///   reads an item's InstanceGuid, or its ancestors' display names. Neither changes while a
    ///   document is loaded, so an entry can never go stale within a document — only a different
    ///   document invalidates it. Long-lived, and safe.
    ///
    /// • <b>Resolved set maps</b> are not: a search set re-evaluates against the model, and the
    ///   rules themselves are whatever the user last typed into the mapping grid. A stale one would
    ///   silently export yesterday's classification, which is worse than any amount of waiting. So
    ///   they are keyed by a signature of the rules AND dropped at every point the pane already
    ///   treats as "things may have moved": a document change, a set refresh, a scope change, or a
    ///   mapping-grid edit.
    ///
    /// When in doubt, drop it. A lost cache costs a scan; a stale one costs a wrong deliverable.
    /// UI-thread only, like everything it caches.
    /// </summary>
    public sealed class ScanCache
    {
        private string _docToken = "";
        private Dictionary<ModelItem, string> _keys = new Dictionary<ModelItem, string>();

        private string _mapsSig = "";
        private ItemCollector.SetMaps? _maps;

        /// <summary>Item-key memo for the current document. Never null; handed straight to
        /// <see cref="ItemCollector.ItemKey(ModelItem, Dictionary{ModelItem, string})"/>.</summary>
        public Dictionary<ModelItem, string> Keys => _keys;

        /// <summary>
        /// Point the cache at the active document, clearing everything if it is not the one the
        /// cache was filled from. Cheap enough to call before every operation.
        /// </summary>
        public void Bind(Document? doc)
        {
            string token = TokenFor(doc);
            if (token == _docToken) return;
            _docToken = token;
            _keys = new Dictionary<ModelItem, string>();
            DropMaps();
        }

        /// <summary>Forget the resolved set maps. Called wherever the pane already assumes the
        /// world may have moved — set refresh, scope change, mapping edit.</summary>
        public void DropMaps() { _maps = null; _mapsSig = ""; }

        /// <summary>The resolved maps for these rules, or null when they must be rebuilt.</summary>
        public ItemCollector.SetMaps? Maps(IEnumerable<ItemCollector.SetRule> rules)
        {
            if (_maps == null) return null;
            return Signature(rules) == _mapsSig ? _maps : null;
        }

        /// <summary>Remember maps against the rules that produced them.</summary>
        public void StoreMaps(IEnumerable<ItemCollector.SetRule> rules, ItemCollector.SetMaps maps)
        {
            _maps = maps;
            _mapsSig = Signature(rules);
        }

        /// <summary>
        /// Identity of the loaded document, as far as the pane can observe it. The Document object
        /// itself can be reused across file loads, so the file name and model count go in too — any
        /// of the three changing is enough to throw the cache away, which is the safe direction.
        /// </summary>
        private static string TokenFor(Document? doc)
        {
            if (doc == null) return "";
            try { return doc.FileName + "\u001F" + doc.Models.Count.ToString(); }
            catch { return Guid.NewGuid().ToString(); }   // unreadable → never match, never reuse
        }

        /// <summary>Exact content of the rule list: a changed set, class or code must miss.</summary>
        private static string Signature(IEnumerable<ItemCollector.SetRule> rules)
        {
            var sb = new StringBuilder();
            foreach (var r in rules)
            {
                sb.Append(r.Set == null ? "" : (r.Set.DisplayName ?? ""));
                sb.Append('\u0001').Append(r.ClassKey)
                  .Append('\u0001').Append(r.Classification)
                  .Append('\u0002');
            }
            return sb.ToString();
        }
    }
}
