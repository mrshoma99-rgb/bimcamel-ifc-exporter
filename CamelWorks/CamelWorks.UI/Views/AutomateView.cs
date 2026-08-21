using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CamelWorks.Core.Automation;
using CamelWorks.Core.Project;
using CamelWorks.Core.Store;

namespace CamelWorks.UI.Views
{
    /// <summary>The graph canvas.</summary>
    public static class AutomateView
    {
        /// <summary>Build the canvas.</summary>
        public static UIElement Build() => new CanvasScreen().Build();

        /// <summary>
        /// A node graph the user drags together, over the same services the ribbon drives.
        ///
        /// <b>Every position on this canvas is arithmetic, not layout.</b> Ports are drawn at
        /// computed coordinates rather than measured after the fact, because measuring means
        /// redrawing wires from a layout pass, and drawing wires changes the layout — a loop that
        /// shows up as a canvas that flickers forever on somebody else's machine. Fixed row heights
        /// cost a little visual flexibility and buy a canvas that is correct by construction.
        /// </summary>
        private sealed class CanvasScreen
        {
            private const double NodeWidth = 210;
            private const double HeaderHeight = 24;
            private const double SettingHeight = 26;
            private const double PortHeight = 20;
            private const double Dot = 11;

            private readonly Canvas _canvas = new Canvas
            {
                Width = 2400,
                Height = 1500,
                Background = new SolidColorBrush(Color.FromArgb(12, 128, 128, 128)),
            };

            private readonly Canvas _wires = new Canvas { IsHitTestVisible = false };
            private readonly StackPanel _log = Ui.Stack();
            private readonly TextBlock _hint = Ui.Line(string.Empty, 0.7);
            private readonly TextBox _name = Ui.Text("New job", null, 180);

            private readonly Dictionary<string, List<(UIElement Element, double Dx, double Dy)>> _visuals =
                new Dictionary<string, List<(UIElement, double, double)>>(StringComparer.Ordinal);

            private Graph _graph = new Graph();
            private GraphNode? _dragging;
            private Point _grab;
            private (string Node, string Port, PortType Type)? _armed;

            internal UIElement Build()
            {
                var session = Host.Current;

                if (session == null)
                    return Ui.Scroll(Ui.Stack(Ui.Heading("Canvas"), Ui.Sub(Host.NoModel)));

                _canvas.Children.Add(_wires);

                _canvas.MouseMove += OnMove;
                _canvas.MouseLeftButtonUp += (s, e) => { _dragging = null; _canvas.ReleaseMouseCapture(); };

                Seed();
                Redraw();

                var root = new Grid();
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                root.Children.Add(Toolbar(session));

                var scroller = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = _canvas,
                };

                Grid.SetRow(scroller, 1);
                root.Children.Add(scroller);

                var footer = new ScrollViewer
                {
                    MaxHeight = 160,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = _log,
                    Margin = new Thickness(10, 4, 10, 8),
                };

                Grid.SetRow(footer, 2);
                root.Children.Add(footer);

                return root;
            }

            // -------------------------------------------------------------------------------

