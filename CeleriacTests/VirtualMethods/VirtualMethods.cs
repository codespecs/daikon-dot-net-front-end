using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Repro1
{
  public interface IMyList<T,Q> : IList<T>
  {
    void TrimExcess();
  }

  public interface INotListSubInterface
  {
    bool IsNewData { get; set; }
  }

  public class MyOtherList<T> : List<T>, IList<T>, INotListSubInterface
  {
    public bool IsNewData { get; set; }
  }

  public class MyList<T,Q> : List<T>, IList<T>, IMyList<T,Q>
  {
    void IMyList<T,Q>.TrimExcess()
    {
      // NOP
    }
  }

  public class ClientClass<T,Q>
  {
    public IMyList<T, Q> GetList()
    {
      var r = new MyList<T, Q>();
      return r;
    }

  }

  class Program
  {
    static void Main(string[] args)
    {
      var y = new ClientClass<string, string>();
      y.GetList();

      var x = new MyList<string, string>();
      x.Add("Hello");
      x.Add("World!");
      x.TrimExcess();
      ((IMyList<string, string>)x).TrimExcess();

      var z = new MyOtherList<string>();
      z.Add("Hello");
      z.Add("World!");
      z.IsNewData = true;
    }
  }
}
