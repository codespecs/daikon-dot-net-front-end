using OtherAssembly;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CeleriacInterfaceTest
{
    public class Implementation : IExternalInterface
    {
      private int callCnt = 1;

      public int CallCount()
      {
        return ++callCnt;
      }

      [Pure]
      public int PositiveNumber
      {
        get { return callCnt; }
      }

      [Pure]
      public int OtherPositiveNumber
      {
        get { return callCnt; }
      }
    }

    public class Driver
    {
      public void CheckExternalInterface(IExternalInterface ex)
      {
         var x = ex.PositiveNumber;
      }

      public static void Main(string[] AssemblyLoadEventArgs)
      {
        var impl = new Implementation();

        for (int i = 0; i < 100; i++)
        {
          impl.CallCount();
          new Driver().CheckExternalInterface(impl);
          var cnt = impl.PositiveNumber;
          cnt = impl.OtherPositiveNumber;

        }
      }
    }
}
