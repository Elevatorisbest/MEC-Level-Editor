using LevelEditorPlugin.Diagnostics;
using System;
using System.ComponentModel;
using System.Windows;
using RelayCommand = Frosty.Core.RelayCommand;

namespace LevelEditorPlugin.Editors;

/// <summary>
/// Dockable panel showing the most recent load report and where the session log is written.
/// </summary>
public class DiagnosticsViewModel : Controls.IDockableItem, INotifyPropertyChanged
{
    public const string Uid = "UID_LevelEditor_Diagnostics";
    public const string IconPath = "Images/XMLFile.png";

    public string Header => "Diagnostics";
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

    public string LogFilePath => LevelEditorLog.CurrentLogFile ?? "(log file not created yet)";

    public RelayCommand CopyReportCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand RefreshCommand { get; }

    private string reportText;

    public DiagnosticsViewModel()
    {
        LevelEditorLog.EnsureStarted();

        CopyReportCommand = new RelayCommand(_ => CopyReport());
        OpenLogFolderCommand = new RelayCommand(_ => LevelEditorLog.OpenLogFolder());
        RefreshCommand = new RelayCommand(_ => Refresh());

        LoadReport.ReportCompleted += OnReportCompleted;
        Refresh();
    }

    private void OnReportCompleted(LoadReport report)
    {
        Application.Current?.Dispatcher.BeginInvoke(new Action(Refresh));
    }

    private void Refresh()
    {
        LoadReport current = LoadReport.Current;
        LoadReport last = LoadReport.Last;

        if (current != null)
            ReportText = $"Loading {current.AssetName} ...\n\nThe report appears here when loading has finished.";
        else if (last != null)
            ReportText = last.Format();
        else
            ReportText = "No load report yet. Open a level or prefab in the level editor.";

        NotifyPropertyChanged(nameof(LogFilePath));
    }

    private void CopyReport()
    {
        try
        {
            Clipboard.SetText($"Log file: {LogFilePath}\n\n{ReportText}");
        }
        catch (Exception ex)
        {
            LevelEditorLog.Warning("Diagnostics", "Could not copy report to clipboard", ex);
        }
    }

    #region -- INotifyPropertyChanged --
    public event PropertyChangedEventHandler PropertyChanged;
    private void NotifyPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    #endregion
}
