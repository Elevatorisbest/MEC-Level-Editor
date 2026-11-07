using Frosty.Core;
using Frosty.Core.Windows;
using LevelEditorPlugin.Diagnostics;
using System;
using System.ComponentModel;
using System.Windows;
using RelayCommand = Frosty.Core.RelayCommand;

namespace LevelEditorPlugin.Editors;

/// <summary>
/// Dockable panel: runs the reference validator over the project's modified assets and shows the
/// result. The same check also runs automatically before Launch and mod export.
/// </summary>
public class ValidationViewModel : Controls.IDockableItem, INotifyPropertyChanged
{
    public const string Uid = "UID_LevelEditor_Validation";
    public const string IconPath = "Images/Properties.png";

    public string Header => "Validation";
    public string UniqueId => Uid;
    public string Icon => IconPath;

    public string ReportText
    {
        get => reportText;
        private set
        {
            if (reportText != value)
            {
                reportText = value;
                NotifyPropertyChanged();
            }
        }
    }

    public string Summary
    {
        get => summary;
        private set
        {
            if (summary != value)
            {
                summary = value;
                NotifyPropertyChanged();
            }
        }
    }

    public RelayCommand RunCommand { get; }
    public RelayCommand CopyCommand { get; }

    private string reportText;
    private string summary;

    public ValidationViewModel()
    {
        RunCommand = new RelayCommand(_ => Run());
        CopyCommand = new RelayCommand(_ =>
        {
            try { Clipboard.SetText(ReportText ?? ""); }
            catch (Exception ex) { LevelEditorLog.Warning("Validation", "Could not copy to clipboard", ex); }
        });

        ReferenceValidator.Completed += r => Application.Current?.Dispatcher.BeginInvoke(new Action(() => Show(r)));
        if (ReferenceValidator.Last != null)
            Show(ReferenceValidator.Last);
        else
        {
            Summary = "Not run yet";
            ReportText = "Press 'Run validation' to check the project's modified assets for broken references.\n\nIt also runs automatically before Launch and mod export.";
        }
    }

    private void Run()
    {
        FrostyTaskWindow.Show("Validating", "Checking references in modified assets...", task =>
        {
            ReferenceValidator.Run();
        });
    }

    private void Show(ValidationResult result)
    {
        Summary = result.Errors == 0 && result.Warnings == 0
            ? $"OK - no problems ({result.AssetsChecked} modified assets)"
            : $"{result.Errors} error(s), {result.Warnings} warning(s)";
        ReportText = result.Format();
    }

    #region -- INotifyPropertyChanged --
    public event PropertyChangedEventHandler PropertyChanged;
    private void NotifyPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    #endregion
}
