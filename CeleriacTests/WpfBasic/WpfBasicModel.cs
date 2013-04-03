using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WpfBasic
{
  public class WpfBasicModel
  {
    int counter;

    List<CounterUpdateListener> listeners;

    public WpfBasicModel() 
    {
      this.listeners = new List<CounterUpdateListener>();
    }

    public void AddListener(CounterUpdateListener listener)
    {
      this.listeners.Add(listener);
    }

    public void NotifyListeners()
    {
      this.listeners.ForEach(listener => listener.Update());
    }

    public int Counter
    {
      get { return this.counter; }
      set { this.counter = value; this.NotifyListeners(); }
    }
    
    public void IncrementCounter()
    {
      this.Counter++;
    }

    public void ResetCounter()
    {
      this.Counter = 0;
    }
  }
}
