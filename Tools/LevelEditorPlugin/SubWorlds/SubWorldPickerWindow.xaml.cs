using Frosty.Controls;
using Frosty.Core;
using FrostySdk.Managers.Entries;
using LevelEditorPlugin.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace LevelEditorPlugin.SubWorlds;

/// <summary>
/// Lets the user choose which sub-worlds of a level to load: all sub-worlds of the level by
/// category (story missions grouped per mission with their approach/briefing parts, side
/// missions, gridnodes, …), with search and presets. Shown when a level is opened (see
/// <see cref="PrepareLevelLoad"/>) and from Map &gt; Sub-worlds to Load.
/// </summary>
public partial class SubWorldPickerWindow : FrostyDockableWindow
{
    /// <summary>One row of the tree: a category, a group (mission, job phase, district) or a sub-world.</summary>
    public sealed class Node : INotifyPropertyChanged
    {
        public string Title { get; set; }
        public string ToolTip { get; set; }
        public FontWeight TitleWeight { get; set; } = FontWeights.Normal;
        public SubWorldInfo Item { get; set; }
        public SubWorldCategoryInfo CategoryInfo { get; set; }
        public Node Parent { get; set; }
        public ObservableCollection<Node> Children { get; } = new ObservableCollection<Node>();

        private bool? isChecked = true;
        private bool isExpanded;
        private Visibility visibility = Visibility.Visible;
        private string detail = "";

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action CheckChanged;

        public bool? IsChecked
        {
            get => isChecked;
            set => SetChecked(value ?? false, true, true);
        }
        public bool IsExpanded
        {
            get => isExpanded;
            set { if (isExpanded != value) { isExpanded = value; Raise(nameof(IsExpanded)); } }
        }
        public Visibility Visibility
        {
            get => visibility;
            set { if (visibility != value) { visibility = value; Raise(nameof(Visibility)); } }
        }
        public string Detail
        {
            get => detail;
            set { if (detail != value) { detail = value; Raise(nameof(Detail)); } }
        }

        public IEnumerable<Node> Leaves()
        {
            if (Item != null)
                yield return this;
            foreach (Node child in Children)
                foreach (Node leaf in child.Leaves())
                    yield return leaf;
        }

        internal void SetChecked(bool value, bool updateChildren, bool updateParent)
        {
            if (updateChildren)
            {
                foreach (Node child in Children)
                    child.SetChecked(value, true, false);
            }
            bool? newValue = Children.Count > 0 ? Combined() : value;
            if (isChecked != newValue)
            {
                isChecked = newValue;
                Raise(nameof(IsChecked));
            }
            if (updateParent)
                Parent?.Refresh();
            UpdateDetail();
            if (updateParent)
                Root().CheckChanged?.Invoke();
        }

        private void Refresh()
        {
            bool? newValue = Combined();
            if (isChecked != newValue)
            {
                isChecked = newValue;
                Raise(nameof(IsChecked));
            }
            UpdateDetail();
            Parent?.Refresh();
        }

        private bool? Combined()
        {
            bool any = false, all = true;
            foreach (Node child in Children)
            {
                if (child.isChecked != false) any = true;
                if (child.isChecked != true) all = false;
            }
            return all ? true : any ? (bool?)null : false;
        }

        internal void UpdateDetail()
        {
            if (Children.Count == 0)
                return;
            List<Node> leaves = Leaves().ToList();
            int on = leaves.Count(l => l.isChecked == true);
            Detail = $"{on} of {leaves.Count}";
        }

        private Node Root() => Parent == null ? this : Parent.Root();

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly bool openingLevel;
    private string levelName;
    private List<SubWorldInfo> items = new List<SubWorldInfo>();
    private readonly ObservableCollection<Node> roots = new ObservableCollection<Node>();
    private bool ready;

    /// <summary>Sub-worlds the user chose to leave out (valid after OK).</summary>
    public HashSet<string> Excluded { get; private set; }
    /// <summary>Loaded mission and side content to show in the view (valid after OK).</summary>
    public HashSet<string> Shown { get; private set; }

    private SubWorldPickerWindow(string level, bool atLevelOpen)
    {
        InitializeComponent();
        openingLevel = atLevelOpen;
        Owner = Application.Current?.MainWindow;
        TreeView_SubWorlds.ItemsSource = roots;
        CheckBox_Ask.IsChecked = SubWorldLoadFilter.AskOnOpen;
        CheckBox_Show.IsChecked = SubWorldLoadFilter.ShowOptionalContent;

        if (atLevelOpen)
        {
            Panel_Level.Visibility = Visibility.Collapsed;
            Text_LevelName.Text = "Opening " + level;
            Button_Ok.Content = "Load";
            Button_Cancel.Content = "Load everything";
            Button_Cancel.ToolTip = "Load every sub-world this time, without changing the saved choice.";
            Load(level);
        }
        else
        {
            Text_LevelName.Visibility = Visibility.Collapsed;
            Button_Ok.Content = "Save";
            List<string> levels = App.AssetManager.EnumerateEbx("LevelData").Select(e => e.Name)
                .Concat(App.AssetManager.EnumerateEbx("DetachedSubWorldData").Select(e => e.Name))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            ComboBox_Level.ItemsSource = levels;
            string initial = levels.FirstOrDefault(l => string.Equals(l, level, StringComparison.OrdinalIgnoreCase))
                ?? levels.FirstOrDefault(l => SubWorldCatalog.ForLevel(l, AllSubWorldNames()).Count > 0)
                ?? levels.FirstOrDefault();
            ComboBox_Level.SelectedItem = initial; // loads it
        }
        ready = true;
    }

