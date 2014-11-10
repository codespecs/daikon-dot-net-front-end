//-----------------------------------------------------------------------------
//
// Copyright (c) Microsoft, Kellen Donohue. All rights reserved.
// This code is licensed under the Microsoft Public License.
// THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
// ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
// IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
// PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.
//
// This file defines a profiler that inserts instrumentation calls 
// at the beginning and end of every function and returns the modified program
// as a MemoryStream.
//
//-----------------------------------------------------------------------------

// This file and ILRewriter.cs originally came from the CCIMetadata (http://ccimetadata.codeplex.com/) 
// sample programs. Specifically, it was Samples/ILMutator/ILMutator.cs. The original code inserted
// a System.WriteLine call for each store to a local variable defined by the programmer. It has been
// modified to insert the call to the instrumentation function at the beginning and exit of every 
// method. This is mostly done in the ProcessOperations() method, with many support methods added.

using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using Celeriac.Comparability;
using Microsoft.Cci;
using Microsoft.Cci.ILToCodeModel;
using Microsoft.Cci.MutableCodeModel;

namespace Celeriac
{
  /// <summary>
  /// Performs insertion of instrumentation calls at entrance and exit of every function in a
  /// executable. Never instantiated, only the RewriteProgramIL() method is used.
  /// </summary>
  public static class ProgramRewriter
  {
    #region Constants

    /// <summary>
    /// The environment variable defining where Celeriac output will go
    /// </summary>
    private static readonly string DaikonEnvVar = "CELERIAC_HOME";

    /// <summary>
    /// The name of the DLL containing the visitor to insert calls to
    /// </summary>
    public static readonly string VisitorDll = "Celeriac.dll";

    #endregion

