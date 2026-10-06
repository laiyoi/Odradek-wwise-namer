using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WemLabeler;

/// <summary>
/// 只读、无边框、外观和 <see cref="TextBlock"/> 基本一样的一段文字，
/// 但可以用鼠标划选、Ctrl+C 或右键「复制」把内容拿走。
///
/// 「文件信息」「来源关联」里的路径 / WemID / bank / txtp 名字经常要复制出去，
/// TextBlock 是选不中的，所以这些地方统一换成它。
/// </summary>
public class SelectableText : TextBox
{
    public SelectableText()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;

        // 外观对齐 TextBlock：没有边框、没有背景、没有内边距
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        Margin = new Thickness(0);
        MinHeight = 0;
        MinWidth = 0;

        IsTabStop = false;
        Cursor = Cursors.IBeam;
        TextWrapping = TextWrapping.Wrap;
        VerticalAlignment = VerticalAlignment.Top;
        HorizontalAlignment = HorizontalAlignment.Stretch;

        // 右键菜单只留「复制」（只读控件本来也只剩这个有意义）
        var copyItem = new MenuItem { Header = Locale.S("menu_copy") };
        copyItem.Click += (_, _) =>
        {
            try
            {
                if (!string.IsNullOrEmpty(SelectedText)) Clipboard.SetText(SelectedText);
                else if (!string.IsNullOrEmpty(Text)) Clipboard.SetText(Text);
            }
            catch
            {
                // 剪贴板被别的进程占着时忽略
            }
        };
        var menu = new ContextMenu();
        menu.Items.Add(copyItem);
        ContextMenu = menu;
    }
}
