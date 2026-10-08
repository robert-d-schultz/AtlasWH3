using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AtlasWH3.App.Scene;

/// <summary>Scene editor hooks for the terrain tools (<see cref="TerrainToolsPanel"/>): a tab next to the inspector, the
/// terrain undo keys while that tab is shown, [ / ] brush size, and a prompt before unsaved terrain edits are dropped.</summary>
public sealed partial class SceneWindow
{
    private TerrainToolsPanel? _terrainTools;
    private TerrainToolsPanel TerrainTools => _terrainTools ??= new TerrainToolsPanel(_paths, _view, _view3d);
    private TabControl? _rightTabs;

    /// <summary>The right-hand column: inspector and terrain tools as tabs.</summary>
    private UIElement RightPanel(UIElement inspector)
    {
        var tabs = _rightTabs = new TabControl { Background = Theme.Brush("Panel"), BorderThickness = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = "Inspector", Content = inspector.Spot("scene.inspector") });
        tabs.Items.Add(new TabItem
        {
            Header = "Terrain & trees", Content = TerrainTools,
            ToolTip = "Edit the kit's land / sea height maps and the CampaignTree map (brushes, fills, undo, save)",
        }.Spot("scene.terrainTab"));
        tabs.Items.Add(new TabItem { Header = "Props", Content = PropTools }.Card("scene.props.add").Spot("scene.propsTab"));
        tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, tabs)) return;
            TerrainTools.IsShown = tabs.SelectedIndex == 1;
        };
        return tabs;
    }

    private void WireTerrainTools()
    {
        TerrainTools.Status += (text, error) => Status(text, error);
        PreviewKeyDown += (_, e) =>
        {
            if (!TerrainTools.IsShown || Keyboard.FocusedElement is TextBox) return;
            var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            e.Handled = e.Key switch
            {
                // while the tab is shown, Ctrl+Z never falls through to the entity journal
                Key.Z when ctrl && !shift => Do(() => TerrainTools.Undo()),
                Key.Y when ctrl => Do(() => TerrainTools.Redo()),
                Key.Z when ctrl && shift => Do(() => TerrainTools.Redo()),
                Key.OemOpenBrackets => Do(() => TerrainTools.ScaleRadius(1 / 1.25)),
                Key.OemCloseBrackets => Do(() => TerrainTools.ScaleRadius(1.25)),
                _ => false,
            };
        };
        TerrainTools.DirtyChanged += UpdateTitle;
        Closing += (_, e) =>
        {
            // a value typed into the inspector but not yet committed (Enter / focus loss) is saved first
            if (Keyboard.FocusedElement is TextBox or ComboBox) Keyboard.ClearFocus();
            if (!TerrainTools.ConfirmDiscard(this)) e.Cancel = true;
        };
        static bool Do(Action a) { a(); return true; }
    }

    /// <summary>Title: project, database and type, with a leading * while Terrain &amp; trees edits are unsaved (entity
    /// edits are written to their layer files at once, so they never leave the scene unsaved).</summary>
    private void UpdateTitle()
    {
        if (!_loadedOnce) return;
        var dirty = _terrainTools?.HasUnsaved == true ? "*" : "";
        Title = dirty + AppInfo.Title($"Scene — {Path.GetFileName(_model.TerryPath)} ({_model.Project.Database} {_model.Project.ProjectType})");
    }

    /// <summary>File > Save (Ctrl+S): the Terrain &amp; trees edits; entity edits are already on disk.</summary>
    private void SaveAll()
    {
        if (_terrainTools?.HasUnsaved != true) { Status("Nothing to save: entity edits are written as you make them; no unsaved terrain or tree edits."); return; }
        TerrainTools.SaveEdits();
    }
}