    /// <summary>
    /// Rewrite program IL with instrumentation calls, returning a MemoryStream, or null if the
    /// program was saved to a file.
    /// Caller must be sure to dispose returned MemoryStream.
    /// </summary>
    /// <param name="typeManager">The type maanager to use while visiting the program</param>
    /// <param name="celeriacArgs">The args to use in profiling the program</param>
    /// <returns>MemoryStream containing the program to be profiled, with the instrumentation
    /// code added, or null if the saveProgram argument is active, or the program was already
    /// rewritten.</returns>
    /// <exception cref="InvalidOperationException">If the program has already been rewritten with 
    /// Celeriac.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage",
       "CA2202:Do not dispose objects multiple times"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability",
       "CA2000:Dispose objects before losing scope")]
    public static MemoryStream RewriteProgramIL(CeleriacArgs celeriacArgs, TypeManager typeManager)
    {
      if (String.IsNullOrWhiteSpace(celeriacArgs.AssemblyPath))
      {
        throw new FileNotFoundException("Path to program to be profiled not provided");
      }

      Stream resultStream;
      var host = typeManager.Host;

      IModule/*?*/ module = host.LoadUnitFrom(celeriacArgs.AssemblyPath) as IModule;
      if (module == null || module == Dummy.Module || module == Dummy.Assembly)
      {
        throw new FileNotFoundException("Given path is not a PE file containing a CLR"
          + " assembly, or an error occurred when loading it.", celeriacArgs.AssemblyPath);
      }

      if (module.GetAllTypes().Any(
          type => type.Name.ToString().Equals(ILRewriter.ArgumentStoringClassName)))
      {
        throw new InvalidOperationException("Program has already been instrumented.");
      }

      string pdbFile = Path.ChangeExtension(module.Location, "pdb");
      Assembly mutable = null;

      using (var pdbReader = LoadPdbReaderAndFile(celeriacArgs, typeManager, module, pdbFile))
      {
        AssemblySummary comparabilityManager = GenerateComparability(celeriacArgs, typeManager,
          host, module, pdbReader, ref mutable);

        if (celeriacArgs.GenerateComparability && celeriacArgs.ComparabilityFile == null)
        {
          return null;
        }

        ILRewriter mutator = new ILRewriter(host, pdbReader, celeriacArgs, typeManager, comparabilityManager);

        module = mutator.Visit(mutable, Path.Combine(FindVisitorDir(), VisitorDll));

        if (celeriacArgs.EmitNullaryInfo || celeriacArgs.GenerateComparability)
        {
          return null;
        }

        // Remove the old PDB file
        try
        {
          File.Delete(pdbFile);
        }
        catch (UnauthorizedAccessException)
        {
          // If they are running the debugger we might not be able to delete the file
          // Save the pdb elsewhere in this case.
          pdbFile = module.Location + ".pdb";
        }

        if (celeriacArgs.SaveProgram != null)
        {
          resultStream = new FileStream(celeriacArgs.SaveProgram, FileMode.Create);
        }
        else
        {
          resultStream = new MemoryStream();
        }


#if __MonoCS__
        // Reading / Writing DEBUG information on Mono is not supported by CCI
        PdbWriter pdbWriter = null;
#else
        var pdbWriter = new PdbWriter(pdbFile, pdbReader);
#endif

        // Need to not pass in a local scope provider until such time as we have one 
        // that will use the mutator to remap things (like the type of a scope 
        // constant) from the original assembly to the mutated one.
        using (pdbWriter)
        {
          PeWriter.WritePeToStream(module, host, resultStream, pdbReader, null, pdbWriter);
        }
      }

      if (celeriacArgs.SaveProgram != null)
      {
        // We aren't going to run the program, so no need to return anything,
        // but close the file stream.
        resultStream.Close();
        return null;
      }
      else
      {
        return (MemoryStream)resultStream; // success
      }
    }

    /// <summary>
    /// Find the directory the Celeriac DLL is located in, based on envrionmental variables.
    /// </summary>
    /// <returns>Absolute path to the celeriac directory.</returns>
    private static string FindVisitorDir()
    {
#if __MonoCS__
      string daikonDir = Environment.GetEnvironmentVariable(DaikonEnvVar);
#else
      // Look for the path to the reflector, it's an environment variable, check the user space 
      // first.
      string daikonDir = Environment.GetEnvironmentVariable(DaikonEnvVar, EnvironmentVariableTarget.User);
      if (daikonDir == null)
      {
        // If that didn't work check the machine space
        daikonDir = Environment.GetEnvironmentVariable(DaikonEnvVar, EnvironmentVariableTarget.Machine);
      }
#endif

      if (daikonDir == null)
      {
        // We can't proceed without this
        Console.Error.WriteLine("Must define " + DaikonEnvVar + " environment variable");
        Environment.Exit(1);
      }
      return daikonDir;
    }

    /// <summary>
    /// Generate the comparability information for the given assembly.
    /// </summary>
    private static AssemblySummary GenerateComparability(CeleriacArgs celeriacArgs, TypeManager typeManager,
      IMetadataHost host, IModule module, PdbReader pdbReader, ref Assembly mutable)
    {
      AssemblySummary comparabilityManager = null;
      IAssembly assembly = module as IAssembly;
      mutable = MetadataCopier.DeepCopy(host, assembly);
      typeManager.SetAssemblyIdentity(UnitHelper.GetAssemblyIdentity(mutable));

      if (celeriacArgs.StaticComparability || celeriacArgs.GenerateComparability)
      {
        if (celeriacArgs.ComparabilityFile != null)
        {
          using (var cmp = File.Open(celeriacArgs.ComparabilityFile, FileMode.Open))
          {
            comparabilityManager = (AssemblySummary)new BinaryFormatter().Deserialize(cmp);
          }
        }
        else
        {
          if (celeriacArgs.VerboseMode)
          {
            Console.WriteLine("Generating Comparability Information");
          }

          Assembly decompiled = Decompiler.GetCodeModelFromMetadataModel(typeManager.Host, mutable,
            pdbReader, DecompilerOptions.AnonymousDelegates | DecompilerOptions.Iterators);
          comparabilityManager = AssemblySummary.MakeSummary(decompiled, typeManager);

          if (celeriacArgs.VerboseMode)
          {
            Console.WriteLine("Finished Generating Comparability Information");
          }

          using (var cmp = File.Open(celeriacArgs.AssemblyPath + CeleriacArgs.ComparabilityFileExtension, FileMode.Create))
          {
            new BinaryFormatter().Serialize(cmp, comparabilityManager);
          }
        }
      }
      return comparabilityManager;
    }

    /// <summary>
    /// Load the PDB reader and pdb file for the source program.
    /// </summary>
    private static PdbReader LoadPdbReaderAndFile(CeleriacArgs celeriacArgs, TypeManager typeManager, IModule module, string pdbFile)
    {
#if __MonoCS__
      if (celeriacArgs.StaticComparability)
      {
        throw new NotSupportedException("Static comparability analysis is not supported on Mono");
      }
      else
      {
        Console.Error.WriteLine("WARNING: Program database (PDB) information is not supported on Mono");
        Console.Error.WriteLine("Celeriac will attempt to continue, but might fail.");
        return null;
      }
#else
      PdbReader pdbReader = null;
      try
      {
        using (var pdbStream = File.OpenRead(pdbFile))
        {
          pdbReader = new PdbReader(pdbStream, typeManager.Host);
        }
        return pdbReader;
      }
      catch (System.IO.IOException ex)
      {
        if (celeriacArgs.StaticComparability)
        {
          throw new InvalidOperationException("Error loading program database (PDB) file for '" + module.Name.Value + "'. Debug information is required for static comparability analysis.", ex);
        }
        else
        {
          // It seems to be non-fatal, so print the error and continue.  
          Console.Error.WriteLine("WARNING: Could not load the PDB file for '" + module.Name.Value + "'");
          Console.Error.WriteLine("Celeriac will attempt to continue, but might fail.");
        }
        return null;
      }
#endif
    }
  }
}
