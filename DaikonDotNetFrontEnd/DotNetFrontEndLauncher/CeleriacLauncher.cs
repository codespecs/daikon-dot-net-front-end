using System;
using Celeriac;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;
using System.Diagnostics;
using Microsoft.Cci;

namespace CeleriacLauncher
{
  /// <summary>
  /// Driver program for the Celeriac .NET front-end for Daikon. Parses command-line args, adds 
  /// instrumentation calls to the program's IL, and then launches program which generates a trace.
  /// </summary>
  class CeleriacLauncher
  {
    static void Main(string[] args)
    {
      var arguments = ProcessArguments(args);
      CeleriacArgs celeriacArgs = arguments.Item1;
      TypeManager typeManager = arguments.Item2;

      // Hold the IL for the program to be profiled in memory if we are running the modified 
      // program directly.
      MemoryStream resultStream = null;
      try
      {
        resultStream = ProgramRewriter.RewriteProgramIL(celeriacArgs, typeManager);
        if (celeriacArgs.EmitNullaryInfo || celeriacArgs.GenerateComparability)
        {
          return;
        }
        else if (celeriacArgs.VerboseMode)
        {
          Console.WriteLine("Rewriting complete");
        }
      }
      catch (InvalidOperationException ex)
      {
        Console.WriteLine(ex.Message);
      }
      //catch (Exception ex)
      //{
      //  if (frontEndArgs.DontCatchExceptions)
      //  {
      //    throw;
      //  }
      //  Console.Error.WriteLine("Exception occurred during IL Rewriting.");
      //  if (frontEndArgs.VerboseMode)
      //  {
      //    Console.Error.WriteLine(ex.Message);
      //    Console.Error.WriteLine(ex.StackTrace);
      //  }
      //  //  Can't proceed any further, so exit here.
      //  return;
      //}

      if (celeriacArgs.SaveAndRun)
      {
        // Do we need to serialize the type manager here?
        ExecuteProgramFromDisk(args, celeriacArgs);
      }
      else if (String.IsNullOrEmpty(celeriacArgs.SaveProgram))
      {
        // Run the program from memory
        Assembly rewrittenAssembly = Assembly.Load(resultStream.ToArray());
        resultStream.Close();
        ExecuteProgramFromMemory(args, celeriacArgs, rewrittenAssembly);
      }
      else
      {
        // Don't execute the program if it should just be saved to disk.
        return;
      }
    }

    /// <summary>
    /// Process the provided Celeriac arguments, creating CeleriacArgs and TypeManager objects
    /// </summary>
    /// <param name="args">Arguments provided to the Celeriac, including program arguments</param>
    /// <returns>FrontEndArgs and TypeManager objects to use during visiting</returns>
    /// <remarks>Dispose of the returned TypeManager yourself if that seems necessary.</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope")]
    private static Tuple<CeleriacArgs, TypeManager> ProcessArguments(string[] args)
    {
      if (args == null || args.Length < 1)
      {
        Console.WriteLine("FrontEndLauncher.exe [arguments] ProgramToBeProfiled");
        Environment.Exit(1);// return;
      }

      CeleriacArgs celeriacArgs = null;
      try
      {
        celeriacArgs = new CeleriacArgs(args);
      }
      catch (ArgumentException ex)
      {
        Console.WriteLine(ex.Message);
        Environment.Exit(1);
      }
      TypeManager typeManager = new TypeManager(celeriacArgs);

      if (!File.Exists(celeriacArgs.AssemblyPath))
      {
        throw new FileNotFoundException("Program " + celeriacArgs.AssemblyPath +
            " does not exist.");
      }

      // The user may just want to get the profiled program, not have it run.
      if (celeriacArgs.SaveProgram != null)
      {
        // Print all the decls in one file
        celeriacArgs.SetDeclExtension();
        return new Tuple<CeleriacArgs, TypeManager>(celeriacArgs, typeManager);
      }

      // Whether to print datatrace file to stdout
      string outputLocation = celeriacArgs.OutputLocation;

      // Delete the existing output file if we aren't appending to the datatrace file.
      // Create directory to the output location if necessary
      if (outputLocation.Contains(Path.DirectorySeparatorChar.ToString()) ||
          outputLocation.Contains(Path.AltDirectorySeparatorChar.ToString()))
      {
        string dirPart = Path.GetDirectoryName(outputLocation);
        Directory.CreateDirectory(dirPart);
      }

      if (!celeriacArgs.DtraceAppend)
      {
        File.Delete(outputLocation);
      }

      // Statically set the arguments for the reflector. 
      Celeriac.VariableVisitor.ReflectionArgs = celeriacArgs;
      Celeriac.VariableVisitor.TypeManager = typeManager;
      if (celeriacArgs.VerboseMode)
      {
        Console.WriteLine("Argument processing complete");
      }
      return new Tuple<CeleriacArgs, TypeManager>(celeriacArgs, typeManager);
    }

