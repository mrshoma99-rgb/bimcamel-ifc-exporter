using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NavApp = Autodesk.Navisworks.Api.Application;

namespace BIMCamel.UI
{
    /// <summary>
    /// The output file name, built from chips instead of typed tokens. The template string
    /// ("{model}_{schema}") is still what profiles store, in the hidden TxtFileName; the editor is
    /// built from it and writes back to it, so old profiles load unchanged.
    /// </summary>
    public partial class ExporterView
    {
        private sealed class NameToken
        {
            public readonly string Key, Label, Tip;
            /// <summary>Scopes (ScopeIndex) in which the token has a value; null = all.</summary>
            public readonly int[]? Scopes;
            public NameToken(string key, string label, string tip, params int[]? scopes)
            {
                Key = key; Label = label; Tip = tip; Scopes = scopes == null || scopes.Length == 0 ? null : scopes;
            }
            public bool AvailableIn(int scope) => Scopes == null || Scopes.Contains(scope);
        }

        private static readonly NameToken[] NameTokens =
        {
            new NameToken("model", "Model file", "The Navisworks file name, without its extension."),
            new NameToken("project", "Project", "The project name from the Project tab (the model file name when that is blank)."),
            new NameToken("schema", "Schema", "IFC4 or IFC2x3."),
            new NameToken("set", "Set name", "The saved set or search set being exported. Filled in the Saved set and batch scopes.", 3, 4),
            new NameToken("element", "Selected element", "The selected element's name, with +N when more are selected. Filled in the Current selection scope.", 1),
            new NameToken("scope", "Scope", "What was exported: model, selection, section-box, set or batch."),
            new NameToken("date", "Date", "The export date, as 2026-10-23."),
            new NameToken("time", "Time", "The export time, as 1430."),
            new NameToken("user", "User", "Your Windows user name."),
        };

        /// <summary>Default when the template is blank: what the exporter always called its files.</summary>
        private const string DefaultNameTemplate = "{model}_{schema}";

        private sealed class NameSeg
        {
            public bool IsToken;
            public string Value = "";   // token key, or literal text
        }

        private readonly List<NameSeg> _nameSegs = new List<NameSeg>();
        /// <summary>Set while the editor writes the template, so that write does not rebuild it.</summary>
        private bool _nameWriting;

        private void InitNameEditor()
        {
            TxtFileName.TextChanged += (_, _) => { if (!_nameWriting) LoadNameTemplate(TxtFileName.Text); };
            NameEditor.DragOver += OnNameDragOver;
            NameEditor.Drop += (s, e) => OnNameDrop(_nameSegs.Count, e);
            // A click on the empty part of the field puts the caret at the end, like a text box.
            NameEditor.MouseLeftButtonDown += (_, e) =>
            {
                if (e.OriginalSource == NameEditor || e.OriginalSource == NameChips)
                    NameChips.Children.OfType<TextBox>().LastOrDefault()?.Focus();
            };
            LoadNameTemplate(TxtFileName.Text);
        }

        private void LoadNameTemplate(string? template)
        {
            _nameSegs.Clear();
            string t = template ?? "";
            int at = 0;
            foreach (Match m in Regex.Matches(t, @"\{(\w+)\}"))
            {
                if (!NameTokens.Any(k => k.Key.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase))) continue;
                if (m.Index > at) _nameSegs.Add(new NameSeg { Value = t.Substring(at, m.Index - at) });
                _nameSegs.Add(new NameSeg { IsToken = true, Value = m.Groups[1].Value.ToLowerInvariant() });
                at = m.Index + m.Length;
            }
            if (at < t.Length) _nameSegs.Add(new NameSeg { Value = t.Substring(at) });
            BuildNameEditor();
        }

        /// <summary>Adjacent text joins up, and the field always ends in a text box to type into.</summary>
        private void NormalizeNameSegs()
        {
            for (int i = _nameSegs.Count - 1; i > 0; i--)
                if (!_nameSegs[i].IsToken && !_nameSegs[i - 1].IsToken)
                {
                    _nameSegs[i - 1].Value += _nameSegs[i].Value;
                    _nameSegs.RemoveAt(i);
                }
            for (int i = _nameSegs.Count - 2; i >= 0; i--)
                if (!_nameSegs[i].IsToken && _nameSegs[i].Value.Length == 0) _nameSegs.RemoveAt(i);
            if (_nameSegs.Count == 0 || _nameSegs[_nameSegs.Count - 1].IsToken) _nameSegs.Add(new NameSeg());
        }

        private void BuildNameEditor()
        {
            NormalizeNameSegs();
            NameChips.Children.Clear();
            int scope = ScopeIndex();
            for (int i = 0; i < _nameSegs.Count; i++)
                NameChips.Children.Add(_nameSegs[i].IsToken ? MakeChip(i, scope) : MakeNameText(i));
            WriteNameTemplate();
        }

