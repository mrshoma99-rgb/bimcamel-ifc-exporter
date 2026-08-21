using System.Collections.Generic;
using CamelWorks.Core.Identity;

namespace CamelWorks.Core.Abstractions
{
    /// <summary>
    /// One element, behind the seam.
    ///
    /// Deliberately narrow. Everything a service needs to identify, describe, place and measure an
    /// element is here; anything host-specific stays on the far side. A service written against
    /// this interface is testable on Linux against <c>FakeDocument</c> and needs no Navisworks to
    /// prove its logic — which is the only reason four host years' worth of behaviour can share one
    /// implementation.
    /// </summary>
    public interface IModelItem
    {
        /// <summary>Stable identity. Computed once at resolve time, never recomputed per call.</summary>
        ElementKey Key { get; }

        /// <summary>Name as the host's selection tree shows it.</summary>
        string DisplayName { get; }

        /// <summary>Category, where the source supplied one.</summary>
        string? Category { get; }

        /// <summary>Type or family name, where the source supplied one.</summary>
        string? TypeName { get; }

        /// <summary>The model this element belongs to.</summary>
        IModelSource Model { get; }

        /// <summary>Ordered display names from the model root down to this element.</summary>
        IReadOnlyList<string> TreePath { get; }

        /// <summary>True when this element carries geometry, as opposed to being a grouping node.</summary>
        bool HasGeometry { get; }

        /// <summary>World-space axis-aligned bounding box.</summary>
        BoundingBox Bounds { get; }

        /// <summary>
        /// Whether the host currently hides this element. Part of the appearance state the
        /// Appearance Manager reports, and the reason "what is hidden right now" is answerable at
        /// all — a question the host itself cannot answer across a whole federation.
        /// </summary>
        bool IsHidden { get; }

        /// <summary>
        /// Read one property. Returns null when absent — absence is ordinary and every caller
        /// handles it, because a federation always contains models that never carried the property.
        /// </summary>
        string? Property(string category, string name);

        /// <summary>Every property this element carries, as (category, name, value).</summary>
        IEnumerable<PropertyValue> Properties();
    }

    /// <summary>One property reading.</summary>
    public readonly struct PropertyValue
    {
        /// <summary>Property category (the tab it appears under in the host).</summary>
        public string Category { get; }

        /// <summary>Property name.</summary>
        public string Name { get; }

        /// <summary>Value as displayed. Typing happens above this seam, never in it.</summary>
        public string? Value { get; }

        /// <summary>Create a reading.</summary>
        public PropertyValue(string category, string name, string? value)
        {
            Category = category; Name = name; Value = value;
        }

        /// <inheritdoc />
        public override string ToString() => Category + "/" + Name + "=" + (Value ?? "<null>");
    }

    /// <summary>A world-space axis-aligned box. The only box the host exposes.</summary>
    public readonly struct BoundingBox
    {
        /// <summary>Minimum corner, X.</summary>
        public double MinX { get; }

        /// <summary>Minimum corner, Y.</summary>
        public double MinY { get; }

        /// <summary>Minimum corner, Z.</summary>
        public double MinZ { get; }

        /// <summary>Maximum corner, X.</summary>
        public double MaxX { get; }

        /// <summary>Maximum corner, Y.</summary>
        public double MaxY { get; }

        /// <summary>Maximum corner, Z.</summary>
        public double MaxZ { get; }

        /// <summary>Create a box.</summary>
        public BoundingBox(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            MinX = minX; MinY = minY; MinZ = minZ;
            MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }

        /// <summary>Size along X.</summary>
        public double SizeX => MaxX - MinX;

        /// <summary>Size along Y.</summary>
        public double SizeY => MaxY - MinY;

        /// <summary>Size along Z.</summary>
        public double SizeZ => MaxZ - MinZ;

        /// <summary>Centre, X.</summary>
        public double CentreX => (MinX + MaxX) / 2.0;

        /// <summary>Centre, Y.</summary>
        public double CentreY => (MinY + MaxY) / 2.0;

        /// <summary>Centre, Z.</summary>
        public double CentreZ => (MinZ + MaxZ) / 2.0;

        /// <summary>True when this box shares any volume with <paramref name="other"/>.</summary>
        public bool Intersects(BoundingBox other) =>
            MinX <= other.MaxX && MaxX >= other.MinX &&
            MinY <= other.MaxY && MaxY >= other.MinY &&
            MinZ <= other.MaxZ && MaxZ >= other.MinZ;

        /// <summary>The smallest box containing both.</summary>
        public BoundingBox Union(BoundingBox other) => new BoundingBox(
            MinX < other.MinX ? MinX : other.MinX,
            MinY < other.MinY ? MinY : other.MinY,
            MinZ < other.MinZ ? MinZ : other.MinZ,
            MaxX > other.MaxX ? MaxX : other.MaxX,
            MaxY > other.MaxY ? MaxY : other.MaxY,
            MaxZ > other.MaxZ ? MaxZ : other.MaxZ);

        /// <inheritdoc />
        public override string ToString() =>
            "(" + MinX + "," + MinY + "," + MinZ + ")-(" + MaxX + "," + MaxY + "," + MaxZ + ")";
    }
}
