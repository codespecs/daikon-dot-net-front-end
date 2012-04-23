// This file defines the driver for the front-end. It parses all command-line
// arguments, then calls the profiler to rewrite the program IL with the
// instrumentation calls. Finally, it executes the rewritten IL.

using System;
using DotNetFrontEnd;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;

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
      if (args == null || args.Length < 1)
      {
        Console.WriteLine("FrontEndLauncher.exe [arguments] ProgramToBeProfiled");
        return;
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
        // Print the decls in one file
        frontEndArgs.SetDeclExtension();
        ILRewriter.rewrite_il(frontEndArgs, typeManager);
        // Normally, the args are handed to the visitor statically. However since the program 
        // is being run separately we have to serialize the args.
        IFormatter formatter = new BinaryFormatter();
        Stream stream = new FileStream(ILRewriter.VisitorDll + VariableVisitor.SavedArgsExtension,
            FileMode.Create, FileAccess.Write, FileShare.None);

        // When the program actually runs print the dtrace in another
        frontEndArgs.SetDtraceExtension();
        formatter.Serialize(stream, frontEndArgs);
        stream.Close();
        stream = new FileStream(ILRewriter.VisitorDll + VariableVisitor.SavedTypeManagerExtension,
            FileMode.Create, FileAccess.Write, FileShare.None);
        formatter.Serialize(stream, typeManager);
        stream.Close();
        return;
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

      // Hold the IL for the program to be profiled in memory if we are running the modified 
      // program directly.
      MemoryStream resultStream = null;
      //try
      //{
        resultStream = ILRewriter.rewrite_il(frontEndArgs, typeManager);
        if (resultStream == null)
        {
          throw new ArgumentException("resultStream", "Invalid result stream produced.");
        }
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

      // Load and execute the rewritten program.
      try
      {
        Assembly rewrittenAssembly = Assembly.Load(resultStream.ToArray());
        resultStream.Dispose();
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

        // First argument relevant to the program to be profiled
        int indexOfFirstArg = frontEndArgs.ProgramArgIndex + 1;

        // Assume the single parameter is an array of strings -- the arguments
        if (rewrittenAssembly.EntryPoint.GetParameters().Length == 1 &&
            rewrittenAssembly.EntryPoint.GetParameters()[0].ParameterType.Equals(typeof(string[])))
        {
          // Pass on arguments to the program, all but the program name
          string[] programArgs = new string[args.Length - indexOfFirstArg];
          Array.Copy(args, indexOfFirstArg, programArgs, 0, args.Length - indexOfFirstArg);
          rewrittenAssembly.EntryPoint.Invoke(null, new object[] { programArgs });
        }
        else if (rewrittenAssembly.EntryPoint.GetParameters().Length == 0)
        {
          // Otherwise assume there are no arguments
          rewrittenAssembly.EntryPoint.Invoke(null, null);
        }
        else
        {
          Console.Error.WriteLine("Unable to execute the program with the given type of arguments");
        }

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
  }
}