            private UIElement Toolbar(Session session)
            {
                var kinds = NodeCatalogue.All.Select(k => k.Group + ": " + k.Title).ToList();
                var picked = kinds[0];

                var picker = Ui.Choice(kinds, picked, v => picked = v, 230);

                var add = Ui.Button("Add node", () =>
                {
                    var kind = NodeCatalogue.All.FirstOrDefault(k => k.Group + ": " + k.Title == picked);
                    if (kind == null) return;

                    var node = new GraphNode(_graph.NextId(kind.Id), kind.Id)
                    {
                        X = 40 + (_graph.Nodes.Count % 5) * (NodeWidth + 40),
                        Y = 40 + (_graph.Nodes.Count / 5) * 190,
                    };

                    _graph.Nodes.Add(node);
                    Redraw();
                });

                var run = Ui.Runner("Run", _log, job =>
                {
                    var result = GraphRunner.Run(_graph, new NavGraphHost(session), job.Say);

                    _log.Children.Add(Ui.Line(result.ToString(), 1, true));

                    foreach (var problem in result.Problems) _log.Children.Add(Ui.Problem(problem));

                    foreach (var step in result.Steps)
                        _log.Children.Add(Ui.Line((step.Ran ? "ran  " : "skip ") + step.Title + " — " + step.Outcome,
                            step.Ran ? 0.85 : 0.6));

                    session.Record(ActivityKind.Job, "ran \"" + _name.Text.Trim() + "\": " + result);
                });

                var save = Ui.Button("Save job", () =>
                {
                    _log.Children.Clear();

                    _graph.Name = _name.Text.Trim().Length == 0 ? "New job" : _name.Text.Trim();

                    var section = session.Store.Section(ProjectStore.JobsSection);
                    var items = section["items"].Kind == JsonKind.Array
                        ? section["items"].Items.ToList()
                        : new List<JsonValue>();

                    items.RemoveAll(j => string.Equals(j["name"].AsString(), _graph.Name, StringComparison.OrdinalIgnoreCase));
                    items.Add(_graph.ToJson());

                    section.Set("items", JsonValue.Array(items));

                    _log.Children.Add(session.Store.Save()
                        ? Ui.Line("Saved \"" + _graph.Name + "\" to " + session.Store.Where + ".", 0.8)
                        : Ui.Problem(session.Store.LastSaveProblem ?? "Could not write the project file."));
                });

                var load = Ui.Button("Load job", () =>
                {
                    _log.Children.Clear();

                    var section = session.Store.Section(ProjectStore.JobsSection);

                    var saved = section["items"].Items
                        .FirstOrDefault(j => string.Equals(j["name"].AsString(), _name.Text.Trim(),
                                                           StringComparison.OrdinalIgnoreCase));

                    if (saved == null)
                    {
                        var names = section["items"].Items.Select(j => j["name"].AsString() ?? "?").ToList();

                        _log.Children.Add(names.Count == 0
                            ? Ui.Empty("No jobs saved on this project yet.", "Build one and press Save job.")
                            : Ui.Line("No job called \"" + _name.Text.Trim() + "\". Saved: "
                                      + string.Join(", ", names), 0.8));
                        return;
                    }

                    _graph = Graph.FromJson(saved);
                    _name.Text = _graph.Name;
                    _armed = null;
                    Redraw();

                    _log.Children.Add(Ui.Line("Loaded " + _graph + ".", 0.8));
                });

                var clear = Ui.Button("Empty the canvas", () =>
                {
                    _graph = new Graph(_name.Text.Trim());
                    _armed = null;
                    Redraw();
                });

                var bar = Ui.Stack(
                    Ui.Across(_name, picker, add, run, save, load, clear),
                    _hint);

                bar.Margin = new Thickness(10, 8, 10, 4);
                return bar;
            }

            private void Seed()
            {
                // A canvas that opens empty is a canvas nobody learns anything from. Two wired
                // nodes say what the thing is in less time than a paragraph of help would.
                var all = new GraphNode(_graph.NextId("input.everything"), "input.everything") { X = 50, Y = 60 };
                var where = new GraphNode(_graph.NextId("filter.where"), "filter.where") { X = 320, Y = 60 };
                var colour = new GraphNode(_graph.NextId("view.colour"), "view.colour") { X = 590, Y = 60 };

                _graph.Nodes.Add(all);
                _graph.Nodes.Add(where);
                _graph.Nodes.Add(colour);

                _graph.Connect(new GraphWire(all.Id, "out", where.Id, "in"));
                _graph.Connect(new GraphWire(where.Id, "out", colour.Id, "in"));
            }

            // -------------------------------------------------------------------------------
            // Drawing
            // -------------------------------------------------------------------------------

            private void Redraw()
            {
                _canvas.Children.Clear();
                _canvas.Children.Add(_wires);
                _visuals.Clear();

                foreach (var node in _graph.Nodes) Draw(node);

                DrawWires();
                Hint();
            }

