using System;
using System.Collections.Generic;
using Autodesk.Navisworks.Api;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Identity;

namespace CamelWorks.Nav
{
    /// <summary>
    /// Colour, transparency, visibility, camera and sectioning.
    ///
    /// The host's two override layers are not symmetric, and the asymmetry drives the whole design
    /// above this seam. The permanent layer can be reset for a given collection and read back; the
    /// temporary layer can only be reset globally and cannot be read at all. That is why the
    /// Appearance Manager's stack lives on the permanent layer, and why this class refuses a
    /// per-element clear on the temporary one rather than pretending it worked.
    ///
    /// <b>Main thread only.</b>
    /// </summary>
    public sealed class NavViewSession : IViewSession
    {
        private readonly NavDocument _document;
        private string? _sectionBox;

        /// <summary>Wrap a document.</summary>
        public NavViewSession(NavDocument document) =>
            _document = document ?? throw new ArgumentNullException(nameof(document));

        /// <inheritdoc />
        public OverrideLayer Layer { get; set; } = OverrideLayer.Permanent;

        private Document Host => _document.Document;

        /// <inheritdoc />
        public void SetColour(IReadOnlyList<ElementKey> keys, Colour colour)
        {
            var items = _document.ItemsFor(keys);
            if (items.Count == 0) return;

            var value = NavValues.FromColour(colour);

            if (Layer == OverrideLayer.Permanent) Host.Models.OverridePermanentColor(items, value);
            else Host.Models.OverrideTemporaryColor(items, value);
        }

        /// <inheritdoc />
        public void SetTransparency(IReadOnlyList<ElementKey> keys, double transparency)
        {
            var items = _document.ItemsFor(keys);
            if (items.Count == 0) return;

            var value = Math.Max(0, Math.Min(1, transparency));

            if (Layer == OverrideLayer.Permanent) Host.Models.OverridePermanentTransparency(items, value);
            else Host.Models.OverrideTemporaryTransparency(items, value);
        }

        /// <inheritdoc />
        public void SetVisible(IReadOnlyList<ElementKey> keys, bool visible)
        {
            var items = _document.ItemsFor(keys);
            if (items.Count == 0) return;

            Host.Models.SetHidden(items, !visible);
        }

        /// <inheritdoc />
        public void ClearOverrides(IReadOnlyList<ElementKey> keys)
        {
            if (Layer == OverrideLayer.Temporary)
                throw new NotSupportedException(
                    "the host's temporary override layer can only be reset globally, so a per-element "
                    + "clear on it is not possible; use the permanent layer, or ClearAllOverrides");

            var items = _document.ItemsFor(keys);
            if (items.Count == 0) return;

            Host.Models.ResetPermanentMaterials(items);
            Host.Models.SetHidden(items, false);
        }

        /// <inheritdoc />
        public void ClearAllOverrides()
        {
            if (Layer == OverrideLayer.Temporary)
            {
                Host.Models.ResetAllTemporaryMaterials();
                return;
            }

            Host.Models.ResetAllPermanentMaterials();
            Host.Models.ResetAllHidden();
        }

        /// <summary>
        /// What the document is actually in, for the elements given.
        /// </summary>
        /// <param name="keys">The elements to read.</param>
        /// <exception cref="NotSupportedException">
        /// The temporary layer is selected. The host cannot read it back, and returning the
        /// permanent state while claiming to describe the temporary one would make the Appearance
        /// Manager quietly wrong about everything it showed.
        /// </exception>
        public IReadOnlyList<AppearanceState> ReadAppearance(IReadOnlyList<ElementKey> keys)
        {
            if (Layer == OverrideLayer.Temporary)
                throw new NotSupportedException(
                    "the host cannot report its temporary override layer; read the permanent one instead");

            var states = new List<AppearanceState>();
            if (keys == null) return states;

            foreach (var key in keys)
            {
                var item = _document.Resolve(key) as NavModelItem;
                if (item == null) continue;

                states.Add(Read(key, item.Item));
            }

            return states;
        }

