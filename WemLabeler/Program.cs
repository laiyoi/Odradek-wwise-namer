using System.Windows;

namespace WemLabeler;

static class Program
{
    [STAThread]
    static void Main()
    {
        var app = new Application();
        app.Run(new MainWindow());
    }
}
