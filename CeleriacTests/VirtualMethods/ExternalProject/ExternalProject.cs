using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ExternalProject
{
  public class ConcreteClass : AbstractClass
  {
    public override bool VirtualMethod()
    {
      return true;
    }

    public override bool VirtualProperty
    {
      get
      {
        return true;
      }
    }
  }

  public abstract class AbstractClass
  {
    public virtual bool VirtualMethod()
    {
      return false;
    }

    public virtual bool VirtualProperty
    {
      get
      {
        return false;
      }
    }
  }
}
