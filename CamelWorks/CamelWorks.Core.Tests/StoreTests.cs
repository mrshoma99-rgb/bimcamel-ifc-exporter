using System;
using System.Linq;
using CamelWorks.Core.Store;
using Xunit;

namespace CamelWorks.Core.Tests
{
    public class JsonTests
    {
        // ---------------------------------------------------------------------------------
        // The two properties the sidecar store depends on
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Object_key_order_survives_a_round_trip()
        {
            // These files sit in the user's project folder and get diffed. A save that reorders
            // keys produces a meaningless diff every time.
            const string text = "{\"zebra\":1,\"apple\":2,\"middle\":3}";

            var back = JsonReader.Parse(text);

            Assert.Equal(new[] { "zebra", "apple", "middle" }, back.Keys.ToArray());
            Assert.Equal(text, back.ToJson(indented: false));
        }

        [Fact]
        public void Numbers_keep_their_original_text()
        {
            // Reading 1.10 and writing 1.1 is a spurious diff; rewriting a long id in exponent
            // form is data loss.
            var back = JsonReader.Parse("{\"a\":1.10,\"b\":1e3,\"big\":123456789012345678}");

            Assert.Equal("{\"a\":1.10,\"b\":1e3,\"big\":123456789012345678}", back.ToJson(indented: false));
            Assert.Equal(1.1, back["a"].AsDouble(), 6);
            Assert.Equal(1000, back["b"].AsDouble(), 6);
        }

        [Fact]
        public void Setting_an_existing_key_keeps_its_position()
        {
            var v = JsonReader.Parse("{\"a\":1,\"b\":2,\"c\":3}");
            v.Set("b", 99L);

            Assert.Equal(new[] { "a", "b", "c" }, v.Keys.ToArray());
            Assert.Equal(99, v["b"].AsLong());
        }

        [Fact]
        public void A_new_key_is_appended()
        {
            var v = JsonReader.Parse("{\"a\":1}");
            v.Set("z", "x");

            Assert.Equal(new[] { "a", "z" }, v.Keys.ToArray());
        }

        // ---------------------------------------------------------------------------------
        // Reading
        // ---------------------------------------------------------------------------------

        [Fact]
        public void A_missing_member_reads_as_null_rather_than_throwing()
        {
            // An older file simply lacks a newer key. That is ordinary, so it must not throw at
            // every read site.
            var v = JsonReader.Parse("{}");

            Assert.Equal(JsonKind.Null, v["nope"].Kind);
            Assert.Null(v["nope"].AsString());
            Assert.Equal(7, v["nope"].AsLong(7));
            Assert.False(v.Has("nope"));
        }

        [Fact]
        public void Reading_a_member_as_the_wrong_type_returns_the_fallback()
        {
            var v = JsonReader.Parse("{\"n\":5,\"s\":\"text\"}");

            Assert.Equal("fallback", v["n"].AsString("fallback"));
            Assert.Equal(42, v["s"].AsLong(42));
            Assert.True(v["s"].AsBool(true));
        }

        [Theory]
        [InlineData("\"plain\"", "plain")]
        [InlineData("\"with \\\"quotes\\\"\"", "with \"quotes\"")]
        [InlineData("\"tab\\there\"", "tab\there")]
        [InlineData("\"newline\\nhere\"", "newline\nhere")]
        [InlineData("\"back\\\\slash\"", "back\\slash")]
        [InlineData("\"unicode \\u00e9\"", "unicode é")]
        [InlineData("\"solidus \\/\"", "solidus /")]
        public void Escapes_round_trip(string json, string expected)
        {
            Assert.Equal(expected, JsonReader.Parse(json).AsString());
        }

        [Fact]
        public void Control_characters_are_escaped_on_the_way_out()
        {
            var s = JsonValue.String("bell\u0007end").ToJson(indented: false);

            Assert.Equal("\"bell\\u0007end\"", s);
            Assert.Equal("bell\u0007end", JsonReader.Parse(s).AsString());
        }

        [Fact]
        public void Nested_structures_round_trip()
        {
            const string text = "{\"a\":[1,{\"b\":[true,null,\"x\"]}],\"c\":{}}";

            Assert.Equal(text, JsonReader.Parse(text).ToJson(indented: false));
        }

        [Fact]
        public void Empty_containers_stay_compact_even_when_indenting()
        {
            var v = JsonValue.Object().Set("arr", JsonValue.Array()).Set("obj", JsonValue.Object());

            Assert.Contains("\"arr\": []", v.ToJson());
            Assert.Contains("\"obj\": {}", v.ToJson());
        }

        // ---------------------------------------------------------------------------------
        // Strictness — a file that is not valid JSON is a symptom, not something to guess at
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData("{\"a\":1,}")]           // trailing comma
        [InlineData("{'a':1}")]              // single quotes
        [InlineData("{a:1}")]                // unquoted key
        [InlineData("{\"a\":1} garbage")]    // trailing content
        [InlineData("{\"a\":}")]             // missing value
        [InlineData("{\"a\" 1}")]            // missing colon
        [InlineData("[1,2")]                 // unterminated array
        [InlineData("{\"a\":\"unterminated")]
        [InlineData("\"bad \\q escape\"")]
        [InlineData("")]
        public void Malformed_input_is_rejected_rather_than_guessed_at(string text)
        {
            Assert.False(JsonReader.TryParse(text, out _));
            Assert.Throws<JsonParseException>(() => JsonReader.Parse(text));
        }

