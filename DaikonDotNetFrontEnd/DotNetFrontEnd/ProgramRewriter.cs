﻿//-----------------------------------------------------------------------------
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
using Microsoft.Cci;

namespace DotNetFrontEnd
{
  /// <summary>
  /// Performs insertion of instrumentation calls at entrance and exit of every function in a
  /// executable. Never instantiated, only the rewrite_il() method is used.
  /// </summary>
  public class ProgramRewriter
  {
    #region Constants

    /// <summary>
    /// The environment variable defining where front-end output will go
    /// </summary>
    private static readonly string DaikonEnvVar = "DNFE_OUT";

    /// <summary>
    /// The name of the DLL containing the visitor to insert calls to
    /// </summary>
    public static readonly string VisitorDll = "DotNetFrontEnd.dll";

    #endregion

    // Don't instantiate
    private ProgramRewriter() { }

    /// <summary>
    /// Rewrite program IL with instrumentation calls, returning a MemoryStream, or null if the
    /// program was saved to a file.
    /// Caller must be sure to dispose returned MemoryStream.
    /// </summary>
    /// <param name="frontEndArgs">The args to use in profiling the program</param>
    /// <returns>MemoryStream containing the program to be profiled, with the instrumentation
    /// code added, or null if the saveProgram argument is active.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage",
       "CA2202:Do not dispose objects multiple times"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability",
       "CA2000:Dispose objects before losing scope")]
    public static MemoryStream RewriteProgramIL(FrontEndArgs frontEndArgs,
        TypeManager typeManager)
    {
      Stream resultStream;
      if (frontEndArgs.AssemblyPath == null || frontEndArgs.AssemblyPath.Length < 1)
      {
        throw new FileNotFoundException("No program found at the specified path.");
      }

      if (frontEndArgs.SaveProgram != null)
      {
        resultStream = new FileStream(frontEndArgs.SaveProgram, FileMode.Create);
      }
      else
      {
        resultStream = new MemoryStream();
      }

      using (var host = new PeReader.DefaultHost())
      {
        IModule/*?*/ module = host.LoadUnitFrom(frontEndArgs.AssemblyPath) as IModule;
        if (module == null || module == Dummy.Module || module == Dummy.Assembly)
        {
          throw new ArgumentException("Given path is not a PE file containing a CLR"
            + " assembly, or an error occurred when loading it.", "reflectorArgs.AssemblyPath");
        }

        PdbReader/*?*/ pdbReader = null;
        string pdbFile = Path.ChangeExtension(module.Location, "pdb");
        if (File.Exists(pdbFile))
        {
          using (var pdbStream = File.OpenRead(pdbFile))
          {
            pdbReader = new PdbReader(pdbStream, host);
          }
        }
        else
        {
          // TODO(#25): Figure out how what happens if we can't load the PDB file.
          // It seems to be non-fatal, so print the error and continue.
          Console.Error.WriteLine("Could not load the PDB file for '" + module.Name.Value +
              "' . Proceeding anyway.");
        }
        using (pdbReader)
        {
          ILRewriter mutator = new ILRewriter(host, pdbReader, frontEndArgs, typeManager);

          // Look for the path to the reflector, it's an environment variable, check the user space 
          // first.
          string daikonDir = Environment.GetEnvironmentVariable(DaikonEnvVar,
              EnvironmentVariableTarget.User);
          if (daikonDir == null)
          {
            // If that didn't work check the machine space
            daikonDir = Environment.GetEnvironmentVariable(DaikonEnvVar,
                EnvironmentVariableTarget.Machine);
          }

          if (daikonDir == null)
          {
            // We can't proceed without this
            Console.WriteLine("Must define" + DaikonEnvVar + " environment variable");
            Environment.Exit(1);
          }
          module = mutator.Visit(module, Path.Combine(daikonDir, VisitorDll));

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

          // Need to not pass in a local scope provider until such time as we have one 
          // that will use the mutator to remap things (like the type of a scope 
          // constant) from the original assembly to the mutated one.
          using (var pdbWriter = new PdbWriter(pdbFile, pdbReader))
          {
            PeWriter.WritePeToStream(module, host, resultStream, pdbReader, null,
                pdbWriter);
          }
        }
      }

      if (frontEndArgs.SaveProgram != null)
      {
        // We aren't going to run the program, so no need to return anything
        return null;
      }
      else
      {
        return (MemoryStream)resultStream; // success
      }
    }
  }
}