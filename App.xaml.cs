using System.Configuration;
using System.Data;
using System.Windows;

namespace IrisPxS;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--verify"))
        {
            await TestRunner.RunVerificationAsync();
            Shutdown(0);
        }
    }
}