    /// <summary>
    /// Load and execute the rewritten program.
    /// </summary>
    /// <param name="args">Arguments for the program</param>
    /// <param name="celeriacArgs">Arguments for Celeriac</param>
    /// <param name="resultStream">Memory stream of the program to be executed</param>
    private static void ExecuteProgramFromMemory(string[] args, CeleriacArgs celeriacArgs,
      Assembly rewrittenAssembly)
    {
      try
      {
        if (celeriacArgs.VerboseMode)
        {
          Console.WriteLine("Loading complete. Starting program.");
        }

        if (rewrittenAssembly.EntryPoint == null)
        {
          Console.Error.WriteLine("Rewritten assembly has no entry point, so cannot be started."
            + " Exiting now.");
          return;
        }

        rewrittenAssembly.EntryPoint.Invoke(null,
          ExtractProgramArguments(args, celeriacArgs, rewrittenAssembly.EntryPoint));

        if (celeriacArgs.VerboseMode)
        {
          Console.WriteLine("Program complete. Exiting Celeriac.");
        }
      }
      catch (BadImageFormatException ex)
      {
        if (celeriacArgs.DontCatchExceptions)
        {
          throw;
        }
        Console.Error.WriteLine("Raw assembly is invalid or of too late a .NET version");
        if (celeriacArgs.VerboseMode)
        {
          Console.Error.WriteLine(ex.StackTrace);
        }
      }
      catch (TargetInvocationException ex)
      {
        if (celeriacArgs.DontCatchExceptions)
        {
          throw;
        }
        Console.Error.WriteLine("Program being profiled threw an exception");
        Console.Error.WriteLine(ex.GetBaseException());
      }
      catch (VariableVisitorException ex)
      {
        if (celeriacArgs.DontCatchExceptions)
        {
          throw;
        }
        Console.Error.WriteLine(ex.Message);
        if (celeriacArgs.VerboseMode)
        {
          Console.Error.WriteLine(ex.InnerException.Message);
          Console.Error.WriteLine(ex.InnerException.StackTrace);
        }
      }
    }

    /// <summary>
    /// Execute the program that has been saved to disk.
    /// </summary>
    /// <param name="args">Args CeleriacLauncher was called with, the arguments for the program will 
    /// be extracted from here</param>
    /// <param name="celeriacArgs">FrontEndArgs object used during instrumentation, the
    /// name of the program to execute will be extracted from here</param>
    private static void ExecuteProgramFromDisk(string[] args, CeleriacArgs celeriacArgs)
    {
      ProcessStartInfo psi = new ProcessStartInfo();
      psi.FileName = celeriacArgs.SaveProgram;
      object[] programArguments = ExtractProgramArguments(args, celeriacArgs);
      if (programArguments != null)
      {
        psi.Arguments = String.Join(" ", programArguments[0]);
      }
      Process p = Process.Start(psi);
      p.WaitForExit();
    }

    /// <summary>
    /// Create an array of arguments to be used by the rewritten assembly, given the arguments
    /// passed to the FrontEndLauncher. Checks that the number of arguments given is a match
    /// to the types expected by the written assembly. Returns null if no parameters are necessary
    /// for the entry assembly, e.g. if it is a library.
    /// </summary>
    /// <param name="args">The arguments given to the Celeriac launcher</param>
    /// <param name="celeriacArgs">Args object used to rewrite assembly</param>
    /// <param name="entryPointInfo">Optional method info for the entry point of the assembly,
    /// if null verification of entry point is skipped and arguments are always copied.</param>
    /// <returns>Array containing a string[] of arguments to be used by the rewritten program
    /// </returns>
    /// <exception cref="Exception">If the arguments after extraction don't match the ones
    /// expected by the entry point of the assembly</exception>
    private static object[] ExtractProgramArguments(string[] args, CeleriacArgs celeriacArgs,
      MethodInfo entryPointInfo = null)
    {
      // First argument relevant to the program to be profiled
      int indexOfFirstArg = celeriacArgs.ProgramArgIndex + 1;

      object[] programArguments = null;

      // Assume the single parameter is an array of strings -- the arguments
      if (entryPointInfo == null ||
          (entryPointInfo.GetParameters().Length == 1 &&
           entryPointInfo.GetParameters()[0].ParameterType.Equals(typeof(string[]))))
      {
        // Pass on arguments to the program, all but the program name
        programArguments = new object[] { new string[args.Length - indexOfFirstArg] };
        Array.Copy(args, indexOfFirstArg, (string[])programArguments[0], 0, args.Length -
          indexOfFirstArg);
      }
      else if (entryPointInfo.GetParameters().Length != 0)
      {
        throw new InvalidOperationException("Unable to execute the program with the given type"
          + " of arguments.");
      }
      return programArguments;
    }
  }
}