            private void Draw(GraphNode node)
            {
                var definition = node.Definition;
                var parts = new List<(UIElement, double, double)>();

                var body = new StackPanel { Width = NodeWidth };

                body.Children.Add(Header(node, definition));

                foreach (var setting in definition?.Settings ?? Array.Empty<NodeSetting>())
                    body.Children.Add(Setting(node, setting));

                var rows = Math.Max(definition?.Inputs.Count ?? 0, definition?.Outputs.Count ?? 0);

                for (var i = 0; i < rows; i++)
                    body.Children.Add(PortRow(definition, i));

                var border = new Border
                {
                    Background = SystemColors.WindowBrush,
                    BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Child = body,
                };

                Place(border, node.X, node.Y);
                _canvas.Children.Add(border);
                parts.Add((border, 0, 0));

                var top = HeaderHeight + (definition?.Settings.Count ?? 0) * SettingHeight;

                for (var i = 0; i < (definition?.Inputs.Count ?? 0); i++)
                {
                    var port = definition!.Inputs[i];
                    var dx = -Dot / 2;
                    var dy = top + i * PortHeight + PortHeight / 2 - Dot / 2;

                    var dot = PortDot(node, port, isInput: true);
                    Place(dot, node.X + dx, node.Y + dy);
                    _canvas.Children.Add(dot);
                    parts.Add((dot, dx, dy));
                }

                for (var i = 0; i < (definition?.Outputs.Count ?? 0); i++)
                {
                    var port = definition!.Outputs[i];
                    var dx = NodeWidth - Dot / 2;
                    var dy = top + i * PortHeight + PortHeight / 2 - Dot / 2;

                    var dot = PortDot(node, port, isInput: false);
                    Place(dot, node.X + dx, node.Y + dy);
                    _canvas.Children.Add(dot);
                    parts.Add((dot, dx, dy));
                }

                _visuals[node.Id] = parts;
            }

