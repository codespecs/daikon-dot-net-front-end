using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace WpfBasic
{
  /// <summary>
  /// Interaction logic for MainWindow.xaml
  /// </summary>
  public partial class MainWindow : Window, CounterUpdateListener
  {
    WpfBasicModel model;

    public MainWindow()
    {
      this.model = App.model;
      this.model.AddListener(this);
      InitializeComponent();
    }

    private void incBtn_Click(object sender, RoutedEventArgs e)
    {
      this.model.IncrementCounter();
    }

    private void resetBtn_Click(object sender, RoutedEventArgs e)
    {
      this.model.ResetCounter();
    }

    #region CounterUpdateListener Members

    void CounterUpdateListener.Update()
    {
      this.incText.Text = model.Counter.ToString();
    }

    #endregion
  }
}