        private FrameworkElement MakeChip(int index, int scope)
        {
            var seg = _nameSegs[index];
            var tok = NameTokens.FirstOrDefault(k => k.Key == seg.Value);
            bool live = tok?.AvailableIn(scope) ?? true;
            var chip = new Border { Style = (Style)FindResource("NameChip"), Tag = index, AllowDrop = true };
            if (!live) chip.Opacity = 0.55;
            chip.ToolTip = (tok?.Tip ?? seg.Value) + (live ? "" : "\nEmpty in the current scope, so it is left out of the name.");

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var label = new TextBlock { Text = tok?.Label ?? seg.Value, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "BadgeAutoTx");
            var x = new Button { Content = "×", Cursor = Cursors.Hand, Margin = new Thickness(3, 0, 0, 0), ToolTip = "Remove", Focusable = false };
            x.SetResourceReference(Control.ForegroundProperty, "BadgeAutoTx");
            x.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate TargetType=\"Button\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                "<Border Background=\"Transparent\" Padding=\"3,0\"><ContentPresenter VerticalAlignment=\"Center\"/></Border></ControlTemplate>");
            x.Click += (_, _) => RemoveNameSeg(index);
            sp.Children.Add(label); sp.Children.Add(x);
            chip.Child = sp;

            var menu = new ContextMenu();
            void Item(string header, bool enabled, Action act)
            {
                var mi = new MenuItem { Header = header, IsEnabled = enabled };
                mi.Click += (_, _) => act();
                menu.Items.Add(mi);
            }
            Item("Move left", index > 0, () => MoveNameSeg(index, index - 1));
            Item("Move right", index < _nameSegs.Count - 1, () => MoveNameSeg(index, index + 2));
            Item("Remove", true, () => RemoveNameSeg(index));
            chip.ContextMenu = menu;

