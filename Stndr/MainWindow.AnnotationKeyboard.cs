using Avalonia.Input;

namespace Stndr;

public partial class MainWindow
{
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && TryHandleReaderAnnotationShortcut(e))
        {
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }
}