        [Fact]
        public void A_parse_error_carries_its_offset()
        {
            // "the settings file is corrupt" with no position is a ticket nobody can act on.
            var ex = Assert.Throws<JsonParseException>(() => JsonReader.Parse("{\"a\":1,}"));

            Assert.True(ex.Offset > 0);
            Assert.Contains("offset", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Whitespace_between_tokens_is_ignored()
        {
            Assert.Equal(1, JsonReader.Parse("  {\n\t\"a\" :\r\n 1 \n}  ")["a"].AsLong());
        }

        [Fact]
        public void NaN_and_infinity_are_refused_because_JSON_has_no_such_thing()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => JsonValue.Number(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => JsonValue.Number(double.PositiveInfinity));
        }
    }

    public class VersionedDocumentTests
    {
        // ---------------------------------------------------------------------------------
        // THE rule — G5
        // ---------------------------------------------------------------------------------

        [Fact]
        public void A_key_written_by_a_newer_build_survives_this_build_rewriting_the_file()
        {
            // The colleague on last month's build opens the project and saves. Without this, they
            // silently strip everyone else's newer settings, and nobody notices until the rules
            // are gone.
            const string fromFuture = "{\"schemaVersion\":1,\"rules\":{\"a\":1},\"featureNobodyKnowsYet\":{\"deep\":[1,2]}}";

            var doc = VersionedDocument.Load(fromFuture, buildSupports: 1);
            doc.Section("rules").Set("a", 2L);
            var saved = doc.Save();

            var reloaded = JsonReader.Parse(saved);
            Assert.True(reloaded.Has("featureNobodyKnowsYet"));
            Assert.Equal(2, reloaded["featureNobodyKnowsYet"]["deep"].Items.Count);
            Assert.Equal(2, reloaded["rules"]["a"].AsLong());
        }

        [Fact]
        public void A_file_from_the_future_is_read_only_and_says_why()
        {
            var doc = VersionedDocument.Load("{\"schemaVersion\":9,\"x\":1}", buildSupports: 2);

            Assert.Equal(LoadOutcome.TooNew, doc.Outcome);
            Assert.True(doc.IsReadOnly);
            Assert.Contains("newer CamelWorks", doc.ReadOnlyReason!, StringComparison.Ordinal);

            // Saving would write 2 over 9 and claim a downgrade it did not perform.
            Assert.Throws<InvalidOperationException>(() => doc.Save());
        }

        [Fact]
        public void Reading_a_future_file_still_works_it_is_only_writing_that_is_refused()
        {
            var doc = VersionedDocument.Load("{\"schemaVersion\":9,\"rules\":{\"a\":7}}", buildSupports: 2);

            Assert.Equal(7, doc.Root["rules"]["a"].AsLong());
        }

        // ---------------------------------------------------------------------------------
        // Zero setup — every one of these is a normal first-run state, not an error
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void No_file_yet_is_an_ordinary_state(string? text)
        {
            var doc = VersionedDocument.Load(text, buildSupports: 3);

            Assert.Equal(LoadOutcome.Missing, doc.Outcome);
            Assert.False(doc.IsReadOnly);
            Assert.Equal(3, doc.SchemaVersion);

            // And it is immediately usable — nothing to configure first.
            doc.Section("rules").Set("a", 1L);
            Assert.Contains("\"a\": 1", doc.Save());
        }

        [Fact]
        public void A_damaged_file_is_reported_not_silently_replaced()
        {
            var doc = VersionedDocument.Load("{this is not json", buildSupports: 1);

            Assert.Equal(LoadOutcome.Malformed, doc.Outcome);
            // The caller decides whether to back it up and start fresh; the store never quietly
            // overwrites something it could not read.
        }

        [Fact]
        public void An_unversioned_file_is_read_as_version_one_rather_than_refused()
        {
            // It can only have come from a build that predates versioning. Refusing it would be a
            // worse outcome than reading it conservatively.
            var doc = VersionedDocument.Load("{\"rules\":{\"a\":1}}", buildSupports: 4);

            Assert.Equal(LoadOutcome.Ok, doc.Outcome);
            Assert.Equal(1, doc.SchemaVersion);
            Assert.Equal(1, doc.Root["rules"]["a"].AsLong());
        }

        // ---------------------------------------------------------------------------------
        // Envelope mechanics
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Saving_stamps_this_builds_version()
        {
            var doc = VersionedDocument.Load("{\"schemaVersion\":1,\"a\":1}", buildSupports: 3);

            Assert.Equal(3, JsonReader.Parse(doc.Save())[VersionedDocument.VersionKey].AsLong());
        }

        [Fact]
        public void Sections_are_created_on_first_use_and_reused_after()
        {
            var doc = VersionedDocument.Create(1);

            doc.Section("parties").Set("count", 3L);
            doc.Section("parties").Set("source", "derived");

            Assert.Equal(3, doc.Root["parties"]["count"].AsLong());
            Assert.Equal("derived", doc.Root["parties"]["source"].AsString());
            Assert.Single(doc.Root.Keys.Where(k => k == "parties"));
        }

        [Fact]
        public void A_section_name_is_required()
        {
            var doc = VersionedDocument.Create(1);

            Assert.Throws<ArgumentException>(() => doc.Section(""));
        }

        [Fact]
        public void Round_trips_through_save_and_load()
        {
            var doc = VersionedDocument.Create(2);
            doc.Section("rules").Set("name", "Level → Grid → Proximity");
            doc.Section("rules").Set("enabled", true);

            var back = VersionedDocument.Load(doc.Save(), buildSupports: 2);

            Assert.Equal(LoadOutcome.Ok, back.Outcome);
            Assert.Equal("Level → Grid → Proximity", back.Root["rules"]["name"].AsString());
            Assert.True(back.Root["rules"]["enabled"].AsBool());
        }
    }
}
