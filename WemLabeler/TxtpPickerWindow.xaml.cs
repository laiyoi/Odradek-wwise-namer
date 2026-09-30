using System.Windows;
using System.Windows.Input;

namespace WemLabeler;

/// <summary>候选 txtp：Path 为完整路径，Display 为展示用的文件名。</summary>
public sealed record TxtpChoice(string Path, string Display)
{
    public override string ToString() => Display;
}

/// <summary>当同一个 WEM 被多个 txtp 引用时，让用户挑选要播放/解析的那一个。</summary>
public partial class TxtpPickerWindow : Window
{
    /// <summary>用户选中的候选。</summary>
    public TxtpChoice? SelectedChoice { get; private set; }

    public TxtpPickerWindow(string header, IEnumerable<TxtpChoice> candidates)
    {
        InitializeComponent();
        Title = Locale.S("dlg_txtp_pick_title");
        HintText.Text = header;
        OkButton.Content = Locale.S("btn_ok");
        CancelButton.Content = Locale.S("btn_cancel");
        foreach (var c in candidates) CandidateList.Items.Add(c);
        if (CandidateList.Items.Count > 0) CandidateList.SelectedIndex = 0;
    }

    private void Accept()
    {
        if (CandidateList.SelectedItem is TxtpChoice selected)
        {
            SelectedChoice = selected;
            DialogResult = true;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Accept();

    private void CandidateList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();
}
