using System.Windows;
using System.Windows.Input;

namespace LootPulse
{
    public partial class PobImportWindow : Window
    {
        public string ShareCode { get; private set; } = string.Empty;

        public PobImportWindow()
        {
            InitializeComponent();
            ShareCodeBox.Focus();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            ShareCode = ShareCodeBox.Text;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
