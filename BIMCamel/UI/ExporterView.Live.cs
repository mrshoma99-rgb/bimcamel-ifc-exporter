using System;
using System.Linq;
using System.Windows;
using Autodesk.Navisworks.Api;
using NavApp = Autodesk.Navisworks.Api.Application;

namespace BIMCamel.UI
{
    /// <summary>
    /// Keeps the pane in step with Navisworks. It used to read the document once, when the pane
    /// opened: open another file and the model label, the set lists and the pre-flight numbers
    /// all went on describing the old one, and "Current selection" said "select items first"
    /// over a selection of three hundred.
    /// </summary>
    public partial class ExporterView
    {
        private Document? _boundDoc;
        private bool _docEventsAttached;

        private void AttachDocumentEvents()
        {
            if (_docEventsAttached) return;
            _docEventsAttached = true;
            NavApp.ActiveDocumentChanged += OnActiveDocumentChanged;
            BindDocument(NavApp.ActiveDocument);
        }

        private void DetachDocumentEvents()
        {
            if (!_docEventsAttached) return;
            _docEventsAttached = false;
            NavApp.ActiveDocumentChanged -= OnActiveDocumentChanged;
            BindDocument(null);
        }

        private void BindDocument(Document? doc)
        {
            if (ReferenceEquals(doc, _boundDoc)) return;
            try
            {
                if (_boundDoc != null)
                {
                    _boundDoc.CurrentSelection.Changed -= OnSelectionChanged;
                    _boundDoc.Models.CollectionChanged -= OnModelsChanged;
                }
            }
            catch { }
            _boundDoc = doc;
            try
            {
                if (doc != null)
                {
                    doc.CurrentSelection.Changed += OnSelectionChanged;
                    doc.Models.CollectionChanged += OnModelsChanged;
                }
            }
            catch { }
        }

        private void OnActiveDocumentChanged(object? sender, EventArgs e)
        {
            BindDocument(NavApp.ActiveDocument);
            OnModelsChanged(sender, e);
        }

        /// <summary>A different file, or models appended or removed: every scanned number now
        /// describes something else, so it is dropped rather than shown as stale.</summary>
        private void OnModelsChanged(object? sender, EventArgs e)
        {
            if (!Tabs.IsEnabled) return;   // mid-export: the export owns the pane until it ends
            _preflight = null;
            RefreshModelStatus(); RefreshSets(); UpdateScopeHint(); PreviewBasePoint(); RenderPreflight();
        }

        private void OnSelectionChanged(object? sender, EventArgs e)
        {
            if (!Tabs.IsEnabled || ScopeIndex() != 1) return;
            UpdateScopeHint();
        }

        private static int SelectedCount()
        {
            try { return NavApp.ActiveDocument?.CurrentSelection.SelectedItems.Count ?? 0; }
            catch { return 0; }
        }

        /// <summary>The Export button says what a click will do.</summary>
        private void UpdateExportLabel()
        {
            string schema = CmbSchema.SelectedIndex == 1 ? "IFC2x3" : "IFC4";
            string form = ChkZip.IsChecked == true ? ".ifczip" : ".ifc";
            if (ScopeIndex() == 4)
            {
                int sets = _batchItems.Count(b => b.Checked);
                LblExportBtn.Text = sets == 0 ? "Export — tick sets above" : $"Export {sets} set(s) → {schema} {form}";
            }
            else LblExportBtn.Text = $"Export {schema} {form}";
        }
    }
}
