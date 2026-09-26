using System.Windows;

namespace SwAiAssistant.AddIn.UI
{
    /// <summary>「继续/新建」二选一对话框结果。</summary>
    public enum DocChoiceResult
    {
        Continue,
        New,
        Cancel
    }

    /// <summary>
    /// 有打开零件时的中文二选一对话框（spec FR-11）。
    /// 当前为 WPF 简易实现，M2 统一视觉风格时替换。
    /// </summary>
    public partial class DocChoiceDialog : Window
    {
        public DocChoiceDialog(string activeDocTitle)
        {
            InitializeComponent();
            MessageText.Text = $"当前已打开零件「{activeDocTitle}」。\n建模命令要在哪里执行？";
            Result = DocChoiceResult.Cancel;
        }

        public DocChoiceResult Result { get; private set; }

        private void ContinueClick(object sender, RoutedEventArgs e)
        {
            Result = DocChoiceResult.Continue;
            DialogResult = true;
            Close();
        }

        private void NewClick(object sender, RoutedEventArgs e)
        {
            Result = DocChoiceResult.New;
            DialogResult = true;
            Close();
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            Result = DocChoiceResult.Cancel;
            Close();
        }
    }
}