            Point? down = null;
            chip.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(chip);
            chip.MouseMove += (_, e) =>
            {
                if (down == null || e.LeftButton != MouseButtonState.Pressed) { down = null; return; }
                var d = e.GetPosition(chip) - down.Value;
                if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                down = null;
                DragDrop.DoDragDrop(chip, new DataObject("BIMCamelNameSeg", index), DragDropEffects.Move);
            };
            chip.DragOver += OnNameDragOver;
            chip.Drop += (_, e) =>
            {
                // Dropped on the right half of a chip = after it.
                bool after = e.GetPosition(chip).X > chip.ActualWidth / 2;
                OnNameDrop(index + (after ? 1 : 0), e);
            };
            return chip;
        }

        private FrameworkElement MakeNameText(int index)
        {
            var seg = _nameSegs[index];
            bool last = index == _nameSegs.Count - 1;
            var tb = new TextBox { Style = (Style)FindResource("NameText"), Text = seg.Value, AllowDrop = true };
            if (last) tb.MinWidth = 40;
            tb.TextChanged += (_, _) =>
            {
                // Path separators and other characters Windows refuses are dropped as they are typed.
                var clean = new string(tb.Text.Where(c => Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), c) < 0 && c != '{' && c != '}').ToArray());
                if (clean != tb.Text) { int caret = Math.Max(0, tb.CaretIndex - (tb.Text.Length - clean.Length)); tb.Text = clean; tb.CaretIndex = caret; return; }
                seg.Value = tb.Text;
                WriteNameTemplate();
            };
            // Typing into the gap between two chips must not lose the caret, so an emptied box
            // stays until the editor is next rebuilt.
            tb.LostKeyboardFocus += (_, _) =>
            {
                if (seg.Value.Length == 0 && !last)
                    Dispatcher.BeginInvoke(new Action(() => { if (!NameChips.IsKeyboardFocusWithin) BuildNameEditor(); }));
            };
            tb.PreviewDragOver += OnNameDragOver;
            tb.PreviewDrop += (_, e) => OnNameDrop(index + 1, e);
            return tb;
        }

        private void OnNameDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent("BIMCamelNameSeg") ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnNameDrop(int target, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("BIMCamelNameSeg")) return;
            e.Handled = true;
            MoveNameSeg((int)e.Data.GetData("BIMCamelNameSeg"), target);
        }

        /// <summary>Move a chip so it sits before position <paramref name="target"/> (in the
        /// current list).</summary>
        private void MoveNameSeg(int from, int target)
        {
            if (from < 0 || from >= _nameSegs.Count) return;
            var seg = _nameSegs[from];
            _nameSegs.RemoveAt(from);
            if (target > from) target--;
            _nameSegs.Insert(Math.Max(0, Math.Min(target, _nameSegs.Count)), seg);
            BuildNameEditor();
        }

        private void RemoveNameSeg(int index)
        {
            if (index < 0 || index >= _nameSegs.Count) return;
            _nameSegs.RemoveAt(index);
            BuildNameEditor();
        }

        private void OnAddToken(object sender, RoutedEventArgs e)
        {
            int scope = ScopeIndex();
            var menu = new ContextMenu { PlacementTarget = BtnAddToken, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var tok in NameTokens)
            {
                bool live = tok.AvailableIn(scope);
                var mi = new MenuItem { Header = tok.Label + (live ? "" : "  (empty in this scope)"), ToolTip = tok.Tip };
                if (!live) mi.Opacity = 0.6;
                mi.Click += (_, _) => AddNameToken(tok.Key);
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        }

        /// <summary>Append a token, with a "_" between it and whatever came before.</summary>
        private void AddNameToken(string key)
        {
            NormalizeNameSegs();
            var tail = _nameSegs[_nameSegs.Count - 1];
            bool needSep = _nameSegs.Count > 1 && tail.Value.Length == 0
                           || tail.Value.Length > 0 && !"_-. ".Contains(tail.Value[tail.Value.Length - 1]);
            if (needSep) tail.Value += "_";
            _nameSegs.Add(new NameSeg { IsToken = true, Value = key });
            BuildNameEditor();
        }

        private void WriteNameTemplate()
        {
            var sb = new StringBuilder();
            foreach (var s in _nameSegs) sb.Append(s.IsToken ? "{" + s.Value + "}" : s.Value);
            _nameWriting = true;
            try { TxtFileName.Text = sb.ToString(); }
            finally { _nameWriting = false; }
            UpdateNamePreview();
        }

        /// <summary>Re-dim the chips and refresh the preview after the scope or selection changed.</summary>
        private void RefreshNameEditor()
        {
            int scope = ScopeIndex();
            foreach (var chip in NameChips.Children.OfType<Border>())
                if (chip.Tag is int i && i < _nameSegs.Count)
                {
                    var tok = NameTokens.FirstOrDefault(k => k.Key == _nameSegs[i].Value);
                    chip.Opacity = tok == null || tok.AvailableIn(scope) ? 1 : 0.55;
                }
            UpdateNamePreview();
        }

        private void UpdateNamePreview()
        {
            if (LblNamePreview == null) return;
            try
            {
                var doc = NavApp.ActiveDocument;
                string model = doc != null ? ModelBaseName(doc) : "Model";
                string schema = CmbSchema.SelectedIndex == 1 ? "IFC2x3" : "IFC4";
                string? set = ScopeIndex() == 4 ? "SetName" : null;
                string ext = ChkZip.IsChecked == true ? "ifczip" : "ifc";
                LblNamePreview.Text = $"→ {FileNameFor(model, schema, set)}.{ext}" + (set != null ? "   (one file per set)" : "");
            }
            catch { LblNamePreview.Text = ""; }
        }

        /// <summary>
        /// The output file name from the template. A batch template with no {set} would write
        /// every set to one name, so {set} is appended in that case. A token with no value in the
        /// current scope is left out, along with the separator it would have left doubled.
        /// </summary>
        private string FileNameFor(string model, string schemaName, string? set)
        {
            string t = (TxtFileName.Text ?? "").Trim();
            if (t.Length == 0) t = set != null ? "{set}_{schema}" : DefaultNameTemplate;
            if (set != null && t.IndexOf("{set}", StringComparison.OrdinalIgnoreCase) < 0) t += "_{set}";

            int scope = ScopeIndex();
            if (set == null && scope == 3) set = CmbSavedSet.SelectedItem as string;
            var now = DateTime.Now;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["model"] = model,
                ["project"] = Def(TxtProject.Text, model),
                ["schema"] = schemaName,
                ["set"] = set ?? "",
                ["element"] = scope == 1 ? SelectedElementName() : "",
                ["scope"] = new[] { "model", "selection", "section-box", "set", "batch" }[Math.Max(0, Math.Min(4, scope))],
                ["date"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["time"] = now.ToString("HHmm", CultureInfo.InvariantCulture),
                ["user"] = Environment.UserName,
            };
            string name = Regex.Replace(t, @"\{(\w+)\}", m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
            // An empty token leaves "a__b" or a trailing "_": tidy those, not the user's own text.
            name = Regex.Replace(name, @"([_\-. ])\1+", "$1").Trim('_', '-', '.', ' ');
            if (name.Length == 0) name = model;
            return Sanitize(name);
        }

        private static string SelectedElementName()
        {
            try
            {
                var sel = NavApp.ActiveDocument?.CurrentSelection?.SelectedItems;
                if (sel == null || sel.Count == 0) return "";
                string first = sel.First?.DisplayName ?? "";
                return sel.Count > 1 ? $"{first}+{sel.Count - 1}" : first;
            }
            catch { return ""; }
        }
    }
}