    private static List<string> allNames;
    private static List<string> AllSubWorldNames()
    {
        if (allNames == null)
            allNames = App.AssetManager.EnumerateEbx("SubWorldData").Select(e => e.Name).ToList();
        return allNames;
    }

    /// <summary>
    /// Called when a level is opened: shows the picker if the level has sub-worlds and asking is
    /// on, otherwise uses the saved choice. Returns the sub-worlds to leave out and the ones to show.
    /// </summary>
    public static void PrepareLevelLoad(string level, out HashSet<string> excluded, out HashSet<string> shown)
    {
        excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            SubWorldLoadFilter.LastLevel = level;
            List<SubWorldInfo> list = SubWorldCatalog.ForLevel(level, AllSubWorldNames());
            if (list.Count == 0)
                return;

            if (SubWorldLoadFilter.AskOnOpen || !SubWorldLoadFilter.HasSavedChoice(level))
            {
                SubWorldPickerWindow window = new SubWorldPickerWindow(level, true);
                if (window.ShowDialog() == true)
                {
                    excluded = window.Excluded;
                    shown = window.Shown;
                }
                else
                {
                    // "Load everything" (or the window was closed): everything this time
                    LevelEditorLog.Info("SubWorlds", $"{level}: loading every sub-world (chosen when opening)");
                }
                return;
            }

            excluded = SubWorldLoadFilter.LoadExcluded(level);
            shown = ShownFor(list, excluded);
            LevelEditorLog.Info("SubWorlds", $"{level}: using the saved sub-world choice ({excluded.Count} left out)");
        }
        catch (Exception ex)
        {
            LevelEditorLog.Warning("SubWorlds", "Could not prepare the sub-world choice; loading everything", ex);
            excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Map &gt; Sub-worlds to Load.</summary>
    public static void ShowSettings()
    {
        try
        {
            new SubWorldPickerWindow(SubWorldLoadFilter.LastLevel, false).ShowDialog();
        }
        catch (Exception ex)
        {
            LevelEditorLog.Warning("SubWorlds", "Could not open the sub-world picker", ex);
            FrostyMessageBox.Show("Could not open the sub-world picker: " + ex.Message, "Sub-worlds to Load");
        }
    }

    private static HashSet<string> ShownFor(List<SubWorldInfo> list, HashSet<string> excluded)
    {
        HashSet<string> shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!SubWorldLoadFilter.ShowOptionalContent)
            return shown;
        foreach (SubWorldInfo item in list)
        {
            if (SubWorldCatalog.Info(item.Category).IsOptionalContent && !excluded.Contains(item.AssetName))
                shown.Add(item.AssetName);
        }
        return shown;
    }

    private void Load(string level)
    {
        levelName = level;
        items = SubWorldCatalog.ForLevel(level, AllSubWorldNames());
        roots.Clear();

        HashSet<string> excluded = SubWorldLoadFilter.LoadExcluded(level);
        bool saved = SubWorldLoadFilter.HasSavedChoice(level);

        foreach (SubWorldCategoryInfo category in SubWorldCatalog.Categories)
        {
            List<SubWorldInfo> inCategory = items.Where(i => i.Category == category.Category).ToList();
            if (inCategory.Count == 0)
                continue;

            Node root = new Node
            {
                Title = category.Title,
                ToolTip = category.Description,
                TitleWeight = FontWeights.SemiBold,
                CategoryInfo = category,
                IsExpanded = true,
            };
            Node currentGroup = null;
            foreach (SubWorldInfo item in inCategory)
            {
                Node parent = root;
                if (item.Group != null)
                {
                    if (currentGroup == null || currentGroup.Title != item.Group)
                    {
                        currentGroup = new Node { Title = item.Group, Parent = root, ToolTip = category.Title + ": " + item.Group };
                        root.Children.Add(currentGroup);
                    }
                    parent = currentGroup;
                }
                // first time: "City only"; afterwards the saved choice
                bool on = saved ? !excluded.Contains(item.AssetName) : !category.IsOptionalContent;
                Node leaf = new Node
                {
                    Title = item.ShortName,
                    ToolTip = item.AssetName,
                    Item = item,
                    Parent = parent,
                };
                leaf.SetChecked(on, false, false);
                parent.Children.Add(leaf);
            }
            RefreshAll(root);
            root.CheckChanged += UpdateSummary;
            roots.Add(root);
        }

        ApplySearch();
        UpdateSummary();
    }

