using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenUtau.App.ViewModels;
using OpenUtau.Core;
using OpenUtau.Core.Util;

namespace OpenUtau.App.Views {
    public partial class ReportIssueDialog : Window {
        public ReportIssueDialog() {
            InitializeComponent();
        }

        void OnOpenIssues(object? sender, RoutedEventArgs e) {
            try {
                OS.OpenWeb(UpdaterViewModel.IssuesUrl);
            } catch (Exception ex) {
                DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(ex));
            }
        }

        void OnOpenLogs(object? sender, RoutedEventArgs e) {
            try {
                OS.OpenFolder(PathManager.Inst.LogsPath);
            } catch (Exception ex) {
                DocManager.Inst.ExecuteCmd(new ErrorMessageNotification(ex));
            }
        }

        void OnClose(object? sender, RoutedEventArgs e) => Close();
    }
}
