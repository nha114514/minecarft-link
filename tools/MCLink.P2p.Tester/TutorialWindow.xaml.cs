using System.Windows;

namespace MCLink.P2p.Tester;

public partial class TutorialWindow : Window
{
    public TutorialWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
