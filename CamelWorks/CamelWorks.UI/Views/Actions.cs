using System;
using System.Collections.Generic;
using System.Globalization;
using CamelWorks.Core.Abstractions;
using CamelWorks.Core.Project;

namespace CamelWorks.UI.Views
{
    /// <summary>
    /// The ribbon commands that act rather than open a screen.
    ///
    /// There are two, and both earn it: sectioning is something you do while looking at the model,
    /// so putting it behind a panel would mean covering the thing being sectioned; and Help is the
    /// one screen that must work when the rest of the product will not.
    /// </summary>
    public static class Actions
    {
        /// <summary>How much context to leave around a section box, in metres.</summary>
        public const double SectionMargin = 0.5;

        /// <summary>
        /// Run an immediate command.
        /// </summary>
        /// <param name="commandId">The ribbon command id.</param>
        /// <returns>Something to tell the user, or null when it simply worked.</returns>
        public static string? Run(string commandId)
        {
            switch (commandId)
            {
                case "ID_CW_SectionBox": return SectionBox();
                default: return null;
            }
        }

        /// <summary>
        /// Box the selection, or toggle sectioning when nothing is selected.
        ///
        /// The toggle is the half people ask for. Navisworks can switch sectioning off, and doing
        /// so loses the box — so the next time you want it you set it up again. Here, off and on
        /// are the same button and the box survives both.
        /// </summary>
        public static string? SectionBox()
        {
            var session = Host.Current;
            if (session == null) return Host.NoModel;

            var keys = session.Model.SelectedKeys();

            if (keys.Count == 0)
            {
                var on = session.View.IsSectionBoxEnabled;
                session.View.SetSectionBoxEnabled(!on);

                return on
                    ? null
                    : "Sectioning is back on with the box CamelWorks last set. With elements selected, "
                      + "this button boxes them instead.";
            }

            var box = Bounds(session, keys);

            if (box == null)
                return "Nothing in the selection has geometry, so there is nothing to box.";

            session.View.SetSectionBox(box.Value, SectionMargin);

            session.Record(ActivityKind.Appearance,
                "sectioned around " + keys.Count.ToString("N0", CultureInfo.InvariantCulture)
                + (keys.Count == 1 ? " element" : " elements"));

            return null;
        }

        private static BoundingBox? Bounds(Session session, IReadOnlyList<Core.Identity.ElementKey> keys)
        {
            double minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
            var any = false;

            foreach (var item in session.Model.Traverse(TraversalScope.FromKeys(keys)))
            {
                if (!item.HasGeometry) continue;

                var bounds = item.Bounds;

                if (!any)
                {
                    minX = bounds.MinX; minY = bounds.MinY; minZ = bounds.MinZ;
                    maxX = bounds.MaxX; maxY = bounds.MaxY; maxZ = bounds.MaxZ;
                    any = true;
                    continue;
                }

                minX = Math.Min(minX, bounds.MinX);
                minY = Math.Min(minY, bounds.MinY);
                minZ = Math.Min(minZ, bounds.MinZ);
                maxX = Math.Max(maxX, bounds.MaxX);
                maxY = Math.Max(maxY, bounds.MaxY);
                maxZ = Math.Max(maxZ, bounds.MaxZ);
            }

            return any ? new BoundingBox(minX, minY, minZ, maxX, maxY, maxZ) : (BoundingBox?)null;
        }
    }
}