            private UIElement Header(GraphNode node, NodeKind? definition)
            {
                var grid = new Grid { Height = HeaderHeight, Background = Ui.Wash(Tone.Plain) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var title = new TextBlock
                {
                    Text = definition?.Title ?? node.Kind,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(7, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = definition?.Summary,
                };

                grid.Children.Add(title);

                // The handler goes on the whole header rather than on the text, so the drag target
                // is the bar the user is aiming at rather than the glyphs inside it. A transparent
                // brush is hit-testable; a null one is not, which is why the header has one.
                grid.Cursor = Cursors.SizeAll;

                grid.MouseLeftButtonDown += (s, e) =>
                {
                    _dragging = node;
                    _grab = e.GetPosition(_canvas);
                    _canvas.CaptureMouse();
                    e.Handled = true;
                };

                var drop = new TextBlock
                {
                    Text = "x",
                    Margin = new Thickness(0, 0, 7, 0),
                    Opacity = 0.6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand,
                    ToolTip = "Remove this node",
                };

                drop.MouseLeftButtonDown += (s, e) =>
                {
                    _graph.Remove(node.Id);
                    Redraw();
                    e.Handled = true;
                };

                Grid.SetColumn(drop, 1);
                grid.Children.Add(drop);

                return grid;
            }

            private static UIElement Setting(GraphNode node, NodeSetting setting)
            {
                var grid = new Grid { Height = SettingHeight, Margin = new Thickness(6, 0, 6, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                grid.Children.Add(new TextBlock
                {
                    Text = setting.Title,
                    FontSize = 11,
                    Opacity = 0.7,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                UIElement control;

                if (setting.Kind == SettingKind.Choice)
                {
                    var box = new ComboBox { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

                    foreach (var option in setting.Choices) box.Items.Add(option);

                    box.SelectedItem = node.Setting(setting.Id);
                    box.SelectionChanged += (s, e) =>
                    {
                        if (box.SelectedItem is string pick) node.Settings[setting.Id] = pick;
                    };

                    control = box;
                }
                else
                {
                    var box = new TextBox
                    {
                        Text = node.Setting(setting.Id),
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                    };

                    box.TextChanged += (s, e) => node.Settings[setting.Id] = box.Text;
                    control = box;
                }

                Grid.SetColumn(control, 1);
                grid.Children.Add(control);
                return grid;
            }

            private static UIElement PortRow(NodeKind? definition, int index)
            {
                var grid = new Grid { Height = PortHeight, Margin = new Thickness(9, 0, 9, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                if (index < (definition?.Inputs.Count ?? 0))
                    grid.Children.Add(new TextBlock
                    {
                        Text = definition!.Inputs[index].Title,
                        FontSize = 11,
                        Opacity = 0.75,
                        VerticalAlignment = VerticalAlignment.Center,
                    });

                if (index < (definition?.Outputs.Count ?? 0))
                {
                    var right = new TextBlock
                    {
                        Text = definition!.Outputs[index].Title,
                        FontSize = 11,
                        Opacity = 0.75,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                    };

                    Grid.SetColumn(right, 1);
                    grid.Children.Add(right);
                }

                return grid;
            }

            private UIElement PortDot(GraphNode node, NodePort port, bool isInput)
            {
                var armed = _armed != null && _armed.Value.Node == node.Id && _armed.Value.Port == port.Id;

                var dot = new Ellipse
                {
                    Width = Dot,
                    Height = Dot,
                    Fill = armed ? Ui.Ink(Tone.Good) : SystemColors.WindowBrush,
                    Stroke = port.Type == PortType.Table ? Ui.Ink(Tone.Warn) : Ui.Ink(Tone.Plain),
                    StrokeThickness = 1.5,
                    Cursor = Cursors.Cross,
                    ToolTip = port.Title + " (" + port.Type.ToString().ToLowerInvariant() + ")",
                };

                dot.MouseLeftButtonDown += (s, e) =>
                {
                    Click(node, port, isInput);
                    e.Handled = true;
                };

                return dot;
            }

            private void Click(GraphNode node, NodePort port, bool isInput)
            {
                if (!isInput)
                {
                    _armed = (node.Id, port.Id, port.Type);
                    Redraw();
                    return;
                }

                if (_armed == null)
                {
                    // Clicking an input with nothing armed disconnects it. There is nothing else
                    // useful for that click to mean, and a wire you cannot remove is worse than one
                    // you have to learn how to remove.
                    var wire = _graph.Into(node.Id, port.Id);
                    if (wire != null) _graph.Wires.Remove(wire);

                    Redraw();
                    return;
                }

                if (_armed.Value.Type != port.Type)
                {
                    _hint.Text = "Those two do not carry the same thing: elements cannot be wired into "
                                 + "an input that wants a table.";
                    _armed = null;
                    Redraw();
                    return;
                }

                _graph.Connect(new GraphWire(_armed.Value.Node, _armed.Value.Port, node.Id, port.Id));
                _armed = null;
                Redraw();
            }

            private void Hint()
            {
                if (_armed != null)
                {
                    _hint.Text = "Now click an input to wire it up.";
                    return;
                }

                _hint.Text = _graph.Nodes.Count == 0
                    ? "Add a node to start."
                    : "Drag a node by its title. Click an output, then an input, to wire them. "
                      + "Click an input on its own to unwire it.";
            }

            private void DrawWires()
            {
                _wires.Children.Clear();

                foreach (var wire in _graph.Wires)
                {
                    var from = PortPoint(wire.FromNode, wire.FromPort, isInput: false);
                    var to = PortPoint(wire.ToNode, wire.ToPort, isInput: true);

                    if (from == null || to == null) continue;

                    var reach = Math.Max(40, Math.Abs(to.Value.X - from.Value.X) / 2);

                    var figure = new PathFigure { StartPoint = from.Value };

                    figure.Segments.Add(new BezierSegment(
                        new Point(from.Value.X + reach, from.Value.Y),
                        new Point(to.Value.X - reach, to.Value.Y),
                        to.Value,
                        isStroked: true));

                    var geometry = new PathGeometry();
                    geometry.Figures.Add(figure);

                    _wires.Children.Add(new Path
                    {
                        Data = geometry,
                        Stroke = Ui.Ink(Tone.Plain),
                        StrokeThickness = 1.6,
                        Opacity = 0.55,
                    });
                }
            }

            private Point? PortPoint(string nodeId, string portId, bool isInput)
            {
                var node = _graph.Find(nodeId);
                var definition = node?.Definition;
                if (node == null || definition == null) return null;

                var ports = isInput ? definition.Inputs : definition.Outputs;

                var index = -1;
                for (var i = 0; i < ports.Count; i++)
                    if (string.Equals(ports[i].Id, portId, StringComparison.Ordinal)) index = i;

                if (index < 0) return null;

                var top = HeaderHeight + definition.Settings.Count * SettingHeight;

                return new Point(
                    node.X + (isInput ? 0 : NodeWidth),
                    node.Y + top + index * PortHeight + PortHeight / 2);
            }

            private static void Place(UIElement element, double x, double y)
            {
                Canvas.SetLeft(element, x);
                Canvas.SetTop(element, y);
            }

            private void OnMove(object sender, MouseEventArgs e)
            {
                if (_dragging == null || e.LeftButton != MouseButtonState.Pressed) return;

                var at = e.GetPosition(_canvas);

                _dragging.X = Math.Max(0, _dragging.X + (at.X - _grab.X));
                _dragging.Y = Math.Max(0, _dragging.Y + (at.Y - _grab.Y));
                _grab = at;

                if (_visuals.TryGetValue(_dragging.Id, out var parts))
                    foreach (var part in parts)
                        Place(part.Element, _dragging.X + part.Dx, _dragging.Y + part.Dy);

                DrawWires();
            }
        }
    }
}
