using EmilStefanov;
using Microsoft.Cci;
using Microsoft.Cci.ILToCodeModel;
using Microsoft.Cci.MutableCodeModel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DotNetFrontEnd.Comparability;

namespace Comparability
{
  class Program
  {
    static IAssembly LoadAssembly(string assemblyName, IMetadataHost host)
    {
      IModule module = host.LoadUnitFrom(assemblyName) as IModule;

      Debug.Assert(module != null || module != Dummy.Module || module != Dummy.Assembly, "Failed to load the module...");

      IAssembly assembly = module as IAssembly;
      Debug.Assert(module != null);

      return assembly;
    }

    static void Main(string[] args)
    {
      string assemblyName = args[0];
      string pdbName = args[1];

      using (var host = new PeReader.DefaultHost())
      {
        IAssembly assembly = LoadAssembly(assemblyName, host);

        Assembly decompiled;
        AssemblyComparability assemblyCmp = null;

        using (var f = File.OpenRead(pdbName))
        {
          using (var pdbReader = new PdbReader(f, host))
          {
            decompiled = Decompiler.GetCodeModelFromMetadataModel(host, assembly, pdbReader);
            //decompiled = new CodeDeepCopier(host).Copy(decompiled);
            assemblyCmp = new AssemblyComparability(decompiled, host, pdbReader);
          }
        }

        foreach (var method in assemblyCmp.MethodComparability.Values)
        {
          var cmp = method.Comparability;
          if (cmp.Keys.Count > 1)
          {
            Console.WriteLine(method.Method.Name);
            foreach (var group in cmp.Keys.GroupBy(n => cmp[n]))
            {
              Console.WriteLine(group.Key + ": " + string.Join(" ", group));
            }
          }
        }
      }
    }
  }
}