        /// <inheritdoc />
        public void ZoomTo(IReadOnlyList<ElementKey> keys, double marginMetres)
        {
            var items = _document.ItemsFor(keys);
            if (items.Count == 0) return;

            var restore = new ModelItemCollection();
            foreach (ModelItem item in Host.CurrentSelection.SelectedItems) restore.Add(item);

            try
            {
                // The host focuses on the current selection, so this borrows it — and puts it back.
                // A tool that quietly changed what was selected would throw away whatever the
                // coordinator had picked, every single time they clicked a row.
                Host.CurrentSelection.CopyFrom(items);
                Host.ActiveView.FocusOnCurrentSelection();
            }
            finally
            {
                Host.CurrentSelection.CopyFrom(restore);
            }
        }

        /// <inheritdoc />
        public void SetSectionBox(CamelWorks.Core.Abstractions.BoundingBox box, double marginMetres)
        {
            var margin = Math.Max(0, marginMetres);

            _sectionBox = Json(
                box.MinX - margin, box.MinY - margin, box.MinZ - margin,
                box.MaxX + margin, box.MaxY + margin, box.MaxZ + margin);

            Host.ActiveView.SetClippingPlanes(_sectionBox);
        }

        /// <inheritdoc />
        public void SetSectionBoxEnabled(bool enabled)
        {
            if (!enabled)
            {
                // Remember what is there before switching it off, so it can be switched back on.
                // The contract is explicit that disabling must not discard the box, and the host
                // gives no other way to get it back.
                var current = Host.ActiveView.GetClippingPlanes();
                if (!string.IsNullOrWhiteSpace(current) && current.IndexOf("\"Enabled\":false", StringComparison.OrdinalIgnoreCase) < 0)
                    _sectionBox = current;

                Host.ActiveView.SetClippingPlanes("{\"Enabled\":false}");
                return;
            }

            if (_sectionBox != null) Host.ActiveView.SetClippingPlanes(_sectionBox);
        }

        // The host's clipping API is JSON in and JSON out, not an object model — GetClippingPlanes
        // returns a string. An axis-aligned oriented box with an identity rotation is the shape it
        // round-trips most reliably across all supported years.
        private static string Json(double minX, double minY, double minZ,
                                   double maxX, double maxY, double maxZ)
        {
            return "{\"Enabled\":true,\"Mode\":\"OrientedBox\",\"OrientedBox\":{\"Box\":[["
                 + N(minX) + "," + N(minY) + "," + N(minZ) + "],["
                 + N(maxX) + "," + N(maxY) + "," + N(maxZ) + "]],\"Rotation\":[0,0,0,1]}}";
        }

        private static string N(double value) =>
            value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        private static AppearanceState Read(ElementKey key, ModelItem item)
        {
            Colour? colour = null;
            double? transparency = null;
            var foreign = false;

            var geometry = item.Geometry;
            if (geometry != null)
            {
                var permanent = geometry.PermanentColor;
                var original = geometry.OriginalColor;

                // The host has no flag for "this element carries an override". The only signal is
                // that the permanent colour differs from the original one, so that is what is used
                // — and whether CamelWorks authored it is decided above this seam, by comparing
                // against the layer stack. Here it is reported as foreign, and the Appearance
                // Manager corrects it for the elements its own layers claim.
                if (!Same(permanent, original))
                {
                    colour = NavValues.ToColour(permanent);
                    foreign = true;
                }

                if (geometry.PermanentTransparency > 0)
                {
                    transparency = geometry.PermanentTransparency;
                    foreign = true;
                }
            }

            return new AppearanceState(key, colour, transparency, item.IsHidden, foreign);
        }

        private static bool Same(Autodesk.Navisworks.Api.Color a, Autodesk.Navisworks.Api.Color b)
        {
            const double tolerance = 1.0 / 512.0;

            return Math.Abs(a.R - b.R) < tolerance
                && Math.Abs(a.G - b.G) < tolerance
                && Math.Abs(a.B - b.B) < tolerance;
        }
    }
}
