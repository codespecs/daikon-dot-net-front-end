// This file defines the driver for the front-end. It parses all command-line
// arguments, then calls the profiler to rewrite the program IL with the
// instrumentation calls. Finally, it executes the rewritten IL.

using System;
using DotNetFrontEnd;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;
using System.Diagnostics;

namespace DotNetFrontEndLauncher
{
  /// <summary>
  /// Driver program for daikon .NET front-end, parses command-line args, adds instrumentation
  /// calls to program's IO, then launches program, which generates datatrace file.
  /// </summary>
  class FrontEndLauncher
  {
    static void Main(string[] args)
    {
      var arguments = ProcessArguments(args);
      FrontEndArgs frontEndArgs = arguments.Item1;
      TypeManager typeManager = arguments.Item2;

      // Hold the IL for the program to be profiled in memory if we are running the modified 
      // program directly.
      MemoryStream resultStream = null;
      //try
      //{
      resultStream = ProgramRewriter.RewriteProgramIL(frontEndArgs, typeManager);
      if (frontEndArgs.VerboseMode)
      {
        Console.WriteLine("Rewriting complete");
      }
      //}
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
      
      if (frontEndArgs.SaveAndRun)
      {
        ExecuteProgramFromDisk(args, frontEndArgs);
      }
      // Don't execute the program if it should just be saved to disk
      else if (String.IsNullOrEmpty(frontEndArgs.SaveProgram))
      {
        Assembly rewrittenAssembly = Assembly.Load(resultStream.ToArray());
        resultStream.Close();
        ExecuteProgramFromMemory(args, frontEndArgs, rewrittenAssembly);
      }
    }
    
    /// <summary>
    /// Process the provided front-end arguments, creating FrontEndArgs and TypeManager objects
    /// </summary>
    /// <param name="args">Arguments provided to the front end, including program arguments</param>
    /// <returns>FrontEndArgs and TypeManager objects to use during visiting</returns>
    private static Tuple<FrontEndArgs, TypeManager> ProcessArguments(string[] args)
    {
      if (args == null || args.Length < 1)
      {
        Console.WriteLine("FrontEndLauncher.exe [arguments] ProgramToBeProfiled");
        Environment.Exit(1);// return;
      }

      FrontEndArgs frontEndArgs = new FrontEndArgs(args);
      TypeManager typeManager = new TypeManager(frontEndArgs);

      if (!File.Exists(frontEndArgs.AssemblyPath))
      {
        throw new FileNotFoundException("Program " + frontEndArgs.AssemblyPath +
            " does not exist.");
      }

      // The user may just want to get the profiled program, not have it run.
      if (frontEndArgs.SaveProgram != null)
      {
        // Print all the decls in one file
        frontEndArgs.SetDeclExtension();
        return new Tuple<FrontEndArgs, TypeManager>(frontEndArgs, typeManager);
      }

      // Whether to print datatrace file to stdout
      string outputLocation = frontEndArgs.OutputLocation;

      // Delete the existing output file if we aren't appending to the datatrace file.
      // Create directory to the output location if necessary
      if (outputLocation.Contains(Path.DirectorySeparatorChar.ToString()) ||
          outputLocation.Contains(Path.AltDirectorySeparatorChar.ToString()))
      {
        string dirPart = Path.GetDirectoryName(outputLocation);
        Directory.CreateDirectory(dirPart);
      }

      if (!frontEndArgs.DtraceAppend)
      {
        File.Delete(outputLocation);
      }

      // Statically set the arguments for the reflector. 
      DotNetFrontEnd.VariableVisitor.SetReflectionArgs(frontEndArgs);
      DotNetFrontEnd.VariableVisitor.SetTypeManager(typeManager);
      if (frontEndArgs.VerboseMode)
      {
        Console.WriteLine("Argument processing complete");
      }
      return new Tuple<FrontEndArgs, TypeManager>(frontEndArgs, typeManager);
    }

    /// <summary>
    /// Load and execute the rewritten program.
    /// </summary>
    /// <param name="args">Arguments for the program</param>
    /// <param name="frontEndArgs">Arguments for the front end</param>
    /// <param name="resultStream">Memory stream of the program to be executed</param>
    private static void ExecuteProgramFromMemory(string[] args, FrontEndArgs frontEndArgs,
      Assembly rewrittenAssembly)
    {
      try
      {
        if (frontEndArgs.VerboseMode)
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
          ExtractProgramArguments(args, frontEndArgs, rewrittenAssembly.EntryPoint));

        if (frontEndArgs.VerboseMode)
        {
          Console.WriteLine("Program complete. Exiting DNFE.");
        }
      }
      catch (BadImageFormatException ex)
      {
        if (frontEndArgs.DontCatchExceptions)
        {
          throw;
        }
        Console.Error.WriteLine("Raw assembly is invalid or of too late a .NET version");
        if (frontEndArgs.VerboseMode)
        {
          Console.Error.WriteLine(ex.StackTrace);
        }
      }
      catch (TargetInvocationException ex)
      {
        if (frontEndArgs.DontCatchExceptions)
        {
          throw;
        }
        Console.Error.WriteLine("Program being profiled threw an exception");
        Console.Error.WriteLine(ex.GetBaseException());
      }
      catch (VariableVisitorException ex)
      {
        if (frontEndArgs.DontCatchExceptions)
        {
          throw;
        }
        Console.Error.WriteLine(ex.Message);
        if (frontEndArgs.VerboseMode)
        {
          Console.Error.WriteLine(ex.InnerException.Message);
          Console.Error.WriteLine(ex.InnerException.StackTrace);
        }
      }
    }

    /// <summary>
    /// Execute the program that has been saved to disk.
    /// </summary>
    /// <param name="args">Args DNFELauncher was called with, the arguments for the program will 
    /// be extracted from here</param>
    /// <param name="frontEndArgs">FrontEndArgs object used during instrumentation, the
    /// name of the program to execute will be extracted from here</param>
    private static void ExecuteProgramFromDisk(string[] args, FrontEndArgs frontEndArgs)
    {      
      ProcessStartInfo psi = new ProcessStartInfo();
      psi.FileName = frontEndArgs.SaveProgram;
      object[] programArguments = ExtractProgramArguments(args, frontEndArgs);
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
    /// <param name="args">The arguments given to the front-end launcher</param>
    /// <param name="frontEndArgs">Args object used to rewrite assembly</param>
    /// <param name="entryPointInfo">Optional method info for the entry point of the assembly,
    /// if null verification of entry point is skipped and arguments are always copied.</param>
    /// <returns>Array containing a string[] of arguments to be used by the rewritten program
    /// </returns>
    /// <exception cref="Exception">If the arguments after extraction don't match the ones
    /// expected by the entry point of the assembly</exception>
    private static object[] ExtractProgramArguments(string[] args, FrontEndArgs frontEndArgs, 
      MethodInfo entryPointInfo = null)
    {
      // First argument relevant to the program to be profiled
      int indexOfFirstArg = frontEndArgs.ProgramArgIndex + 1;

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
