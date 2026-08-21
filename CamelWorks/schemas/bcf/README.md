# BCF schemas

The published buildingSMART XSDs for BCF 2.1 and 3.0, taken verbatim from
`buildingSMART/BCF-XML` (`release_2_1` and `release_3_0`).

They are here so `validate.sh` can check what `BcfWriter` actually produces against
the real schema. Unit tests cannot do that job: a BCF file can be perfectly
well-formed XML, round-trip through our own reader, pass every assertion — and still
be rejected by the receiving tool because an element the schema requires is missing.

That is not hypothetical. `AspectRatio` is required on both camera types in BCF 3.0
and does not exist in 2.1 at all; the writer omitted it until this check was run.

`shared-types.xsd` is included by `markup30.xsd`, so it has to sit alongside it.