    /// <summary>Recomputes the ticks and counts of every group from its sub-worlds.</summary>
    private static void RefreshAll(Node node)
    {
        foreach (Node child in node.Children)
            RefreshAll(child);
        if (node.Children.Count > 0)
            node.SetChecked(false, false, false); // a group takes its state from its children
    }

    private IEnumerable<Node> AllLeaves() => roots.SelectMany(r => r.Leaves());

    private void UpdateSummary()
    {
        List<Node> leaves = AllLeaves().ToList();
        int on = leaves.Count(l => l.IsChecked == true);
        Text_Summary.Text = $"Loading {on} of {leaves.Count} sub-worlds. The {CountGeometry()} zone-streamed city pieces follow their root under City geometry.";
    }

    private int CountGeometry()
    {
        string folder = SubWorldCatalog.LevelFolder(levelName ?? "");
        return AllSubWorldNames().Count(n => folder.Length > 0 && n.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
            && SubWorldCatalog.ShortNameOf(n).IndexOf("_ZS_", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private void SetAll(Func<SubWorldInfo, bool> on)
    {
        foreach (Node root in roots)
        {
            foreach (Node leaf in root.Leaves())
                leaf.SetChecked(on(leaf.Item), false, false);
            RefreshAll(root);
        }
        UpdateSummary();
    }

    private void ApplySearch()
    {
        string[] words = (TextBox_Search.Text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (Node root in roots)
        {
            bool rootVisible = false;
            foreach (Node child in root.Children)
            {
                bool childVisible;
                if (child.Item != null)
                {
                    childVisible = Matches(words, root.Title, child.Title, child.ToolTip);
                }
                else
                {
                    childVisible = false;
                    foreach (Node leaf in child.Children)
                    {
                        bool v = Matches(words, root.Title, child.Title, leaf.Title, leaf.ToolTip);
                        leaf.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
                        childVisible |= v;
                    }
                    child.IsExpanded = words.Length > 0 && childVisible;
                }
                child.Visibility = childVisible ? Visibility.Visible : Visibility.Collapsed;
                rootVisible |= childVisible;
            }
            root.Visibility = rootVisible ? Visibility.Visible : Visibility.Collapsed;
            if (words.Length > 0)
                root.IsExpanded = rootVisible;
        }
    }

    private static bool Matches(string[] words, params string[] texts)
    {
        foreach (string word in words)
        {
            if (!texts.Any(t => t != null && t.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                return false;
        }
        return true;
    }

    private void Commit()
    {
        Excluded = new HashSet<string>(AllLeaves().Where(l => l.IsChecked != true).Select(l => l.Item.AssetName), StringComparer.OrdinalIgnoreCase);
        SubWorldLoadFilter.SaveExcluded(levelName, Excluded);
        SubWorldLoadFilter.AskOnOpen = CheckBox_Ask.IsChecked == true;
        SubWorldLoadFilter.ShowOptionalContent = CheckBox_Show.IsChecked == true;
        Shown = ShownFor(items, Excluded);
        LevelEditorLog.Info("SubWorlds", $"{levelName}: saved sub-world choice, {items.Count - Excluded.Count} of {items.Count} loaded");
    }

    private void Button_Ok_Click(object sender, RoutedEventArgs e)
    {
        if (levelName == null)
        {
            Close();
            return;
        }
        Commit();
        DialogResult = true;
        if (!openingLevel)
            FrostyMessageBox.Show("Saved. The choice is used the next time this level is opened.", "Sub-worlds to Load");
    }

    private void Button_Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ComboBox_Level_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ComboBox_Level.SelectedItem is string level)
            Load(level);
    }

    private void TextBox_Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ready)
            ApplySearch();
    }

    private void Button_ClearSearch_Click(object sender, RoutedEventArgs e) => TextBox_Search.Text = "";
    private void Button_Everything_Click(object sender, RoutedEventArgs e) => SetAll(i => true);
    private void Button_CityOnly_Click(object sender, RoutedEventArgs e) => SetAll(i => !SubWorldCatalog.Info(i.Category).IsOptionalContent);
    private void Button_None_Click(object sender, RoutedEventArgs e) => SetAll(i => false);

    private void Button_Expand_Click(object sender, RoutedEventArgs e) => SetExpanded(true);
    private void Button_Collapse_Click(object sender, RoutedEventArgs e) => SetExpanded(false);

    private void SetExpanded(bool expanded)
    {
        foreach (Node root in roots)
        {
            root.IsExpanded = expanded;
            foreach (Node child in root.Children)
                child.IsExpanded = expanded;
        }
    }
}
