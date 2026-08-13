using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace BIMCamel.UI
{
    /// <summary>A ticked item in one of the WPF check-lists (property sets, batch sets).</summary>
    public sealed class CheckItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        private bool _checked;
        public bool Checked { get => _checked; set { _checked = value; OnChanged(nameof(Checked)); } }

        public CheckItem() { }
        public CheckItem(string name, bool isChecked) { Name = name; _checked = isChecked; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// One row of the parameter-mapping grid. The list of source properties depends on the chosen
    /// source category, so each row computes its own <see cref="AvailableProps"/> via the shared
    /// <see cref="Resolver"/>. Shared catalogs are static so the grid's "new row" (parameterless
    /// ctor) picks them up too.
    /// </summary>
    public sealed class ParamRow : INotifyPropertyChanged
    {
        public static Func<string, IEnumerable<string>>? Resolver;
        public static readonly ObservableCollection<string> SharedCategories = new ObservableCollection<string>();
        public static readonly ObservableCollection<string> SharedPsets = new ObservableCollection<string>();
        public static readonly ObservableCollection<string> SharedParamNames = new ObservableCollection<string>();

        public ObservableCollection<string> Categories => SharedCategories;
        public ObservableCollection<string> Psets => SharedPsets;
        public ObservableCollection<string> ParamNames => SharedParamNames;
        public ObservableCollection<string> AvailableProps { get; } = new ObservableCollection<string>();

        private string _cat = "";
        public string SourceCategory
        {
            get => _cat;
            set { _cat = value ?? ""; OnChanged(nameof(SourceCategory)); RefreshProps(); }
        }

        private string _prop = "";
        public string SourceProperty
        {
            get => _prop;
            set { _prop = value ?? ""; OnChanged(nameof(SourceProperty)); }
        }

        public string TargetPset { get; set; } = "";
        public string TargetName { get; set; } = "";

        private void RefreshProps()
        {
            AvailableProps.Clear();
            var names = Resolver?.Invoke(_cat) ?? Enumerable.Empty<string>();
            foreach (var n in names) AvailableProps.Add(n);
        }

        /// <summary>Recompute the available source-property list (after a fresh scan).</summary>
        public void Refresh() => RefreshProps();

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>One row of the set → IFC-class mapping grid.</summary>
    public sealed class MapRow : INotifyPropertyChanged
    {
        public static readonly ObservableCollection<string> SharedSets = new ObservableCollection<string>();
        public static readonly ObservableCollection<string> SharedClasses = new ObservableCollection<string>();

        public ObservableCollection<string> Sets => SharedSets;
        public ObservableCollection<string> Classes => SharedClasses;

        private string _set = "", _cls = "", _predef = "", _classif = "";

        public string Set { get => _set; set { _set = value ?? ""; OnChanged(nameof(Set)); } }
        public string IfcClass { get => _cls; set { _cls = value ?? ""; OnChanged(nameof(IfcClass)); } }
        public string Predefined { get => _predef; set { _predef = value ?? ""; OnChanged(nameof(Predefined)); } }

        /// <summary>
        /// Classification code applied to every element in this set. A Navisworks search set is
        /// already an IF/THEN rule, so this column turns the existing set mapping into the rule
        /// engine — "IF Category = Walls AND Name contains External THEN Uniclass = …" — without
        /// inventing a second rule language. Overrides the Classification property role.
        /// </summary>
        public string Classification { get => _classif; set { _classif = value ?? ""; OnChanged(nameof(Classification)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
