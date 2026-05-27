# IFC structure → Navisworks source mapping (grounding notes)

Purpose: ground BIMCamel's export in how IFC actually models an element, and be honest about
what Navisworks can and cannot supply (Navisworks is an aggregator/viewer: geometry is
**tessellated**, properties are **flat, read-only** data inherited from the source authoring tool).

## How IFC4 structures a building element

```
IfcProject
 ├─ IfcUnitAssignment (SI metre…)
 ├─ IfcGeometricRepresentationContext  (+ IfcMapConversion → IfcProjectedCRS for georef)
 └─ IfcRelAggregates → IfcSite → IfcBuilding → IfcBuildingStorey
                                                   └─ IfcRelContainedInSpatialStructure → elements

Element occurrence  (IfcWall, IfcPipeSegment, …  or IfcBuildingElementProxy)
 ├─ GlobalId, OwnerHistory, Name, Description, ObjectType, Tag, PredefinedType
 ├─ ObjectPlacement = IfcLocalPlacement
 ├─ Representation  = IfcProductDefinitionShape → IfcShapeRepresentation → geometry
 ├─ IfcRelDefinesByType ───────────────→ IfcElementType (IfcWallType…)  [type-level Psets]
 ├─ IfcRelDefinesByProperties ─────────→ IfcPropertySet  (Pset_WallCommon, custom…)
 ├─ IfcRelDefinesByProperties ─────────→ IfcElementQuantity (IfcQuantityVolume/Area/Length…)
 ├─ IfcRelAssociatesMaterial ──────────→ IfcMaterial / IfcMaterialLayerSet / …
 ├─ IfcRelAssociatesClassification ────→ IfcClassificationReference → IfcClassification
 └─ IfcStyledItem → IfcSurfaceStyle (colour)
```

Key relationships (buildingSMART): **occurrence vs type** is the backbone — `IfcRelDefinesByType`
assigns one `IfcElementType` to many occurrences; type Psets are shared, occurrence Psets
override. Quantities and materials attach to either the occurrence or the type.

## Where each piece comes from in Navisworks — and feasibility

| IFC need | Navisworks source | Feasible? |
|---|---|---|
| Geometry | tessellated fragments (`GenerateSimplePrimitives`) | ✅ mesh only — **no** parametric solids, swept profiles, or true material-layer thicknesses |
| GlobalId | `ModelItem.InstanceGuid` (deterministic fallback if empty) | ✅ have it |
| Name | `ModelItem.DisplayName` | ✅ have it |
| Element **class** (IfcWall…) | not labelled by Navisworks; from a property (source category/type) **or user set→class mapping** | ✅ via mapping (have set-based); ⚠ no auto-detect |
| **PredefinedType** (subtype) | a property value, or user choice | ⚙ to add (user picks enum / custom) |
| **Type object** (`IfcElementType` + `IfcRelDefinesByType`) | group occurrences by a "Type"/"Family Type" property (Revit `Type`, IFC-source type name) | ⚙ **synthesizable** — high value, currently missing |
| Property sets | `PropertyCategories → Properties → VariantData` | ✅ have it (typed). Source-IFC models carry original Pset names. |
| Standard Psets (Pset_WallCommon…) | remap source properties → standard Pset/name | ⚙ param-mapping (in progress) |
| **Base quantities** (`IfcElementQuantity`) | **compute from the mesh**: volume (signed tetrahedra), surface area (Σ triangle areas), L/W/H (bbox); or read quantity-like properties | ⚙ **computable** — high value, currently missing |
| **Material** (`IfcMaterial` + `IfcRelAssociatesMaterial`) | a "Material" property (Revit material name) + fragment colour | ⚙ currently only colour as `IfcStyledItem`; should add material entity |
| **Classification** (`IfcClassificationReference`) | code properties if present (Assembly Code, Uniclass, OmniClass) | ⚙ source-dependent; add when present / user-driven |
| Spatial **storeys** | a "Level"/"Storey" property (common from Revit/IFC) | ⚙ currently single storey; can split by level property |
| Units / georef / owner | `doc.Units`; base point; user | ✅ have it |

## Honest scope statement

BIMCamel can produce a **well-structured** IFC — occurrences + **types**, **computed quantities**,
**materials by name**, **classification**, standard + custom **Psets**, correct spatial structure,
units and georeferencing — but **not** a fully native parametric IFC (no swept/Brep solids, no
material-layer thicknesses, no parametric openings), because the Navisworks input is tessellated
geometry + flat properties. When the source NWC originated from IFC, the original semantics live
in the properties and should be **preserved/round-tripped** rather than re-invented.

## Gap analysis — current export vs "proper"

Current: tessellated geometry (+instancing), set→class typing, flat Psets from categories,
surface colour, units, georef, single Site/Building/Storey.

Missing / bare-bones (priority order):
1. **Type objects** (`IfcRelDefinesByType`) — none. Grouping by a type-name property.
2. **Base quantities** (`IfcElementQuantity`) — none. Compute from mesh.
3. **PredefinedType** on typed elements — none (requested).
4. **Material entities** (`IfcRelAssociatesMaterial`) — only colour style today.
5. **Standard Pset mapping** — flat categories only (param-map in progress).
6. **Classification** — none.
7. **Multi-storey** spatial structure from a level property — single storey today.

Sources: buildingSMART IFC4.3 docs (IfcProduct, IfcRelDefinesByType, IfcTypeObject, IfcElement,
IfcElementQuantity, IfcRelAssociatesMaterial); BIM Corner IFC export rules; Navisworks .NET API
property model (PropertyCategory/DataProperty).
